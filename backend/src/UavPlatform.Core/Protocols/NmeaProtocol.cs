using System.Globalization;
using UavPlatform.Core.Models;

namespace UavPlatform.Core.Protocols;

/// <summary>
/// NMEA 0183 语句分帧与解析（基座 UM982 使用的文本协议）。
/// 分帧规则：以 <c>\n</c> 为界，逐行切出；行首必须是 <c>$</c>。
/// </summary>
public sealed class NmeaProtocol : DeviceProtocol
{
    /// <summary>单行最大长度保护，超过则视为噪声丢弃。</summary>
    public const int MaxLineLength = 1024;

    private readonly Um982ProtocolSettings _settings;

    public NmeaProtocol(Um982ProtocolSettings settings) => _settings = settings;

    public override string Name => "NMEA 0183 (UM982)";

    /// <inheritdoc />
    public override FrameScan Scan(ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty) return FrameScan.NeedMore;

        var newline = buffer.IndexOf((byte)'\n');
        if (newline < 0)
        {
            // 没有整行：若已超长，说明是噪声，丢弃；否则等更多数据
            return buffer.Length > MaxLineLength ? FrameScan.Skip(buffer.Length) : FrameScan.NeedMore;
        }

        // 行首必须是 '$'，否则丢弃到本行结束
        if (buffer[0] != (byte)'$')
        {
            return FrameScan.Skip(newline + 1);
        }

