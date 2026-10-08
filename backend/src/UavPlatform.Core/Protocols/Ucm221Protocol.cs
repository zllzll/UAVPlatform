using UavPlatform.Core.Models;

namespace UavPlatform.Core.Protocols;

/// <summary>
/// UCM221 雷达通用协议 V1.1.0 的上传数据流（本项目只取 GPS / IMU / 无人机信息）。
/// <para>
/// 帧格式：<c>A5 5A | 数据总长度L(2) | 汇总信息(20) | 目标信息(5+14*N1) | 点云信息(5+14*N2)[+扩展信息][+无人机信息] | 校验和(1)</c>，
/// 总帧长 = L + 5。
/// </para>
/// <para>
/// ⚠️ 协议原文写「大端模式」，但参考项目真机实测为**小端**（唯一例外是各模块起始码按字面字节序列出现：
/// <c>A5 5A / A7 7A / A8 8A / A9 9A / A3 3A / A2 2A</c>）。因此默认按小端解析，
/// 可通过 <see cref="Ucm221ProtocolSettings.BigEndian"/> 切换。
/// </para>
/// </summary>
public sealed class Ucm221Protocol : DeviceProtocol
{
    public static readonly byte[] FrameHeader = [0xA5, 0x5A];
    public static readonly byte[] SummaryHeader = [0xA7, 0x7A];
    public static readonly byte[] TargetHeader = [0xA8, 0x8A];
    public static readonly byte[] PointCloudHeader = [0xA9, 0x9A];
    public static readonly byte[] ExtendedHeader = [0xA3, 0x3A];
    public static readonly byte[] UavInfoHeader = [0xA2, 0x2A];

    /// <summary>汇总信息固定长度。</summary>
    public const int SummarySize = 20;

    /// <summary>目标 / 点云单条记录长度。</summary>
    public const int RecordSize = 14;

    /// <summary>目标信息模块的固定开销：起始码 2 + 长度 2 + 校验和 1。</summary>
    public const int ModuleOverhead = 5;

    /// <summary>扩展信息（IMU + GPS）模块总长度。</summary>
    public const int ExtendedSize = 45;

    /// <summary>无人机信息模块总长度。</summary>
    public const int UavInfoSize = 35;

    // 汇总信息中「数据包状态」的位掩码
    public const int StatusDataValid = 0x01;
    public const int StatusExtendedPresent = 0x02;
    public const int StatusUavInfoPresent = 0x04;

    private readonly Ucm221ProtocolSettings _settings;

    public Ucm221Protocol(Ucm221ProtocolSettings settings) => _settings = settings;

    public override string Name => "UCM221 V1.1.0";

    /// <inheritdoc />
    public override FrameScan Scan(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 2) return FrameScan.NeedMore;

        if (buffer[0] != FrameHeader[0] || buffer[1] != FrameHeader[1])
        {
            var next = buffer[1..].IndexOf(FrameHeader[0]);
            return FrameScan.Skip(next < 0 ? buffer.Length : next + 1);
        }

        if (buffer.Length < 4) return FrameScan.NeedMore;

        int length = Endian.U16(buffer, 2, _settings.BigEndian);
        var total = length + 5;

        // 长度字段必须能容纳汇总信息，否则视为失步
        if (length < SummarySize || length > 60000)
        {
            return FrameScan.Skip(2);
        }

        if (buffer.Length < total) return FrameScan.NeedMore;

        byte sum = 0;
        for (var i = 4; i < 4 + length; i++) sum += buffer[i];
        if (sum != buffer[4 + length]) return FrameScan.Skip(2);

