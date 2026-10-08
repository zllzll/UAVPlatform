using UavPlatform.Core.Models;

namespace UavPlatform.Core.Relative;

/// <summary>局部坐标参考点（显示坐标系原点所对应的 WGS84 位置）。</summary>
public sealed record GeoReference
{
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public double AltitudeM { get; init; }

    /// <summary>参考点来源：<c>BaseStation</c> / <c>Manual</c> / <c>Drone</c> / <c>Radar</c>。</summary>
    public string Source { get; init; } = "Manual";

    /// <summary>参考点是否已确定。未确定时相对位置结果不可用。</summary>
    public bool Resolved { get; init; }

    public bool IsZero => Latitude == 0 && Longitude == 0;
}

/// <summary>相对位置结果里的一个设备节点。</summary>
public sealed record RelativeNode
{
    /// <summary>设备显示名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>当前是否在线（链路已连接且最近有数据）。</summary>
    public bool Online { get; init; }

    /// <summary>参与相对位置的数据是否新鲜（在超时窗口内）。</summary>
    public bool Fresh { get; init; }

    /// <summary>显示坐标系下的位置（米）。基座为原点时基座恒为 0。</summary>
    public EnuPoint Position { get; init; }

    /// <summary>WGS84 纬度（度）。设备无定位时为 null。</summary>
    public double? Latitude { get; init; }

    /// <summary>WGS84 经度（度）。</summary>
    public double? Longitude { get; init; }

    /// <summary>海拔高（米）。</summary>
    public double? AltitudeM { get; init; }

    /// <summary>航向（度，0~360 罗盘方位，正北为 0）。</summary>
    public double? HeadingDeg { get; init; }

    /// <summary>定位质量：0 无效 / 1 单点 / 2 差分 / 4 RTK 固定 / 5 RTK 浮动。</summary>
    public int? FixQuality { get; init; }

    /// <summary>卫星数。</summary>
    public int? Satellites { get; init; }

    /// <summary>俯仰角（度，抬头为正），无人机姿态用。</summary>
    public double? PitchDeg { get; init; }

    /// <summary>横滚角（度，右倾为正），无人机姿态用。</summary>
    public double? RollDeg { get; init; }

    /// <summary>数据距今的毫秒数。</summary>
    public double AgeMs { get; init; }

    /// <summary>
    /// 上位机收到该设备这条数据时的**本机时间**（本地 PC 时钟）。
    /// 缺数据时为 null。落盘时与 <see cref="DeviceTime"/> 成对出现，供后续算法把三个设备的数据对齐到同一时间轴。
    /// </summary>
    public DateTimeOffset? PcTime { get; init; }

    /// <summary>该设备**自身给出的时间**（协议携带时），ISO 8601 字符串；设备不带时间时为 null。</summary>
    public string? DeviceTime { get; init; }

    /// <summary>该设备本次连接以来的帧序号。</summary>
    public long Sequence { get; init; }

    /// <summary>补充说明（例如「等待定位」「链路断开」）。</summary>
    public string? Note { get; init; }
}

/// <summary>相对位置结果里的一个雷达目标 / 点云点（已转换到显示坐标系）。</summary>
public sealed record RelativeTarget
{
    public uint Id { get; init; }
    public int Type { get; init; }
    public string TypeName { get; init; } = string.Empty;

    /// <summary>显示坐标系下的位置（米，东/北/天）。</summary>
    public double East { get; init; }
    public double North { get; init; }
    public double Up { get; init; }

    /// <summary>相对雷达的距离（米）。</summary>
    public double RangeM { get; init; }

    /// <summary>相对雷达的方位角（度）。</summary>
    public double AzimuthDeg { get; init; }

    /// <summary>相对雷达的俯仰角（度）。</summary>
    public double ElevationDeg { get; init; }

    public double Snr { get; init; }
    public double PeakEnergyDb { get; init; }
    public int AreaMask { get; init; }

    /// <summary>速度矢量（显示坐标系，m/s）。</summary>
    public double VelocityEast { get; init; }
    public double VelocityNorth { get; init; }
    public double VelocityUp { get; init; }

    /// <summary>合速度（m/s）。</summary>
    public double SpeedMps { get; init; }

    /// <summary>与无人机的三维距离（米），用于体现无人机相对目标的态势。</summary>
    public double? DistanceToDroneM { get; init; }

