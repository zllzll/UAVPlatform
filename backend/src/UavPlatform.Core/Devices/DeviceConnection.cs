using System.Text;
using UavPlatform.Core.Models;
using UavPlatform.Core.Protocols;

namespace UavPlatform.Core.Devices;

/// <summary>一段原始字节及其接收时刻（原样保存，不做任何加工）。</summary>
public readonly record struct RawSegment(DateTimeOffset Timestamp, byte[] Data);

/// <summary>设备链路的运行统计。</summary>
public sealed class DeviceStatistics
{
    /// <summary>累计接收字节数。</summary>
    public long BytesReceived { get; internal set; }

    /// <summary>累计切出的完整帧数。</summary>
    public long FramesReceived { get; internal set; }

    /// <summary>解析失败次数。</summary>
    public long ParseErrors { get; internal set; }

    /// <summary>为重新同步而丢弃的字节数（坏帧 / 噪声）。</summary>
    public long SkippedBytes { get; internal set; }

    /// <summary>产出的解析样本数。</summary>
    public long Samples { get; internal set; }

    /// <summary>累计目标 / 点云条数。</summary>
    public long TargetCount { get; internal set; }

    /// <summary>最近一次收到数据的时刻。</summary>
    public DateTimeOffset? LastDataAt { get; internal set; }

    /// <summary>最近一次收到数据距今的毫秒数。</summary>
    public double IdleMs => LastDataAt.HasValue ? (DateTimeOffset.Now - LastDataAt.Value).TotalMilliseconds : double.NaN;

    /// <summary>累计重连次数。</summary>
    public long Reconnects { get; internal set; }

    /// <summary>清零统计（重连后按需调用）。</summary>
    public void Reset()
    {
        BytesReceived = 0;
        FramesReceived = 0;
        ParseErrors = 0;
        SkippedBytes = 0;
        Samples = 0;
        TargetCount = 0;
    }
}

/// <summary>
/// 单台设备的运行实例：链路（<see cref="IDeviceTransport"/>）+ 协议（<see cref="DeviceProtocol"/>）
/// + 解码循环 + 自动重连 + 设备专属的初始化与看门狗。
/// </summary>
/// <remarks>
/// 解码循环模型：<c>ReadAsync → 原始数据落点 → 入 ByteQueue → Scan 切帧 → Parse 出样本</c>。
/// 粘包 / 分包 / 残帧 / 坏帧重同步全部由 <see cref="DeviceProtocol.Scan"/> 承担。
/// </remarks>
public sealed class DeviceConnection : IAsyncDisposable
{
    private readonly DeviceConfig _config;
    private readonly DeviceProtocol _protocol;
    private readonly NsrRadarProtocol? _radarProtocol;
    private readonly Ucm221Protocol? _ucm221Protocol;
    private readonly NmeaProtocol? _nmeaProtocol;
    private readonly NmeaAggregator? _nmeaAggregator;
    private readonly ByteQueue _queue = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _stateLock = new();

    private IDeviceTransport? _transport;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _sequence;

    /// <summary>最近一次收到有效 GGA 的时刻（单调计时，用于看门狗）。</summary>
    private long _lastGgaTicks;

    /// <summary>最近一次收到有效 RMC 的时刻（单调计时，用于看门狗）。</summary>
    private long _lastRmcTicks;

    /// <summary>最近一次下发配置命令的时刻（单调计时，用于看门狗冷却）。</summary>
    private long _lastConfigSentTicks;

    public DeviceConnection(DeviceConfig config)
    {
        _config = config;
        _protocol = CreateProtocol(config);

        switch (_protocol)
        {
            case NsrRadarProtocol radar:
                _radarProtocol = radar;
                break;
            case Ucm221Protocol ucm:
                _ucm221Protocol = ucm;
                break;
            case NmeaProtocol nmea:
                _nmeaProtocol = nmea;
                _nmeaAggregator = new NmeaAggregator(config.Um982);
                break;
        }
    }

    /// <summary>本连接的配置快照（外部修改配置后需重建连接）。</summary>
    public DeviceConfig Config => _config;

    /// <summary>设备类别。</summary>
    public DeviceKind Kind => _config.Kind;

    /// <summary>设备显示名。</summary>
    public string Name => string.IsNullOrWhiteSpace(_config.Name) ? _config.Kind.ToString() : _config.Name;

