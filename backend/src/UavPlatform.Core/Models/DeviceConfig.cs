namespace UavPlatform.Core.Models;

/// <summary>
/// 链路参数。三个设备共用同一套可配置项，按 <see cref="TransportKind"/> 取用其中一部分。
/// </summary>
public sealed class TransportSettings
{
    // ---- TCP 客户端 / UDP 远端 ----
    /// <summary>目标主机（TCP 客户端）或远端地址（UDP）。</summary>
    public string Host { get; set; } = "192.168.10.128";

    /// <summary>目标端口。NSR 雷达 TCP 默认 50000。</summary>
    public int Port { get; set; } = 50000;

    // ---- TCP 服务端 ----
    /// <summary>监听地址（TCP 服务端）。</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>监听端口（TCP 服务端）。</summary>
    public int ListenPort { get; set; } = 50000;

    // ---- UDP ----
    /// <summary>UDP 本地绑定端口。NSR 雷达 UDP 默认 8100。</summary>
    public int LocalPort { get; set; } = 8100;

    /// <summary>UDP 是否允许广播发送。</summary>
    public bool UdpBroadcast { get; set; }

    // ---- 串口 ----
    /// <summary>串口名，例如 COM3。</summary>
    public string SerialPort { get; set; } = "COM3";

    /// <summary>波特率。基座 UM982 默认 460800；NSR 雷达串口默认 115200。</summary>
    public int BaudRate { get; set; } = 460800;

    /// <summary>数据位。</summary>
    public int DataBits { get; set; } = 8;

    /// <summary>校验位：None / Odd / Even / Mark / Space。</summary>
    public string Parity { get; set; } = "None";

    /// <summary>停止位：One / Two / OnePointFive。</summary>
    public string StopBits { get; set; } = "One";

    /// <summary>流控：None / XOnXOff / RequestToSend / RequestToSendXOnXOff。</summary>
    public string Handshake { get; set; } = "None";

    /// <summary>读超时（毫秒）。</summary>
    public int ReadTimeoutMs { get; set; } = 500;

    /// <summary>返回一份深拷贝，便于配置快照与比较。</summary>
    public TransportSettings Clone() => (TransportSettings)MemberwiseClone();

    /// <summary>生成用于日志与界面的链路描述字符串。</summary>
    public string Describe(TransportKind kind) => kind switch
    {
        TransportKind.TcpClient => $"TCP 客户端 {Host}:{Port}",
        TransportKind.TcpServer => $"TCP 服务端 监听 {ListenAddress}:{ListenPort}",
        TransportKind.Udp => $"UDP 本地 {LocalPort}（远端 {Host}:{Port}{(UdpBroadcast ? " 广播" : "")}）",
        TransportKind.Serial => $"串口 {SerialPort} @ {BaudRate} {DataBits}{Parity[0]}{StopBitsShort(StopBits)}",
        _ => kind.ToString(),
    };

    private static string StopBitsShort(string stopBits) => stopBits switch
    {
        "Two" => "2",
        "OnePointFive" => "1.5",
        _ => "1",
    };
}

/// <summary>雷达专属协议参数。</summary>
public sealed class RadarProtocolSettings
{
    /// <summary>上位机（PC）地址编码，协议 §6.1 规定为 0x10。</summary>
    public byte LocalAddress { get; set; } = 0x10;

    /// <summary>雷达地址编码。0xFF 表示广播；接入前未知时先用广播，收到帧后自动学习源地址。</summary>
    public byte RadarAddress { get; set; } = 0xFF;

    /// <summary>目标输出形态：Auto 表示按命令码自动识别（0xA8 目标 / 0xA9 点云）。</summary>
    public RadarOutputMode OutputMode { get; set; } = RadarOutputMode.Auto;

    /// <summary>是否在连接后主动发送心跳包（0xA4）。</summary>
    public bool SendHeartbeat { get; set; } = true;

    /// <summary>心跳间隔秒数（协议 §4.13，0~255，默认 5）。</summary>
    public int HeartbeatIntervalSec { get; set; } = 5;

    /// <summary>是否在连接后读取雷达状态（0x0A），用于获取雷达型号与固件版本。</summary>
    public bool QueryStatusOnConnect { get; set; } = true;

    /// <summary>是否把收到的通用应答（0xA2）解析为命令结果。</summary>
    public bool ParseAcks { get; set; } = true;

    public RadarProtocolSettings Clone() => (RadarProtocolSettings)MemberwiseClone();
}

/// <summary>雷达目标输出形态。</summary>
public enum RadarOutputMode
{
    /// <summary>自动识别：0xA8 按目标解析，0xA9 按点云解析。</summary>
    Auto = 0,

    /// <summary>强制按目标信息（0xA8）解析。</summary>
    Targets = 1,

    /// <summary>强制按点云（0xA9）解析。</summary>
    PointCloud = 2,
}

/// <summary>UCM221（无人机 wifi 模块）专属协议参数。</summary>
public sealed class Ucm221ProtocolSettings
{
    /// <summary>
    /// 上传流是否按大端解析。协议原文写「大端」，但参考项目真机实测为小端，
    /// 因此默认 false（小端）；若现场固件确为大端可打开。
    /// </summary>
    public bool BigEndian { get; set; }

    /// <summary>是否解析扩展信息模块（0xA33A，含 GPS）。</summary>
    public bool ParseExtendedInfo { get; set; } = true;

    /// <summary>是否解析无人机信息模块（0xA22A，含 GPS 位置与姿态）。</summary>
    public bool ParseUavInfo { get; set; } = true;

    public Ucm221ProtocolSettings Clone() => (Ucm221ProtocolSettings)MemberwiseClone();
}