        return FrameScan.Frame(total);
    }

    /// <summary>解析一整帧上传数据。</summary>
    public Ucm221FrameResult Parse(ReadOnlySpan<byte> frame, long sequence, string? deviceName)
    {
        var big = _settings.BigEndian;
        var length = Endian.U16(frame, 2, big);
        var payload = frame.Slice(4, length);

        var result = new Ucm221FrameResult
        {
            Timestamp = DateTimeOffset.Now,
            Sequence = sequence,
            DeviceName = deviceName,
        };

        // ---- 汇总信息（payload[0..19]）----
        if (payload.Length < SummarySize) return result;

        result.SummaryHeaderValid = payload[0] == SummaryHeader[0] && payload[1] == SummaryHeader[1];
        result.PacketStatus = payload[3];
        var targetCount = payload[4];
        result.TargetCount = targetCount;
        result.TargetSize = payload[5];

        var yearOffset = payload[6];
        var month = payload[7];
        var day = payload[8];
        var hour = payload[9];
        var minute = payload[10];
        var second = payload[11];
        var millisecond = Endian.U16(payload, 12, big);
        result.DeviceId = Endian.U16(payload, 14, big);
        var pointCount = Endian.U16(payload, 16, big);
        result.PointCloudCount = pointCount;
        result.PointSize = payload[18];

        result.DeviceTime = FormatDeviceTime(yearOffset, month, day, hour, minute, second, millisecond);

        // ---- 目标信息（紧随汇总信息，5 + 14*N1 字节）----
        var targetInfoOffset = SummarySize;
        result.TargetInfoOffset = targetInfoOffset;
        result.TargetInfoHeaderValid = Matches(payload, targetInfoOffset, TargetHeader);

        var pointInfoOffset = targetInfoOffset + ModuleOverhead + RecordSize * targetCount;
        result.PointCloudInfoOffset = pointInfoOffset;
        result.PointCloudInfoHeaderValid = Matches(payload, pointInfoOffset, PointCloudHeader);

        // ---- 可选模块：扩展信息（0xA33A）与无人机信息（0xA22A）----
        // 协议原文把 IMU+GPS 记在「点云信息总长度」之内，但没有明确无人机信息模块的落位，
        // 且帧总长表格存在 1 字节的边界歧义。因此这里采用「按起始码扫描 + 长度与校验和双重校验」，
        // 并以汇总信息的状态位作为前置门限，兼顾两种排版。
        if (_settings.ParseExtendedInfo && (result.PacketStatus & StatusExtendedPresent) != 0)
        {
            TryScanModule(payload, ExtendedHeader, ExtendedSize, big, out var offset);
            if (offset >= 0)
            {
                result.ExtendedOffset = offset;
                result.Extended = ParseExtended(payload.Slice(offset, ExtendedSize), big);
            }
        }

        if (_settings.ParseUavInfo && (result.PacketStatus & StatusUavInfoPresent) != 0)
        {
            TryScanModule(payload, UavInfoHeader, UavInfoSize, big, out var offset);
            if (offset >= 0)
            {
                result.UavInfoOffset = offset;
                result.UavInfo = ParseUavInfo(payload.Slice(offset, UavInfoSize), big);
            }
        }

        return result;
    }

    /// <summary>
    /// 在汇总信息之后扫描指定模块：要求起始码匹配、长度字段与预期一致、且模块校验和正确。
    /// </summary>
    private static void TryScanModule(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> header, int expectedSize,
        bool bigEndian, out int offset)
    {
        offset = -1;
        for (var i = SummarySize; i + expectedSize <= payload.Length; i++)
        {
            if (!Matches(payload, i, header)) continue;

            var declared = Endian.U16(payload, i + 2, bigEndian);
            if (declared != expectedSize && declared != expectedSize - ModuleOverhead) continue;

            var slice = payload.Slice(i, expectedSize);
            if (!ModuleChecksumValid(slice)) continue;

            offset = i;
            return;
        }
    }

    /// <summary>模块自校验和：起始码至校验和前一字节的累加和低 8 位（同时兼容不含起始码的算法）。</summary>
    private static bool ModuleChecksumValid(ReadOnlySpan<byte> module)
    {
        var expected = module[^1];
        byte withHeader = 0;
        for (var i = 0; i < module.Length - 1; i++) withHeader += module[i];
        if (withHeader == expected) return true;

        byte withoutHeader = 0;
        for (var i = 2; i < module.Length - 1; i++) withoutHeader += module[i];
        return withoutHeader == expected;
    }

    private static bool Matches(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> pattern) =>
        offset >= 0 && offset + pattern.Length <= buffer.Length &&
        buffer[offset] == pattern[0] && buffer[offset + 1] == pattern[1];

    private static string FormatDeviceTime(int yearOffset, int month, int day, int hour, int minute, int second, int millisecond)
    {
        if (month is < 1 or > 12 || day is < 1 or > 31) return string.Empty;
        var year = 1900 + yearOffset;
        var ms = Math.Clamp(millisecond, 0, 999);
        return $"{year:D4}-{month:D2}-{day:D2}T{hour:D2}:{minute:D2}:{second:D2}.{ms:D3}";
    }

    /// <summary>解析扩展信息模块（0xA33A，45 字节）。</summary>
    private static Ucm221Extended ParseExtended(ReadOnlySpan<byte> module, bool bigEndian)
    {
        static double? Scaled(short raw, double factor, short invalid = -32768) =>
            raw == invalid ? null : raw * factor;

        var extended = new Ucm221Extended
        {
            Accel1 =
            [
                Scaled(Endian.I16(module, 4, bigEndian), 0.004788) ?? double.NaN,
                Scaled(Endian.I16(module, 6, bigEndian), 0.004788) ?? double.NaN,
                Scaled(Endian.I16(module, 8, bigEndian), 0.004788) ?? double.NaN,
            ],
            Gyro1 =
            [
                Scaled(Endian.I16(module, 10, bigEndian), 0.001065) ?? double.NaN,
                Scaled(Endian.I16(module, 12, bigEndian), 0.001065) ?? double.NaN,
                Scaled(Endian.I16(module, 14, bigEndian), 0.001065) ?? double.NaN,
            ],
            Accel2 =
            [
                Scaled(Endian.I16(module, 16, bigEndian), 0.004788) ?? double.NaN,
                Scaled(Endian.I16(module, 18, bigEndian), 0.004788) ?? double.NaN,
                Scaled(Endian.I16(module, 20, bigEndian), 0.004788) ?? double.NaN,
            ],
            Gyro2 =
            [
                Scaled(Endian.I16(module, 22, bigEndian), 0.001065) ?? double.NaN,
                Scaled(Endian.I16(module, 24, bigEndian), 0.001065) ?? double.NaN,
                Scaled(Endian.I16(module, 26, bigEndian), 0.001065) ?? double.NaN,
            ],
            Latitude = ScaledInt(Endian.I32(module, 28, bigEndian), 1e-7),
            Longitude = ScaledInt(Endian.I32(module, 32, bigEndian), 1e-7),
            AltitudeM = ScaledInt(Endian.I32(module, 36, bigEndian), 0.001),
            SpeedMps = Scaled(Endian.I16(module, 40, bigEndian), 0.01),
            MagneticDeclinationDeg = Scaled(Endian.I16(module, 42, bigEndian), 0.01),
        };
        return extended;
    }

    /// <summary>解析无人机信息模块（0xA22A，35 字节）。</summary>
    private static Ucm221UavInfo ParseUavInfo(ReadOnlySpan<byte> module, bool bigEndian)
    {
        static double? Scaled(short raw, double factor, short invalid = -32768) =>
            raw == invalid ? null : raw * factor;

        var flags = Endian.U16(module, 4, bigEndian);
        return new Ucm221UavInfo
        {
            Flags = flags,
            PositionValid = (flags & 0x01) != 0,
            AttitudeValid = (flags & 0x02) != 0,
            Latitude = ScaledInt(Endian.I32(module, 6, bigEndian), 1e-7),
            Longitude = ScaledInt(Endian.I32(module, 10, bigEndian), 1e-7),
            AltitudeM = ScaledInt(Endian.I32(module, 14, bigEndian), 0.001),
            RelativeAltitudeM = ScaledInt(Endian.I32(module, 18, bigEndian), 0.001),
            VelocityNorthMps = Scaled(Endian.I16(module, 22, bigEndian), 0.01),
            VelocityEastMps = Scaled(Endian.I16(module, 24, bigEndian), 0.01),
            VelocityDownMps = Scaled(Endian.I16(module, 26, bigEndian), 0.01),
            RollRad = Scaled(Endian.I16(module, 28, bigEndian), 0.0001, short.MinValue),
            PitchRad = Scaled(Endian.I16(module, 30, bigEndian), 0.0001, short.MinValue),
            HeadingRad = Scaled(Endian.I16(module, 32, bigEndian), 0.0001, short.MinValue),
        };
    }

    /// <summary>Int32 字段按缩放因子换算；无效值 −2147483648 返回 null。</summary>
    private static double? ScaledInt(int raw, double factor) =>
        raw == int.MinValue ? null : raw * factor;
}

