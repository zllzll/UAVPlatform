using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using UavPlatform.Core.Devices;
using UavPlatform.Core.Relative;
using UavPlatform.Core.Models;
using UavPlatform.Core.Protocols;
using UavPlatform.Core.Storage;

namespace UavPlatform.Tests;

// ==========================================================================================
// 端到端集成测试：真实字节 → 解析 → 相对位置 → 落盘 → TCP 回环
//
// 本文件坚持「先把真实规格字节喂进真实解析器」：设备字节 → 真实分帧/解析器 → 相对位置 → 落盘，
// 因此覆盖的是「没接设备时也能证明整条链路正确」这一段。
// （曾经的内置仿真源已删除：它走的是「直接注入已解析对象」的旁路，绕过了全部协议解析器。）
//
// 期望值来源：全部由协议规格 / WGS84 闭式解手算得到，推导写在每个断言的注释里。
// 手算所用的四舍五入值已在注释中给出，容差理由一并写明。
// ==========================================================================================

/// <summary>
/// A（分帧 / 解析）、B（相对位置）、C（落盘）三段端到端链路。
/// </summary>
public class EndToEndPipelineTests
{
    // 手算常量（推导见各处注释）：
    //   WGS84: a = 6378137, f = 1/298.257223563, e² = f(2-f) = 0.00669437999014132
    //   φ = 30° 处卯酉圈曲率半径 N = a / sqrt(1 - e²·sin²φ) = 6383480.91769011
    //   基座 (30°N, 120°E, 50 m)、无人机 (30°N, 120.001°E, 120 m)，Δλ = 0.001°、Δh = 70 m
    private const double ExpectedDroneEastM = 96.4870359966515;
    private const double ExpectedDroneUpM = 69.9992707994269;
    private const double ExpectedDroneToBaseHorizM = 96.4870359966515;
    private const double ExpectedDroneToRadarM = 119.204219840871;

    /// <summary>容差：手算与 GeoMath 的 ECEF→ENU 数学上等价，0.5 m 只是工程裕度。</summary>
    private const double GeometryToleranceM = 0.5;

    // ======================================================================================
    // A. 真实字节 → 解析（逐字节喂入，验证跨 TCP 包 / 串口分片的流式分帧）
    // ======================================================================================

    [Fact]
    public void 端到端A1_NSR雷达真实字节逐字节喂入应切出目标帧并解析出具体数值()
    {
        var protocol = new NsrRadarProtocol(new RadarProtocolSettings());
        var raw = EndToEndFixtures.RadarFrame;

        // 帧长手算：A5 5A | 源 | 目的 | A8 | 长度(2,LE) | 参数 | 校验和
        //   参数 = 目标数(1) + 2 × 68 = 137 → 整帧 = 137 + 8 = 145 字节
        Assert.Equal(145, raw.Length);

        // chunkSize: 1 = 逐字节喂入。若 Scan 的分帧状态机有任何跨块假设，这里必然暴露。
        var frames = ScanPump.Drain(protocol, raw, chunkSize: 1);

        var frame = Assert.Single(frames);
        Assert.Equal(raw.Length, frame.Length);
        Assert.Equal(raw, frame);   // 取出的帧必须与发送端逐字节一致

        var result = protocol.Parse(frame, DeviceKind.Radar, "雷达(NSR)", 1);

        Assert.Equal(NsrRadarProtocol.CmdTargets, result.Command);   // 0xA8
        Assert.Equal(0x10, result.SourceAddress);                    // FrameBuilder.Nsr 默认源地址
        Assert.False(result.IsPointCloud);
        Assert.Equal(2, result.DeclaredCount);
        Assert.Equal(2, result.Targets.Length);

        // ---- 目标 1（量程 100 m / 方位 90° / 俯仰 0°）----
        var t1 = result.Targets[0];
        Assert.Equal(EndToEndFixtures.Target1Id, t1.Id);
        Assert.Equal(EndToEndFixtures.Target1Type, t1.Type);
        Assert.Equal("车", t1.TypeName);                              // 类型 2 → RadarTargetTypes.Vehicle
        Assert.Equal(100.0, t1.Range, 1e-4);                          // float 100f，容差仅吸收 float↔double
        Assert.Equal(90.0, t1.AzimuthDeg, 1e-4);
        Assert.Equal(0.0, t1.ElevationDeg, 1e-4);
        Assert.Equal(21.5, t1.Snr, 1e-4);
        Assert.Equal(33.5, t1.PeakEnergyDb, 1e-4);
        Assert.Equal(EndToEndFixtures.Target1AreaMask, t1.AreaMask);
        // 速度模长手算：sqrt(1.5² + (-2.5)² + 0.25²) = sqrt(2.25 + 6.25 + 0.0625) = sqrt(8.5625) = 2.926175
        Assert.Equal(2.926175, t1.SpeedMps, 1e-6);

        // ---- 目标 2（量程 200 m / 方位 0° / 俯仰 30°）----
        var t2 = result.Targets[1];
        Assert.Equal(EndToEndFixtures.Target2Id, t2.Id);
        Assert.Equal("人", t2.TypeName);                              // 类型 1 → Person
        Assert.Equal(200.0, t2.Range, 1e-4);
        Assert.Equal(0.0, t2.AzimuthDeg, 1e-4);
        Assert.Equal(30.0, t2.ElevationDeg, 1e-4);
        Assert.Equal(15.25, t2.Snr, 1e-4);

        // 本帧刻意把目标的 X/Y/Z 三个直角坐标写成 0，只用「量程 / 方位角 / 俯仰角」描述，
        // 因此解析器如实回报 0：极坐标 → 直角的回填是 RelativeService 的职责，
        // 对应的手算断言放在 B 段第二个用例（目标 ENU 的 east/north/up）。
        Assert.Equal(0.0, t2.X, 1e-4);
        Assert.Equal(0.0, t2.Y, 1e-4);
        Assert.Equal(0.0, t2.Z, 1e-4);

        // ---- 转样本 ----
        var sample = result.ToSample();
        Assert.Equal(DeviceKind.Radar, sample.Device);
        Assert.Equal(2, sample.Targets.Length);
        Assert.Equal((byte)0xA8, sample.Command);
    }

