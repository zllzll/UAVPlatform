using System.Text.Json.Serialization;

namespace UavPlatform.Core.Models;

/// <summary>WGS84 定位结果。基座（UM982）与无人机（UCM221）共用。</summary>
public sealed record GnssFix
{
    /// <summary>纬度（度，WGS84，北正南负）。</summary>
    public double Latitude { get; init; }

    /// <summary>经度（度，WGS84，东正西负）。</summary>
    public double Longitude { get; init; }

    /// <summary>海拔高度（米，WGS84 椭球高或海拔，取决于设备输出）。</summary>
    public double? AltitudeM { get; init; }

    /// <summary>对地速度（m/s）。</summary>
    public double? SpeedMps { get; init; }

    /// <summary>对地航向（度，0~360，正北为 0，顺时针）。</summary>
    public double? CourseDeg { get; init; }

    /// <summary>可见/参与定位卫星数。</summary>
    public int? Satellites { get; init; }

    /// <summary>定位质量：0 无效 / 1 单点 / 2 差分 / 4 RTK 固定 / 5 RTK 浮动（GGA 定义）。</summary>
    public int? FixQuality { get; init; }

    /// <summary>水平精度因子。</summary>
    public double? Hdop { get; init; }

    /// <summary>大地水准面差距（米）。</summary>
    public double? GeoidSeparationM { get; init; }

    /// <summary>磁偏角（度，东偏为正）。</summary>
    public double? MagneticVariationDeg { get; init; }

    /// <summary>定位是否有效（RMC 状态为 A，或 GGA 质量 &gt; 0）。</summary>
    public bool Valid { get; init; }

    /// <summary>设备给出的 UTC 时间。</summary>
    public DateTimeOffset? UtcTime { get; init; }
}

/// <summary>无人机姿态与速度（UCM221 无人机信息模块 0xA22A）。</summary>
public sealed record UavAttitude
{
    /// <summary>横滚角（弧度，向右为正）。</summary>
    public double? RollRad { get; init; }

    /// <summary>俯仰角（弧度，向上为正）。</summary>
    public double? PitchRad { get; init; }

    /// <summary>航向角（弧度，顺时针为正）。</summary>
    public double? HeadingRad { get; init; }

    /// <summary>朝北飞行速度（m/s）。</summary>
    public double? VelocityNorthMps { get; init; }

    /// <summary>朝东飞行速度（m/s）。</summary>
    public double? VelocityEastMps { get; init; }

    /// <summary>朝下飞行速度（m/s）。</summary>
    public double? VelocityDownMps { get; init; }

    /// <summary>相对地面高度（米）。</summary>
    public double? RelativeAltitudeM { get; init; }
}

/// <summary>IMU 原始量（UCM221 扩展信息模块 0xA33A）。</summary>
public sealed record ImuSample
{
    /// <summary>加速度组 1（m/s²），顺序 X、Y、Z。</summary>
    public double[]? Accel1 { get; init; }

    /// <summary>角速度组 1（rad/s），顺序 X、Y、Z。</summary>
    public double[]? Gyro1 { get; init; }

    /// <summary>加速度组 2（m/s²），顺序 X、Y、Z。</summary>
    public double[]? Accel2 { get; init; }

    /// <summary>角速度组 2（rad/s），顺序 X、Y、Z。</summary>
    public double[]? Gyro2 { get; init; }
}

/// <summary>
/// 雷达探测到的单个目标 / 点云点。坐标为**雷达自身坐标系**：雷达位于原点，探测方向为 Y 轴正向，单位米。
/// </summary>
public sealed record RadarTarget
{
    /// <summary>目标 ID。</summary>
    public uint Id { get; init; }

    /// <summary>目标类型原始值。目标协议：0 未识别 / 1 人 / 2 车 / 3 树 / 4 船 / 0xFFFF 已删除；点云协议另含 6 小船 / 7 中船 / 8 大船。</summary>
    public int Type { get; init; }

    /// <summary>X 方向速度（m/s）。</summary>
    public double SpeedX { get; init; }

    /// <summary>Y 方向速度（m/s）。</summary>
    public double SpeedY { get; init; }

    /// <summary>Z 方向速度（m/s）。</summary>
    public double SpeedZ { get; init; }

    /// <summary>X 方向坐标（米，雷达坐标系）。</summary>
    public double X { get; init; }

    /// <summary>Y 方向坐标（米，雷达坐标系，探测方向）。</summary>
    public double Y { get; init; }

    /// <summary>Z 方向坐标（米，雷达坐标系）。</summary>
    public double Z { get; init; }

    /// <summary>测量距离（米）。</summary>
    public double Range { get; init; }

    /// <summary>方位角（度，−90~90）。</summary>
    public double AzimuthDeg { get; init; }

    /// <summary>俯仰角（度，−90~90）。</summary>
    public double ElevationDeg { get; init; }

    /// <summary>信噪比。</summary>
    public double Snr { get; init; }

    /// <summary>峰值能量（能量幅值的 dB 值）。</summary>
    public double PeakEnergyDb { get; init; }

    /// <summary>报警区域位掩码，共 16 bit，bit0 = 第一防区。</summary>
    public int AreaMask { get; init; }

