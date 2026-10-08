using UavPlatform.Core.Relative;

namespace UavPlatform.Core.Models;

/// <summary>局部坐标参考点（ENU 原点）的取值方式。</summary>
public enum ReferencePointMode
{
    /// <summary>自动：取基座第一个有效定位结果作为参考点；基座无定位时退化为手动坐标。</summary>
    Auto = 0,

    /// <summary>手动指定参考点经纬高（用于基座暂未定位时先跑起来，或复现历史数据）。</summary>
    Manual = 1,
}

/// <summary>雷达本体相对参考点的安装位置来源。</summary>
public enum RadarPlacementMode
{
    /// <summary>与基座同点（雷达与基座天线共址，偏移为 0）。</summary>
    CoLocatedWithBase = 0,

    /// <summary>雷达有独立的 WGS84 定位（例如雷达自带 RTK 或人工测得经纬度）。</summary>
    ManualWgs84 = 1,

    /// <summary>按相对基座的 ENU 偏移量给出（现场皮尺测量最常见）。</summary>
    OffsetFromBase = 2,
}

/// <summary>
/// 雷达「正前方」（本体 +Y 轴）指向的解算方式。
/// 基座与雷达同址安装时，基座的两根天线（主天线给位置，副天线与主天线构成一条基线）
/// 实时给出基线航向；雷达正前方由该基线航向加一个固定的安装夹角确定。
/// </summary>
public enum RadarYawSource
{
    /// <summary>直接给相对正北的绝对方位角（现场用罗盘或地图量取）。</summary>
    ManualAbsolute = 0,

    /// <summary>基座双天线基线航向（THS 真航向）+ 安装夹角实时推算；双天线无定向时回落到手动绝对角。</summary>
    BaseHeadingPlusOffset = 1,
}

/// <summary>雷达安装位置与姿态。决定雷达坐标系 → ENU 的刚体变换。</summary>
public sealed class RadarPlacementSettings
{
    public RadarPlacementMode Mode { get; set; } = RadarPlacementMode.CoLocatedWithBase;

    /// <summary>雷达经度（度，WGS84）。<see cref="RadarPlacementMode.ManualWgs84"/> 时使用。</summary>
    public double Longitude { get; set; }

    /// <summary>雷达纬度（度，WGS84）。<see cref="RadarPlacementMode.ManualWgs84"/> 时使用。</summary>
    public double Latitude { get; set; }

    /// <summary>雷达海拔高（米）。<see cref="RadarPlacementMode.ManualWgs84"/> 时使用。</summary>
    public double AltitudeM { get; set; }

    /// <summary>相对基座的东向偏移（米，东为正）。<see cref="RadarPlacementMode.OffsetFromBase"/> 时使用。</summary>
    public double OffsetEastM { get; set; }

    /// <summary>相对基座的北向偏移（米，北为正）。</summary>
    public double OffsetNorthM { get; set; }

    /// <summary>相对基座的天向偏移（米，上为正）。</summary>
    public double OffsetUpM { get; set; }

    /// <summary>
    /// 雷达探测方向（本体 +Y 轴）相对**正北**的水平夹角，顺时针为正，单位度。
    /// 0 = 探测方向朝正北；90 = 朝正东。
    /// </summary>
    public double YawDeg { get; set; }

    /// <summary>雷达本体俯仰安装角（度，抬头为正）。</summary>
    public double PitchDeg { get; set; }

    /// <summary>雷达本体滚转安装角（度，右倾为正）。</summary>
    public double RollDeg { get; set; }

    /// <summary>雷达正前方指向的解算方式。</summary>
    public RadarYawSource YawSource { get; set; } = RadarYawSource.BaseHeadingPlusOffset;

    /// <summary>
    /// 雷达正前方相对**基座双天线基线**的安装夹角（度，顺时针为正）。
    /// 仅在 <see cref="YawSource"/> 为 <see cref="RadarYawSource.BaseHeadingPlusOffset"/> 时参与解算：
    /// 雷达正前方方位角 = 基线航向 + 该夹角。0 表示雷达正前方与基线同向，180 表示反向。
    /// </summary>
    public double YawOffsetFromBaselineDeg { get; set; }

    /// <summary>
    /// 解算雷达正前方方位角（相对正北，顺时针为正）。
    /// 只认 <b>THS 双天线真航向</b>作为基线航向：RMC / VTG 给的是航迹向（对地速度方向），
    /// 基座静止时那是噪声，拿它去转雷达会把所有目标系统性转偏。
    /// </summary>
    /// <param name="baselineHeadingDeg">基座双天线基线航向（度），不可用时为 null。</param>
    /// <param name="headingSource">航向来源，只有 <c>THS</c> 会被采用。</param>
    /// <returns>解算出的方位角，以及它是否来自双天线基线。</returns>
    public (double YawDeg, bool FromBaseline) ResolveYawDeg(double? baselineHeadingDeg, string? headingSource)
    {
        if (YawSource == RadarYawSource.BaseHeadingPlusOffset
            && baselineHeadingDeg is { } heading
            && !double.IsNaN(heading)
            && string.Equals(headingSource, "THS", StringComparison.OrdinalIgnoreCase))
        {
            return (GeoMath.Normalize360(heading + YawOffsetFromBaselineDeg), true);
        }

        return (GeoMath.Normalize360(YawDeg), false);
    }

