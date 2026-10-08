using UavPlatform.Core.Relative;
using UavPlatform.Core.Models;
using UavPlatform.Core.Storage;

namespace UavPlatform.Core.Devices;

/// <summary>
/// 设备管理器：装配三台设备（基座 / 雷达 / 无人机 GPS），串起
/// 链路 → 分帧解析 → 分级存储 → 坐标相对位置 → 轨迹 的完整数据流。
/// </summary>
/// <remarks>
/// 数据流：
/// <code>
/// DeviceConnection.RawReceived    → SessionStorage.WriteRaw（原始字节）
/// DeviceConnection.SampleReceived → SessionStorage.WriteParsed（解析数据）
///                                 → RelativeService.OnXxx
/// RadarSample                     → RelativeService.ProjectRadarToBase → SessionStorage.WriteRadarBase（雷达转基座系）
/// RelativeFrameReady              → Tracks / SessionStorage.WriteFrame（三设备同帧）
/// </code>
/// 相对位置帧由「任一设备新数据到达」与「定时心跳」两条路径触发，保证设备静默时状态也会刷新（超时标记）。
/// 配置热更新：<see cref="ApplyConfig"/> 比较新旧配置，只在真正变化的部分重建连接与存储。
/// </remarks>
public sealed class DeviceManager : IAsyncDisposable
{
    private readonly string _baseDirectory;
    private readonly Dictionary<DeviceKind, DeviceConnection> _connections = [];
    private readonly Dictionary<DeviceKind, long> _lastSampleTicks = [];
    private readonly object _gate = new();
    private readonly List<string> _recentLogs = [];

    /// <summary>无链路实例时（设备未启用 / 链路正在重建），多久内有样本就视为在线。</summary>
    private const long OnlineWindowMs = 5000;

    private PlatformConfig _config;
    private SessionStorage _storage;
    private RelativeService _relative;
    private TrackStore _tracks;
    private CancellationTokenSource? _cts;
    private Task? _watchdogTask;
    private long _relativeFrames;
    private bool _running;
    private bool _disposed;

    public DeviceManager(PlatformConfig config, string baseDirectory)
    {
        _baseDirectory = baseDirectory;
        _config = config.Clone();
        _config.EnsureAllDevices();

        _storage = new SessionStorage(_config.Storage, baseDirectory);
        _relative = new RelativeService(_config.Relative);
        _tracks = BuildTrackStore(_config.Ui.TrailSeconds);

        WireRelative();
        BuildConnections();
    }

    /// <summary>设备数据解析完成（供 SignalR 广播）。</summary>
    public event Action<DeviceConnection, DeviceSample, byte[]?>? SampleParsed;

    /// <summary>收到原始字节（供 SignalR 广播与统计）。</summary>
    public event Action<DeviceConnection, RawSegment>? RawReceived;

    /// <summary>产生新相对位置帧。</summary>
    public event Action<RelativeFrame>? RelativeFrameReady;

    /// <summary>设备链路状态变化。</summary>
    public event Action<DeviceConnection>? StateChanged;

    /// <summary>运行日志。</summary>
    public event Action<string>? Log;

    /// <summary>当前配置快照。</summary>
    public PlatformConfig Config
    {
        get { lock (_gate) return _config.Clone(); }
    }

    /// <summary>存储实例。</summary>
    public SessionStorage Storage => _storage;

    /// <summary>相对位置服务。</summary>
    public RelativeService Relative => _relative;

    /// <summary>轨迹存储。</summary>
    public TrackStore Tracks => _tracks;

    /// <summary>最近一帧相对位置结果。</summary>
    public RelativeFrame? CurrentFrame { get; private set; }

    /// <summary>是否正在运行。</summary>
    public bool Running => _running;

    /// <summary>按类型取连接。</summary>
    public DeviceConnection? Connection(DeviceKind kind) =>
        _connections.TryGetValue(kind, out var c) ? c : null;

