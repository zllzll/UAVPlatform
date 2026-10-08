using System.Net;
using System.Net.Sockets;
using UavPlatform.Core.Models;

namespace UavPlatform.Core.Devices;

/// <summary>
/// 设备链路抽象。统一采用「拉取式」读取模型（<see cref="ReadAsync"/>），
/// 使 TCP 客户端 / TCP 服务端 / UDP / 串口 四种链路对上层的解码循环完全一致。
/// </summary>
public interface IDeviceTransport : IAsyncDisposable
{
    /// <summary>链路描述（日志与界面显示）。</summary>
    string Describe { get; }

    /// <summary>当前链路状态。</summary>
    LinkState State { get; }

    /// <summary>建立链路。对 TCP 服务端而言是「开始监听并等待设备接入」。</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>断开链路并释放底层资源。</summary>
    Task DisconnectAsync();

    /// <summary>发送一段字节。</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// 读取一块数据。
    /// 返回值 &gt; 0 表示读到的字节数；0 表示对端已关闭（需重连）；−1 表示本次超时无数据（可继续读）。
    /// </summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>底层链路发生异常或对端关闭时触发（用于上层日志）。</summary>
    event Action<string>? Faulted;
}

/// <summary>链路读取辅助。</summary>
internal static class TransportRead
{
    public const int Timeout = -1;

    /// <summary>带超时地读取流；超时返回 −1，对端关闭返回 0。</summary>
    public static async ValueTask<int> ReadStreamAsync(Stream stream, Memory<byte> buffer, int timeoutMs, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutMs > 0) cts.CancelAfter(timeoutMs);
        try
        {
            return await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Timeout;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
    }
}

/// <summary>按配置构造链路实现。</summary>
public static class TransportFactory
{
    public static IDeviceTransport Create(DeviceConfig config) => config.Transport switch
    {
        TransportKind.TcpClient => new TcpClientTransport(config.TransportSettings),
        TransportKind.TcpServer => new TcpServerTransport(config.TransportSettings),
        TransportKind.Udp => new UdpTransport(config.TransportSettings),
        TransportKind.Serial => new SerialTransport(config.TransportSettings),
        _ => throw new NotSupportedException($"不支持的链路类型：{config.Transport}"),
    };
}

/// <summary>把配置里的字符串枚举安全转换为 BCL 枚举。</summary>
internal static class TransportEnums
{
    public static System.IO.Ports.Parity ToParity(string value) =>
        Enum.TryParse<System.IO.Ports.Parity>(value, ignoreCase: true, out var v) ? v : System.IO.Ports.Parity.None;

    public static System.IO.Ports.StopBits ToStopBits(string value)
    {
        if (string.Equals(value, "OnePointFive", StringComparison.OrdinalIgnoreCase)) return System.IO.Ports.StopBits.OnePointFive;
        return Enum.TryParse<System.IO.Ports.StopBits>(value, ignoreCase: true, out var v) ? v : System.IO.Ports.StopBits.One;
    }

    public static System.IO.Ports.Handshake ToHandshake(string value) =>
        Enum.TryParse<System.IO.Ports.Handshake>(value, ignoreCase: true, out var v) ? v : System.IO.Ports.Handshake.None;

    public static IPAddress ResolveListenAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return IPAddress.Any;
        if (string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
        return IPAddress.TryParse(value, out var ip) ? ip : IPAddress.Any;
    }
}
