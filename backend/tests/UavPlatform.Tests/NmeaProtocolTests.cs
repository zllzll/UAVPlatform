using UavPlatform.Core.Models;
using UavPlatform.Core.Protocols;

namespace UavPlatform.Tests;

/// <summary>
/// 基座 UM982 的 NMEA 0183 文本协议单元测试。
/// 语句字符串由 <see cref="FrameBuilder.Nmea"/> 按「$ 与 * 之间所有字节异或」独立算出校验和，
/// 不调用被测实现的任何校验逻辑。
/// </summary>
public class NmeaProtocolTests
{
    /// <summary>北京参考点（39.9041984N / 116.4073984E）的 ddmm.mmmm 写法。</summary>
    private const string BeijingLat = "3954.251904";

    /// <inheritdoc cref="BeijingLat" />
    private const string BeijingLon = "11624.443904";

    /// <summary>一条能独立触发定位结果的 GGA（聚合器认为 GGA 是主触发源）。</summary>
    private static readonly string TriggerGga =
        $"GPGGA,123519,{BeijingLat},N,{BeijingLon},E,1,08,0.9,1.0,M,0.0,M,,";

    private static NmeaProtocol NewProtocol(Um982ProtocolSettings? settings = null) =>
        new(settings ?? new Um982ProtocolSettings());

    private static NmeaAggregator NewAggregator(Um982ProtocolSettings? settings = null) =>
        new(settings ?? new Um982ProtocolSettings());

    /// <summary>造一条语句并解析成 <see cref="NmeaSentence"/>。</summary>
    private static NmeaSentence Sentence(
        string body,
        int checksumDelta = 0,
        bool withChecksum = true,
        Um982ProtocolSettings? settings = null)
    {
        var protocol = NewProtocol(settings);
        var ok = protocol.TryParse(FrameBuilder.NmeaLine(body, checksumDelta, withChecksum), out var sentence);
        Assert.True(ok, $"语句应能被解析：{body}");
        return sentence;
    }

    /// <summary>送入一条语句并断言它触发了新的定位结果。</summary>
    private static GnssSample Apply(
        NmeaAggregator aggregator,
        string body,
        int checksumDelta = 0,
        bool withChecksum = true)
    {
        var sentence = Sentence(body, checksumDelta, withChecksum);
        Assert.True(aggregator.Apply(sentence, out var sample), $"语句应触发定位结果：{body}");
        return sample!;
    }

    /// <summary>
    /// 依次送入多条语句，返回最后一条真正触发定位结果的语句所产出的样本。
    /// VTG / THS 只更新内部状态、不单独触发，必须配一条 GGA 才能取出结果。
    /// </summary>
    private static GnssSample Feed(NmeaAggregator aggregator, params string[] bodies)
    {
        var protocol = NewProtocol();
        GnssSample? last = null;
        foreach (var body in bodies)
        {
            Assert.True(protocol.TryParse(FrameBuilder.NmeaLine(body), out var sentence), $"语句应能被解析：{body}");
            if (aggregator.Apply(sentence, out var sample)) last = sample;
        }

        Assert.NotNull(last);
        return last!;
    }

    // ==================== Scan：按行分帧 ====================

    [Fact]
    public void Scan_完整一行应按LF位置切出且帧内含CR()
    {
        var data = FrameBuilder.Ascii(FrameBuilder.Nmea("GPGGA,123519,3954.251904,N,11624.443904,E,1,08,0.9,545.4,M,46.9,M,,"));

        var scan = NewProtocol().Scan(data);

        Assert.Equal(FrameScanStatus.Frame, scan.Status);
        Assert.Equal(data.Length - 1, scan.Length);      // 帧长 = LF 下标，不含 LF
        Assert.Equal((byte)'\r', data[scan.Length - 1]); // 含 CR
    }

    [Fact]
    public void Scan_无换行时少于上限应等待更多数据()
    {
        var scan = NewProtocol().Scan(FrameBuilder.Ascii("$GPGGA,123519,39"));

        Assert.Equal(FrameScanStatus.NeedMore, scan.Status);
    }