    [Fact]
    public void 端到端A2_UCM221真实字节逐字节喂入应解析出无人机位置与姿态()
    {
        var protocol = new Ucm221Protocol(new Ucm221ProtocolSettings());
        var raw = EndToEndFixtures.DroneFrame;

        // 帧长手算：A5 5A | 长度(2,LE) | 负载 | 校验和，整帧 = 负载长 + 5
        //   负载 = 汇总 20 + 无人机信息 35 = 55（目标记录数 / 点云数都是 0，不占负载）
        //   → 长度字段 = 55 = 0x0037（小端 37 00），整帧 = 55 + 5 = 60 字节
        Assert.Equal(60, raw.Length);
        Assert.Equal(0xA5, raw[0]);
        Assert.Equal(0x5A, raw[1]);
        Assert.Equal(0x37, raw[2]);
        Assert.Equal(0x00, raw[3]);
        Assert.Equal(0xA7, raw[4]);   // 汇总模块起始码
        Assert.Equal(0x7A, raw[5]);
        Assert.Equal(20, raw[6]);     // 汇总模块长度字段
        Assert.Equal(EndToEndFixtures.StatusUavInfoPresent, raw[7]);

        var frames = ScanPump.Drain(protocol, raw, chunkSize: 1);
        var frame = Assert.Single(frames);
        Assert.Equal(raw.Length, frame.Length);

        var result = protocol.Parse(frame, 1, "无人机 GPS(UCM221)");

        Assert.True(result.SummaryHeaderValid);
        Assert.Equal(EndToEndFixtures.StatusUavInfoPresent, result.PacketStatus);   // 0x04
        Assert.True(result.UavInfoOffset >= 20, "无人机信息模块应排在 20 字节的汇总之后");

        // DeviceTime 手算：1900 + 124 = 2024，格式 yyyy-MM-ddTHH:mm:ss.fff
        Assert.Equal("2024-05-17T08:30:12.250", result.DeviceTime);

        var info = Assert.IsType<Ucm221UavInfo>(result.UavInfo);
        Assert.True(info.PositionValid);      // flags bit0
        Assert.True(info.AttitudeValid);      // flags bit1

        // 缩放因子取自协议：经纬度 ×1e-7、高度/相对高度 ×0.001、速度 ×0.01、姿态角 ×0.0001
        Assert.Equal(30.0, info.Latitude!.Value, 1e-9);          // 300000000 × 1e-7
        Assert.Equal(120.001, info.Longitude!.Value, 1e-9);      // 1200010000 × 1e-7
        Assert.Equal(120.0, info.AltitudeM!.Value, 1e-9);        // 120000 × 0.001
        Assert.Equal(45.6, info.RelativeAltitudeM!.Value, 1e-9); // 45600 × 0.001
        Assert.Equal(3.25, info.VelocityNorthMps!.Value, 1e-9);  // 325 × 0.01
        Assert.Equal(-1.5, info.VelocityEastMps!.Value, 1e-9);   // -150 × 0.01
        Assert.Equal(0.5, info.VelocityDownMps!.Value, 1e-9);    // 50 × 0.01
        Assert.Equal(0.05, info.RollRad!.Value, 1e-9);           // 500 × 0.0001
        Assert.Equal(-0.02, info.PitchRad!.Value, 1e-9);         // -200 × 0.0001
        Assert.Equal(1.2, info.HeadingRad!.Value, 1e-9);         // 12000 × 0.0001

        var sample = result.ToSample();
        Assert.Equal(DeviceKind.DroneGps, sample.Device);
        Assert.NotNull(sample.Fix);
        Assert.True(sample.Fix!.Valid);
        Assert.Equal(30.0, sample.Fix.Latitude, 1e-9);
        Assert.Equal(120.001, sample.Fix.Longitude, 1e-9);
        Assert.Equal(120.0, sample.Fix.AltitudeM!.Value, 1e-9);
        Assert.NotNull(sample.Attitude);
        Assert.Equal(1.2, sample.Attitude!.HeadingRad!.Value, 1e-9);
        Assert.Equal(45.6, sample.Attitude.RelativeAltitudeM!.Value, 1e-9);
    }

