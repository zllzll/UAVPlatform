using System.Net;
using System.Net.Sockets;
using UavPlatform.Core.Models;

namespace UavPlatform.Core.Devices;

/// <summary>TCP 客户端链路：上位机主动连接设备（NSR 雷达默认方式，雷达作服务端）。</summary>
public sealed class TcpClientTransport : IDeviceTransport
{
    private readonly TransportSettings _settings;
    private TcpClient? _client;
    private NetworkStream? _stream;

    public TcpClientTransport(TransportSettings settings) => _settings = settings;

    public string Describe => _settings.Describe(TransportKind.TcpClient);

    public LinkState State { get; private set; } = LinkState.Disconnected;

    public event Action<string>? Faulted;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        State = LinkState.Connecting;
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(_settings.Host, _settings.Port, cancellationToken).ConfigureAwait(false);
            _client = client;
            _stream = client.GetStream();
            State = LinkState.Connected;
        }
        catch (Exception ex)
        {
            client.Dispose();
            State = LinkState.Faulted;
            Faulted?.Invoke($"TCP 连接 {_settings.Host}:{_settings.Port} 失败：{ex.Message}");
            throw;
        }
    }

    public Task DisconnectAsync()
    {
        try { _stream?.Dispose(); } catch { /* 忽略关闭异常 */ }
        try { _client?.Dispose(); } catch { /* 忽略关闭异常 */ }
        _stream = null;
        _client = null;
        State = LinkState.Disconnected;
        return Task.CompletedTask;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("TCP 链路未连接。");
        return stream.WriteAsync(data, cancellationToken);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var stream = _stream;
        if (stream is null) return ValueTask.FromResult(0);
        return TransportRead.ReadStreamAsync(stream, buffer, _settings.ReadTimeoutMs, cancellationToken);
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}

/// <summary>
/// TCP 服务端链路：上位机监听端口，等待设备（如无人机 wifi 模块）主动连入。
/// 监听器在 <see cref="DisconnectAsync"/> 之前一直存活，客户端断开后允许新的客户端接入。
/// </summary>
public sealed class TcpServerTransport : IDeviceTransport
{
    private readonly TransportSettings _settings;
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private int _clientCounter;

    public TcpServerTransport(TransportSettings settings) => _settings = settings;

    public string Describe => _settings.Describe(TransportKind.TcpServer);

    public LinkState State { get; private set; } = LinkState.Disconnected;

    /// <summary>当前已接入设备的远端地址。</summary>
    public string? RemoteEndPoint { get; private set; }

    public event Action<string>? Faulted;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            var address = TransportEnums.ResolveListenAddress(_settings.ListenAddress);
            _listener = new TcpListener(address, _settings.ListenPort);
            try
            {
                _listener.Start();
            }
            catch (Exception ex)
            {
                _listener = null;
                State = LinkState.Faulted;
                Faulted?.Invoke($"监听 {_settings.ListenAddress}:{_settings.ListenPort} 失败：{ex.Message}");
                throw;
            }
        }

        State = LinkState.Connecting;
        try
        {
            var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            client.NoDelay = true;
            _client = client;
            _stream = client.GetStream();
            RemoteEndPoint = client.Client.RemoteEndPoint?.ToString();
            Interlocked.Increment(ref _clientCounter);
            State = LinkState.Connected;
        }
        catch (OperationCanceledException)
        {
            State = LinkState.Disconnected;
            throw;
        }
        catch (Exception ex)
        {
            State = LinkState.Faulted;
            Faulted?.Invoke($"接受设备连接失败：{ex.Message}");
            throw;
        }
    }

    public Task DisconnectAsync()
    {
        try { _stream?.Dispose(); } catch { /* 忽略 */ }
        try { _client?.Dispose(); } catch { /* 忽略 */ }
        try { _listener?.Stop(); } catch { /* 忽略 */ }
        _stream = null;
        _client = null;
        _listener = null;
        RemoteEndPoint = null;
        State = LinkState.Disconnected;
        return Task.CompletedTask;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("尚无设备接入，无法发送。");
        return stream.WriteAsync(data, cancellationToken);
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var stream = _stream;
        if (stream is null) return ValueTask.FromResult(0);
        return TransportRead.ReadStreamAsync(stream, buffer, _settings.ReadTimeoutMs, cancellationToken);
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}