/// <summary>UCM221 扩展信息模块（0xA33A）解析结果。</summary>
public sealed class Ucm221Extended
{
    public double[] Accel1 { get; set; } = [];
    public double[] Gyro1 { get; set; } = [];
    public double[] Accel2 { get; set; } = [];
    public double[] Gyro2 { get; set; } = [];
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? AltitudeM { get; set; }
    /// <summary>GPS 对地速度（m/s，负值表示后退）。</summary>
    public double? SpeedMps { get; set; }
    /// <summary>GPS 磁偏角（度，东偏为正）。</summary>
    public double? MagneticDeclinationDeg { get; set; }
}

/// <summary>UCM221 无人机信息模块（0xA22A）解析结果。</summary>
public sealed class Ucm221UavInfo
{
    public int Flags { get; set; }
    /// <summary>Bit0：GPS 位置有效。</summary>
    public bool PositionValid { get; set; }
    /// <summary>Bit1：姿态有效。</summary>
    public bool AttitudeValid { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? AltitudeM { get; set; }
    /// <summary>无人机相对地面高度（米）。</summary>
    public double? RelativeAltitudeM { get; set; }
    public double? VelocityNorthMps { get; set; }
    public double? VelocityEastMps { get; set; }
    public double? VelocityDownMps { get; set; }
    /// <summary>横滚角（弧度，向右为正）。</summary>
    public double? RollRad { get; set; }
    /// <summary>俯仰角（弧度，向上为正）。</summary>
    public double? PitchRad { get; set; }
    /// <summary>航向角（弧度，顺时针为正）。</summary>
    public double? HeadingRad { get; set; }
}

/// <summary>一帧 UCM221 上传数据的解析结果。</summary>
public sealed class Ucm221FrameResult
{
    public DateTimeOffset Timestamp { get; set; }
    public long Sequence { get; set; }
    public string? DeviceName { get; set; }