    public RadarPlacementSettings Clone() => (RadarPlacementSettings)MemberwiseClone();
}

/// <summary>目标显示过滤（只影响显示与相对位置输出，不影响原始/解析存储）。</summary>
public sealed class TargetFilterSettings
{
    /// <summary>最大显示距离（米），0 表示不限制。</summary>
    public double MaxRangeM { get; set; }

    /// <summary>最小信噪比，0 表示不限制。</summary>
    public double MinSnr { get; set; }

    /// <summary>是否过滤掉协议标记为「已删除」的目标（type = 0xFFFF）。</summary>
    public bool DropDeleted { get; set; } = true;

    /// <summary>
    /// 单帧相对位置输出最多包含多少个目标 / 点云点，0 表示不限制。
    /// 只影响相对位置输出（三维显示与 05_frame 目录）；雷达的完整点云已由 02_radar 的原始与解析文件全量保存。
    /// 04_radar_base 也受此限制约束（它复用同一套目标构建逻辑）。
    /// </summary>
    public int MaxTargets { get; set; } = 4000;

    public TargetFilterSettings Clone() => (TargetFilterSettings)MemberwiseClone();
}

/// <summary>
/// 无人机「雷达探测 vs RTK 定位」对比设置。
/// 无人机本身就是一个运动目标：雷达按自己的坐标系探到它，RTK 给出它相对基座的真实位置，
/// 两者同框比对即可校核雷达探测精度。
/// </summary>
public sealed class DroneComparisonSettings
{
    /// <summary>是否做对比。关闭后相对位置帧里不带对比结果。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 匹配半径（米）。在雷达目标里取离无人机 RTK 位置最近的一个作为「无人机回波」，
    /// 三维距离超过该值即认为本帧雷达没探到无人机，只报未匹配而不给偏差。
    /// </summary>
    public double MatchRadiusM { get; set; } = 20;

    public DroneComparisonSettings Clone() => (DroneComparisonSettings)MemberwiseClone();
}

/// <summary>
/// 相对位置与三维显示配置。
/// 显示坐标系**恒为以基座为原点的世界 ENU 系**（东 X+、北 Y+、天 Z+），
/// 不再提供「以雷达为原点」的选项：雷达只是该坐标系里的一个安装点，
/// 目标由雷达本体极坐标经安装姿态变换进该坐标系。
/// </summary>
public sealed class RelativeSettings
{
    /// <summary>是否启用相对位置输出与落盘。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>ENU 参考点（原点）的来源。</summary>
    public ReferencePointMode ReferenceMode { get; set; } = ReferencePointMode.Auto;

    /// <summary>手动参考点经度（度）。</summary>
    public double? ManualLongitude { get; set; }

    /// <summary>手动参考点纬度（度）。</summary>
    public double? ManualLatitude { get; set; }

    /// <summary>手动参考点海拔高（米）。</summary>
    public double? ManualAltitudeM { get; set; }

    /// <summary>雷达安装位置与姿态。</summary>
    public RadarPlacementSettings Radar { get; set; } = new();

    /// <summary>目标显示过滤。</summary>
    public TargetFilterSettings Filter { get; set; } = new();

    /// <summary>无人机与雷达探测目标的对比（校核雷达探测精度）。</summary>
    public DroneComparisonSettings Comparison { get; set; } = new();

    /// <summary>相对位置帧的最小间隔（毫秒），避免高频设备把相对位置输出打爆。</summary>
    public int RelativeIntervalMs { get; set; } = 100;

    // 这里原有 TrackHistorySeconds（轨迹保留时长，默认 600、配置文件里是 60）。
    // 它与工具条上的「尾迹时长」是同一个意思（轨迹在屏幕上活多久），两个旋钮必然对不上：
    // 尾迹拉到 600 s、后端只留 60 s，刷新页面就只能回看 60 s。现在只留一个旋钮
    // ui.TrailSeconds，后端的轨迹保留窗口由 DeviceManager.ApplyTrackRetention 从它推出。

    /// <summary>设备数据超过该时长未更新即视为不新鲜（毫秒）。只影响相对位置结果的 Fresh 标记，不影响落盘。</summary>
    public int StaleTimeoutMs { get; set; } = 3000;

    /// <summary>参考点在锁定后是否允许随基座定位漂移而移动。默认锁定，保证轨迹参考系稳定。</summary>
    public bool FollowReferenceDrift { get; set; }

    public RelativeSettings Clone() => new()
    {
        Enabled = Enabled,
        ReferenceMode = ReferenceMode,
        ManualLongitude = ManualLongitude,
        ManualLatitude = ManualLatitude,
        ManualAltitudeM = ManualAltitudeM,
        Radar = Radar.Clone(),
        Filter = Filter.Clone(),
        Comparison = Comparison.Clone(),
        RelativeIntervalMs = RelativeIntervalMs,
        StaleTimeoutMs = StaleTimeoutMs,
        FollowReferenceDrift = FollowReferenceDrift,
    };
}
