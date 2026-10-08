using UavPlatform.Core.Relative;
using UavPlatform.Core.Models;
using UavPlatform.Core.Protocols;

namespace UavPlatform.Tests;

/// <summary>
/// 端到端集成测试共用的「真实规格字节场景」。
///
/// <para>
/// 与 <see cref="FrameBuilder"/> 的分工：FrameBuilder 只负责按协议逐字节拼装；
/// 本类在其之上固化**一套三设备共用、期望值全部可手算**的场景，
/// 让「真实字节 → 协议解析 → 相对位置 → 落盘 → TCP 回环」共用同一批数据，
/// 这样任何一环出问题都能用同一组手算数值定位。
/// </para>
///
/// <para><b>场景数值（手算期望值的输入）</b></para>
/// <list type="bullet">
/// <item>基座 UM982：30.000000°N / 120.000000°E / 海拔 50 m，GGA 质量 1、卫星 8、HDOP 0.9；RMC 真航向 84.4°。</item>
/// <item>无人机 UCM221：30.000000°N / 120.001000°E / 海拔 120 m（与基座同纬度，纯东向 0.001°，高 70 m）。</item>
/// <item>雷达 NSR：与基座共址（Yaw=Pitch=Roll=0），两个目标只用「量程/方位角/俯仰角」描述，
/// 本体 XYZ 全写 0 —— 刻意走 RelativeService 里「XYZ 全 0 时按极坐标回填」的那条支路。</item>
/// </list>
/// </summary>
internal static class EndToEndFixtures
{
    // ==================================================================
    // 场景常量
    // ==================================================================

    public const double BaseLatitudeDeg = 30.0;
    public const double BaseLongitudeDeg = 120.0;
    public const double BaseAltitudeM = 50.0;

    public const double DroneLatitudeDeg = 30.0;
    public const double DroneLongitudeDeg = 120.001;
    public const double DroneAltitudeM = 120.0;

    /// <summary>PacketStatus 的 bit2：无人机信息模块存在（= Ucm221Protocol 的 StatusUavInfoPresent）。</summary>
    public const byte StatusUavInfoPresent = 0x04;

    // ---- 雷达目标 1：方位 90°（正东）、俯仰 0°、量程 100 m ----
    public const uint Target1Id = 42;
    public const int Target1Type = 2;                 // RadarTargetTypes.Vehicle → "车"
    public const double Target1RangeM = 100.0;
    public const double Target1AzimuthDeg = 90.0;
    public const double Target1ElevationDeg = 0.0;
    public const double Target1Snr = 21.5;
    public const double Target1PeakEnergyDb = 33.5;
    public const int Target1AreaMask = 0x0005;

    // ---- 雷达目标 2：方位 0°（正北）、俯仰 30°、量程 200 m ----
    public const uint Target2Id = 7;
    public const int Target2Type = 1;                 // RadarTargetTypes.Person → "人"
    public const double Target2RangeM = 200.0;
    public const double Target2AzimuthDeg = 0.0;
    public const double Target2ElevationDeg = 30.0;
    public const double Target2Snr = 15.25;
    public const double Target2PeakEnergyDb = 27.75;
    public const int Target2AreaMask = 0x0001;

    // ---- 基座 NMEA（真航向来自 RMC：UtcCourseDeg 084.4） ----
    public const double BaseTrueHeadingDeg = 84.4;
    public const int BaseFixQuality = 1;
    public const int BaseSatellites = 8;
    public const double BaseHdop = 0.9;

    // ==================================================================
    // 真实规格字节
    // ==================================================================

    /// <summary>
    /// NSR 0xA8 目标数据帧（TCP，小端）。
    /// <para>布局：A5 5A | 源 10 | 目的 01 | A8 | 长度(2,LE) | 目标数(1) | N × 68 | 校验和</para>
    /// <para>长度字段 = 1 + 68×2 = 137；整帧 = 137 + 8 = 145 字节。</para>
    /// </summary>
    public static byte[] RadarFrame { get; } = BuildRadarFrame();

    /// <summary>
    /// UCM221 一帧完整的上传记录（TCP，小端）：汇总模块 20 B + 无人机信息模块 35 B。
    /// <para>整帧：A5 5A | 长度(2,LE)=50 | 汇总 | 无人机信息 | 校验和 = 55 字节。</para>
    /// </summary>
    public static byte[] DroneFrame { get; } = BuildDroneFrame();

    /// <summary>GPRMC：航向 084.4°（真航向）、速度 22.4 kn、日期 230394（1994-03-23）、UTC 08:30:12.00。</summary>
    public static string RmcSentence { get; } =
        FrameBuilder.Nmea("GPRMC,083012.00,A,3000.000,N,12000.000,E,022.4,084.4,230394,003.1,W");