    /// <summary>协议名称。</summary>
    public string ProtocolName => _protocol.Name;

    /// <summary>链路描述。</summary>
    public string Describe => _transport?.Describe ?? _config.LinkDescription;

    /// <summary>当前链路状态。</summary>
    public LinkState State { get; private set; } = LinkState.Disconnected;

    /// <summary>最近一次链路错误。</summary>
    public string? LastError { get; private set; }

    /// <summary>运行统计。</summary>
    public DeviceStatistics Statistics { get; } = new();

    /// <summary>TCP 服务端模式下当前接入的远端地址。</summary>
    public string? RemoteEndPoint => (_transport as TcpServerTransport)?.RemoteEndPoint
                                     ?? (_transport as UdpTransport)?.LastRemote?.ToString();

    /// <summary>收到原始字节（每次底层读取触发一次，字节原样）。</summary>
    public event Action<DeviceConnection, RawSegment>? RawReceived;

    /// <summary>解析出一个样本（附带触发它的原始帧字节）。</summary>
    public event Action<DeviceConnection, DeviceSample, byte[]?>? SampleReceived;

    /// <summary>解析出一个雷达通用应答。</summary>
    public event Action<DeviceConnection, RadarAck>? AckReceived;

    /// <summary>链路状态或错误信息变化。</summary>
    public event Action<DeviceConnection>? StateChanged;

    /// <summary>日志。</summary>
    public event Action<DeviceConnection, string>? Log;

    /// <summary>启动连接（含自动重连）。立即返回，循环在后台运行。</summary>
    public void Start()
    {
        if (_loop is { IsCompleted: false }) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    /// <summary>停止连接并关闭链路。</summary>
    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts is not null)
        {
            try { await cts.CancelAsync().ConfigureAwait(false); } catch { /* 已释放 */ }
        }

