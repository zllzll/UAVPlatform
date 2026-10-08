namespace UavPlatform.Core.Models;

/// <summary>本项目接入的三类设备。</summary>
public enum DeviceKind
{
    /// <summary>基座：NebulasIV 高精度产品（UM982 双天线 GNSS），串口 NMEA。</summary>
    BaseStation = 0,

    /// <summary>雷达：NSR 系列（湖南纳雷 V1.2.9 协议），TCP。</summary>
    Radar = 1,

    /// <summary>无人机载 wifi 模块：UCM221 通用协议上行数据流，输出 GPS / 姿态。</summary>
    DroneGps = 2,
}

/// <summary>物理链路类型。三个设备均可配置为其中任意一种，以适配现场接线差异。</summary>
public enum TransportKind
{
    /// <summary>TCP 客户端：上位机主动连接设备（NSR 雷达默认方式，雷达作服务端）。</summary>
    TcpClient = 0,

    /// <summary>TCP 服务端：上位机监听端口，等待设备连入（wifi 模块常见方式）。</summary>
    TcpServer = 1,

    /// <summary>UDP：收设备主动上报的报文，亦可回发（NSR 雷达 UDP 默认端口 8100）。</summary>
    Udp = 2,

    /// <summary>串口：基座 UM982 默认方式（默认 460800，无校验，1 停止位）。</summary>
    Serial = 3,
}

/// <summary>链路连接状态。</summary>
public enum LinkState
{
    Disabled = 0,
    Disconnected = 1,
    Connecting = 2,
    Connected = 3,
    Faulted = 4,
}

/// <summary>原始数据的落盘格式。</summary>
public enum RawFormat
{
    /// <summary>二进制：紧凑、无损，按 <c>[int64 ticks][int32 len][bytes]</c> 记录。</summary>
    Binary = 0,

    /// <summary>十六进制文本：一行一条记录，人可读、可 grep。</summary>
    HexText = 1,

    /// <summary>纯文本：仅适用于 NMEA 这类本身就是文本的协议。</summary>
    Text = 2,
}