    /// <summary>汇总信息起始码是否为约定的 <c>A7 7A</c>。</summary>
    public bool SummaryHeaderValid { get; set; }

    /// <summary>数据包状态位掩码。</summary>
    public int PacketStatus { get; set; }

    /// <summary>目标个数 N1。</summary>
    public int TargetCount { get; set; }

    /// <summary>单个目标大小（协议声明值）。</summary>
    public int TargetSize { get; set; }

    /// <summary>点云个数 N2。</summary>
    public int PointCloudCount { get; set; }

    /// <summary>单个点云大小（协议声明值）。</summary>
    public int PointSize { get; set; }

    /// <summary>设备 ID（与设备 IP 地址相同）。</summary>
    public int DeviceId { get; set; }

    /// <summary>设备时间（由汇总信息合成，ISO 8601 本地格式）。</summary>
    public string? DeviceTime { get; set; }

    /// <summary>目标信息模块在负载中的偏移。</summary>
    public int TargetInfoOffset { get; set; }

    /// <summary>目标信息起始码是否为 <c>A8 8A</c>。</summary>
    public bool TargetInfoHeaderValid { get; set; }

    /// <summary>点云信息模块在负载中的偏移（按 N1 推算）。</summary>
    public int PointCloudInfoOffset { get; set; }

    /// <summary>点云信息起始码是否为 <c>A9 9A</c>。</summary>
    public bool PointCloudInfoHeaderValid { get; set; }

    /// <summary>扩展信息模块在负载中的偏移，−1 表示未找到。</summary>
    public int ExtendedOffset { get; set; } = -1;

    /// <summary>扩展信息（IMU + GPS）。</summary>
    public Ucm221Extended? Extended { get; set; }

    /// <summary>无人机信息模块在负载中的偏移，−1 表示未找到。</summary>
    public int UavInfoOffset { get; set; } = -1;

    /// <summary>无人机信息（位置 + 姿态）。</summary>
    public Ucm221UavInfo? UavInfo { get; set; }

    /// <summary>转换为落盘 / 推送用的样本。</summary>
    public DroneGpsSample ToSample()
    {
        // 无人机信息模块（0xA22A）优先，其位置带有效性标志；否则回退到扩展信息的 GPS。
        GnssFix? fix = null;
        if (UavInfo is { PositionValid: true } uav && uav.Latitude.HasValue && uav.Longitude.HasValue)
        {
            fix = new GnssFix
            {
                Latitude = uav.Latitude.Value,
                Longitude = uav.Longitude.Value,
                AltitudeM = uav.AltitudeM,
                Valid = true,
            };
        }
        else if (Extended is { } ext && ext.Latitude.HasValue && ext.Longitude.HasValue)
        {
            fix = new GnssFix
            {
                Latitude = ext.Latitude.Value,
                Longitude = ext.Longitude.Value,
                AltitudeM = ext.AltitudeM,
                SpeedMps = ext.SpeedMps,
                MagneticVariationDeg = ext.MagneticDeclinationDeg,
                Valid = true,
            };
        }

        var attitude = UavInfo is { AttitudeValid: true } a
            ? new UavAttitude
            {
                RollRad = a.RollRad,
                PitchRad = a.PitchRad,
                HeadingRad = a.HeadingRad,
                VelocityNorthMps = a.VelocityNorthMps,
                VelocityEastMps = a.VelocityEastMps,
                VelocityDownMps = a.VelocityDownMps,
                RelativeAltitudeM = a.RelativeAltitudeM,
            }
            : null;

        var imu = Extended is { } e
            ? new ImuSample { Accel1 = e.Accel1, Gyro1 = e.Gyro1, Accel2 = e.Accel2, Gyro2 = e.Gyro2 }
            : null;

        return new DroneGpsSample
        {
            Device = DeviceKind.DroneGps,
            Timestamp = Timestamp,
            Sequence = Sequence,
            DeviceName = DeviceName,
            DeviceTime = DeviceTime,
            Fix = fix,
            Attitude = attitude,
            Imu = imu,
            GpsSpeedMps = Extended?.SpeedMps,
            MagneticDeclinationDeg = Extended?.MagneticDeclinationDeg,
            PacketStatus = PacketStatus,
            DeviceId = DeviceId,
            TargetCount = TargetCount,
            PointCloudCount = PointCloudCount,
        };
    }
}