        return newline == 0 ? FrameScan.Skip(1) : FrameScan.Frame(newline);
    }

    /// <summary>解析一行 NMEA 语句（不含行尾的 CR/LF）。</summary>
    public bool TryParse(ReadOnlySpan<byte> line, out NmeaSentence sentence)
    {
        sentence = default;
        if (line.IsEmpty || line[0] != (byte)'$') return false;

        var text = System.Text.Encoding.ASCII.GetString(line).TrimEnd('\r');

        // 校验和：'$' 与 '*' 之间所有字符 XOR
        var star = text.LastIndexOf('*');
        string body;
        int? declared = null;
        if (star > 0 && star + 3 <= text.Length &&
            int.TryParse(text.AsSpan(star + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            declared = parsed;
            body = text[1..star];
        }
        else
        {
            body = text[1..];
        }

        byte checksum = 0;
        foreach (var ch in body) checksum ^= (byte)ch;
        var checksumValid = declared.HasValue && declared.Value == checksum;

        if (_settings.RequireChecksum && declared.HasValue && !checksumValid) return false;

        var fields = body.Split(',');
        if (fields.Length == 0) return false;

        var tag = fields[0];
        if (tag.Length < 3) return false;
        var type = tag[^3..].ToUpperInvariant();

        sentence = new NmeaSentence
        {
            Raw = text,
            Tag = tag,
            Type = type,
            Fields = fields,
            ChecksumValid = checksumValid,
            HasChecksum = declared.HasValue,
        };
        return true;
    }

    /// <summary>该语句是否为本项目关心的类型。</summary>
    public bool IsRelevant(string type) => type switch
    {
        "GGA" => _settings.ParseGga,
        "RMC" => _settings.ParseRmc,
        "VTG" => _settings.ParseVtg,
        "THS" => _settings.ParseThs,
        _ => false,
    };
}

/// <summary>一条 NMEA 语句。</summary>
public readonly record struct NmeaSentence
{
    /// <summary>原始文本（含 <c>$</c> 与校验和）。</summary>
    public string Raw { get; init; }

    /// <summary>语句标识，例如 <c>GPGGA</c>、<c>GNGGA</c>。</summary>
    public string Tag { get; init; }

    /// <summary>三位类型码：GGA / RMC / VTG / THS。</summary>
    public string Type { get; init; }

    /// <summary>按逗号切分后的字段（下标 0 为语句标识）。</summary>
    public string[] Fields { get; init; }

    /// <summary>校验和是否正确。</summary>
    public bool ChecksumValid { get; init; }

    /// <summary>语句是否带校验和。</summary>
    public bool HasChecksum { get; init; }

    /// <summary>安全取字段。</summary>
    public string Field(int index) => index >= 0 && index < Fields.Length ? Fields[index] : string.Empty;
}

/// <summary>
/// 把 GGA / RMC / VTG / THS 汇聚成一个定位结果。
/// 汇聚策略：以 GGA 为触发（GGA 关闭或未收到时退化为 RMC 触发），合并最近一次各语句的值。
/// </summary>
public sealed class NmeaAggregator
{
    private readonly Um982ProtocolSettings _settings;
    private GnssFix _fix = new();
    private double? _trueHeadingDeg;
    private string? _headingSource;
    private DateTimeOffset? _rmcUtc;
    private long _sequence;
    private bool _everGga;

    public NmeaAggregator(Um982ProtocolSettings settings) => _settings = settings;

    /// <summary>最近一次 RMC 给出的 UTC 时间。</summary>
    public DateTimeOffset? LastUtcTime => _rmcUtc;

    /// <summary>链路重连后清空汇聚状态，避免把上一次连接的定位结果带进来。</summary>
    public void Reset()
    {
        _fix = new GnssFix();
        _trueHeadingDeg = null;
        _headingSource = null;
        _rmcUtc = null;
        _sequence = 0;
        _everGga = false;
    }

    /// <summary>
    /// 送入一条语句。<paramref name="sample"/> 非空表示本语句触发了新的定位结果。
    /// </summary>
    public bool Apply(in NmeaSentence sentence, out GnssSample? sample)
    {
        sample = null;
        var trigger = false;

        switch (sentence.Type)
        {
            case "GGA" when _settings.ParseGga:
                ApplyGga(sentence);
                _everGga = true;
                trigger = true;
                break;

            case "RMC" when _settings.ParseRmc:
                ApplyRmc(sentence);
                // GGA 可用时不重复产出；否则由 RMC 承担触发
                trigger = !_everGga || !_settings.ParseGga;
                break;

            case "VTG" when _settings.ParseVtg:
                ApplyVtg(sentence);
                break;

            case "THS" when _settings.ParseThs:
                ApplyThs(sentence);
                break;

            default:
                return false;
        }

        if (!trigger) return false;

        sample = new GnssSample
        {
            Device = DeviceKind.BaseStation,
            Timestamp = DateTimeOffset.Now,
            Sequence = ++_sequence,
            DeviceTime = _rmcUtc?.ToString("O"),
            Fix = _fix,
            TrueHeadingDeg = _trueHeadingDeg,
            HeadingSource = _headingSource,
            Sentences = [sentence.Raw],
        };
        return true;
    }

    /// <summary>GGA：定位质量、卫星数、HDOP、海拔、大地水准面差距。</summary>
    private void ApplyGga(in NmeaSentence s)
    {
        var lat = ParseLatitude(s.Field(2), s.Field(3));
        var lon = ParseLongitude(s.Field(4), s.Field(5));
        var quality = ParseInt(s.Field(6));

        _fix = _fix with
        {
            Latitude = lat ?? _fix.Latitude,
            Longitude = lon ?? _fix.Longitude,
            FixQuality = quality,
            Satellites = ParseInt(s.Field(7)) ?? _fix.Satellites,
            Hdop = ParseDouble(s.Field(8)) ?? _fix.Hdop,
            AltitudeM = ParseAltitude(s.Field(9), s.Field(10)) ?? _fix.AltitudeM,
            GeoidSeparationM = ParseAltitude(s.Field(11), s.Field(12)) ?? _fix.GeoidSeparationM,
            Valid = quality is > 0 || _fix.Valid,
            UtcTime = _fix.UtcTime,
        };
    }

    /// <summary>RMC：有效性、对地速度（节）、对地航向、UTC 日期时间、磁偏角。</summary>
    private void ApplyRmc(in NmeaSentence s)
    {
        var status = s.Field(2);
        var valid = string.Equals(status, "A", StringComparison.OrdinalIgnoreCase);

        var lat = ParseLatitude(s.Field(3), s.Field(4));
        var lon = ParseLongitude(s.Field(5), s.Field(6));
        var speedKnots = ParseDouble(s.Field(7));
        var course = ParseDouble(s.Field(8));
        var utc = ParseRmcTime(s.Field(1), s.Field(9));

        double? magneticVariation = ParseDouble(s.Field(10));
        if (magneticVariation.HasValue && string.Equals(s.Field(11), "W", StringComparison.OrdinalIgnoreCase))
        {
            magneticVariation = -magneticVariation.Value;
        }

        _rmcUtc = utc ?? _rmcUtc;

        _fix = _fix with
        {
            Latitude = lat ?? _fix.Latitude,
            Longitude = lon ?? _fix.Longitude,
            // RMC 速度为节，1 节 = 0.514444 m/s
            SpeedMps = speedKnots.HasValue ? speedKnots.Value * 0.5144444444444444 : _fix.SpeedMps,
            CourseDeg = course ?? _fix.CourseDeg,
            MagneticVariationDeg = magneticVariation ?? _fix.MagneticVariationDeg,
            Valid = valid,
            UtcTime = _rmcUtc,
        };

        // THS 优先；无 THS 时用 RMC 航向
        if (_settings.RmcCourseAsHeading && course.HasValue && _headingSource != "THS")
        {
            _trueHeadingDeg = Normalize360(course.Value);
            _headingSource = "RMC";
        }
    }

    /// <summary>VTG：对地航向与对地速度。</summary>
    private void ApplyVtg(in NmeaSentence s)
    {
        var courseTrue = ParseDouble(s.Field(1));
        var speedKmh = ParseDouble(s.Field(7));

        _fix = _fix with
        {
            // km/h → m/s
            SpeedMps = speedKmh.HasValue ? speedKmh.Value / 3.6 : _fix.SpeedMps,
            CourseDeg = courseTrue ?? _fix.CourseDeg,
        };

        if (_settings.RmcCourseAsHeading && courseTrue.HasValue && _headingSource != "THS")
        {
            _trueHeadingDeg = Normalize360(courseTrue.Value);
            _headingSource = "VTG";
        }
    }

    /// <summary>THS：双天线真航向，优先级最高。</summary>
    private void ApplyThs(in NmeaSentence s)
    {
        var heading = ParseDouble(s.Field(1));
        if (!heading.HasValue) return;

        // 模式位（NMEA 0183 THS）：A 自主解算 / E 估算 / M 手动 / S 模拟器 / V 无效
        var mode = s.Field(2).Trim();
        if (mode.Length == 0) return;
        var flag = char.ToUpperInvariant(mode[0]);
        if (flag is not ('A' or 'E' or 'M' or 'S')) return;

        // 驱动与开源实现均把航向归一化到 ±180；本平台对外统一用 0~360 罗盘方位角
        _trueHeadingDeg = Normalize360(heading.Value);
        _headingSource = "THS";
    }

    /// <summary>纬度 <c>ddmm.mmmm</c> → 度；南纬取负。</summary>
    public static double? ParseLatitude(string value, string hemisphere)
    {
        var raw = ParseDouble(value);
        if (!raw.HasValue) return null;
        var degrees = Math.Floor(raw.Value / 100.0);
        var minutes = raw.Value - degrees * 100.0;
        var result = degrees + minutes / 60.0;
        if (string.Equals(hemisphere, "S", StringComparison.OrdinalIgnoreCase)) result = -result;
        return result;
    }

    /// <summary>经度 <c>dddmm.mmmm</c> → 度；西经取负。</summary>
    public static double? ParseLongitude(string value, string hemisphere)
    {
        var raw = ParseDouble(value);
        if (!raw.HasValue) return null;
        var degrees = Math.Floor(raw.Value / 100.0);
        var minutes = raw.Value - degrees * 100.0;
        var result = degrees + minutes / 60.0;
        if (string.Equals(hemisphere, "W", StringComparison.OrdinalIgnoreCase)) result = -result;
        return result;
    }

    /// <summary>RMC 的 <c>hhmmss.sss</c> + <c>ddmmyy</c> 合成 UTC 时间。</summary>
    public static DateTimeOffset? ParseRmcTime(string time, string date)
    {
        if (time.Length < 6 || date.Length < 6) return null;
        if (!int.TryParse(date.AsSpan(0, 2), out var day)) return null;
        if (!int.TryParse(date.AsSpan(2, 2), out var month)) return null;
        if (!int.TryParse(date.AsSpan(4, 2), out var year2)) return null;
        if (!int.TryParse(time.AsSpan(0, 2), out var hour)) return null;
        if (!int.TryParse(time.AsSpan(2, 2), out var minute)) return null;

        var second = ParseDouble(time[4..]) ?? 0;
        var wholeSeconds = (int)Math.Floor(second);
        var milliseconds = (int)Math.Round((second - wholeSeconds) * 1000);
        if (milliseconds >= 1000) { milliseconds -= 1000; wholeSeconds += 1; }

        var year = year2 >= 80 ? 1900 + year2 : 2000 + year2;
        if (month is < 1 or > 12 || day is < 1 or > 31) return null;
        if (hour > 23 || minute > 59 || wholeSeconds > 60) return null;

        try
        {
            return new DateTimeOffset(new DateTime(year, month, day, hour, minute, Math.Min(wholeSeconds, 59), DateTimeKind.Utc))
                .AddMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>归一化到 [0, 360)。</summary>
    public static double Normalize360(double value)
    {
        var result = value % 360.0;
        if (result < 0) result += 360.0;
        return result;
    }

    private static double? ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static int? ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>高度字段带单位：M = 米，F = 英尺（×0.3048）。</summary>
    private static double? ParseAltitude(string value, string unit)
    {
        var v = ParseDouble(value);
        if (!v.HasValue) return null;
        return unit.Length > 0 && (unit[0] is 'F' or 'f') ? v.Value * 0.3048 : v.Value;
    }
}