    /// <summary>与无人机的相对高度（米，目标减无人机）。</summary>
    public double? HeightAboveDroneM { get; init; }
}

/// <summary>
/// 无人机「雷达探测 vs RTK 定位」的对比结果：用无人机当运动靶标校核雷达探测精度。
/// 位置一律是显示坐标系（米，东/北/天），偏差定义为 <b>雷达 − RTK</b>。
/// </summary>
public sealed record DroneRadarComparison
{
    /// <summary>是否在匹配半径内找到了对应的雷达回波。</summary>
    public bool Matched { get; init; }

    /// <summary>本帧使用的匹配半径（米）。</summary>
    public double MatchRadiusM { get; init; }

    /// <summary>匹配到的雷达目标 id（未匹配时为 0）。</summary>
    public uint TargetId { get; init; }

    /// <summary>匹配到的雷达目标类型。</summary>
    public int TargetType { get; init; }

    /// <summary>匹配到的雷达目标类型名。</summary>
    public string TargetTypeName { get; init; } = string.Empty;

    /// <summary>雷达探测位置（显示坐标系，米）。</summary>
    public double RadarEast { get; init; }
    public double RadarNorth { get; init; }
    public double RadarUp { get; init; }

    /// <summary>无人机 RTK 上报位置（显示坐标系，米）。</summary>
    public double RtkEast { get; init; }
    public double RtkNorth { get; init; }
    public double RtkUp { get; init; }

    /// <summary>位置偏差（米，雷达 − RTK）。</summary>
    public double DeltaEastM { get; init; }
    public double DeltaNorthM { get; init; }
    public double DeltaUpM { get; init; }

    /// <summary>三维偏差（米）：雷达探测点与 RTK 点的直线距离，即本帧的探测误差。</summary>
    public double DeltaDistanceM { get; init; }

    /// <summary>水平偏差（米）。</summary>
    public double DeltaHorizontalM { get; init; }

    /// <summary>RTK 位置换算到雷达本体后的距离（米），与 <see cref="RadarRangeM"/> 直接可比。</summary>
    public double RtkRangeM { get; init; }

    /// <summary>RTK 位置换算到雷达本体后的方位角（度）。</summary>
    public double RtkAzimuthDeg { get; init; }

    /// <summary>RTK 位置换算到雷达本体后的俯仰角（度）。</summary>
    public double RtkElevationDeg { get; init; }

    /// <summary>雷达实测距离（米）。</summary>
    public double RadarRangeM { get; init; }

    /// <summary>雷达实测方位角（度）。</summary>
    public double RadarAzimuthDeg { get; init; }

    /// <summary>雷达实测俯仰角（度）。</summary>
    public double RadarElevationDeg { get; init; }

    /// <summary>距离偏差（米，雷达 − RTK）。</summary>
    public double DeltaRangeM { get; init; }

    /// <summary>方位角偏差（度，雷达 − RTK，归一化到 ±180）。</summary>
    public double DeltaAzimuthDeg { get; init; }

    /// <summary>俯仰角偏差（度，雷达 − RTK）。</summary>
    public double DeltaElevationDeg { get; init; }
}

/// <summary>
/// 一帧相对位置结果：把基座、雷达、无人机统一到同一个显示坐标系，并给出该时刻的平台态势。
/// 这是「相对位置数据」落盘与三维显示的唯一数据结构（只含解析结果，不含原始字节）。
/// </summary>
public sealed record RelativeFrame
{
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>相对位置帧序号（自增）。</summary>
    public long Sequence { get; init; }

    /// <summary>显示坐标系原点的设备名（本项目恒为基座）。</summary>
    public string OriginName { get; init; } = "基座";

    /// <summary>参考点是否已确定。</summary>
    public bool ReferenceResolved { get; init; }

    /// <summary>局部坐标参考点。</summary>
    public GeoReference Reference { get; init; } = new();

    /// <summary>基座节点。</summary>
    public RelativeNode BaseStation { get; init; } = new();

    /// <summary>雷达节点。</summary>
    public RelativeNode Radar { get; init; } = new();

    /// <summary>无人机节点。</summary>
    public RelativeNode Drone { get; init; } = new();

    /// <summary>雷达与基座之间的基线（米）。同址安装时为 0。</summary>
    public double RadarBaseLineM { get; init; }