    [Fact]
    public void 端到端A3_基座NMEA两句被拆成多块到达时仍应解析出定位与航向()
    {
        var settings = EndToEndFixtures.BaseNmeaSettings();
        var protocol = new NmeaProtocol(settings);
        var aggregator = new NmeaAggregator(settings);

        // 真实串口不会按句子边界给字节：这里把 RMC + GGA 整体拆成 4 个不等长分片。
        var raw = EndToEndFixtures.BaseRaw;
        var firstCut = raw.Length / 3;
        var secondCut = raw.Length * 2 / 3;
        var chunks = EndToEndFixtures.Split(raw, firstCut, secondCut - firstCut, 7);

        var lines = EndToEndFixtures.FeedInChunks(protocol, chunks);

        // 两个分片边界都可能落在句子中间，因此切出来的必须是恰好 2 句完整 NMEA
        Assert.Equal(2, lines.Count);

        var sentences = new List<NmeaSentence>();
        foreach (var line in lines)
        {
            Assert.True(protocol.TryParse(line, out var sentence), "切出的行应能通过校验和解析");
            sentences.Add(sentence);
        }

        Assert.Equal("RMC", sentences[0].Type);   // FrameBuilder 里 RMC 在前
        Assert.Equal("GGA", sentences[1].Type);

        // GGA 提供定位质量/卫星数，RMC 提供真航向；聚合器在两句都到齐后才产出样本。
        GnssSample? sample = null;
        foreach (var sentence in sentences)
        {
            if (!protocol.IsRelevant(sentence.Type)) continue;
            if (aggregator.Apply(sentence, out var produced) && produced is not null)
            {
                sample = produced;
            }
        }

        Assert.NotNull(sample);
        var fix = sample!.Fix;

        // 纬度 3000.000,N → 30 + 0.000/60 = 30.0；经度 12000.000,E → 120 + 0.000/60 = 120.0
        Assert.Equal(30.0, fix.Latitude, 1e-9);
        Assert.Equal(120.0, fix.Longitude, 1e-9);
        Assert.Equal(50.0, fix.AltitudeM!.Value, 1e-9);     // GGA 海拔 50.0,M
        Assert.Equal(1, fix.FixQuality);                    // GGA 质量指示 1 = 单点定位
        Assert.Equal(8, fix.Satellites);                    // GGA 卫星数 08
        Assert.Equal(0.9, fix.Hdop!.Value, 1e-9);           // GGA HDOP 0.9
        Assert.Equal(46.9, fix.GeoidSeparationM!.Value, 1e-9);

        // 航向来自 RMC 的 084.4（真航向）
        Assert.Equal(EndToEndFixtures.BaseTrueHeadingDeg, sample.TrueHeadingDeg!.Value, 1e-9);
        Assert.Equal("RMC", sample.HeadingSource);
        Assert.True(fix.Valid);
    }

    [Fact]
    public void 端到端A4_半帧到达后续补齐应切出一帧()
    {
        var protocol = new NsrRadarProtocol(new RadarProtocolSettings());
        var raw = EndToEndFixtures.RadarFrame;

        // 30 / 110 / 5 三段：第一段连帧头都没到齐，第二段把参数区截断，第三段才补齐尾部。
        var chunks = EndToEndFixtures.Split(raw, 30, 110, 5);
        Assert.Equal(3, chunks.Length);
        Assert.True(chunks[0].Length < raw.Length);

        var frames = EndToEndFixtures.FeedInChunks(protocol, chunks);

        var frame = Assert.Single(frames);
        Assert.Equal(raw.Length, frame.Length);
        Assert.Equal(raw, frame);
    }

    [Fact]
    public void 端到端A4_粘包时两帧一次到达应各切出一帧()
    {
        var protocol = new NsrRadarProtocol(new RadarProtocolSettings());
        var raw = EndToEndFixtures.RadarFrame;

        // 同一个 TCP 段里塞两个完整帧 + 第 3 帧的半截
        var payload = FrameBuilder.Concat(raw, raw, raw.AsSpan(0, 40).ToArray());
        var frames = EndToEndFixtures.FeedInChunks(protocol, payload);

        Assert.Equal(2, frames.Count);
        Assert.Equal(raw, frames[0]);
        Assert.Equal(raw, frames[1]);
    }

    [Fact]
    public void 端到端A4_噪声字节前缀应被跳过且不影响后续分帧()
    {
        var protocol = new NsrRadarProtocol(new RadarProtocolSettings());
        var raw = EndToEndFixtures.RadarFrame;

        // 前置 3 个不可能出现在 NSR 起始位置的字节（0x11 不是 0xA5）
        var noise = new byte[] { 0x11, 0x22, 0x33 };
        var frames = EndToEndFixtures.FeedInChunks(protocol, FrameBuilder.Concat(noise, raw));

        var frame = Assert.Single(frames);
        Assert.Equal(raw, frame);
    }

    // ======================================================================================
    // B. 解析结果 → 真实 RelativeService
    // ======================================================================================