        var loop = _loop;
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch { /* 循环内部已兜底 */ }
        }

        _loop = null;
        cts?.Dispose();
        _cts = null;

        var transport = _transport;
        _transport = null;
        if (transport is not null)
        {
            try { await transport.DisposeAsync().ConfigureAwait(false); } catch { /* 忽略关闭异常 */ }
        }

        SetState(LinkState.Disconnected, null);
    }

    /// <summary>发送一段数据（自动串行化，避免多线程交错写）。</summary>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var transport = _transport;
        if (transport is null || State != LinkState.Connected)
        {
            throw new InvalidOperationException($"{Name} 未连接，无法发送数据。");
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await transport.SendAsync(data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>发送一条 ASCII 文本命令（自动补 CRLF）。</summary>
    public ValueTask SendTextAsync(string text, CancellationToken cancellationToken = default) =>
        SendAsync(Encoding.ASCII.GetBytes(text + "\r\n"), cancellationToken);

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // ------------------------------------------------------------------ 解码循环

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SetState(LinkState.Connecting, null);
                _transport = TransportFactory.Create(_config);
                _transport.Faulted += OnTransportFaulted;

                await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);

                _queue.Clear();
                Interlocked.Exchange(ref _lastGgaTicks, 0);
                Interlocked.Exchange(ref _lastRmcTicks, 0);
                Interlocked.Exchange(ref _lastConfigSentTicks, 0);
                _nmeaAggregator?.Reset();

                SetState(LinkState.Connected, null);
                LogMessage($"已连接：{_transport.Describe}");

                await OnConnectedAsync(cancellationToken).ConfigureAwait(false);
                await ReadLoopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetState(LinkState.Faulted, ex.Message);
                LogMessage($"链路异常：{ex.Message}");
            }
            finally
            {
                await CloseTransportAsync().ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested || !_config.AutoReconnect) break;

            SetState(LinkState.Disconnected, LastError);
            Statistics.Reconnects++;
            try
            {
                await Task.Delay(Math.Max(200, _config.ReconnectDelayMs), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        SetState(LinkState.Disconnected, LastError);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var transport = _transport!;
        var buffer = new byte[64 * 1024];

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await transport.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                // 对端关闭：交由上层重连（TCP 服务端会重新 accept，串口会重开）
                if (State == LinkState.Connected)
                {
                    SetState(LinkState.Disconnected, "对端已关闭连接");
                    LogMessage("对端已关闭连接");
                }
                return;
            }

            if (read < 0)
            {
                // 本次读取超时：借这个节拍做看门狗与心跳
                await OnIdleTickAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            var now = DateTimeOffset.Now;
            var chunk = buffer.AsSpan(0, read).ToArray();

            Statistics.BytesReceived += read;
            Statistics.LastDataAt = now;
            RawReceived?.Invoke(this, new RawSegment(now, chunk));

            _queue.Append(chunk);
            DrainQueue(cancellationToken);
        }
    }

    /// <summary>把队列里所有完整帧处理掉。</summary>
    private void DrainQueue(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _queue.Count > 0)
        {
            var scan = _protocol.Scan(_queue.Span);

            switch (scan.Status)
            {
                case FrameScanStatus.NeedMore:
                    return;

                case FrameScanStatus.Skip:
                    Statistics.SkippedBytes += scan.Length;
                    _queue.Consume(scan.Length);
                    continue;

                case FrameScanStatus.Frame:
                    ProcessFrame(_queue.Take(scan.Length));
                    continue;

                default:
                    return;
            }
        }
    }

    /// <summary>解析一帧。</summary>
    private void ProcessFrame(byte[] frame)
    {
        Statistics.FramesReceived++;

        try
        {
            switch (_protocol)
            {
                case NsrRadarProtocol radar:
                    ProcessRadarFrame(radar, frame);
                    break;

                case Ucm221Protocol ucm:
                    ProcessUcm221Frame(ucm, frame);
                    break;

                case NmeaProtocol nmea:
                    ProcessNmeaLine(nmea, frame);
                    break;
            }
        }
        catch (Exception ex)
        {
            Statistics.ParseErrors++;
            LogMessage($"解析失败：{ex.Message}");
        }
    }

    private void ProcessRadarFrame(NsrRadarProtocol radar, byte[] frame)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var result = radar.Parse(frame, _config.Kind, Name, sequence);

        switch (result.Kind)
        {
            case RadarFrameKind.Targets:
            case RadarFrameKind.PointCloud:
            {
                var sample = new RadarSample
                {
                    Device = _config.Kind,
                    DeviceName = Name,
                    Timestamp = DateTimeOffset.Now,
                    Sequence = sequence,
                    IsPointCloud = result.IsPointCloud,
                    Command = result.Command,
                    SourceAddress = result.SourceAddress,
                    Targets = result.Targets,
                    DeclaredCount = result.DeclaredCount,
                };
                Statistics.Samples++;
                Statistics.TargetCount += result.Targets.Length;
                SampleReceived?.Invoke(this, sample, frame);
                break;
            }

            case RadarFrameKind.Ack when result.Ack is { } ack:
                AckReceived?.Invoke(this, ack);
                break;

            case RadarFrameKind.Heartbeat:
                // 雷达主动发心跳：按协议回一条通用应答之外的确认，这里只记录
                LogMessage($"收到雷达心跳（间隔 {result.HeartbeatIntervalSec} 秒）");
                break;
        }
    }

    private void ProcessUcm221Frame(Ucm221Protocol ucm, byte[] frame)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var result = ucm.Parse(frame, sequence, Name);

        // 复用协议层的样本转换（无人机信息模块优先，回退到扩展信息 GPS）
        var sample = result.ToSample() with { Device = _config.Kind, DeviceName = Name };

        Statistics.Samples++;
        SampleReceived?.Invoke(this, sample, frame);
    }

    private void ProcessNmeaLine(NmeaProtocol nmea, byte[] frame)
    {
        if (!nmea.TryParse(frame, out var sentence)) return;

        if (!nmea.IsRelevant(sentence.Type))
        {
            // 非目标语句也计数，便于现场确认模块实际输出
            return;
        }

        var now = Environment.TickCount64;
        if (sentence.Type == "GGA") Interlocked.Exchange(ref _lastGgaTicks, now);
        else if (sentence.Type == "RMC") Interlocked.Exchange(ref _lastRmcTicks, now);

        if (_nmeaAggregator is null) return;
        if (!_nmeaAggregator.Apply(sentence, out var sample) || sample is null) return;

        // 补上设备名，并保证时间戳为接收时刻
        sample = sample with
        {
            Device = _config.Kind,
            DeviceName = Name,
            Timestamp = DateTimeOffset.Now,
        };

        Statistics.Samples++;
        SampleReceived?.Invoke(this, sample, frame);
    }

    // ------------------------------------------------------------------ 连接后动作

    private async Task OnConnectedAsync(CancellationToken cancellationToken)
    {
        switch (_config.Kind)
        {
            case DeviceKind.BaseStation:
                if (_config.Um982.SendInitCommands)
                {
                    await SendUm982ConfigAsync(includeSave: true, cancellationToken).ConfigureAwait(false);
                }
                break;

            case DeviceKind.Radar:
                if (_config.Radar.QueryStatusOnConnect)
                {
                    await TrySendAsync(() => _radarProtocol!.BuildReadStatus(), cancellationToken).ConfigureAwait(false);
                }
                break;
        }
    }

    /// <summary>空闲节拍：串口/网络无数据时每秒触发几次，用于心跳与看门狗。</summary>
    private async Task OnIdleTickAsync(CancellationToken cancellationToken)
    {
        if (_config.Kind == DeviceKind.BaseStation && _config.Um982.EnableWatchdog)
        {
            var now = Environment.TickCount64;
            var timeout = Math.Max(1, _config.Um982.WatchdogTimeoutSec) * 1000L;
            var gga = Interlocked.Read(ref _lastGgaTicks);
            var rmc = Interlocked.Read(ref _lastRmcTicks);
            var sent = Interlocked.Read(ref _lastConfigSentTicks);

            var stale = gga == 0 || rmc == 0 || now - gga > timeout || now - rmc > timeout;
            var cooldown = Math.Max(1, _config.Um982.WatchdogCooldownSec) * 1000L;

            if (stale && (sent == 0 || now - sent > cooldown))
            {
                LogMessage("看门狗：GGA/RMC 超时未更新，重发 UM982 输出配置命令");
                await SendUm982ConfigAsync(includeSave: false, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 下发 UM982 输出配置命令。依据 um982_driver v1.0.0：
    /// 5 条命令（GGA/RMC/VTG/THS + saveconfig），命令之间间隔 100 ms，每条以 CRLF 结尾、不带 <c>$</c> 与校验和。
    /// </summary>
    private async Task SendUm982ConfigAsync(bool includeSave, CancellationToken cancellationToken)
    {
        var interval = Math.Clamp(_config.Um982.OutputIntervalSec, 0.01, 10.0)
            .ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

        string[] commands = includeSave
            ? [$"gpgga com1 {interval}", $"gprmc com1 {interval}", $"gpvtg com1 {interval}", $"gpths com1 {interval}", "saveconfig"]
            : [$"gpgga com1 {interval}", $"gprmc com1 {interval}", $"gpvtg com1 {interval}", $"gpths com1 {interval}"];

        foreach (var command in commands)
        {
            if (cancellationToken.IsCancellationRequested) return;
            try
            {
                await SendTextAsync(command, cancellationToken).ConfigureAwait(false);
                LogMessage($"→ {command}");
            }
            catch (Exception ex)
            {
                LogMessage($"配置命令下发失败（{command}）：{ex.Message}");
                return;
            }

            try
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        Interlocked.Exchange(ref _lastConfigSentTicks, Environment.TickCount64);
    }

    private async Task TrySendAsync(Func<byte[]> build, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(build(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogMessage($"命令下发失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------------ 工具

    /// <summary>主动发送雷达命令（界面按钮用）。</summary>
    public ValueTask SendRadarCommandAsync(byte[] command, CancellationToken cancellationToken = default) =>
        SendAsync(command, cancellationToken);

    private async Task CloseTransportAsync()
    {
        var transport = _transport;
        _transport = null;
        if (transport is null) return;

        transport.Faulted -= OnTransportFaulted;
        try { await transport.DisposeAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
    }

    private void OnTransportFaulted(string message) => LogMessage($"链路报告：{message}");

    private void SetState(LinkState state, string? error)
    {
        lock (_stateLock)
        {
            if (State == state && LastError == error) return;
            State = state;
            LastError = error;
        }
        StateChanged?.Invoke(this);
    }

    private void LogMessage(string message) => Log?.Invoke(this, message);

    private static DeviceProtocol CreateProtocol(DeviceConfig config) => config.Kind switch
    {
        DeviceKind.BaseStation => new NmeaProtocol(config.Um982),
        DeviceKind.Radar => new NsrRadarProtocol(config.Radar),
        DeviceKind.DroneGps => new Ucm221Protocol(config.Ucm221),
        _ => throw new NotSupportedException($"未知设备类型：{config.Kind}"),
    };
}