    /// <summary>本帧实际用于目标换算的雷达正前方方位角（度，相对正北，顺时针为正）。</summary>
    public double RadarBoresightDeg { get; init; }

    /// <summary>该朝向是否由基座双天线基线航向推算得到（false 表示用了手动绝对角）。</summary>
    public bool RadarBoresightFromBaseline { get; init; }

    /// <summary>基座双天线基线航向（度），未收到 THS 定向时为 null。</summary>
    public double? RadarBaselineHeadingDeg { get; init; }

    /// <summary>本帧包含的目标 / 点云（已过滤已转换）。</summary>
    public RelativeTarget[] Targets { get; init; } = [];

    /// <summary>本帧是否来自点云传输。</summary>
    public bool IsPointCloud { get; init; }

    /// <summary>无人机与基座的水平距离（米），平台轨迹的关键指标。</summary>
    public double? DroneDistanceToBaseM { get; init; }

    /// <summary>无人机相对基座的高度差（米）。</summary>
    public double? DroneHeightAboveBaseM { get; init; }

    /// <summary>无人机相对雷达的距离（米）。</summary>
    public double? DroneDistanceToRadarM { get; init; }

    /// <summary>触发本帧的原因，便于排查。</summary>
    public string Trigger { get; init; } = string.Empty;

    /// <summary>各设备数据的时间差（毫秒），衡量相对位置的时间一致性。</summary>
    public double? BaseToDroneSkewMs { get; init; }

    /// <summary>无人机「雷达探测 vs RTK 定位」对比结果。未开启对比或缺数据时为 null。</summary>
    public DroneRadarComparison? DroneComparison { get; init; }
}

/// <summary>
/// 「雷达转换到基座系」的一条记录：一帧雷达回波里**每个目标**由雷达本体坐标换算到
/// **以基座为原点**的东北天（ENU）坐标系，直接可以和无人机 RTK 的基座系坐标做对齐分析。
/// </summary>
/// <remarks>
/// 与界面显示无关：本记录一律以基座为原点、不做朝向旋转，
/// 所以同一个目标在两次采集里的 east/north/up 是可比的。
/// 基座尚未定位时 <see cref="BaseResolved"/> 为 false，此时只保留雷达自身观测量
/// （rangeM / azimuthDeg / elevationDeg 与本体 x/y/z），直角坐标字段为 0，不丢数据。
/// </remarks>
public sealed record RadarToBaseFrame
{
    /// <summary>上位机收到这帧雷达数据的时间（本地 PC 时钟）。</summary>
    public DateTimeOffset PcTime { get; init; }

    /// <summary>雷达自带的时间（协议携带时），ISO 8601 字符串。</summary>
    public string? DeviceTime { get; init; }

    /// <summary>雷达自本次连接以来的帧序号。</summary>
    public long Sequence { get; init; }

    /// <summary>触发本记录的原因（device:Radar / sim:Radar）。</summary>
    public string Trigger { get; init; } = string.Empty;

    /// <summary>基座定位是否已知。false 时下面的基座系直角坐标不可用。</summary>
    public bool BaseResolved { get; init; }

    /// <summary>基座系原点（即基座的 WGS84 定位）。</summary>
    public GeoReference Origin { get; init; } = new();

    /// <summary>本帧换算所用的雷达正前方方位角（度，相对正北）。</summary>
    public double BoresightDeg { get; init; }

    /// <summary>该朝向是否由双天线基线航向（THS）+ 安装夹角实时解算得到。</summary>
    public bool BoresightFromBaseline { get; init; }

    /// <summary>基座双天线基线航向（度）；没有 THS 定向时为 null。</summary>
    public double? BaselineHeadingDeg { get; init; }

    /// <summary>雷达在基座系里的位置（米）。与基座共址时为 0。</summary>
    public double RadarEast { get; init; }
    public double RadarNorth { get; init; }
    public double RadarUp { get; init; }

    /// <summary>本帧是否来自点云传输（命令 0xA9）。</summary>
    public bool IsPointCloud { get; init; }

    /// <summary>触发本帧的命令码。</summary>
    public byte Command { get; init; }

    /// <summary>帧内声明的目标 / 点云个数（未经过滤，便于发现丢点）。</summary>
    public int DeclaredCount { get; init; }

    /// <summary>换算到基座系的目标列表（已按过滤配置筛选）。</summary>
    public RelativeTarget[] Targets { get; init; } = [];
}
