using UavPlatform.Api.Contracts;
using UavPlatform.Core.Relative;
using UavPlatform.Core.Models;

namespace UavPlatform.Api.Services;

/// <summary>
/// 无硬件时的仿真数据源：按 <see cref="SimulationOptions"/> 生成三台设备的解析样本，
/// 通过 <see cref="Core.Devices.DeviceManager.InjectSample"/> 注入，
/// 从而走通「落盘 → 相对位置 → 三维显示」的完整下游链路（不含协议解析本身）。
///
/// 默认场景（三台设备的关系是刻意摆成这样的，改之前先看清楚）：
///   · 基座双天线基线东西向（<see cref="BaselineHeadingDeg"/> = 90°），雷达正前方朝正北
///     （由配置里的「相对基线夹角 −90°」算出）；
///   · 无人机在 40 m 高空、半径 120 m、周期 60 s 逆时针绕基座一圈，
///     它的 GPS 真值与雷达「看到的它」用的是同一个真值，两条轨迹才可比；
///   · 杂波默认全关（TargetCount / PointCount = 0），所以目标帧里最多只有无人机这一个空中目标；
///   · 无人机绕到雷达后方（本体方位角超出 ±90°）时不再输出该目标，用来复现真实雷达的后半球盲区。
/// </summary>
public sealed class SimulationFeed : IAsyncDisposable
{
    private readonly PlatformService _service;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _task;

    /// <summary>
    /// 仿真里基座双天线基线的航向：正东 90°，即两根天线沿东西方向架设。
    /// 它与「雷达正前方相对基线的夹角」配置（默认 −90°）合起来，使仿真雷达的正前方正好落在正北，
    /// 同时让「基线航向 + 夹角」这条配置链路有非零输入可验。
    /// </summary>
    private const double BaselineHeadingDeg = 90.0;

    /// <summary>
    /// 仿真雷达的方位视场半角：NSR 协议中目标方位角的输出范围就是 −90°～90°，
    /// 也就是只能看到正前方半球。超出该范围时仿真不再输出无人机目标，
    /// 用来复现「无人机飞到雷达后方就丢失」的真实行为。
    /// </summary>
    private const double RadarHalfFovDeg = 90.0;

    public SimulationFeed(PlatformService service, ILogger logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>当前仿真参数。</summary>
    public SimulationOptions Options { get; private set; } = new();

    /// <summary>是否正在仿真。</summary>
    public bool Running
    {
        get { lock (_gate) return _task is { IsCompleted: false }; }
    }

    public Task StartAsync(SimulationOptions options)
    {
        lock (_gate)
        {
            if (_task is { IsCompleted: false }) return Task.CompletedTask;
            Options = options;
            Options.Enabled = true;
            _cts = new CancellationTokenSource();
            _task = Task.Run(() => RunAsync(options, _cts.Token), CancellationToken.None);
        }

        _logger.LogInformation("仿真数据源已启动：{Options}", ConfigJson.Serialize(options));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_gate)
        {
            cts = _cts;
            task = _task;
            _cts = null;
            _task = null;
            Options.Enabled = false;
        }

        if (cts is null) return;
        await cts.CancelAsync();
        if (task is not null)
        {
            try { await task; } catch (OperationCanceledException) { }
        }

        cts.Dispose();
        _logger.LogInformation("仿真数据源已停止。");
    }