/// <summary>UDP 链路：收设备主动上报的报文（NSR 雷达 UDP 默认端口 8100），并可向远端回发。</summary>
public sealed class UdpTransport : IDeviceTransport
{
    private readonly TransportSettings _settings;
    private UdpClient? _udp;

    public UdpTransport(TransportSettings settings) => _settings = settings;

    public string Describe => _settings.Describe(TransportKind.Udp);

    public LinkState State { get; private set; } = LinkState.Disconnected;

    /// <summary>最近一次收到报文的来源地址（用于在未知远端时回发）。</summary>
    public IPEndPoint? LastRemote { get; private set; }

    public event Action<string>? Faulted;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        State = LinkState.Connecting;
        try
        {
            var local = new IPEndPoint(IPAddress.Any, _settings.LocalPort);
            _udp = new UdpClient(local);
            _udp.EnableBroadcast = _settings.UdpBroadcast;
            State = LinkState.Connected;
        }
        catch (Exception ex)
        {
            _udp = null;
            State = LinkState.Faulted;
            Faulted?.Invoke($"绑定 UDP 端口 {_settings.LocalPort} 失败：{ex.Message}");
            throw;
        }
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        try { _udp?.Dispose(); } catch { /* 忽略 */ }
        _udp = null;
        State = LinkState.Disconnected;
        return Task.CompletedTask;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var udp = _udp ?? throw new InvalidOperationException("UDP 链路未就绪。");
        var target = LastRemote ?? new IPEndPoint(IPAddress.Parse(_settings.Host), _settings.Port);
        await udp.SendAsync(data, target, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var udp = _udp;
        if (udp is null) return 0;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_settings.ReadTimeoutMs > 0) cts.CancelAfter(_settings.ReadTimeoutMs);
        try
        {
            var result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            LastRemote = result.RemoteEndPoint;
            var count = Math.Min(result.Buffer.Length, buffer.Length);
            result.Buffer.AsSpan(0, count).CopyTo(buffer.Span);
            return count;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TransportRead.Timeout;
        }
        catch (SocketException ex)
        {
            Faulted?.Invoke($"UDP 接收失败：{ex.Message}");
            return TransportRead.Timeout;
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}

/// <summary>串口链路：基座 UM982 默认方式（默认 460800 / 无校验 / 8 数据位 / 1 停止位）。</summary>
public sealed class SerialTransport : IDeviceTransport
{
    private readonly TransportSettings _settings;
    private System.IO.Ports.SerialPort? _port;

    public SerialTransport(TransportSettings settings) => _settings = settings;

    public string Describe => _settings.Describe(TransportKind.Serial);

    public LinkState State { get; private set; } = LinkState.Disconnected;

    public event Action<string>? Faulted;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        State = LinkState.Connecting;
        try
        {
            var port = new System.IO.Ports.SerialPort(_settings.SerialPort, _settings.BaudRate,
                TransportEnums.ToParity(_settings.Parity), _settings.DataBits, TransportEnums.ToStopBits(_settings.StopBits))
            {
                Handshake = TransportEnums.ToHandshake(_settings.Handshake),
                ReadTimeout = _settings.ReadTimeoutMs,
                WriteTimeout = 1000,
                DtrEnable = false,
                RtsEnable = false,
            };
            port.Open();
            _port = port;
            State = LinkState.Connected;
        }
        catch (Exception ex)
        {
            _port = null;
            State = LinkState.Faulted;
            Faulted?.Invoke($"打开串口 {_settings.SerialPort} 失败：{ex.Message}");
            throw;
        }
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        try { _port?.Close(); } catch { /* 忽略 */ }
        try { _port?.Dispose(); } catch { /* 忽略 */ }
        _port = null;
        State = LinkState.Disconnected;
        return Task.CompletedTask;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var port = _port ?? throw new InvalidOperationException("串口未打开。");
        return new ValueTask(Task.Run(() => port.BaseStream.Write(data.Span), cancellationToken));
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var port = _port;
        if (port is null) return ValueTask.FromResult(0);
        return TransportRead.ReadStreamAsync(port.BaseStream, buffer, _settings.ReadTimeoutMs, cancellationToken);
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}