    [Fact]
    public void Scan_无换行且超过行上限时应整段丢弃()
    {
        var noise = new byte[1025];
        Array.Fill(noise, (byte)'x');

        var scan = NewProtocol().Scan(noise);

        Assert.Equal(FrameScanStatus.Skip, scan.Status);
        Assert.Equal(noise.Length, scan.Length);
    }

    [Fact]
    public void Scan_行首不是美元符时应丢弃整行()
    {
        var data = FrameBuilder.Ascii("noise here\r\n$GPGGA\r\n");

        var scan = NewProtocol().Scan(data);

        Assert.Equal(FrameScanStatus.Skip, scan.Status);
        Assert.Equal(12, scan.Length); // "noise here\r\n" 共 12 字节，整行丢弃
    }

    [Fact]
    public void Scan_行首即为换行时应只丢弃一个字节()
    {
        var scan = NewProtocol().Scan(FrameBuilder.Ascii("\n$GPGGA\r\n"));

        Assert.Equal(FrameScanStatus.Skip, scan.Status);
        Assert.Equal(1, scan.Length);
    }

    [Fact]
    public void 分帧_多行粘连与逐字节喂入应切出全部语句()
    {
        var ggaBody = $"GPGGA,123519,{BeijingLat},N,{BeijingLon},E,4,12,0.7,45.6,M,46.9,M,,";
        var rmcBody = $"GPRMC,123519,A,{BeijingLat},N,{BeijingLon},E,22.4,84.4,230394,3.5,W";
        var thsBody = "GPTHS,100.5,A";
        // 行前有垃圾字节（NMEA 是行协议，整行被丢弃），三行粘连，逐字节喂入
        var data = FrameBuilder.Ascii("junk\r\n" + FrameBuilder.Nmea(ggaBody) + FrameBuilder.Nmea(rmcBody) + FrameBuilder.Nmea(thsBody));

        var frames = ScanPump.Drain(NewProtocol(), data, chunkSize: 1);

        Assert.Equal(3, frames.Count);
        Assert.Equal(FrameBuilder.NmeaLine(ggaBody), frames[0]);
        Assert.Equal(FrameBuilder.NmeaLine(rmcBody), frames[1]);
        Assert.Equal(FrameBuilder.NmeaLine(thsBody), frames[2]);

        var protocol = NewProtocol();
        Assert.True(protocol.TryParse(frames[0], out var gga));
        Assert.True(protocol.TryParse(frames[1], out var rmc));
        Assert.True(protocol.TryParse(frames[2], out var ths));
        Assert.Equal("GGA", gga.Type);
        Assert.Equal("RMC", rmc.Type);
        Assert.Equal("THS", ths.Type);
        Assert.Equal(39.9041984, NmeaAggregator.ParseLatitude(gga.Field(2), gga.Field(3))!.Value, 6);
    }

    [Fact]
    public void 分帧_末尾不完整的行不应被切出()
    {
        var data = FrameBuilder.Ascii(FrameBuilder.Nmea("GPGGA,1,2,3") + "$GPRMC,12");

        var frames = ScanPump.Drain(NewProtocol(), data);

        Assert.Single(frames);
    }

    // ==================== TryParse：标识、字段与校验和 ====================

    [Fact]
    public void TryParse_应取标识末三位作为类型码并保留全部字段()
    {
        var sentence = Sentence($"GNGGA,123519,{BeijingLat},N,{BeijingLon},E,4,12,0.7,45.6,M,46.9,M,,");

        Assert.Equal("GNGGA", sentence.Tag);
        Assert.Equal("GGA", sentence.Type);
        Assert.True(sentence.ChecksumValid);
        Assert.True(sentence.HasChecksum);
        Assert.Equal("123519", sentence.Field(1));
        Assert.Equal(BeijingLat, sentence.Field(2));
        Assert.Equal(15, sentence.Fields.Length);
    }