    private async Task RunAsync(SimulationOptions options, CancellationToken token)
    {
        var rate = Math.Clamp(options.RateHz, 0.5, 50.0);
        var interval = TimeSpan.FromMilliseconds(1000.0 / rate);

        // 点云帧吞吐远大于目标帧，按 1 Hz 单独节流（RateHz=10 时每 10 拍发一次）。
        var pointCloudEvery = Math.Max(1, (int)Math.Round(rate));

        var start = DateTimeOffset.Now;
        var tick = 0L;
        var sequence = 0L;
        var name = _service.Config.Name;

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var manager = _service.Manager;
                if (!manager.Running) break;

                var now = DateTimeOffset.Now;
                var t = (now - start).TotalSeconds;

                // 无人机本拍的真值位置：雷达要「看到」的就是它，所以两者必须用同一个真值。
                var droneEnu = DroneEnu(options, t);

                // 雷达正前方与相对位置服务用同一套解算（仿真基座基线航向固定为正东、来源恒为 THS），
                // 这样用户在配置里改「相对双天线基线的夹角」后，仿真目标也会跟着一起转。
                var (boresightDeg, _) = _service.Config.Relative.Radar.ResolveYawDeg(BaselineHeadingDeg, "THS");

                // 基座 UM982：1 Hz 一条汇聚样本
                if (tick % (long)Math.Max(1, Math.Round(rate)) == 0)
                {
                    manager.InjectSample(DeviceKind.BaseStation, BuildBaseStation(options, now, ref sequence, name, t));
                }

                // 无人机 wifi 模块：每拍一条
                manager.InjectSample(DeviceKind.DroneGps, BuildDrone(options, now, ref sequence, name, t));

                // 雷达：每拍一帧目标（末尾额外挂一个跟着无人机飞的空中目标，供对比卡片使用）
                manager.InjectSample(DeviceKind.Radar,
                    BuildRadar(options, now, ref sequence, name, t, isPointCloud: false, index: tick,
                        droneEnu: droneEnu, boresightDeg: boresightDeg));

                // 雷达：低频点云帧
                if (options.PointCount > 0 && tick % pointCloudEvery == 0)
                {
                    manager.InjectSample(DeviceKind.Radar,
                        BuildRadar(options, now, ref sequence, name, t, isPointCloud: true, index: tick,
                            droneEnu: droneEnu, boresightDeg: boresightDeg));
                }

                tick++;
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "仿真数据源异常退出。");
        }
    }

    // ── 三台设备的样本构造 ──────────────────────────────────────────────────

    private static GnssSample BuildBaseStation(SimulationOptions o, DateTimeOffset now, ref long sequence, string name, double t)
    {
        // 基座固定不动，只叠加厘米级抖动，用来观察显示原点是否稳定。
        var jitterE = Math.Sin(t * 0.7) * 0.012;
        var jitterN = Math.Cos(t * 0.9) * 0.012;
        var (lat, lon, alt) = GeoMath.EnuToGeodetic(new EnuPoint(jitterE, jitterN, 0), o.Latitude, o.Longitude, o.AltitudeM);

        return new GnssSample
        {
            Device = DeviceKind.BaseStation,
            DeviceName = name,
            Timestamp = now,
            Sequence = ++sequence,
            DeviceTime = now.UtcDateTime.ToString("O"),
            Fix = new GnssFix
            {
                Latitude = lat,
                Longitude = lon,
                AltitudeM = alt,
                SpeedMps = 0,
                CourseDeg = 0,
                Satellites = 24,
                FixQuality = 4, // RTK 固定
                Hdop = 0.6,
                GeoidSeparationM = -8.5,
                MagneticVariationDeg = -6.8,
                Valid = true,
                UtcTime = now,
            },
            TrueHeadingDeg = BaselineHeadingDeg,
            HeadingSource = "THS",
            Sentences = ["$GNGGA,(仿真)", "$GNRMC,(仿真)", "$GPTHS,(仿真)"],
        };
    }

    /// <summary>
    /// 无人机在基座 ENU 下的真值位置：逆时针绕圆，东 = R·cosθ、北 = R·sinθ。
    /// 无人机的 GPS 样本与雷达「看到的无人机」都以它为唯一真值，否则对比卡片会自相矛盾。
    /// </summary>
    private static EnuPoint DroneEnu(SimulationOptions o, double t)
    {
        var theta = 2.0 * Math.PI / Math.Max(2.0, o.DronePeriodSec) * t;
        return new EnuPoint(
            o.DroneRadiusM * Math.Cos(theta),
            o.DroneRadiusM * Math.Sin(theta),
            o.DroneAltitudeM);
    }

    private static DroneGpsSample BuildDrone(SimulationOptions o, DateTimeOffset now, ref long sequence, string name, double t)
    {
        var period = Math.Max(2.0, o.DronePeriodSec);
        var omega = 2.0 * Math.PI / period;
        var theta = omega * t;

        // 逆时针绕圆：东 = R·cosθ，北 = R·sinθ
        var position = DroneEnu(o, t);
        var east = position.East;
        var north = position.North;
        var up = position.Up;

        // 速度（对 t 求导）
        var velEast = -o.DroneRadiusM * omega * Math.Sin(theta);
        var velNorth = o.DroneRadiusM * omega * Math.Cos(theta);

        var (lat, lon, alt) = GeoMath.EnuToGeodetic(new EnuPoint(east, north, up), o.Latitude, o.Longitude, o.AltitudeM);

        // 航向：罗盘角（正北 0，顺时针），顺时针为正
        var headingDeg = GeoMath.Normalize360(GeoMath.ToDegrees(Math.Atan2(velEast, velNorth)));

        // 横滚向圆心内侧倾斜，俯仰略微上仰
        var bankRad = GeoMath.ToRadians(-8.0);

        return new DroneGpsSample
        {
            Device = DeviceKind.DroneGps,
            DeviceName = name,
            Timestamp = now,
            Sequence = ++sequence,
            DeviceTime = now.UtcDateTime.ToString("O"),
            Fix = new GnssFix
            {
                Latitude = lat,
                Longitude = lon,
                AltitudeM = alt,
                SpeedMps = Math.Sqrt(velEast * velEast + velNorth * velNorth),
                CourseDeg = headingDeg,
                Satellites = 18,
                FixQuality = 4,
                Hdop = 0.8,
                Valid = true,
                UtcTime = now,
            },
            Attitude = new UavAttitude
            {
                RollRad = bankRad,
                PitchRad = GeoMath.ToRadians(3.0),
                HeadingRad = GeoMath.ToRadians(headingDeg),
                VelocityNorthMps = velNorth,
                VelocityEastMps = velEast,
                VelocityDownMps = 0,
                RelativeAltitudeM = up,
            },
            Imu = new ImuSample
            {
                Accel1 = [0.05, 0.03, 9.79],
                Gyro1 = [0.0, 0, omega],
                Accel2 = [0.05, 0.03, 9.79],
                Gyro2 = [0.0, 0, omega],
            },
            GpsSpeedMps = Math.Sqrt(velEast * velEast + velNorth * velNorth),
            MagneticDeclinationDeg = -6.8,
            PacketStatus = 0x07, // 数据有效 + 扩展信息 + 无人机信息
            DeviceId = 271,
            TargetCount = 0,
            PointCloudCount = 0,
        };
    }

    private static RadarSample BuildRadar(
        SimulationOptions o, DateTimeOffset now, ref long sequence, string name,
        double t, bool isPointCloud, long index, EnuPoint droneEnu, double boresightDeg)
    {
        // 杂波是可选项：TargetCount / PointCount 为 0 时分别表示「只发无人机」「不发点云」。
        var groundCount = isPointCloud ? Math.Max(0, o.PointCount) : Math.Max(0, o.TargetCount);

        // 目标帧末尾追加一个「雷达眼里的无人机」；点云帧不追加（点云不参与目标比对）。
        // 无人机飞出雷达方位视场时 BuildDroneTarget 返回 null，该帧就只剩杂波（默认场景下是空帧）。
        var droneTarget = isPointCloud ? null : BuildDroneTarget(droneEnu, boresightDeg, t);
        var targets = new RadarTarget[groundCount + (droneTarget is null ? 0 : 1)];

        for (var i = 0; i < groundCount; i++)
        {
            // 方位角在 ±70° 内均匀铺开；距离用黄金比散布，避免排成整齐同心弧。
            var fraction = groundCount == 1 ? 0.5 : i / (double)(groundCount - 1);
            var spread = Fraction(i * 0.6180339887);
            var azimuthDeg = -70.0 + 140.0 * (isPointCloud ? spread : fraction);
            var range = 25.0 + 235.0 * Fraction(i * 0.7548776662 + 0.13);

            // 目标缓慢往复移动，使三维视图可见运动。
            var wobble = Math.Sin(t * 0.35 + i * 1.7);
            var radial = isPointCloud ? 1.2 * wobble : 7.0 * wobble;
            var lateral = Math.Cos(t * 0.28 + i * 2.3) * (isPointCloud ? 0.8 : 4.0);
            range += radial;

            var r = Math.Max(5.0, range);
            var azRad = GeoMath.ToRadians(azimuthDeg);
            var height = isPointCloud
                ? 0.4 * Math.Sin(i * 2.399) + 1.6 * Fraction(i * 0.4142135)
                : 0.6 + 1.2 * Fraction(i * 0.3183099);

            var x = r * Math.Sin(azRad) + lateral;
            var y = r * Math.Cos(azRad);
            var z = height;

            var trueRange = Math.Sqrt(x * x + y * y + z * z);
            var type = isPointCloud
                ? 0x00
                : i % 5 switch { 0 => 1, 1 => 2, 2 => 3, 3 => 0, _ => 4 };

            targets[i] = new RadarTarget
            {
                Id = (uint)(isPointCloud ? i + 1 : 1000 + i),
                Type = type,
                X = x,
                Y = y,
                Z = z,
                Range = trueRange,
                AzimuthDeg = GeoMath.ToDegrees(Math.Atan2(x, y)),
                ElevationDeg = GeoMath.ToDegrees(Math.Asin(Math.Clamp(trueRange < 1e-6 ? 0 : z / trueRange, -1, 1))),
                SpeedX = isPointCloud ? 0 : 0.35 * lateral * Math.Cos(t * 0.28 + i * 2.3),
                SpeedY = isPointCloud ? 0 : Math.Abs(0.35 * radial * Math.Cos(t * 0.35 + i * 1.7)),
                SpeedZ = 0,
                Snr = 12.0 + 18.0 * Fraction(i * 0.2718281),
                PeakEnergyDb = -40.0 + 25.0 * Fraction(i * 0.5772156),
                AreaMask = isPointCloud ? 0 : i % 3 == 0 ? 0b1 : 0,
            };
        }

        if (droneTarget is not null) targets[groundCount] = droneTarget;

        return new RadarSample
        {
            Device = DeviceKind.Radar,
            DeviceName = name,
            Timestamp = now,
            Sequence = ++sequence,
            DeviceTime = now.UtcDateTime.ToString("O"),
            IsPointCloud = isPointCloud,
            Command = isPointCloud ? (byte)0xA9 : (byte)0xA8,
            SourceAddress = 0x40,
            Targets = targets,
            DeclaredCount = targets.Length,
        };
    }

    /// <summary>
    /// 构造雷达「看到」的无人机：真值取无人机 RTK 位置，换算到雷达本体坐标系后
    /// 叠加缓慢漂移的测距/测角误差，使相对位置页能给出非零的探测偏差读数。
    ///
    /// 返回 null 表示这一拍雷达没看到无人机（本体方位角超出 ±<see cref="RadarHalfFovDeg"/>° 的视场），
    /// 对应真实雷达「无人机飞到后方就丢点」的行为。
    ///
    /// 输出刻意只给本体极坐标（距离/方位/俯仰），x/y/z 留 0：这才是协议里雷达给出的**转换前**原始量，
    /// 「极坐标 → 本体直角 → 旋到基座 ENU」的解算由上位机在 RelativeService.BuildTargets 里完成。
    /// </summary>
    private static RadarTarget? BuildDroneTarget(EnuPoint droneEnu, double boresightDeg, double t)
    {
        // 仿真里雷达与基座共址（俯仰/横滚为 0），所以 ENU 位移就是相对雷达的位移。
        var (bx, by, bz) = GeoMath.EnuToRadarBody(droneEnu.East, droneEnu.North, droneEnu.Up, boresightDeg, 0, 0);

        var trueRange = Math.Sqrt(bx * bx + by * by + bz * bz);
        var azimuthDeg = GeoMath.ToDegrees(Math.Atan2(bx, by));
        var elevationDeg = GeoMath.ToDegrees(Math.Asin(trueRange < 1e-6 ? 0 : bz / trueRange));

        // 视场门控用**无误差**的真值方位角，免得噪声把目标在边界上抖进抖出。
        if (Math.Abs(azimuthDeg) > RadarHalfFovDeg) return null;

        // 测距按比例偏、测角按绝对量偏，随时间缓慢变化（而不是噪声），便于肉眼核对读数。
        var range = trueRange * (1.0 + 0.006 * Math.Sin(t * 0.21));
        var azimuth = azimuthDeg + 0.35 * Math.Sin(t * 0.17);
        var elevation = elevationDeg + 0.22 * Math.Cos(t * 0.13);

        return new RadarTarget
        {
            Id = 1099,
            Type = RadarTargetTypes.Air,
            X = 0,
            Y = 0,
            Z = 0,
            Range = range,
            AzimuthDeg = azimuth,
            ElevationDeg = elevation,
            SpeedX = 0,
            SpeedY = 0,
            SpeedZ = 0,
            Snr = 30.0,
            PeakEnergyDb = -28.0,
            AreaMask = 0,
        };
    }

    private static double Fraction(double value) => value - Math.Floor(value);

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