    /// <summary>全部连接。</summary>
    public IReadOnlyCollection<DeviceConnection> Connections => _connections.Values;

    /// <summary>最近若干条运行日志（供界面启动时补齐）。</summary>
    public IReadOnlyList<string> RecentLogs
    {
        get { lock (_gate) return _recentLogs.ToArray(); }
    }

    // ── 生命周期 ────────────────────────────────────────────────────────────

    /// <summary>启动存储、设备链路与定时相对位置。</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_running) return Task.CompletedTask;
        _running = true;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;

        if (_config.Storage.Enabled)
        {
            try
            {
                _storage.Open(_config.Devices, _config.Relative);
                Info($"存储会话目录：{_storage.SessionDirectory}");
            }
            catch (Exception ex)
            {
                Error($"存储初始化失败：{ex.Message}");
            }
        }
        else
        {
            Info("存储已关闭（配置中 Enabled = false）");
        }

        foreach (var connection in _connections.Values)
        {
            connection.Start();
        }

        _watchdogTask = Task.Run(() => PeriodicFrameAsync(token), CancellationToken.None);
        Info("设备管理器已启动");
        return Task.CompletedTask;
    }

    /// <summary>停止设备链路并刷盘。</summary>
    public async Task StopAsync()
    {
        if (!_running) return;
        _running = false;

        try { _cts?.Cancel(); } catch { /* 忽略 */ }

        if (_watchdogTask is not null)
        {
            try { await _watchdogTask.ConfigureAwait(false); } catch { /* 忽略 */ }
            _watchdogTask = null;
        }

        foreach (var connection in _connections.Values)
        {
            try { await connection.StopAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
        }

        try { _storage.Complete(); } catch { /* 忽略 */ }
        Info("设备管理器已停止，数据已刷盘");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync().ConfigureAwait(false);

        foreach (var connection in _connections.Values)
        {
            try { await connection.DisposeAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
        }
        _connections.Clear();

        _storage.Dispose();
        _cts?.Dispose();
    }

    // ── 配置热更新 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 应用新配置。设备通讯参数变化时重建该设备连接；存储配置变化时重建存储会话；
    /// 相对位置配置变化时即时生效（原点设备或安装位置变化会重置参考点）。
    /// </summary>
    public void ApplyConfig(PlatformConfig config)
    {
        config.EnsureAllDevices();

        PlatformConfig previous;
        lock (_gate)
        {
            previous = _config;
            _config = config.Clone();
        }

        // 1. 存储
        if (!StorageEquals(previous.Storage, config.Storage))
        {
            var old = _storage;
            try { old.Complete(); } catch { /* 忽略 */ }
            old.Dispose();

            _storage = new SessionStorage(config.Storage, _baseDirectory);
            if (_running && config.Storage.Enabled)
            {
                try
                {
                    _storage.Open(config.Devices, config.Relative);
                    Info($"存储配置已更新，新会话目录：{_storage.SessionDirectory}");
                }
                catch (Exception ex)
                {
                    Error($"存储重新初始化失败：{ex.Message}");
                }
            }
        }

        // 2. 相对位置配置
        var referenceChanged = previous.Relative.ReferenceMode != config.Relative.ReferenceMode ||
                               previous.Relative.ManualLatitude != config.Relative.ManualLatitude ||
                               previous.Relative.ManualLongitude != config.Relative.ManualLongitude ||
                               previous.Relative.ManualAltitudeM != config.Relative.ManualAltitudeM;
        var placementChanged = !PlacementEquals(previous.Relative.Radar, config.Relative.Radar);

        _relative.UpdateSettings(config.Relative);
        ApplyTrackRetention(config.Ui.TrailSeconds);

        if (referenceChanged || placementChanged)
        {
            _relative.ResetReference();
            Info("相对位置配置已更新，显示坐标系原点将重新锁定");
        }

        // 3. 设备连接
        foreach (var deviceConfig in config.Devices)
        {
            var changed = true;
            var old = previous.Device(deviceConfig.Kind);
            if (old is not null && DeviceEquals(old, deviceConfig)) changed = false;

            var existing = Connection(deviceConfig.Kind);

            if (existing is null)
            {
                if (deviceConfig.Enabled) AddConnection(deviceConfig);
                continue;
            }

            if (!changed) continue;

            RemoveConnection(deviceConfig.Kind, existing);
            if (deviceConfig.Enabled) AddConnection(deviceConfig);
            Info($"{deviceConfig.Name} 通讯参数已更新，连接已重建");
        }
    }

    // ── 状态 ────────────────────────────────────────────────────────────────

    /// <summary>取平台状态快照。</summary>
    public PlatformStatus GetStatus()
    {
        var devices = new List<DeviceStatus>();
        foreach (var kind in new[] { DeviceKind.BaseStation, DeviceKind.Radar, DeviceKind.DroneGps })
        {
            var cfg = _config.Device(kind);
            var conn = Connection(kind);

            if (cfg is null) continue;

            var stats = conn?.Statistics;
            devices.Add(new DeviceStatus
            {
                Kind = kind.ToString(),
                KindLabel = SessionStorage.LabelOf(kind),
                Name = cfg.Name,
                Enabled = cfg.Enabled,
                State = conn?.State.ToString() ?? nameof(LinkState.Disabled),
                Transport = cfg.Transport.ToString(),
                TransportDescription = cfg.TransportSettings.Describe(cfg.Transport),
                Protocol = conn?.ProtocolName ?? string.Empty,
                RemoteEndPoint = conn?.RemoteEndPoint,
                LastError = conn?.LastError,
                LastDataAt = stats?.LastDataAt,
                IdleMs = stats?.IdleMs ?? 0,
                Reconnects = (int)(stats?.Reconnects ?? 0),
                BytesReceived = stats?.BytesReceived ?? 0,
                FramesReceived = stats?.FramesReceived ?? 0,
                ParseErrors = stats?.ParseErrors ?? 0,
                SkippedBytes = stats?.SkippedBytes ?? 0,
                Samples = stats?.Samples ?? 0,
                TargetCount = stats?.TargetCount ?? 0,
                SaveRaw = cfg.SaveRaw,
                SaveParsed = cfg.SaveParsed,
            });
        }

        var frame = CurrentFrame;
        return new PlatformStatus
        {
            Name = _config.Name,
            Timestamp = DateTimeOffset.Now,
            Running = _running,
            OriginName = frame?.OriginName ?? "基座",
            ReferenceResolved = _relative.HasReference,
            Reference = _relative.Reference,
            Devices = devices,
            Storage = new StorageStatus
            {
                Enabled = _storage.Enabled,
                SessionDirectory = _storage.SessionDirectory,
                RawRecords = _storage.RawRecords,
                ParsedRecords = _storage.ParsedRecords,
                RadarBaseRecords = _storage.RadarBaseRecords,
                FrameRecords = _storage.FrameRecords,
                TotalBytes = _storage.TotalBytes,
                Files = _storage.CurrentFiles
                    .Select(f => new StorageFileStatus { Device = f.Device, RawPath = f.RawPath, ParsedPath = f.ParsedPath })
                    .ToList(),
            },
            RelativeFrames = Interlocked.Read(ref _relativeFrames),
            DroppedRelativeFrames = _relative.SuppressedFrames,
            DroneTrackPoints = _tracks.DronePointCount,
            TrackedTargets = _tracks.TrackedTargetCount,
            Drone = frame?.Drone,
            BaseStation = frame?.BaseStation,
            Radar = frame?.Radar,
            DroneDistanceToBaseM = frame?.DroneDistanceToBaseM,
            DroneHeightAboveBaseM = frame?.DroneHeightAboveBaseM,
            DroneDistanceToRadarM = frame?.DroneDistanceToRadarM,
        };
    }

    /// <summary>手动触发一次相对位置（强制，不受限频影响）。</summary>
    public RelativeFrame? ForceRebuild(string trigger = "manual") => _relative.Build(trigger, force: true);

    /// <summary>清理轨迹。</summary>
    public void ClearTracks()
    {
        _tracks.Clear();
        Info("轨迹已清空");
    }

    // ── 内部实现 ────────────────────────────────────────────────────────────

    private void BuildConnections()
    {
        foreach (var device in _config.Devices)
        {
            if (device.Enabled) AddConnection(device);
        }
    }

    private void AddConnection(DeviceConfig device)
    {
        var connection = new DeviceConnection(device);
        connection.RawReceived += OnRawReceived;
        connection.SampleReceived += OnSampleReceived;
        connection.AckReceived += (c, ack) => Info($"{c.Name} 应答：命令 0x{ack.Command:X2} {(ack.Success ? "成功" : "失败")}");
        connection.StateChanged += c =>
        {
            Info($"{c.Name} 链路状态：{DescribeState(c.State)}{(c.LastError is null ? "" : $"（{c.LastError}）")}");
            StateChanged?.Invoke(c);
        };
        connection.Log += (_, message) => Info(message);

        _connections[device.Kind] = connection;
        Info($"{device.Name} 已装配：{device.TransportSettings.Describe(device.Transport)}");

        if (_running) connection.Start();
    }

    private void RemoveConnection(DeviceKind kind, DeviceConnection connection)
    {
        _connections.Remove(kind);
        _ = Task.Run(async () =>
        {
            try { await connection.DisposeAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
        });
    }

    private void WireRelative()
    {
        _relative.OnlineProvider = kind =>
        {
            var connection = Connection(kind);
            if (connection is not null) return connection.State == LinkState.Connected;

            // 无连接实例（设备未启用 / 链路正在重建）：按最近样本的新鲜度判定
            lock (_gate)
            {
                return _lastSampleTicks.TryGetValue(kind, out var ticks) &&
                       Environment.TickCount64 - ticks <= OnlineWindowMs;
            }
        };

        _relative.FrameReady += frame =>
        {
            CurrentFrame = frame;
            Interlocked.Increment(ref _relativeFrames);
            _tracks.Append(frame);

            try { _storage.WriteFrame(frame); } catch (Exception ex) { Error($"三设备同帧数据落盘失败：{ex.Message}"); }

            RelativeFrameReady?.Invoke(frame);
        };
    }

    private void OnRawReceived(DeviceConnection connection, RawSegment segment)
    {
        try { _storage.WriteRaw(connection.Kind, segment); } catch (Exception ex) { Error($"原始数据落盘失败：{ex.Message}"); }
        RawReceived?.Invoke(connection, segment);
    }

    private void OnSampleReceived(DeviceConnection connection, DeviceSample sample, byte[]? frameBytes)
    {
        DistributeSample(connection.Kind, sample, frameBytes);
        SampleParsed?.Invoke(connection, sample, frameBytes);
        _relative.Build($"device:{connection.Kind}");
    }

    /// <summary>落盘 + 送入相对位置（下游公共路径）。</summary>
    private void DistributeSample(DeviceKind kind, DeviceSample sample, byte[]? frameBytes)
    {
        lock (_gate) _lastSampleTicks[kind] = Environment.TickCount64;

        try
        {
            _storage.WriteParsed(kind, sample, frameBytes);
        }
        catch (Exception ex)
        {
            Error($"解析数据落盘失败：{ex.Message}");
        }

        switch (sample)
        {
            case GnssSample gnss: _relative.OnGnss(gnss); break;
            case RadarSample radar:
                _relative.OnRadar(radar);
                WriteRadarBase(kind, radar);
                break;
            case DroneGpsSample drone: _relative.OnDrone(drone); break;
        }
    }

    /// <summary>
    /// 把一帧雷达数据换算到以基座为原点的东北天坐标系并落盘（04_radar_base）。
    /// 这里按**雷达帧**写而不是按出帧写：出帧被 RelativeSettings.RelativeIntervalMs 限频，
    /// 只有按雷达帧写才能把雷达数据一条不落地留档。
    /// </summary>
    private void WriteRadarBase(DeviceKind kind, RadarSample radar)
    {
        try
        {
            var record = _relative.ProjectRadarToBase(radar, $"device:{kind}");
            if (record is not null) _storage.WriteRadarBase(record);
        }
        catch (Exception ex)
        {
            Error($"雷达转基座系落盘失败：{ex.Message}");
        }
    }

    /// <summary>定时相对位置：设备静默时也要刷新超时状态与限频节流后的输出。</summary>
    private async Task PeriodicFrameAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    _relative.Build("timer");
                }
                catch (Exception ex)
                {
                    Error($"定时相对位置异常：{ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
    }

    /// <summary>尾迹时长（秒）→ 后端轨迹保留窗口与单条轨迹的点数上限。</summary>
    /// <remarks>
    /// 尾迹只管「屏幕上活多久」，但两者必须一致：尾迹拉到 600 s、后端只留 60 s，刷新页面就只能回看 60 s
    /// （现场反馈过「两个参数是不是重复」）。所以只留 ui.TrailSeconds 一个旋钮，保留窗口由它推出。
    /// 下限抬到 30 s：尾迹设 0 表示不画尾迹，但刷新后仍应有半分钟的数据可回看。
    /// </remarks>
    public static (int RetentionSeconds, int PointLimit) TrackRetention(int trailSeconds)
    {
        var seconds = Math.Clamp(trailSeconds, 30, 600);
        return (seconds, Math.Max(10, seconds * 5));
    }

    private static TrackStore BuildTrackStore(int trailSeconds)
    {
        var (retention, points) = TrackRetention(trailSeconds);
        return new TrackStore(retention, points, 300);
    }

    /// <summary>界面改了尾迹时长：刷新后端轨迹窗口（走 /api/config/ui，不重建管线）。</summary>
    public void ApplyTrackRetention(int trailSeconds)
    {
        var (retention, points) = TrackRetention(trailSeconds);
        _tracks.Configure(retention, points, 300);
    }

    private static string DescribeState(LinkState state) => state switch
    {
        LinkState.Disabled => "已禁用",
        LinkState.Disconnected => "未连接",
        LinkState.Connecting => "连接中",
        LinkState.Connected => "已连接",
        LinkState.Faulted => "故障",
        _ => state.ToString(),
    };

    private void Info(string message)
    {
        lock (_gate)
        {
            _recentLogs.Add($"{DateTime.Now:HH:mm:ss} {message}");
            if (_recentLogs.Count > 500) _recentLogs.RemoveRange(0, _recentLogs.Count - 500);
        }
        Log?.Invoke(message);
    }

    private void Error(string message)
    {
        lock (_gate)
        {
            _recentLogs.Add($"{DateTime.Now:HH:mm:ss} [错误] {message}");
            if (_recentLogs.Count > 500) _recentLogs.RemoveRange(0, _recentLogs.Count - 500);
        }
        Log?.Invoke("[错误] " + message);
    }

    // ── 配置比较 ────────────────────────────────────────────────────────────

    private static bool DeviceEquals(DeviceConfig a, DeviceConfig b) =>
        ConfigJson.Serialize(a) == ConfigJson.Serialize(b);

    private static bool StorageEquals(StorageConfig a, StorageConfig b) =>
        ConfigJson.Serialize(a) == ConfigJson.Serialize(b);

    private static bool PlacementEquals(RadarPlacementSettings a, RadarPlacementSettings b) =>
        ConfigJson.Serialize(a) == ConfigJson.Serialize(b);
}