    [Fact]
    public void TryParse_校验和错误且要求校验时应拒绝该句()
    {
        var protocol = NewProtocol(); // RequireChecksum 默认 true

        var ok = protocol.TryParse(FrameBuilder.NmeaLine("GPGGA,123519,1,2,3", checksumDelta: 1), out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryParse_校验和错误但不要求校验时应接受且标记为无效()
    {
        var protocol = NewProtocol(new Um982ProtocolSettings { RequireChecksum = false });

        var ok = protocol.TryParse(FrameBuilder.NmeaLine("GPGGA,123519,1,2,3", checksumDelta: 1), out var sentence);

        Assert.True(ok);
        Assert.False(sentence.ChecksumValid);
        Assert.True(sentence.HasChecksum);
        Assert.Equal("GGA", sentence.Type);
    }

    [Fact]
    public void TryParse_完全不写校验和的语句应被接受且标记为无校验和()
    {
        var protocol = NewProtocol(); // 即便 RequireChecksum = true 也接受

        var ok = protocol.TryParse(FrameBuilder.NmeaLine("GPGGA,123519,1,2,3", withChecksum: false), out var sentence);

        Assert.True(ok);
        Assert.False(sentence.HasChecksum);
        Assert.False(sentence.ChecksumValid);
        Assert.Equal("GGA", sentence.Type);
    }

    [Fact]
    public void TryParse_不以美元符开头或为空应被拒绝()
    {
        var protocol = NewProtocol();

        Assert.False(protocol.TryParse(FrameBuilder.Ascii("GPGGA,1,2,3\r"), out _));
        Assert.False(protocol.TryParse([], out _));
    }

    [Fact]
    public void TryParse_标识不足三位应被拒绝()
    {
        var protocol = NewProtocol();

        Assert.False(protocol.TryParse(FrameBuilder.NmeaLine("GP"), out _));
        Assert.False(protocol.TryParse(FrameBuilder.NmeaLine(string.Empty), out _));
    }

    [Fact]
    public void Field_越界应返回空串而不是抛异常()
    {
        var sentence = Sentence("GPGGA,1,2");

        Assert.Equal(string.Empty, sentence.Field(99));
        Assert.Equal(string.Empty, sentence.Field(-1));
    }

    // ==================== 空字段不错位 ====================

    [Fact]
    public void 空字段不应导致后续字段错位()
    {
        // GGA：第 11 字段（大地水准面差距数值）为空、第 12 字段（单位）为 M，第 13、14 为空
        var gga = Sentence($"GPGGA,123519,{BeijingLat},N,{BeijingLon},E,1,08,0.9,545.4,M,,M,,");

        Assert.Equal("GPGGA", gga.Field(0));
        Assert.Equal("545.4", gga.Field(9));
        Assert.Equal("M", gga.Field(10));
        Assert.Equal(string.Empty, gga.Field(11));
        Assert.Equal("M", gga.Field(12));
        Assert.Equal(string.Empty, gga.Field(13));

        // RMC：第 10 字段（磁偏角）为空、第 11 字段（磁偏方向）为 W
        var rmc = Sentence($"GPRMC,123519,A,{BeijingLat},N,{BeijingLon},E,22.4,84.4,230394,,W");

        Assert.Equal("84.4", rmc.Field(8));
        Assert.Equal("230394", rmc.Field(9));
        Assert.Equal(string.Empty, rmc.Field(10));
        Assert.Equal("W", rmc.Field(11));
    }

    [Fact]
    public void 空字段不应影响聚合结果的取值()
    {
        var aggregator = NewAggregator();

        // 大地水准面差距字段为空 → 该字段保持 null，但经纬度与海拔仍要正确
        var gga = Feed(aggregator, $"GPGGA,123519,{BeijingLat},N,{BeijingLon},E,1,08,0.9,545.4,M,,M,,");

        Assert.Equal(39.9041984, gga.Fix.Latitude, 6);
        Assert.Equal(116.4073984, gga.Fix.Longitude, 6);
        Assert.Equal(545.4, gga.Fix.AltitudeM!.Value, 6);
        Assert.Null(gga.Fix.GeoidSeparationM);

        // 磁偏角字段为空 → 保持 null，但航向仍取自第 8 字段
        var after = Feed(aggregator,
            $"GPRMC,123519,A,{BeijingLat},N,{BeijingLon},E,22.4,84.4,230394,,",
            $"GPGGA,123520,{BeijingLat},N,{BeijingLon},E,1,08,0.9,546.0,M,0.0,M,,");

        Assert.Null(after.Fix.MagneticVariationDeg);
        Assert.Equal(84.4, after.Fix.CourseDeg!.Value, 6);
        Assert.Equal(546.0, after.Fix.AltitudeM!.Value, 6); // 空字段没有把海拔冲掉
    }

    // ==================== 经纬度换算 ====================

    [Theory]
    [InlineData("3954.251904", "N", 39.9041984)]
    [InlineData("3954.251904", "S", -39.9041984)]
    [InlineData("3954.251904", "s", -39.9041984)] // 实现用 OrdinalIgnoreCase，小写也接受
    [InlineData("0000.000000", "N", 0.0)]
    [InlineData("3954.251904", "", 39.9041984)]
    public void ParseLatitude_应按度分换算且仅南纬取负(string value, string hemisphere, double expected)
    {
        var actual = NmeaAggregator.ParseLatitude(value, hemisphere);

        Assert.NotNull(actual);
        Assert.Equal(expected, actual!.Value, 6);
    }

    [Theory]
    [InlineData("11624.443904", "E", 116.4073984)]
    [InlineData("11624.443904", "W", -116.4073984)]
    [InlineData("11624.443904", "w", -116.4073984)]
    [InlineData("00000.000000", "E", 0.0)]
    [InlineData("11624.443904", "", 116.4073984)]
    public void ParseLongitude_应按度分换算且仅西经取负(string value, string hemisphere, double expected)
    {
        var actual = NmeaAggregator.ParseLongitude(value, hemisphere);

        Assert.NotNull(actual);
        Assert.Equal(expected, actual!.Value, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("   ")]
    public void ParseLatitude_非数值应返回空(string value)
    {
        Assert.Null(NmeaAggregator.ParseLatitude(value, "N"));
        Assert.Null(NmeaAggregator.ParseLongitude(value, "E"));
    }

    [Fact]
    public void ParseLatitude_缺少小数点时按度分理解()
    {
        // 39.5 语义上是「0 度 39.5 分」
        var actual = NmeaAggregator.ParseLatitude("39.5", "N");

        Assert.NotNull(actual);
        Assert.Equal(0.6583333, actual!.Value, 6);
    }

    [Fact]
    public void ParseRmcTime_时与日期应合成UTC时间()
    {
        var actual = NmeaAggregator.ParseRmcTime("123519.500", "230394");

        Assert.NotNull(actual);
        Assert.Equal(new DateTimeOffset(1994, 3, 23, 12, 35, 19, TimeSpan.Zero).AddMilliseconds(500), actual);
    }

    [Fact]
    public void ParseRmcTime_两位年份大于等于80时应按19xx处理()
    {
        var actual = NmeaAggregator.ParseRmcTime("000000", "010180");

        Assert.NotNull(actual);
        Assert.Equal(new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero), actual);
    }

    [Theory]
    [InlineData("1235", "230394")]
    [InlineData("123519", "2303")]
    [InlineData("123519", "321394")] // 月 13 非法
    [InlineData("123519", "239994")] // 日 99 非法
    [InlineData("253519", "230394")] // 时 25 非法
    [InlineData("", "230394")]
    [InlineData("123519", "")]
    public void ParseRmcTime_字段不合法应返回空(string time, string date)
    {
        Assert.Null(NmeaAggregator.ParseRmcTime(time, date));
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(90.0, 90.0)]
    [InlineData(360.0, 0.0)]
    [InlineData(370.0, 10.0)]
    [InlineData(-10.0, 350.0)]
    [InlineData(-370.0, 350.0)]
    public void Normalize360_应归一化到0到360(double input, double expected)
    {
        Assert.Equal(expected, NmeaAggregator.Normalize360(input), 9);
    }

    // ==================== GGA ====================

    [Fact]
    public void GGA_全部字段应逐项对应()
    {
        var sample = Apply(NewAggregator(), $"GPGGA,123519,{BeijingLat},N,{BeijingLon},E,4,12,0.7,45.6,M,46.9,M,,");

        Assert.Equal(DeviceKind.BaseStation, sample.Device);
        Assert.Equal(1, sample.Sequence);
        Assert.Equal(39.9041984, sample.Fix.Latitude, 6);
        Assert.Equal(116.4073984, sample.Fix.Longitude, 6);
        Assert.Equal(4, sample.Fix.FixQuality);
        Assert.Equal(12, sample.Fix.Satellites);
        Assert.Equal(0.7, sample.Fix.Hdop!.Value, 6);
        Assert.Equal(45.6, sample.Fix.AltitudeM!.Value, 6);
        Assert.Equal(46.9, sample.Fix.GeoidSeparationM!.Value, 6);
        Assert.True(sample.Fix.Valid);
    }

    [Fact]
    public void GGA_高度单位为英尺时应换算成米()
    {
        var sample = Apply(NewAggregator(), $"GPGGA,123519,{BeijingLat},N,{BeijingLon},E,1,08,0.9,100.0,F,0.0,M,,");

        Assert.Equal(30.48, sample.Fix.AltitudeM!.Value, 6);
        Assert.Equal(0.0, sample.Fix.GeoidSeparationM!.Value, 6);
    }

    [Fact]
    public void GGA_定位质量为0时定位无效()
    {
        var sample = Apply(NewAggregator(), $"GPGGA,123519,{BeijingLat},N,{BeijingLon},E,0,03,0.9,1.0,M,0.0,M,,");

        Assert.Equal(0, sample.Fix.FixQuality);
        Assert.False(sample.Fix.Valid);
    }

    [Fact]
    public void GGA_南纬西经应取负()
    {
        var sample = Apply(NewAggregator(), "GPGGA,123519,3954.251904,S,11624.443904,W,1,08,0.9,1.0,M,0.0,M,,");

        Assert.Equal(-39.9041984, sample.Fix.Latitude, 6);
        Assert.Equal(-116.4073984, sample.Fix.Longitude, 6);
    }

    [Fact]
    public void GGA_连续两句应各自触发并递增序号()
    {
        var aggregator = NewAggregator();

        var first = Apply(aggregator, $"GPGGA,123519,{BeijingLat},N,{BeijingLon},E,1,08,0.9,10.0,M,0.0,M,,");
        var second = Apply(aggregator, $"GPGGA,123520,{BeijingLat},N,{BeijingLon},E,1,09,0.8,11.0,M,0.0,M,,");

        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(11.0, second.Fix.AltitudeM!.Value, 6);
        Assert.Equal(9, second.Fix.Satellites);
    }

    // ==================== RMC ====================

    [Fact]
    public void RMC_全部字段应逐项对应()
    {
        var aggregator = NewAggregator();
        var sample = Apply(aggregator, $"GPRMC,123519,A,{BeijingLat},N,{BeijingLon},E,22.4,84.4,230394,3.5,W");

        Assert.Equal(39.9041984, sample.Fix.Latitude, 6);
        Assert.Equal(116.4073984, sample.Fix.Longitude, 6);
        Assert.True(sample.Fix.Valid);
        Assert.Equal(22.4 * 0.5144444444444444, sample.Fix.SpeedMps!.Value, 9); // 节 → m/s
        Assert.Equal(84.4, sample.Fix.CourseDeg!.Value, 6);
        Assert.Equal(-3.5, sample.Fix.MagneticVariationDeg!.Value, 6);          // W 取负
        Assert.Equal(new DateTimeOffset(1994, 3, 23, 12, 35, 19, TimeSpan.Zero), sample.Fix.UtcTime);
        Assert.Equal(sample.Fix.UtcTime, aggregator.LastUtcTime);
    }

    [Fact]
    public void RMC_状态为V时定位无效()
    {
        var sample = Apply(NewAggregator(), $"GPRMC,123519,V,{BeijingLat},N,{BeijingLon},E,22.4,84.4,230394,3.5,E");

        Assert.False(sample.Fix.Valid);
        Assert.Equal(3.5, sample.Fix.MagneticVariationDeg!.Value, 6); // 东偏为正
    }

    [Fact]
    public void RMC_航向应作为航向来源RMC()
    {
        var sample = Apply(NewAggregator(), $"GPRMC,123519,A,{BeijingLat},N,{BeijingLon},E,22.4,84.4,230394,,W");

        Assert.Equal(84.4, sample.TrueHeadingDeg!.Value, 6);
        Assert.Equal("RMC", sample.HeadingSource);
    }

    [Fact]
    public void RMC_航向为负时应归一化到0到360()
    {
        var sample = Apply(NewAggregator(), $"GPRMC,123519,A,{BeijingLat},N,{BeijingLon},E,0.0,-10.0,230394,,W");

        Assert.Equal(350.0, sample.TrueHeadingDeg!.Value, 6);
        Assert.Equal("RMC", sample.HeadingSource);
    }

    // ==================== VTG ====================

    [Fact]
    public void VTG_航向与对地速度应换算成米每秒()
    {
        var aggregator = NewAggregator();

        // VTG 不触发，只累积状态；由随后的 GGA 触发产出
        // 字段 5 是「 knots」，字段 7 是「km/h」；实现取字段 7 并除以 3.6
        var sample = Feed(aggregator, "GPVTG,90.0,T,88.0,M,19.4384,N,36.0,K,A", TriggerGga);

        Assert.Equal(10.0, sample.Fix.SpeedMps!.Value, 6);  // 36 km/h → 10 m/s
        Assert.Equal(90.0, sample.Fix.CourseDeg!.Value, 6);
        Assert.Equal(90.0, sample.TrueHeadingDeg!.Value, 6);
        Assert.Equal("VTG", sample.HeadingSource);
    }

    [Fact]
    public void VTG_单独送入不应触发定位结果()
    {
        var aggregator = NewAggregator();
        var protocol = NewProtocol();

        Assert.True(protocol.TryParse(FrameBuilder.NmeaLine("GPVTG,90.0,T,88.0,M,19.4384,N,36.0,K,A"), out var vtg));
        Assert.False(aggregator.Apply(vtg, out var sample));
        Assert.Null(sample);
    }

    // ==================== THS：航向独占 ====================

    [Fact]
    public void THS_有效时应成为航向来源()
    {
        var sample = Feed(NewAggregator(), "GPTHS,100.5,A", TriggerGga);

        Assert.Equal(100.5, sample.TrueHeadingDeg!.Value, 6);
        Assert.Equal("THS", sample.HeadingSource);
    }

    [Fact]
    public void THS_单独送入不应触发定位结果()
    {
        var aggregator = NewAggregator();
        var protocol = NewProtocol();

        Assert.True(protocol.TryParse(FrameBuilder.NmeaLine("GPTHS,100.5,A"), out var ths));
        Assert.False(aggregator.Apply(ths, out var sample));
        Assert.Null(sample);
    }

    [Fact]
    public void THS_一旦有效应独占航向且不被后续RMC覆盖()
    {
        var aggregator = NewAggregator();

        var beforeThs = Feed(aggregator, TriggerGga);
        Assert.Null(beforeThs.TrueHeadingDeg); // GGA 不携带航向

        var afterThs = Feed(aggregator, "GPTHS,100.5,A", TriggerGga);
        Assert.Equal(100.5, afterThs.TrueHeadingDeg!.Value, 6);
        Assert.Equal("THS", afterThs.HeadingSource);

        // 再喂一条带航向的 RMC：对外航向仍应保持 THS，但 CourseDeg 自身会更新
        var afterRmc = Feed(aggregator,
            $"GPRMC,123520,A,{BeijingLat},N,{BeijingLon},E,22.4,200.0,230394,,W",
            TriggerGga);

        Assert.Equal(100.5, afterRmc.TrueHeadingDeg!.Value, 6);
        Assert.Equal("THS", afterRmc.HeadingSource);
        Assert.Equal(200.0, afterRmc.Fix.CourseDeg!.Value, 6);
    }

    [Fact]
    public void THS_一旦有效应压过后来的VTG()
    {
        var aggregator = NewAggregator();

        var sample = Feed(aggregator,
            "GPTHS,100.5,A",
            "GPVTG,20.0,T,18.0,M,19.4384,N,36.0,K,A",
            TriggerGga);

        Assert.Equal(100.5, sample.TrueHeadingDeg!.Value, 6);
        Assert.Equal("THS", sample.HeadingSource);
        Assert.Equal(20.0, sample.Fix.CourseDeg!.Value, 6);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("E")]
    [InlineData("M")]
    [InlineData("S")]
    [InlineData("a")]
    public void THS_合法模式位应被接受(string mode)
    {
        var sample = Feed(NewAggregator(), $"GPTHS,45.5,{mode}", TriggerGga);

        Assert.Equal(45.5, sample.TrueHeadingDeg!.Value, 6);
        Assert.Equal("THS", sample.HeadingSource);
    }

    [Theory]
    [InlineData("V")]
    [InlineData("X")]
    [InlineData("")]
    public void THS_非法或空模式位应被忽略(string mode)
    {
        var sample = Feed(NewAggregator(), $"GPTHS,45.5,{mode}", TriggerGga);

        Assert.Null(sample.TrueHeadingDeg);
        Assert.Null(sample.HeadingSource);
    }

    [Fact]
    public void THS_航向为负时应归一化到0到360()
    {
        var sample = Feed(NewAggregator(), "GPTHS,-30.0,A", TriggerGga);

        Assert.Equal(330.0, sample.TrueHeadingDeg!.Value, 6);
    }

    [Fact]
    public void THS_航向字段为空时应被忽略()
    {
        var sample = Feed(NewAggregator(), "GPTHS,,A", TriggerGga);

        Assert.Null(sample.TrueHeadingDeg);
        Assert.Null(sample.HeadingSource);
    }

    [Fact]
    public void THS_关闭解析开关时应被忽略()
    {
        var settings = new Um982ProtocolSettings { ParseThs = false };
        var aggregator = NewAggregator(settings);
        var protocol = NewProtocol(settings);

        protocol.TryParse(FrameBuilder.NmeaLine("GPTHS,100.5,A"), out var ths);
        Assert.False(aggregator.Apply(ths, out _)); // IsRelevant 为 false → 直接忽略

        protocol.TryParse(FrameBuilder.NmeaLine(TriggerGga), out var gga);
        Assert.True(aggregator.Apply(gga, out var sample));
        Assert.Null(sample!.TrueHeadingDeg);
    }

    // ==================== 汇聚触发策略 ====================

    [Fact]
    public void 汇聚_收到GGA后RMC不再重复触发()
    {
        var aggregator = NewAggregator();
        var protocol = NewProtocol();

        protocol.TryParse(FrameBuilder.NmeaLine(TriggerGga), out var gga);
        Assert.True(aggregator.Apply(gga, out _));

        protocol.TryParse(FrameBuilder.NmeaLine($"GPRMC,123519,A,{BeijingLat},N,{BeijingLon},E,22.4,84.4,230394,,W"), out var rmc);
        Assert.False(aggregator.Apply(rmc, out var sample));
        Assert.Null(sample);
    }

    [Fact]
    public void 汇聚_未收到GGA时由RMC承担触发()
    {
        var aggregator = NewAggregator();
        var protocol = NewProtocol();

        protocol.TryParse(FrameBuilder.NmeaLine($"GPRMC,123519,A,{BeijingLat},N,{BeijingLon},E,22.4,84.4,230394,,W"), out var rmc);

        Assert.True(aggregator.Apply(rmc, out var sample));
        Assert.Equal(1, sample!.Sequence);
        Assert.Equal(39.9041984, sample.Fix.Latitude, 6);
    }

    [Fact]
    public void 汇聚_关闭GGA解析后由RMC承担触发()
    {
        var settings = new Um982ProtocolSettings { ParseGga = false };
        var aggregator = NewAggregator(settings);
        var protocol = NewProtocol(settings);

        protocol.TryParse(FrameBuilder.NmeaLine(TriggerGga), out var gga);
        Assert.False(aggregator.Apply(gga, out _)); // GGA 已关闭，不解析也不触发

        protocol.TryParse(FrameBuilder.NmeaLine($"GPRMC,123519,A,{BeijingLat},N,{BeijingLon},E,22.4,84.4,230394,,W"), out var rmc);
        Assert.True(aggregator.Apply(rmc, out var sample));
        Assert.Equal(84.4, sample!.TrueHeadingDeg!.Value, 6);
    }

    [Fact]
    public void 汇聚_不关心的语句类型应被忽略()
    {
        var aggregator = NewAggregator();
        var protocol = NewProtocol();

        protocol.TryParse(FrameBuilder.NmeaLine("GPGSA,A,3,04,05,,09,12,,,24,,,,,2.5,1.3,2.1"), out var gsa);

        Assert.False(aggregator.Apply(gsa, out var sample));
        Assert.Null(sample);
    }

    [Fact]
    public void 汇聚_RMC的空字段不应清空GGA已解析的值()
    {
        var aggregator = NewAggregator();

        var ggaSample = Apply(aggregator, $"GPGGA,123519,{BeijingLat},N,{BeijingLon},E,4,12,0.7,45.6,M,46.9,M,,");

        // RMC 的经纬度、速度、磁偏角字段全空：不能把 GGA 的定位抹掉
        var after = Feed(aggregator,
            "GPRMC,123519,A,,,,,0.0,,230394,,",
            $"GPGGA,123520,{BeijingLat},N,{BeijingLon},E,4,12,0.7,45.7,M,46.9,M,,");

        Assert.Equal(39.9041984, ggaSample.Fix.Latitude, 6);
        Assert.Equal(39.9041984, after.Fix.Latitude, 6);
        Assert.Equal(116.4073984, after.Fix.Longitude, 6);
        Assert.Equal(45.7, after.Fix.AltitudeM!.Value, 6);
        Assert.Equal(12, after.Fix.Satellites);
        Assert.Equal(0.0, after.Fix.SpeedMps!.Value, 6); // RMC 给了 0 节，覆盖为 0
    }

    [Fact]
    public void 汇聚_重置后序号与状态应清空()
    {
        var aggregator = NewAggregator();
        Apply(aggregator, $"GPRMC,123519,A,{BeijingLat},N,{BeijingLon},E,22.4,84.4,230394,,W");
        Assert.NotNull(aggregator.LastUtcTime);

        aggregator.Reset();

        Assert.Null(aggregator.LastUtcTime);
        var sample = Apply(aggregator, TriggerGga);
        Assert.Equal(1, sample.Sequence);
    }

    [Fact]
    public void 汇聚_样本应带上触发本结果的原始语句()
    {
        var sample = Apply(NewAggregator(), "GPGGA,105459,3954.251904,N,11624.443904,E,1,08,0.9,1.0,M,0.0,M,,");

        var raw = Assert.Single(sample.Sentences);
        Assert.StartsWith("$GPGGA,105459,", raw);
    }

    // ==================== IsRelevant ====================

    [Theory]
    [InlineData("GGA", true)]
    [InlineData("RMC", true)]
    [InlineData("VTG", true)]
    [InlineData("THS", true)]
    [InlineData("GSA", false)]
    [InlineData("GSV", false)]
    [InlineData("ZDA", false)]
    public void IsRelevant_默认设置下仅关心四类语句(string type, bool expected)
    {
        Assert.Equal(expected, NewProtocol().IsRelevant(type));
    }

    [Fact]
    public void IsRelevant_应随设置项变化()
    {
        var protocol = NewProtocol(new Um982ProtocolSettings
        {
            ParseGga = false,
            ParseRmc = false,
            ParseVtg = false,
            ParseThs = false,
        });

        Assert.False(protocol.IsRelevant("GGA"));
        Assert.False(protocol.IsRelevant("RMC"));
        Assert.False(protocol.IsRelevant("VTG"));
        Assert.False(protocol.IsRelevant("THS"));
    }
}