    [Fact]
    public void 端到端B_真实解析样本经RelativeService应算出原点与基座_无人机相对关系()
    {
        var scenario = EndToEndFixtures.ParseAll();

        // 先钉住「喂进去的确实是 A 断言过的那批解析结果」，避免相对位置断言掩盖解析回归
        Assert.Equal(2, scenario.Radar.Targets.Length);
        Assert.Equal(30.0, scenario.Base.Fix.Latitude, 1e-9);
        Assert.Equal(120.001, scenario.Drone.Fix!.Longitude, 1e-9);

        var relative = new RelativeService(EndToEndFixtures.RelativeSettings());
        relative.OnGnss(scenario.Base);
        relative.OnDrone(scenario.Drone);
        relative.OnRadar(scenario.Radar);

        // force: true 绕过 RelativeIntervalMs 限频，保证第一次调用必然产帧
        var frame = relative.Build("e2e", force: true);
        Assert.NotNull(frame);
        var f = frame!;

        // ---- 原点 / 基准 ----
        Assert.True(f.ReferenceResolved);
        Assert.Equal("基座", f.OriginName);
        Assert.Equal("基座", f.Reference.Source);
        Assert.Equal(EndToEndFixtures.BaseLatitudeDeg, f.Reference.Latitude, 1e-9);
        Assert.Equal(EndToEndFixtures.BaseLongitudeDeg, f.Reference.Longitude, 1e-9);
        Assert.Equal(EndToEndFixtures.BaseAltitudeM, f.Reference.AltitudeM, 1e-9);
        Assert.False(f.Reference.IsZero);

        // ---- 基座就是参考点本身 → ENU 恒为 (0,0,0) ----
        Assert.Equal(0.0, f.BaseStation.Position.East, 1e-9);
        Assert.Equal(0.0, f.BaseStation.Position.North, 1e-9);
        Assert.Equal(0.0, f.BaseStation.Position.Up, 1e-9);
        Assert.Equal(1, f.BaseStation.FixQuality);
        Assert.Equal(8, f.BaseStation.Satellites);
        Assert.Equal(EndToEndFixtures.BaseTrueHeadingDeg, f.BaseStation.HeadingDeg!.Value, 1e-9);
        Assert.Equal("航向来自 RMC", f.BaseStation.Note);   // Note 规则：$"航向来自 {headingSource}"

        // ---- 雷达与基座共址 ----
        Assert.Equal("雷达位置：与基座共址", f.Radar.Note);
        Assert.Equal(0.0, f.RadarBaseLineM, 1e-9);
        Assert.Equal(0.0, f.Radar.Position.East, 1e-9);
        Assert.Equal(0.0, f.Radar.Position.North, 1e-9);
        Assert.Equal(0.0, f.Radar.Position.Up, 1e-9);

        // ---- 无人机相对基座 ----
        // 手算（WGS84 闭式解，φ = 30°、Δλ = 0.001°、Δh = 70 m）：
        //   N = a / sqrt(1 - e²·sin²φ) = 6383480.91769011
        //   east  = (N+h)·cosφ·sin(Δλ)                     = 96.4870359966515 m
        //   north = (N+h)·sinφ·cosφ·(1-cos(Δλ))            = 0.000421 m（同纬度，理论上应为 0）
        //   up    = Δh - (N+h)·cos²φ·(1-cos(Δλ))           = 69.9992707994269 m
        // DroneDistanceToBaseM 是水平距离（不含高度），所以期望 96.487 m。
        Assert.Equal(ExpectedDroneToBaseHorizM, f.DroneDistanceToBaseM!.Value, GeometryToleranceM);
        Assert.Equal(ExpectedDroneUpM, f.DroneHeightAboveBaseM!.Value, 0.05);
        Assert.Equal(EndToEndFixtures.DroneAltitudeM, f.Drone.AltitudeM!.Value, 1e-9);

        // 雷达与基座共址 → 无人机到雷达的三维距离 = 无人机到参考点的三维距离
        //   sqrt(96.4870359966515² + 0.000421² + 69.9992707994269²) = 119.204219840871 m
        Assert.Equal(ExpectedDroneToRadarM, f.DroneDistanceToRadarM!.Value, GeometryToleranceM);

        // ---- 无人机姿态：弧度 → 度（UCM221 原始值 ×0.0001 rad）----
        //   1.2 rad      = 68.7549354156988°
        //   0.05 rad     = 2.86478897565412°（→ RollDeg）
        //   -0.02 rad    = -1.14591559026165°（→ PitchDeg）
        Assert.Equal(68.7549354156988, f.Drone.HeadingDeg!.Value, 1e-9);
        Assert.Equal(2.86478897565412, f.Drone.RollDeg!.Value, 1e-9);
        Assert.Equal(-1.14591559026165, f.Drone.PitchDeg!.Value, 1e-9);
    }

    [Fact]
    public void 端到端B_雷达目标应换算到ENU且与量程方位角俯仰角手算值一致()
    {
        var scenario = EndToEndFixtures.ParseAll();

        var relative = new RelativeService(EndToEndFixtures.RelativeSettings());
        relative.OnGnss(scenario.Base);
        relative.OnDrone(scenario.Drone);
        relative.OnRadar(scenario.Radar);

        var frame = relative.Build("e2e-targets", force: true);
        Assert.NotNull(frame);
        var f = frame!;

        // Filter 默认值：MaxRangeM = 0、MinSnr = 0 都表示不限；DropDeleted 只滤 0xFFFF
        Assert.Equal(2, f.Targets.Length);
        Assert.False(f.IsPointCloud);

        // ---- 目标 1 ----
        var t1 = f.Targets.Single(t => t.Id == EndToEndFixtures.Target1Id);
        // 手算：RadarPolarToCartesian(az=90°, el=0°, r=100)
        //   horizontal = 100·cos0° = 100
        //   x = 100·sin90° = 100；y = 100·cos90° = 0；z = 100·sin0° = 0
        // 安装角 yaw=pitch=roll=0 → RotateRadarToEnu 为恒等；雷达与基座共址 → ENU = (0,0,0) + (x,y,z)
        Assert.Equal(100.0, t1.East, 1e-6);
        Assert.Equal(0.0, t1.North, 1e-6);
        Assert.Equal(0.0, t1.Up, 1e-6);
        Assert.Equal(EndToEndFixtures.Target1Type, t1.Type);
        Assert.Equal("车", t1.TypeName);
        Assert.Equal(100.0, t1.RangeM, 1e-4);
        Assert.Equal(90.0, t1.AzimuthDeg, 1e-4);
        Assert.Equal(0.0, t1.ElevationDeg, 1e-4);
        Assert.Equal(21.5, t1.Snr, 1e-4);
        Assert.Equal(33.5, t1.PeakEnergyDb, 1e-4);
        Assert.Equal(EndToEndFixtures.Target1AreaMask, t1.AreaMask);

        // 速度经同一次旋转（yaw/pitch/roll 全 0 → 恒等）
        Assert.Equal(1.5, t1.VelocityEast, 1e-6);
        Assert.Equal(-2.5, t1.VelocityNorth, 1e-6);
        Assert.Equal(0.25, t1.VelocityUp, 1e-6);
        Assert.Equal(2.926175, t1.SpeedMps, 1e-6);   // sqrt(1.5² + 2.5² + 0.25²) = sqrt(8.5625)

        // 目标 1 到无人机的距离：目标在 (100, 0, 0)，无人机在 (96.487, 0.00042, 69.999)
        //   sqrt((100-96.4870359966515)² + 0.000421² + (0-69.9992707994269)²)
        //   = sqrt(3.512964² + 4899.897913) = sqrt(4912.238829) ≈ 70.0874 m
        Assert.Equal(70.0874, t1.DistanceToDroneM!.Value, 0.05);
        // HeightAboveDroneM = 目标的 up − 无人机的 up = 0 − 69.9992707994269
        Assert.Equal(-ExpectedDroneUpM, t1.HeightAboveDroneM!.Value, 0.05);

        // ---- 目标 2 ----
        var t2 = f.Targets.Single(t => t.Id == EndToEndFixtures.Target2Id);
        // 手算：RadarPolarToCartesian(az=0°, el=30°, r=200)
        //   horizontal = 200·cos30° = 173.205080756888
        //   x = horizontal·sin0° = 0；y = horizontal·cos0° = 173.205080756888；z = 200·sin30° = 100
        Assert.Equal(0.0, t2.East, 1e-6);
        Assert.Equal(173.205080756888, t2.North, 1e-6);
        Assert.Equal(100.0, t2.Up, 1e-6);
        Assert.Equal(EndToEndFixtures.Target2Type, t2.Type);
        Assert.Equal("人", t2.TypeName);
        Assert.Equal(200.0, t2.RangeM, 1e-4);
        Assert.Equal(0.0, t2.AzimuthDeg, 1e-4);
        Assert.Equal(30.0, t2.ElevationDeg, 1e-4);
        Assert.Equal(15.25, t2.Snr, 1e-4);
    }

