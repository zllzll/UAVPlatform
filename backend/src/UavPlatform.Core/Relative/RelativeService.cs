using UavPlatform.Core.Devices;
using UavPlatform.Core.Models;

namespace UavPlatform.Core.Relative;

/// <summary>
/// 相对位置服务（需求③）：把基座、雷达、无人机三个设备的数据统一到同一个显示坐标系。
/// </summary>
/// <remarks>
/// 处理链：
/// <list type="number">
/// <item>确定三个设备各自的大地坐标（WGS84）。基座来自 NMEA；无人机来自 UCM221 的无人机信息 / 扩展信息模块；
/// 雷达由 <see cref="RadarPlacementSettings"/> 给出（与基座共址 / 相对基座偏移 / 独立经纬度）。</item>
/// <item>确定显示坐标系原点：恒为<b>基座</b>，被选设备即为 (0,0,0)。
/// 实现上把局部 ENU 的切平面原点直接取在基座位置上，因此基座的 ENU 恒为零，无需额外平移；
/// 原点在锁定后保持不动，保证轨迹参考系稳定（<see cref="RelativeSettings.FollowReferenceDrift"/> 可改为跟随漂移）。</item>
/// <item>把三个设备的位置由经纬度换算为相对原点的 ENU（东/北/天，米）。</item>
/// <item>把雷达本体坐标系下的目标按安装姿态旋转到 ENU，再平移到雷达位置。</item>
/// <item>计算目标与无人机、无人机与基座之间的相对关系。</item>
/// </list>
/// 本类线程安全。任一设备数据到达都可调用 <see cref="Build"/> 产出新的相对位置帧，<see cref="RelativeSettings.RelativeIntervalMs"/> 用于限频。
/// </remarks>
public sealed class RelativeService
{
    private readonly object _gate = new();
    private RelativeSettings _settings;
    private GnssSample? _base;
    private RadarSample? _radar;
    private DroneGpsSample? _drone;
    private GeoReference _reference = new();
    private long _sequence;
    private long _lastBuildTicks;
    private long _suppressed;

    public RelativeService(RelativeSettings settings)
    {
        _settings = settings.Clone();
    }

    /// <summary>相对位置结果产出事件（仅在通过限频时触发）。</summary>
    public event Action<RelativeFrame>? FrameReady;

    /// <summary>当前配置（快照）。</summary>
    public RelativeSettings Settings
    {
        get { lock (_gate) return _settings.Clone(); }
    }

    /// <summary>链路在线判定回调，由 DeviceManager 注入。</summary>
    public Func<DeviceKind, bool>? OnlineProvider { get; set; }

    /// <summary>当前显示坐标系原点（大地坐标）。</summary>
    public GeoReference Reference
    {
        get { lock (_gate) return _reference; }
    }

    /// <summary>原点是否已确定。</summary>
    public bool HasReference
    {
        get { lock (_gate) return _reference.Resolved; }
    }

    /// <summary>因限频被丢弃的相对位置帧数。</summary>
    public long SuppressedFrames => Interlocked.Read(ref _suppressed);

    /// <summary>替换配置。原点已锁定时不会因配置变更而移动（除非调用 <see cref="ResetReference"/>）。</summary>
    public void UpdateSettings(RelativeSettings settings)
    {
        lock (_gate)
        {
            _settings = settings.Clone();
        }
    }

    /// <summary>更新基座定位样本。</summary>
    public void OnGnss(GnssSample sample)
    {
        lock (_gate)
        {
            _base = sample;
        }
    }

    /// <summary>更新雷达目标 / 点云样本。</summary>
    public void OnRadar(RadarSample sample)
    {
        lock (_gate)
        {
            _radar = sample;
        }
    }

    /// <summary>更新无人机样本。</summary>
    public void OnDrone(DroneGpsSample sample)
    {
        lock (_gate)
        {
            _drone = sample;
        }
    }

    /// <summary>丢弃当前原点，下一帧重新锁定（设备搬动或重启后使用）。</summary>
    public void ResetReference()
    {
        lock (_gate)
        {
            _reference = new GeoReference();
        }
    }