    /// <summary>GPGGA：质量 1、卫星 08、HDOP 0.9、海拔 50.0 m、大地水准面差距 46.9 m。</summary>
    public static string GgaSentence { get; } =
        FrameBuilder.Nmea("GPGGA,083012.00,3000.000,N,12000.000,E,1,08,0.9,50.0,M,46.9,M,,");

    /// <summary>基座串口字节流：RMC 先到、GGA 后到（模拟真实 20 Hz 交织输出）。</summary>
    public static byte[] BaseRaw { get; } = FrameBuilder.Ascii(RmcSentence + GgaSentence);

    private static byte[] BuildRadarFrame()
    {
        // 两个目标的 X/Y/Z 全部写 0，只给 length/azimuth/elevation，
        // 迫使 RelativeService.BuildTargets 走 RadarPolarToCartesian 回填支路。
        var target1 = FrameBuilder.NsrRecord(
            id: Target1Id,
            type: Target1Type,
            xSpeed: 1.5f,
            ySpeed: -2.5f,
            zSpeed: 0.25f,
            x: 0f,
            y: 0f,
            z: 0f,
            length: (float)Target1RangeM,
            azimuth: (float)Target1AzimuthDeg,
            elevation: (float)Target1ElevationDeg,
            snr: (float)Target1Snr,
            peakEnergy: (float)Target1PeakEnergyDb,
            area: (ushort)Target1AreaMask);

        var target2 = FrameBuilder.NsrRecord(
            id: Target2Id,
            type: Target2Type,
            x: 0f,
            y: 0f,
            z: 0f,
            length: (float)Target2RangeM,
            azimuth: (float)Target2AzimuthDeg,
            elevation: (float)Target2ElevationDeg,
            snr: (float)Target2Snr,
            peakEnergy: (float)Target2PeakEnergyDb,
            area: (ushort)Target2AreaMask);

        return FrameBuilder.Nsr(0xA8, FrameBuilder.Concat([0x02], target1, target2));
    }

    private static byte[] BuildDroneFrame()
    {
        var summary = FrameBuilder.Ucm221Summary(
            packetStatus: StatusUavInfoPresent,
            targetCount: 0,
            pointCount: 0,
            yearOffset: 124,      // 1900 + 124 = 2024
            month: 5,
            day: 17,
            hour: 8,
            minute: 30,
            second: 12,
            millisecond: 250,
            deviceId: 0x2210);

        // 缩放因子（均取自 Ucm221Protocol.ParseUavInfo，手算即照此乘）：
        //   经纬度 ×1e-7、高度/相对高度 ×0.001、速度 ×0.01、姿态角 ×0.0001（弧度）
        var uavInfo = FrameBuilder.Ucm221UavInfo(
            lengthField: 35,
            flags: 0x0003,                                        // bit0 位置有效 + bit1 姿态有效
            latitude: (int)Math.Round(DroneLatitudeDeg * 1e7),    // 300000000 → 30.0
            longitude: (int)Math.Round(DroneLongitudeDeg * 1e7),  // 1200010000 → 120.001
            altitude: (int)Math.Round(DroneAltitudeM * 1000),     // 120000 → 120.0 m
            relativeAltitude: 45600,                              // 45.6 m
            velocityNorth: 325,                                   // 3.25 m/s
            velocityEast: -150,                                   // -1.5 m/s
            velocityDown: 50,                                     // 0.5 m/s
            roll: 500,                                            // 0.05 rad
            pitch: -200,                                          // -0.02 rad
            heading: 12000);                                      // 1.2 rad

        return FrameBuilder.Ucm221Frame(big: false, summary, uavInfo);
    }

    // ==================================================================
    // 解析入口：把上面三份真实字节喂进真实协议解析器
    // ==================================================================

    /// <summary>三台设备各自解析出来的样本，以及对应的原始字节（供落盘/回环测试复用）。</summary>
    internal sealed record Scenario(
        GnssSample Base,
        RadarSample Radar,
        DroneGpsSample Drone,
        byte[] BaseRaw,
        byte[] RadarRaw,
        byte[] DroneRaw);

    /// <summary>
    /// 端到端链路共用的相对位置设置：原点取基座、参考点为自动、雷达与基座共址且安装角全 0。
    /// <para>
    /// 安装角全 0 是刻意的：此时 <c>GeoMath.RotateRadarToEnu</c> 退化为恒等变换，
    /// 目标 ENU = 雷达位置 + 极坐标直角化结果，期望值可以直接手算，不需要再叠一层旋转。
    /// </para>
    /// <para><c>RelativeIntervalMs = 0</c> 关闭相对位置节流，让落盘测试里的每一帧都能写出。</para>
    /// </summary>
    public static RelativeSettings RelativeSettings()
    {
        var settings = new RelativeSettings
        {
            Enabled = true,
            ReferenceMode = ReferencePointMode.Auto,
            RelativeIntervalMs = 0,
            StaleTimeoutMs = 3000,
            FollowReferenceDrift = false,
        };

        settings.Radar.Mode = RadarPlacementMode.CoLocatedWithBase;
        settings.Radar.YawDeg = 0;
        settings.Radar.PitchDeg = 0;
        settings.Radar.RollDeg = 0;

        // 过滤开关保持默认：MaxRangeM = 0 / MinSnr = 0 都表示不限；
        // DropDeleted = true 只会滤掉 0xFFFF，本场景两个目标都不是。
        return settings;
    }