    // ======================================================================================
    // C. 同一条链路的落盘（临时目录，绝不污染仓库 data/）
    // ======================================================================================

    [Fact]
    public void 端到端C_同一批样本应落成分设备原始解析_雷达转基座系_三设备同帧五类非空文件并保留关键字段()
    {
        var root = Path.Combine(Path.GetTempPath(), "UavPlatformTests", "e2e-" + Guid.NewGuid().ToString("N"));
        var config = new StorageConfig
        {
            Enabled = true,
            RootPath = root,
            FolderMode = SessionFolderMode.Fixed,   // 固定会话名，路径可预测
            FixedSessionName = "e2e",
            RawFormat = RawFormat.HexText,          // 十六进制文本，便于直接断言字节
            ParsedFormat = ParsedFormat.JsonLines,
            SaveRadarBase = true,
            SaveFrame = true,
            EmbedRawInParsed = true,                // 解析文件里同时带原始字节
            WriteManifest = true,
        };

        try
        {
            var scenario = EndToEndFixtures.ParseAll();
            var session = Path.Combine(root, "e2e");

            using (var storage = new SessionStorage(config, root))
            {
                Assert.True(storage.Enabled);
                // 目录在 Open 时才分配（默认模式下每次开始采集都是新目录），所以这里断言在 Open 之后
                Assert.Equal(string.Empty, storage.SessionDirectory);

                storage.Open(
                    [
                        DeviceConfig.CreateDefault(DeviceKind.BaseStation),
                        DeviceConfig.CreateDefault(DeviceKind.Radar),
                        DeviceConfig.CreateDefault(DeviceKind.DroneGps),
                    ],
                    EndToEndFixtures.RelativeSettings());

                Assert.Equal(session, storage.SessionDirectory);   // 固定会话名，路径可预测

                var files = storage.CurrentFiles;
                Assert.Equal(5, files.Count);                       // 三台设备 + 雷达转基座系 + 三设备同帧
                Assert.Equal("雷达转基座系", files[3].Device);
                Assert.Equal("三设备同帧", files[4].Device);
                // 两份派生数据只有一个结果文件，按既有约定放在 RawPath 槽、ParsedPath 留空，
                // 界面据此只显示一个路径且不贴「原始 / 解析」标签。
                Assert.False(string.IsNullOrEmpty(files[3].RawPath));
                Assert.Equal(string.Empty, files[3].ParsedPath);
                Assert.False(string.IsNullOrEmpty(files[4].RawPath));
                Assert.Equal(string.Empty, files[4].ParsedPath);

                // 真实接收时刻与真实原始字节
                var stamp = DateTimeOffset.Now;
                storage.WriteRaw(DeviceKind.BaseStation, new RawSegment(stamp, scenario.BaseRaw));
                storage.WriteRaw(DeviceKind.Radar, new RawSegment(stamp, scenario.RadarRaw));
                storage.WriteRaw(DeviceKind.DroneGps, new RawSegment(stamp, scenario.DroneRaw));

                storage.WriteParsed(DeviceKind.BaseStation, scenario.Base, scenario.BaseRaw);
                storage.WriteParsed(DeviceKind.Radar, scenario.Radar, scenario.RadarRaw);
                storage.WriteParsed(DeviceKind.DroneGps, scenario.Drone, scenario.DroneRaw);

                // 相对位置走真实 RelativeService（与 B 段同一条路径）
                var relative = new RelativeService(EndToEndFixtures.RelativeSettings());
                relative.OnGnss(scenario.Base);
                relative.OnDrone(scenario.Drone);
                relative.OnRadar(scenario.Radar);
                var frame = relative.Build("e2e-storage", force: true);
                Assert.NotNull(frame);
                storage.WriteFrame(frame!);

                // 雷达转基座系：按雷达帧单独产出一条（与 DeviceManager.WriteRadarBase 同一条路径）
                var radarBase = relative.ProjectRadarToBase(scenario.Radar, "e2e-storage");
                Assert.NotNull(radarBase);
                storage.WriteRadarBase(radarBase!);

                Assert.Equal(3, storage.RawRecords);
                Assert.Equal(3, storage.ParsedRecords);
                Assert.Equal(1, storage.RadarBaseRecords);
                Assert.Equal(1, storage.FrameRecords);

                // RollingFileWriter 有写缓冲，断言文件内容前必须刷盘
                storage.Complete();
            }

            var baseDir = Path.Combine(session, "01_base_station");
            var radarDir = Path.Combine(session, "02_radar");
            var droneDir = Path.Combine(session, "03_drone_gps");
            var radarBaseDir = Path.Combine(session, "04_radar_base");
            var frameDir = Path.Combine(session, "05_frame");

            var baseRawPath = Path.Combine(baseDir, "raw_0001.txt");
            var radarRawPath = Path.Combine(radarDir, "raw_0001.txt");
            var droneRawPath = Path.Combine(droneDir, "raw_0001.txt");
            var baseParsedPath = Path.Combine(baseDir, "parsed_0001.jsonl");
            var radarParsedPath = Path.Combine(radarDir, "parsed_0001.jsonl");
            var droneParsedPath = Path.Combine(droneDir, "parsed_0001.jsonl");
            var radarBasePath = Path.Combine(radarBaseDir, "radar_base_0001.jsonl");
            var framePath = Path.Combine(frameDir, "frame_0001.jsonl");
            var manifestPath = Path.Combine(session, "session.json");

            foreach (var path in new[]
                     {
                         baseRawPath, radarRawPath, droneRawPath,
                         baseParsedPath, radarParsedPath, droneParsedPath,
                         radarBasePath, framePath, manifestPath,
                     })
            {
                Assert.True(File.Exists(path), $"缺少输出文件：{path}");
                Assert.True(new FileInfo(path).Length > 0, $"输出文件为空：{path}");
            }

            // ---- 原始文件：HexText 逐字节大写十六进制 ----
            Assert.Contains("24 47 50 52 4D 43 ", File.ReadAllText(baseRawPath));   // "$GPRMC"
            Assert.Contains("A5 5A 10 01 A8 ", File.ReadAllText(radarRawPath));     // NSR 帧头 + 0xA8
            // UCM221：帧头 A5 5A + 长度 55(0x0037, LE) + 汇总起始码 A7 7A + 汇总长度 20(0x14) + 状态 0x04
            Assert.Contains("A5 5A 37 00 A7 7A 14 04 ", File.ReadAllText(droneRawPath));

            // ---- 解析文件：JSONL，字段名 CamelCase ----
            var baseJson = ReadFirstJsonLine(baseParsedPath);
            Assert.Equal("BaseStation", baseJson.GetProperty("deviceKind").GetString());
            var baseFix = baseJson.GetProperty("fix");
            Assert.Equal(30.0, baseFix.GetProperty("latitude").GetDouble(), 1e-9);
            Assert.Equal(120.0, baseFix.GetProperty("longitude").GetDouble(), 1e-9);
            Assert.Equal(50.0, baseFix.GetProperty("altitudeM").GetDouble(), 1e-9);
            Assert.Equal(1, baseFix.GetProperty("fixQuality").GetInt32());
            Assert.Equal(8, baseFix.GetProperty("satellites").GetInt32());
            Assert.Equal(84.4, baseJson.GetProperty("trueHeadingDeg").GetDouble(), 1e-9);
            Assert.Equal("RMC", baseJson.GetProperty("headingSource").GetString());

            var radarJson = ReadFirstJsonLine(radarParsedPath);
            Assert.Equal("Radar", radarJson.GetProperty("deviceKind").GetString());
            Assert.Equal(2, radarJson.GetProperty("declaredCount").GetInt32());
            var targetsJson = radarJson.GetProperty("targets");
            Assert.Equal(2, targetsJson.GetArrayLength());
            Assert.Equal(EndToEndFixtures.Target1Id, targetsJson[0].GetProperty("id").GetUInt32());
            Assert.Equal(100.0, targetsJson[0].GetProperty("range").GetDouble(), 1e-4);
            Assert.Equal(90.0, targetsJson[0].GetProperty("azimuthDeg").GetDouble(), 1e-4);
            // TypeName 带 [JsonIgnore]，落盘只保留数值型 type；中文名在 A 段按对象断言
            Assert.Equal(EndToEndFixtures.Target1Type, targetsJson[0].GetProperty("type").GetInt32());
            Assert.Equal(EndToEndFixtures.Target1Snr, targetsJson[0].GetProperty("snr").GetDouble(), 1e-4);
            Assert.Equal(EndToEndFixtures.Target1AreaMask, targetsJson[0].GetProperty("areaMask").GetInt32());
            Assert.Equal(EndToEndFixtures.Target2RangeM, targetsJson[1].GetProperty("range").GetDouble(), 1e-4);

            // EmbedRawInParsed = true → 解析记录里能找回原始字节
            Assert.Equal(EndToEndFixtures.RadarFrame.Length, radarJson.GetProperty("rawLength").GetInt32());
            var rawHex = radarJson.GetProperty("rawHex").GetString()!.Replace(" ", string.Empty);
            Assert.StartsWith("A55A1001A8", rawHex, StringComparison.OrdinalIgnoreCase);

            var droneJson = ReadFirstJsonLine(droneParsedPath);
            Assert.Equal("DroneGps", droneJson.GetProperty("deviceKind").GetString());
            Assert.Equal("2024-05-17T08:30:12.250", droneJson.GetProperty("deviceTime").GetString());
            var droneFix = droneJson.GetProperty("fix");
            Assert.Equal(30.0, droneFix.GetProperty("latitude").GetDouble(), 1e-9);
            Assert.Equal(120.001, droneFix.GetProperty("longitude").GetDouble(), 1e-9);
            Assert.Equal(120.0, droneFix.GetProperty("altitudeM").GetDouble(), 1e-9);
            var droneAttitude = droneJson.GetProperty("attitude");
            Assert.Equal(1.2, droneAttitude.GetProperty("headingRad").GetDouble(), 1e-9);
            Assert.Equal(0.05, droneAttitude.GetProperty("rollRad").GetDouble(), 1e-9);
            Assert.Equal(45.6, droneAttitude.GetProperty("relativeAltitudeM").GetDouble(), 1e-9);

            // ---- 三设备同帧文件：与 B 段同一批手算期望值 ----
            var frameJson = ReadFirstJsonLine(framePath);
            Assert.Equal("基座", frameJson.GetProperty("originName").GetString());
            Assert.True(frameJson.GetProperty("referenceResolved").GetBoolean());
            Assert.Equal(2, frameJson.GetProperty("targets").GetArrayLength());
            Assert.Equal(0.0, frameJson.GetProperty("radarBaseLineM").GetDouble(), 1e-9);
            Assert.Equal(ExpectedDroneToBaseHorizM, frameJson.GetProperty("droneDistanceToBaseM").GetDouble(), GeometryToleranceM);
            Assert.Equal(ExpectedDroneToRadarM, frameJson.GetProperty("droneDistanceToRadarM").GetDouble(), GeometryToleranceM);
            Assert.Equal(ExpectedDroneUpM, frameJson.GetProperty("droneHeightAboveBaseM").GetDouble(), 0.05);

            // 需求④：一帧里三个设备各自的「本机时间」都要能取到，供事后对齐。
            foreach (var nodeName in new[] { "baseStation", "radar", "drone" })
            {
                var node = frameJson.GetProperty(nodeName);
                Assert.True(node.GetProperty("pcTime").ValueKind != JsonValueKind.Null, $"{nodeName} 缺 pcTime");
                Assert.True(node.GetProperty("pcTime").GetDateTimeOffset() > DateTimeOffset.UnixEpoch, $"{nodeName} 的 pcTime 不合理");
                Assert.True(node.GetProperty("sequence").GetInt64() >= 1, $"{nodeName} 的 sequence 应为正数");
            }
            // 无人机样本自带设备时间（UCM221 汇总信息里的时间戳），必须原样出现在帧里。
            Assert.Equal("2024-05-17T08:30:12.250", frameJson.GetProperty("drone").GetProperty("deviceTime").GetString());

            // ---- 雷达转基座系文件：每帧雷达一条，目标是以基座为原点的东北天坐标 ----
            var radarBaseJson = ReadFirstJsonLine(radarBasePath);
            Assert.True(radarBaseJson.GetProperty("baseResolved").GetBoolean());
            Assert.Equal("基座", radarBaseJson.GetProperty("origin").GetProperty("source").GetString());
            Assert.Equal(2, radarBaseJson.GetProperty("targets").GetArrayLength());
            // 雷达与基座共址 → 雷达在基座系里的位置是 0
            Assert.Equal(0.0, radarBaseJson.GetProperty("radarEast").GetDouble(), 1e-9);
            Assert.Equal(0.0, radarBaseJson.GetProperty("radarNorth").GetDouble(), 1e-9);
            Assert.Equal(0.0, radarBaseJson.GetProperty("radarUp").GetDouble(), 1e-9);
            // 目标 1 本体坐标 (100,0,0)：雷达朝向 0 且与基座共址 → 基座系仍是 (100,0,0)
            var radarBaseTargets = radarBaseJson.GetProperty("targets");
            Assert.Equal(100.0, radarBaseTargets[0].GetProperty("east").GetDouble(), GeometryToleranceM);
            Assert.Equal(0.0, radarBaseTargets[0].GetProperty("north").GetDouble(), GeometryToleranceM);
            Assert.Equal(0.0, radarBaseTargets[0].GetProperty("up").GetDouble(), GeometryToleranceM);
            Assert.Equal(EndToEndFixtures.Target2RangeM, radarBaseTargets[1].GetProperty("rangeM").GetDouble(), 1e-4);
            // 需求④：雷达这条派生记录也必须带本机时间与序号（雷达协议不带设备时间，deviceTime 为 null）
            Assert.True(radarBaseJson.GetProperty("pcTime").GetDateTimeOffset() > DateTimeOffset.UnixEpoch);
            Assert.Equal(1, radarBaseJson.GetProperty("sequence").GetInt64());

            // ---- 清单 ----
            var manifest = File.ReadAllText(manifestPath);
            Assert.Contains("uav-platform/session", manifest);
            Assert.Contains("01_base_station", manifest);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不应让测试失败
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>读 JSONL 的第一条非空记录（Clone 出来，避免 JsonDocument 释放后失效）。</summary>
    private static JsonElement ReadFirstJsonLine(string path)
    {
        var line = File.ReadLines(path).First(l => !string.IsNullOrWhiteSpace(l));
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }
}

// ==========================================================================================
// D. 真实 TCP 回环：证明「网络层 + 分帧 + 解析」在没接真雷达时也已被证明可用
// ==========================================================================================

/// <summary>
/// 用 <see cref="TcpListener"/> 在回环地址上扮演雷达，用项目真实的 TCP 传输/解码实现去接。
/// 端口取 0 让系统分配再读回实际端口，避免端口冲突；所有等待都带 5 秒上限，避免测试挂死。
/// </summary>
public class EndToEndTcpLoopbackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task 端到端D_真实TCP回环_DeviceConnection应把分段到达的雷达字节解成样本()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var samples = new List<RadarSample>();
        var raws = new List<byte[]>();
        var gate = new object();

        DeviceConnection? connection = null;
        TcpClient? peer = null;
        try
        {
            var config = DeviceConfig.CreateDefault(DeviceKind.Radar);
            config.Transport = TransportKind.TcpClient;
            config.TransportSettings.Host = "127.0.0.1";
            config.TransportSettings.Port = port;
            config.TransportSettings.ReadTimeoutMs = 200;
            config.AutoReconnect = false;                 // 断线不自动重连，测试自己控制生命周期
            config.Radar.QueryStatusOnConnect = false;    // 不让上位机向我们回写查询命令
            config.Radar.SendHeartbeat = false;

            connection = new DeviceConnection(config);
            connection.SampleReceived += (_, sample, _) =>
            {
                if (sample is RadarSample radar)
                {
                    lock (gate) samples.Add(radar);
                }
            };
            connection.RawReceived += (_, segment) =>
            {
                lock (gate) raws.Add(segment.Data);
            };

            connection.Start();

            using (var acceptCts = new CancellationTokenSource(Timeout))
            {
                peer = await listener.AcceptTcpClientAsync(acceptCts.Token);
            }

            var stream = peer.GetStream();
            var frame = EndToEndFixtures.RadarFrame;

            // 真实节奏：先发半帧，间隔 60 ms 再发剩余（模拟 TCP 分包 / 网卡分片）
            await stream.WriteAsync(frame.AsMemory(0, 71));
            await stream.FlushAsync();
            await Task.Delay(60);
            await stream.WriteAsync(frame.AsMemory(71));
            await stream.FlushAsync();

            var deadline = DateTime.UtcNow.Add(Timeout);
            while (DateTime.UtcNow < deadline)
            {
                lock (gate)
                {
                    if (samples.Count > 0) break;
                }

                await Task.Delay(20);
            }

            var sample = Assert.Single(samples);
            Assert.Equal(2, sample.Targets.Length);
            Assert.Equal(EndToEndFixtures.Target1Id, sample.Targets[0].Id);
            Assert.Equal(EndToEndFixtures.Target2Id, sample.Targets[1].Id);
            Assert.Equal((byte)0xA8, sample.Command);
            Assert.Equal(0x10, sample.SourceAddress);
            Assert.Equal(2, sample.DeclaredCount);

            lock (gate)
            {
                Assert.NotEmpty(raws);   // 原始字节同样被如实上报（供落盘）
            }
        }
        finally
        {
            if (connection is not null) await connection.DisposeAsync();
            peer?.Dispose();
            listener.Stop();
        }
    }