    /// <summary>清空全部缓存数据与原点。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _base = null;
            _radar = null;
            _drone = null;
            _reference = new GeoReference();
            _sequence = 0;
        }
    }

    /// <summary>
    /// 产出一帧相对位置结果。<paramref name="force"/> 为假时按 <see cref="RelativeSettings.RelativeIntervalMs"/> 限频。
    /// 返回 null 表示未启用或被限频丢弃。
    /// </summary>
    public RelativeFrame? Build(string trigger, bool force = false)
    {
        RelativeSettings settings;
        GnssSample? baseSample;
        RadarSample? radarSample;
        DroneGpsSample? droneSample;
        long seq;

        lock (_gate)
        {
            if (!_settings.Enabled) return null;

            var interval = Math.Max(0, _settings.RelativeIntervalMs);
            if (!force && interval > 0)
            {
                var nowTicks = Environment.TickCount64;
                var last = Interlocked.Read(ref _lastBuildTicks);
                if (nowTicks - last < interval)
                {
                    Interlocked.Increment(ref _suppressed);
                    return null;
                }
                Interlocked.Exchange(ref _lastBuildTicks, nowTicks);
            }

            settings = _settings.Clone();
            baseSample = _base;
            radarSample = _radar;
            droneSample = _drone;
            seq = ++_sequence;
        }

        var stale = Math.Max(500, settings.StaleTimeoutMs);

        // ── 1. 三个设备的大地坐标 ──────────────────────────────────────────
        var baseGeo = ResolveBase(baseSample);
        var radarGeo = ResolveRadar(settings, baseGeo, out var radarGeoSource);
        var droneGeo = ResolveDrone(droneSample);

        // ── 2. 显示坐标系原点 = 基座 ────────────────────────────────────────
        // 基座与无人机的定位本来就是 WGS84 世界坐标；雷达目标给的是本体极坐标，
        // 由安装姿态换算进这个 ENU 系（见 BuildTargets）。三者同系才能直接比对，
        // 所以全项目不再提供「以雷达为原点」的选项。
        const string originName = "基座";
        var reference = ResolveReference(settings, baseGeo, originName, radarGeo, "雷达");

        // ENU 切平面原点即参考点，故被选设备的 ENU 恒为 (0,0,0)。
        var baseEnu = EnuPoint.Zero;
        var radarEnu = EnuPoint.Zero;
        var droneEnu = EnuPoint.Zero;
        if (reference.Resolved)
        {
            if (baseGeo is not null) baseEnu = ToEnu(baseGeo.Value, reference);
            if (radarGeo is not null) radarEnu = ToEnu(radarGeo.Value, reference);
            if (droneGeo is not null) droneEnu = ToEnu(droneGeo.Value, reference);
        }

        // 雷达正前方方位角：优先由基座双天线基线航向（THS）+ 安装夹角实时解算；
        // 拿不到双天线定向时回落到手动绝对角，保证没有 THS 时系统照常可用。
        var baselineHeadingDeg = baseSample?.TrueHeadingDeg is { } bh ? GeoMath.Normalize360(bh) : (double?)null;
        var (boresightDeg, boresightFromBaseline) =
            settings.Radar.ResolveYawDeg(baselineHeadingDeg, baseSample?.HeadingSource);

        var baseNode = MakeGnssNode("基座", DeviceKind.BaseStation, baseGeo, baseEnu,
            baseSample, baseSample?.Timestamp, stale, null,
            baselineHeadingDeg,
            baseSample?.Fix.FixQuality, baseSample?.Fix.Satellites,
            "未收到基座定位数据", baseSample?.HeadingSource);

        var radarNode = MakeRadarNode(radarSample, radarGeo, radarEnu, settings, boresightDeg, stale, radarGeoSource);

        var droneNode = MakeDroneNode(droneSample, droneGeo, droneEnu, stale);

        // ── 3. 目标变换 ──────────────────────────────────────────────────
        var targets = BuildTargets(settings, radarSample, reference.Resolved, radarEnu, boresightDeg, droneNode);

        // ── 3b. 无人机 ↔ 最近雷达目标对比（校核雷达探测精度）────────────
        var comparison = BuildDroneComparison(settings, targets, droneNode,
            reference.Resolved, droneEnu, radarEnu, boresightDeg);

        // ── 4. 平台相对关系 ──────────────────────────────────────────────
        double? droneToBase = null, droneHeightAboveBase = null, droneToRadar = null, skewMs = null;
        if (baseGeo is not null && droneGeo is not null && reference.Resolved)
        {
            var delta = droneEnu - baseEnu;
            droneToBase = delta.HorizontalDistance;
            droneHeightAboveBase = delta.Up;
        }
        if (radarGeo is not null && droneGeo is not null && reference.Resolved)
        {
            droneToRadar = (droneEnu - radarEnu).Distance;
        }
        if (baseSample is not null && droneSample is not null)
        {
            skewMs = Math.Abs((droneSample.Timestamp - baseSample.Timestamp).TotalMilliseconds);
        }

        var frame = new RelativeFrame
        {
            Timestamp = DateTimeOffset.Now,
            Sequence = seq,
            OriginName = originName,
            ReferenceResolved = reference.Resolved,
            Reference = reference,
            BaseStation = baseNode,
            Radar = radarNode,
            Drone = droneNode,
            RadarBaseLineM = reference.Resolved && baseGeo is not null && radarGeo is not null
                ? (radarEnu - baseEnu).Distance
                : 0,
            RadarBoresightDeg = boresightDeg,
            RadarBoresightFromBaseline = boresightFromBaseline,
            RadarBaselineHeadingDeg = baselineHeadingDeg,
            Targets = targets,
            IsPointCloud = radarSample?.IsPointCloud ?? false,
            DroneDistanceToBaseM = droneToBase,
            DroneHeightAboveBaseM = droneHeightAboveBase,
            DroneDistanceToRadarM = droneToRadar,
            Trigger = trigger,
            BaseToDroneSkewMs = skewMs,
            DroneComparison = comparison,
        };

        FrameReady?.Invoke(frame);
        return frame;
    }

    /// <summary>
    /// 把一帧雷达数据换算到**以基座为原点**的东北天坐标系，产出一条「雷达转换到基座系」记录。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Build"/> 的区别：这里每收到一帧雷达数据就算一次，不受出帧限频影响；
    /// 结果是稳定的基座系（与界面视图无关），可以直接跨会话比对。
    /// 每收到一帧雷达数据调用一次，所以雷达的每一帧、每一个目标都会留档，不受出帧限频影响。
    /// 雷达服务未启用时返回 null。
    /// </remarks>
    public RadarToBaseFrame? ProjectRadarToBase(RadarSample sample, string trigger)
    {
        RelativeSettings settings;
        GnssSample? baseSample;
        DroneGpsSample? droneSample;
        GeoReference currentReference;

        lock (_gate)
        {
            if (!_settings.Enabled) return null;
            settings = _settings.Clone();
            baseSample = _base;
            droneSample = _drone;
            currentReference = _reference;
        }

        var baseGeo = ResolveBase(baseSample);
        var radarGeo = ResolveRadar(settings, baseGeo, out _);
        var droneGeo = ResolveDrone(droneSample);

        // 基座系原点就是基座自己的定位。基座还没定位时退回当前显示参考点（通常也是基座），
        // 这样雷达的观测量照常留档，只是没有基座系直角坐标。
        var origin = baseGeo is not null
            ? new GeoReference
            {
                Latitude = baseGeo.Value.Lat,
                Longitude = baseGeo.Value.Lon,
                AltitudeM = baseGeo.Value.Alt,
                Source = "基座",
                Resolved = true,
            }
            : currentReference;

        var radarEnu = origin.Resolved && radarGeo is not null ? ToEnu(radarGeo.Value, origin) : EnuPoint.Zero;
        var droneEnu = origin.Resolved && droneGeo is not null ? ToEnu(droneGeo.Value, origin) : EnuPoint.Zero;

        var baselineHeadingDeg = baseSample?.TrueHeadingDeg is { } bh ? GeoMath.Normalize360(bh) : (double?)null;
        var (boresightDeg, boresightFromBaseline) =
            settings.Radar.ResolveYawDeg(baselineHeadingDeg, baseSample?.HeadingSource);

        var stale = Math.Max(500, settings.StaleTimeoutMs);
        var droneNode = MakeDroneNode(droneSample, droneGeo, droneEnu, stale);

        var targets = BuildTargets(settings, sample, origin.Resolved, radarEnu, boresightDeg, droneNode);

        return new RadarToBaseFrame
        {
            PcTime = sample.Timestamp,
            DeviceTime = sample.DeviceTime,
            Sequence = sample.Sequence,
            Trigger = trigger,
            BaseResolved = origin.Resolved,
            Origin = origin,
            BoresightDeg = boresightDeg,
            BoresightFromBaseline = boresightFromBaseline,
            BaselineHeadingDeg = baselineHeadingDeg,
            RadarEast = radarEnu.East,
            RadarNorth = radarEnu.North,
            RadarUp = radarEnu.Up,
            IsPointCloud = sample.IsPointCloud,
            Command = sample.Command,
            DeclaredCount = sample.DeclaredCount,
            Targets = targets,
        };
    }

    /// <summary>把相对位置帧里的显示坐标（米）反算回 WGS84，供热力图与地图使用。</summary>
    public (double LatitudeDeg, double LongitudeDeg, double HeightMeters) DisplayToGeodetic(RelativeFrame frame,
        double east, double north, double up)
    {
        var point = new EnuPoint(east, north, up);

        var r = frame.Reference;
        return r.Resolved
            ? GeoMath.EnuToGeodetic(point, r.Latitude, r.Longitude, r.AltitudeM)
            : (double.NaN, double.NaN, double.NaN);
    }

    // ── 位置解析 ────────────────────────────────────────────────────────────

    private static (double Lat, double Lon, double Alt)? ResolveBase(GnssSample? sample)
    {
        var fix = sample?.Fix;
        if (fix is null || !fix.Valid) return null;
        if (!IsPlausible(fix.Latitude, fix.Longitude)) return null;
        return (fix.Latitude, fix.Longitude, fix.AltitudeM ?? 0);
    }

    private static (double Lat, double Lon, double Alt)? ResolveRadar(
        RelativeSettings settings, (double Lat, double Lon, double Alt)? baseGeo, out string? source)
    {
        source = null;
        var placement = settings.Radar;

        switch (placement.Mode)
        {
            case RadarPlacementMode.ManualWgs84:
                if (!IsPlausible(placement.Latitude, placement.Longitude)) return null;
                source = "雷达位置：手动经纬度";
                return (placement.Latitude, placement.Longitude, placement.AltitudeM);

            case RadarPlacementMode.OffsetFromBase:
            {
                if (baseGeo is null) return null;
                var offset = new EnuPoint(placement.OffsetEastM, placement.OffsetNorthM, placement.OffsetUpM);
                var geo = GeoMath.EnuToGeodetic(offset, baseGeo.Value.Lat, baseGeo.Value.Lon, baseGeo.Value.Alt);
                source = "雷达位置：基座 + 偏移";
                return geo;
            }

            default:
            {
                if (baseGeo is null) return null;
                source = "雷达位置：与基座共址";
                return baseGeo;
            }
        }
    }

    private static (double Lat, double Lon, double Alt)? ResolveDrone(DroneGpsSample? sample)
    {
        var fix = sample?.Fix;
        if (fix is null || !fix.Valid) return null;
        if (!IsPlausible(fix.Latitude, fix.Longitude)) return null;
        return (fix.Latitude, fix.Longitude, fix.AltitudeM ?? 0);
    }

    /// <summary>确定显示坐标系原点。优先手动坐标；否则取被选设备的实测位置；再退化到另一台设备。</summary>
    private GeoReference ResolveReference(
        RelativeSettings settings,
        (double Lat, double Lon, double Alt)? originGeo,
        string originName,
        (double Lat, double Lon, double Alt)? fallbackGeo,
        string fallbackName)
    {
        lock (_gate)
        {
            if (_reference.Resolved && !settings.FollowReferenceDrift) return _reference;

            GeoReference? candidate = null;

            if (settings.ReferenceMode == ReferencePointMode.Manual &&
                settings.ManualLatitude is { } mLat && settings.ManualLongitude is { } mLon &&
                IsPlausible(mLat, mLon))
            {
                candidate = new GeoReference
                {
                    Latitude = mLat,
                    Longitude = mLon,
                    AltitudeM = settings.ManualAltitudeM ?? 0,
                    Source = "Manual",
                    Resolved = true,
                };
            }
            else if (originGeo is not null)
            {
                candidate = new GeoReference
                {
                    Latitude = originGeo.Value.Lat,
                    Longitude = originGeo.Value.Lon,
                    AltitudeM = originGeo.Value.Alt,
                    Source = originName,
                    Resolved = true,
                };
            }
            else if (fallbackGeo is not null)
            {
                candidate = new GeoReference
                {
                    Latitude = fallbackGeo.Value.Lat,
                    Longitude = fallbackGeo.Value.Lon,
                    AltitudeM = fallbackGeo.Value.Alt,
                    Source = fallbackName,
                    Resolved = true,
                };
            }

            if (candidate is not null) _reference = candidate;
            return _reference;
        }
    }

    private static EnuPoint ToEnu((double Lat, double Lon, double Alt) geo, GeoReference reference) =>
        GeoMath.GeodeticToEnu(geo.Lat, geo.Lon, geo.Alt, reference.Latitude, reference.Longitude, reference.AltitudeM);

    private static bool IsPlausible(double lat, double lon) =>
        !double.IsNaN(lat) && !double.IsNaN(lon) &&
        Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180 &&
        !(lat == 0 && lon == 0);

    // ── 节点构造 ────────────────────────────────────────────────────────────

    private RelativeNode MakeGnssNode(string name, DeviceKind kind,
        (double Lat, double Lon, double Alt)? geo, EnuPoint position,
        DeviceSample? sample, DateTimeOffset? sampleTime, long stale, string? missingNote,
        double? headingDeg, int? fixQuality, int? satellites, string notPositionedNote, string? headingSource)
    {
        var ageMs = sampleTime is null ? double.NaN : Math.Max(0, (DateTimeOffset.Now - sampleTime.Value).TotalMilliseconds);
        var fresh = sample is not null && ageMs <= stale;
        var positioned = geo is not null;

        return new RelativeNode
        {
            Name = name,
            Online = OnlineProvider?.Invoke(kind) ?? sample is not null,
            Fresh = fresh,
            Position = position,
            Latitude = positioned ? geo!.Value.Lat : null,
            Longitude = positioned ? geo!.Value.Lon : null,
            AltitudeM = positioned ? geo!.Value.Alt : null,
            HeadingDeg = headingDeg,
            FixQuality = fixQuality,
            Satellites = satellites,
            AgeMs = ageMs,
            PcTime = sample?.Timestamp,
            DeviceTime = sample?.DeviceTime,
            Sequence = sample?.Sequence ?? 0,
            Note = BuildNote(sample is not null, positioned, fresh, notPositionedNote, missingNote, headingSource),
        };
    }

    private RelativeNode MakeRadarNode(RadarSample? sample, (double Lat, double Lon, double Alt)? geo,
        EnuPoint position, RelativeSettings settings, double boresightDeg, long stale, string? geoSource)
    {
        var ageMs = sample is null ? double.NaN : Math.Max(0, (DateTimeOffset.Now - sample.Timestamp).TotalMilliseconds);
        var fresh = sample is not null && ageMs <= stale;
        var positioned = geo is not null;

        return new RelativeNode
        {
            Name = "雷达",
            Online = OnlineProvider?.Invoke(DeviceKind.Radar) ?? sample is not null,
            Fresh = fresh,
            Position = position,
            Latitude = positioned ? geo!.Value.Lat : null,
            Longitude = positioned ? geo!.Value.Lon : null,
            AltitudeM = positioned ? geo!.Value.Alt : null,
            HeadingDeg = boresightDeg,
            PitchDeg = settings.Radar.PitchDeg,
            RollDeg = settings.Radar.RollDeg,
            AgeMs = ageMs,
            PcTime = sample?.Timestamp,
            DeviceTime = sample?.DeviceTime,
            Sequence = sample?.Sequence ?? 0,
            Note = positioned
                ? geoSource
                : "雷达位置未确定：请选择安装位置方式（共址 / 相对基座偏移 / 手动经纬度）",
        };
    }

    private RelativeNode MakeDroneNode(DroneGpsSample? sample, (double Lat, double Lon, double Alt)? geo,
        EnuPoint position, long stale)
    {
        var ageMs = sample is null ? double.NaN : Math.Max(0, (DateTimeOffset.Now - sample.Timestamp).TotalMilliseconds);
        var fresh = sample is not null && ageMs <= stale;
        var positioned = geo is not null;

        // 无人机信息模块的航向为弧度、顺时针为正；统一输出 0~360 罗盘方位角。
        double? headingDeg = sample?.Attitude?.HeadingRad is { } rad
            ? GeoMath.Normalize360(GeoMath.ToDegrees(rad))
            : null;

        var note = !positioned ? "无人机尚未输出有效定位" : null;

        return new RelativeNode
        {
            Name = "无人机",
            Online = OnlineProvider?.Invoke(DeviceKind.DroneGps) ?? sample is not null,
            Fresh = fresh,
            Position = position,
            Latitude = positioned ? geo!.Value.Lat : null,
            Longitude = positioned ? geo!.Value.Lon : null,
            AltitudeM = positioned ? geo!.Value.Alt : null,
            HeadingDeg = headingDeg,
            FixQuality = sample?.Fix?.FixQuality,
            Satellites = sample?.Fix?.Satellites,
            PitchDeg = sample?.Attitude?.PitchRad is { } p ? GeoMath.ToDegrees(p) : null,
            RollDeg = sample?.Attitude?.RollRad is { } r ? GeoMath.ToDegrees(r) : null,
            AgeMs = ageMs,
            PcTime = sample?.Timestamp,
            DeviceTime = sample?.DeviceTime,
            Sequence = sample?.Sequence ?? 0,
            Note = BuildNote(sample is not null, positioned, fresh, "未收到无人机数据", note, null),
        };
    }

    private static string? BuildNote(bool received, bool positioned, bool fresh, string notPositioned, string? missing, string? headingSource)
    {
        if (!received) return missing ?? "未收到数据";
        if (!positioned) return notPositioned;
        if (!fresh) return "数据已超时";
        return string.IsNullOrEmpty(headingSource) ? null : $"航向来自 {headingSource}";
    }

    // ── 目标变换 ────────────────────────────────────────────────────────────

    private static RelativeTarget[] BuildTargets(RelativeSettings settings, RadarSample? radarSample, bool referenceResolved,
        EnuPoint radarPosition, double boresightDeg, RelativeNode droneNode)
    {
        if (radarSample is null || radarSample.Targets.Length == 0) return [];

        var filter = settings.Filter;
        var placement = settings.Radar;
        var max = filter.MaxTargets > 0 ? filter.MaxTargets : int.MaxValue;
        var list = new List<RelativeTarget>(Math.Min(radarSample.Targets.Length, 4096));

        foreach (var t in radarSample.Targets)
        {
            if (filter.DropDeleted && t.Type == RadarTargetTypes.Deleted) continue;
            if (filter.MaxRangeM > 0 && t.Range > filter.MaxRangeM) continue;
            if (filter.MinSnr > 0 && t.Snr < filter.MinSnr) continue;

            // 目标的本体坐标：优先用 x/y/z；雷达只报了距离与角度时按极坐标换算。
            var (bx, by, bz) = (t.X, t.Y, t.Z);
            if (bx == 0 && by == 0 && bz == 0 && t.Range > 0)
            {
                (bx, by, bz) = GeoMath.RadarPolarToCartesian(t.AzimuthDeg, t.ElevationDeg, t.Range);
            }

            var delta = referenceResolved
                ? GeoMath.RotateRadarToEnu(bx, by, bz, boresightDeg, placement.PitchDeg, placement.RollDeg)
                : EnuPoint.Zero;

            var position = radarPosition + delta;

            var velocity = GeoMath.RotateRadarToEnu(t.SpeedX, t.SpeedY, t.SpeedZ,
                boresightDeg, placement.PitchDeg, placement.RollDeg);

            double? distanceToDrone = null, heightAboveDrone = null;
            if (droneNode.Latitude is not null)
            {
                var d = position - droneNode.Position;
                distanceToDrone = d.Distance;
                heightAboveDrone = d.Up;
            }

            list.Add(new RelativeTarget
            {
                Id = t.Id,
                Type = t.Type,
                TypeName = t.TypeName,
                East = position.East,
                North = position.North,
                Up = position.Up,
                RangeM = t.Range,
                AzimuthDeg = t.AzimuthDeg,
                ElevationDeg = t.ElevationDeg,
                Snr = t.Snr,
                PeakEnergyDb = t.PeakEnergyDb,
                AreaMask = t.AreaMask,
                VelocityEast = velocity.East,
                VelocityNorth = velocity.North,
                VelocityUp = velocity.Up,
                SpeedMps = Math.Sqrt(velocity.East * velocity.East +
                                     velocity.North * velocity.North +
                                     velocity.Up * velocity.Up),
                DistanceToDroneM = distanceToDrone,
                HeightAboveDroneM = heightAboveDrone,
            });

            if (list.Count >= max) break;
        }

        return list.ToArray();
    }

    /// <summary>
    /// 把无人机当成一个运动靶标，与雷达探测到的目标比对，用来校核雷达探测精度。
    /// 在雷达目标里取离无人机 RTK 位置最近的一个作为「无人机回波」，
    /// 同时给出显示坐标系下的位置偏差与雷达本体系下的距离/方位/俯仰偏差。
    /// </summary>
    private static DroneRadarComparison? BuildDroneComparison(RelativeSettings settings, RelativeTarget[] targets,
        RelativeNode droneNode, bool referenceResolved, EnuPoint droneEnu, EnuPoint radarEnu, double boresightDeg)
    {
        var compare = settings.Comparison;
        if (!compare.Enabled || !referenceResolved) return null;
        if (droneNode.Latitude is null || targets.Length == 0) return null;

        var rtk = droneNode.Position;
        var radius = Math.Max(0, compare.MatchRadiusM);

        // 取离 RTK 位置最近的雷达目标。
        RelativeTarget? nearest = null;
        var nearestDistance = double.PositiveInfinity;
        foreach (var t in targets)
        {
            var dx = t.East - rtk.East;
            var dy = t.North - rtk.North;
            var dz = t.Up - rtk.Up;
            var d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (d < nearestDistance)
            {
                nearestDistance = d;
                nearest = t;
            }
        }

        if (nearest is null) return null;

        var deltaEast = nearest.East - rtk.East;
        var deltaNorth = nearest.North - rtk.North;
        var deltaUp = nearest.Up - rtk.Up;

        // RTK 位置相对雷达换算到雷达本体系，得到一组「真值极坐标」，与雷达实测值直接可比。
        var placement = settings.Radar;
        var toDrone = droneEnu - radarEnu;
        var (bx, by, bz) = GeoMath.EnuToRadarBody(toDrone.East, toDrone.North, toDrone.Up,
            boresightDeg, placement.PitchDeg, placement.RollDeg);
        var (azimuth, elevation, range) = GeoMath.CartesianToRadarPolar(bx, by, bz);
        azimuth = GeoMath.Normalize360(azimuth);

        return new DroneRadarComparison
        {
            Matched = nearestDistance <= radius,
            MatchRadiusM = radius,
            TargetId = nearest.Id,
            TargetType = nearest.Type,
            TargetTypeName = nearest.TypeName,
            RadarEast = nearest.East,
            RadarNorth = nearest.North,
            RadarUp = nearest.Up,
            RtkEast = rtk.East,
            RtkNorth = rtk.North,
            RtkUp = rtk.Up,
            DeltaEastM = deltaEast,
            DeltaNorthM = deltaNorth,
            DeltaUpM = deltaUp,
            DeltaDistanceM = nearestDistance,
            DeltaHorizontalM = Math.Sqrt(deltaEast * deltaEast + deltaNorth * deltaNorth),
            RtkRangeM = range,
            RtkAzimuthDeg = azimuth,
            RtkElevationDeg = elevation,
            RadarRangeM = nearest.RangeM,
            RadarAzimuthDeg = nearest.AzimuthDeg,
            RadarElevationDeg = nearest.ElevationDeg,
            DeltaRangeM = nearest.RangeM - range,
            DeltaAzimuthDeg = GeoMath.Normalize180(nearest.AzimuthDeg - azimuth),
            DeltaElevationDeg = nearest.ElevationDeg - elevation,
        };
    }
}