    /// <summary>目标类型的中文标注。</summary>
    [JsonIgnore]
    public string TypeName => RadarTargetTypes.Describe(Type);

    /// <summary>合速度（m/s）。</summary>
    [JsonIgnore]
    public double SpeedMps => Math.Sqrt(SpeedX * SpeedX + SpeedY * SpeedY + SpeedZ * SpeedZ);
}

/// <summary>雷达目标类型描述表（协议表 4-19 与表 4-22 合并）。</summary>
public static class RadarTargetTypes
{
    public const int Unknown = 0;
    public const int Person = 1;
    public const int Vehicle = 2;
    public const int Tree = 3;
    public const int Boat = 4;

    /// <summary>空中目标（协议表 4-19 中 0x05）——无人机在雷达眼里通常归到这一类。</summary>
    public const int Air = 5;

    public const int SmallBoat = 6;
    public const int MediumBoat = 7;
    public const int LargeBoat = 8;

    /// <summary>已删除目标的类型值（目标协议）。</summary>
    public const int Deleted = 0xFFFF;

    public static string Describe(int type) => type switch
    {
        0x00 => "未识别",
        0x01 => "人",
        0x02 => "车",
        0x03 => "树",
        0x04 => "船",
        0x05 => "空",
        0x06 => "小船",
        0x07 => "中船",
        0x08 => "大船",
        0xFFFF => "已删除",
        _ => $"类型{type}",
    };
}

/// <summary>所有设备解析结果样本的基类。</summary>
public abstract record DeviceSample
{
    /// <summary>来源设备。</summary>
    public DeviceKind Device { get; init; }

    /// <summary>上位机接收该数据的时间戳（本地时钟）。</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>该设备的自增序号。</summary>
    public long Sequence { get; init; }

    /// <summary>设备自身给出的时间（若协议携带），ISO 8601 字符串。</summary>
    public string? DeviceTime { get; init; }

    /// <summary>该设备的显示名（由运行时填充，便于相对位置与界面识别）。</summary>
    public string? DeviceName { get; init; }
}

/// <summary>基座（UM982）解析结果：一次 NMEA 汇聚后的定位状态。</summary>
public sealed record GnssSample : DeviceSample
{
    /// <summary>定位结果。</summary>
    public GnssFix Fix { get; init; } = new();

    /// <summary>真航向（度，来自 THS 双天线解算；无 THS 时回退 RMC/VTG 航向）。</summary>
    public double? TrueHeadingDeg { get; init; }

    /// <summary>航向来源：THS / RMC / VTG。</summary>
    public string? HeadingSource { get; init; }

    /// <summary>本次触发汇聚的原始 NMEA 语句（逐条）。</summary>
    public string[] Sentences { get; init; } = [];
}

/// <summary>雷达（NSR）解析结果：一帧目标或点云。</summary>
public sealed record RadarSample : DeviceSample
{
    /// <summary>true 表示来自点云传输（0xA9），false 表示目标信息传输（0xA8）。</summary>
    public bool IsPointCloud { get; init; }

    /// <summary>触发本帧的命令码。</summary>
    public byte Command { get; init; }

    /// <summary>雷达地址编码。</summary>
    public byte SourceAddress { get; init; }

    /// <summary>本帧目标 / 点云列表（雷达坐标系）。</summary>
    public RadarTarget[] Targets { get; init; } = [];

    /// <summary>本帧声明的目标个数。</summary>
    public int DeclaredCount { get; init; }
}

/// <summary>雷达通用应答（0xA2）解析结果。</summary>
public sealed record RadarAck
{
    /// <summary>被响应的命令码。</summary>
    public byte Command { get; init; }

    /// <summary>true = 0x0F 成功，false = 0xF0 不成功。</summary>
    public bool Success { get; init; }
}

/// <summary>无人机 wifi 模块（UCM221）解析结果。</summary>
public sealed record DroneGpsSample : DeviceSample
{
    /// <summary>无人机 GPS 位置（来自扩展信息 0xA33A 或无人机信息 0xA22A，后者优先）。</summary>
    public GnssFix? Fix { get; init; }

    /// <summary>无人机姿态与速度（0xA22A）。</summary>
    public UavAttitude? Attitude { get; init; }

    /// <summary>IMU（0xA33A）。</summary>
    public ImuSample? Imu { get; init; }

    /// <summary>GPS 对地速度（m/s，来自扩展信息的 GPS 速度字段）。</summary>
    public double? GpsSpeedMps { get; init; }

    /// <summary>GPS 磁偏角（度，东偏为正）。</summary>
    public double? MagneticDeclinationDeg { get; init; }

    /// <summary>汇总信息中的数据包状态位掩码。</summary>
    public int PacketStatus { get; init; }

    /// <summary>设备 ID（与设备 IP 相同）。</summary>
    public int DeviceId { get; init; }

    /// <summary>本帧包含的目标个数（本项目不解析内容，仅记录）。</summary>
    public int TargetCount { get; init; }

    /// <summary>本帧包含的点云个数（本项目不解析内容，仅记录）。</summary>
    public int PointCloudCount { get; init; }
}