    /// <summary>基座 NMEA 设置：全部语句都解析、校验和要求。</summary>
    public static Um982ProtocolSettings BaseNmeaSettings() => new()
    {
        ParseGga = true,
        ParseRmc = true,
        ParseVtg = true,
        ParseThs = true,
        RmcCourseAsHeading = true,
        RequireChecksum = true,
        SendInitCommands = false,   // 测试里不向对端写初始化命令
    };

    /// <summary>
    /// 用真实协议解析器把三份真实字节**逐字节喂入**并解析成样本。
    /// 基座走 NmeaProtocol + NmeaAggregator（与 DeviceConnection.ProcessNmeaLine 同一条路径）。
    /// </summary>
    public static Scenario ParseAll()
    {
        // ---- 基座：NMEA 串口流 ----
        var nmeaSettings = BaseNmeaSettings();
        var nmea = new NmeaProtocol(nmeaSettings);
        var aggregator = new NmeaAggregator(nmeaSettings);
        GnssSample? baseSample = null;
        foreach (var frame in ScanPump.Drain(nmea, BaseRaw, chunkSize: 1))
        {
            if (!nmea.TryParse(frame, out var sentence)) continue;
            if (!nmea.IsRelevant(sentence.Type)) continue;
            if (aggregator.Apply(sentence, out var produced) && produced is not null)
            {
                baseSample = produced;
            }
        }

        Assert.NotNull(baseSample);

        // ---- 雷达：NSR TCP 流 ----
        var radarProtocol = new NsrRadarProtocol(new RadarProtocolSettings());
        var radarFrames = ScanPump.Drain(radarProtocol, RadarFrame, chunkSize: 1);
        Assert.Single(radarFrames);
        var radarSample = radarProtocol.Parse(radarFrames[0], DeviceKind.Radar, "雷达(NSR)", 1).ToSample();

        // ---- 无人机：UCM221 TCP 流 ----
        var ucmProtocol = new Ucm221Protocol(new Ucm221ProtocolSettings());
        var droneFrames = ScanPump.Drain(ucmProtocol, DroneFrame, chunkSize: 1);
        Assert.Single(droneFrames);
        var droneSample = ucmProtocol.Parse(droneFrames[0], 2, "无人机 GPS(UCM221)").ToSample();

        // NmeaAggregator 不负责填 DeviceName，这里补上以贴近真实运行时的样本。
        return new Scenario(
            baseSample! with { DeviceName = "基座(UM982)" },
            radarSample,
            droneSample,
            BaseRaw,
            RadarFrame,
            DroneFrame);
    }

    // ==================================================================
    // 分片喂入辅助（模拟 TCP 分包 / 串口分片 / 半帧到达）
    // ==================================================================

    /// <summary>
    /// 按**不定长分片**把数据喂进协议解析器，跨多次调用保持同一个累积缓冲区。
    /// 语义与 <c>DeviceConnection.DrainQueue</c> 一致：NeedMore 等待下一块、Skip 丢弃、Frame 取出。
    /// 与 <see cref="ScanPump.Drain"/> 的区别是分片边界由调用方显式给定，
    /// 用来验证「半帧先到、稍后补全」这种 ScanPump 的固定 chunkSize 表达不了的情况。
    /// </summary>
    public static List<byte[]> FeedInChunks(DeviceProtocol protocol, params byte[][] chunks)
    {
        var queue = new ByteQueue();
        var frames = new List<byte[]>();

        foreach (var chunk in chunks)
        {
            queue.Append(chunk);
            while (queue.Count > 0)
            {
                var scan = protocol.Scan(queue.Span);
                if (scan.Status == FrameScanStatus.NeedMore) break;

                if (scan.Status == FrameScanStatus.Skip)
                {
                    // 防御：Skip(0) 会死循环，至少消费 1 字节。
                    queue.Consume(Math.Clamp(scan.Length, 1, queue.Count));
                    continue;
                }

                frames.Add(queue.Take(scan.Length));
            }
        }

        return frames;
    }

    /// <summary>把一段字节按给定长度切成多块（最后一块可能更短）。</summary>
    public static byte[][] Split(byte[] data, params int[] lengths)
    {
        var chunks = new List<byte[]>();
        var offset = 0;
        foreach (var length in lengths)
        {
            var take = Math.Min(length, data.Length - offset);
            if (take <= 0) break;
            chunks.Add(data.AsSpan(offset, take).ToArray());
            offset += take;
        }

        if (offset < data.Length)
        {
            chunks.Add(data.AsSpan(offset).ToArray());
        }

        return chunks.ToArray();
    }
}