/// <summary>基座 UM982 专属协议参数。</summary>
public sealed class Um982ProtocolSettings
{
    /// <summary>是否解析 GGA（定位质量、卫星数、海拔）。</summary>
    public bool ParseGga { get; set; } = true;

    /// <summary>是否解析 RMC（速度、航向、UTC 时间）。</summary>
    public bool ParseRmc { get; set; } = true;

    /// <summary>是否解析 VTG（对地速度、航向）。</summary>
    public bool ParseVtg { get; set; } = true;

    /// <summary>是否解析 THS（双天线真航向，优先级高于 RMC/VTG 的航向）。</summary>
    public bool ParseThs { get; set; } = true;

    /// <summary>RMC 的航向（course）是否作为航向来源。THS 存在且新鲜时 THS 优先。</summary>
    public bool RmcCourseAsHeading { get; set; } = true;

    /// <summary>NMEA 校验和错误时是否丢弃该句。</summary>
    public bool RequireChecksum { get; set; } = true;

    // ---- 依据 um982_driver v1.0.0 源码确定的初始化与看门狗行为 ----

    /// <summary>
    /// 连接后是否下发 UM982 输出配置命令
    /// （<c>gpgga/gprmc/gpvtg/gpths com1 &lt;间隔&gt;</c> + <c>saveconfig</c>，命令间 100 ms）。
    /// 打开后无需用上位机工具预先配置模块即可收到 GGA/RMC/VTG/THS。
    /// </summary>
    public bool SendInitCommands { get; set; } = true;

    /// <summary>输出间隔（秒）。驱动默认 <c>0.05</c> 即 20 Hz。</summary>
    public double OutputIntervalSec { get; set; } = 0.05;

    /// <summary>是否启用看门狗：GGA 或 RMC 超过 <see cref="WatchdogTimeoutSec"/> 未更新则重发配置命令。</summary>
    public bool EnableWatchdog { get; set; } = true;

    /// <summary>看门狗判定失联的秒数（驱动默认 5）。</summary>
    public int WatchdogTimeoutSec { get; set; } = 5;

    /// <summary>两次重发配置命令之间的最小间隔（秒），防止重发风暴。</summary>
    public int WatchdogCooldownSec { get; set; } = 5;

    /// <summary>是否解析 THS 真航向（需双天线；单天线时模块不输出该语句）。</summary>
    public bool UseTrueHeading { get; set; } = true;

    public Um982ProtocolSettings Clone() => (Um982ProtocolSettings)MemberwiseClone();
}

/// <summary>单个设备的完整配置。</summary>
public sealed class DeviceConfig
{
    public DeviceKind Kind { get; set; }

    /// <summary>显示名称，可自定义。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>是否启用该设备（禁用则不建立链路）。</summary>
    public bool Enabled { get; set; } = true;

    public TransportKind Transport { get; set; } = TransportKind.TcpClient;

    public TransportSettings TransportSettings { get; set; } = new();

    public RadarProtocolSettings Radar { get; set; } = new();

    public Ucm221ProtocolSettings Ucm221 { get; set; } = new();

    public Um982ProtocolSettings Um982 { get; set; } = new();

    /// <summary>断线是否自动重连。</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>重连间隔（毫秒）。</summary>
    public int ReconnectDelayMs { get; set; } = 3000;

    /// <summary>该设备的原始数据是否落盘（受存储总开关约束）。</summary>
    public bool SaveRaw { get; set; } = true;

    /// <summary>该设备的解析数据是否落盘（受存储总开关约束）。</summary>
    public bool SaveParsed { get; set; } = true;

    /// <summary>链路描述（日志/界面用）。</summary>
    public string LinkDescription => TransportSettings.Describe(Transport);

    public DeviceConfig Clone() => new()
    {
        Kind = Kind,
        Name = Name,
        Enabled = Enabled,
        Transport = Transport,
        TransportSettings = TransportSettings.Clone(),
        Radar = Radar.Clone(),
        Ucm221 = Ucm221.Clone(),
        Um982 = Um982.Clone(),
        AutoReconnect = AutoReconnect,
        ReconnectDelayMs = ReconnectDelayMs,
        SaveRaw = SaveRaw,
        SaveParsed = SaveParsed,
    };

    /// <summary>三类设备的默认配置（现场可直接改，无需从零填）。</summary>
    public static DeviceConfig CreateDefault(DeviceKind kind) => kind switch
    {
        DeviceKind.BaseStation => new DeviceConfig
        {
            Kind = kind,
            Name = "基座(UM982)",
            Transport = TransportKind.Serial,
            TransportSettings = new TransportSettings
            {
                SerialPort = "COM3",
                BaudRate = 460800, // UM982 驱动默认 460800
                DataBits = 8,
                Parity = "None",
                StopBits = "One",
            },
        },
        DeviceKind.Radar => new DeviceConfig
        {
            Kind = kind,
            Name = "雷达(NSR)",
            Transport = TransportKind.TcpClient,
            TransportSettings = new TransportSettings
            {
                Host = "192.168.10.128", // 参考项目真机地址
                Port = 50000,            // 协议默认端口，雷达作服务端
            },
        },
        DeviceKind.DroneGps => new DeviceConfig
        {
            Kind = kind,
            Name = "无人机 GPS(UCM221)",
            // 无人机用无线链路回传，走 UDP：地面站绑本地端口收无人机主动上报的报文。
            Transport = TransportKind.Udp,
            TransportSettings = new TransportSettings
            {
                ListenAddress = "0.0.0.0",
                LocalPort = 8100,
            },
        },
        _ => new DeviceConfig { Kind = kind, Name = kind.ToString() },
    };
}