    [Fact]
    public async Task 端到端D_真实TCP回环_TcpClientTransport应原样读到雷达真实字节并解析()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        TcpClient? peer = null;
        try
        {
            var settings = new TransportSettings
            {
                Host = "127.0.0.1",
                Port = port,
                ReadTimeoutMs = 200,
            };

            await using var transport = new TcpClientTransport(settings);

            using var cts = new CancellationTokenSource(Timeout);
            var connecting = transport.ConnectAsync(cts.Token);
            peer = await listener.AcceptTcpClientAsync(cts.Token);
            await connecting;

            Assert.Equal(LinkState.Connected, transport.State);

            var frame = EndToEndFixtures.RadarFrame;
            var stream = peer.GetStream();

            // 按真实节奏分两次发：11 字节（帧头 + 长度字段）→ 其余
            await stream.WriteAsync(frame.AsMemory(0, 11), cts.Token);
            await stream.FlushAsync(cts.Token);
            await Task.Delay(50, cts.Token);
            await stream.WriteAsync(frame.AsMemory(11), cts.Token);
            await stream.FlushAsync(cts.Token);

            var queue = new ByteQueue();
            var protocol = new NsrRadarProtocol(new RadarProtocolSettings());
            var buffer = new byte[512];
            var deadline = DateTime.UtcNow.Add(Timeout);

            while (DateTime.UtcNow < deadline)
            {
                if (protocol.Scan(queue.Span).Status == FrameScanStatus.Frame) break;
                var read = await transport.ReadAsync(buffer, cts.Token);
                if (read > 0) queue.Append(buffer.AsSpan(0, read));
            }

            var scan = protocol.Scan(queue.Span);
            Assert.Equal(FrameScanStatus.Frame, scan.Status);
            Assert.Equal(frame.Length, scan.Length);

            var received = queue.Take(scan.Length);
            Assert.Equal(frame, received);   // 逐字节与发送端一致

            var result = protocol.Parse(received, DeviceKind.Radar, "雷达(NSR)", 1);
            Assert.Equal(2, result.Targets.Length);
            Assert.Equal(EndToEndFixtures.Target1Id, result.Targets[0].Id);
            Assert.Equal(EndToEndFixtures.Target2Id, result.Targets[1].Id);
            Assert.Equal(EndToEndFixtures.Target1RangeM, result.Targets[0].Range, 1e-4);
            Assert.Equal(EndToEndFixtures.Target1AzimuthDeg, result.Targets[0].AzimuthDeg, 1e-4);
            Assert.Equal(EndToEndFixtures.Target2ElevationDeg, result.Targets[1].ElevationDeg, 1e-4);
        }
        finally
        {
            peer?.Dispose();
            listener.Stop();
        }
    }
}
