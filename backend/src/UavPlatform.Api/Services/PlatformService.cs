using UavPlatform.Api.Contracts;
using UavPlatform.Core.Devices;
using UavPlatform.Core.Relative;
using UavPlatform.Core.Models;
using UavPlatform.Core.Storage;

namespace UavPlatform.Api.Services;

/// <summary>
/// 平台单例：持有配置、设备管理器、最近样本缓冲，并对外暴露统一事件供 SignalR 广播。
/// 配置改动一律通过 <see cref="SaveConfigAsync"/>，内部做落盘 + 热更新。
/// </summary>
public sealed class PlatformService : IAsyncDisposable
{
    private const int SampleBufferSize = 600;

    private readonly ILogger<PlatformService> _logger;
    private readonly object _gate = new();
    private readonly LinkedList<SampleSummary> _samples = new();
    private readonly DeviceManager _manager;

    public PlatformService(IHostEnvironment env, ILogger<PlatformService> logger)
    {
        _logger = logger;
        BaseDirectory = env.ContentRootPath;

        // 运行时配置固定在用户目录下的唯一权威路径：换一种启动方式（dotnet run / 直接跑 exe）
        // 读写的都是同一份，重新构建也不会把它覆盖回默认值。详见 ConfigLocator。
        var factoryDefaultPath = ConfigLocator.FactoryDefaultPath(BaseDirectory);
        var configPath = ConfigLocator.ResolveConfigPath(BaseDirectory);
        var seeded = ConfigLocator.SeedIfMissing(configPath, factoryDefaultPath);
        Store = new ConfigStore(configPath);
        if (seeded)
        {
            _logger.LogInformation("首次运行：已从出厂默认配置播种到 {Path}", configPath);
        }

        Config = Store.Load();
        _manager = new DeviceManager(Config, BaseDirectory);

        _manager.SampleParsed += (connection, sample, frame) =>
        {
            var summary = SampleSummary.From(sample, SessionStorage.LabelOf(connection.Kind));
            Append(summary);
            SampleParsed?.Invoke(connection, sample, frame, summary);
        };
        _manager.RelativeFrameReady += frame => RelativeFrameReady?.Invoke(frame);
        _manager.StateChanged += connection => StateChanged?.Invoke(connection);
        _manager.Log += message => LogLine?.Invoke(message);

        _logger.LogInformation("配置路径：{Path}；数据根目录：{Root}", Store.Path, StorageRoot);
    }

    /// <summary>是否正在采集。</summary>
    public bool Running => _manager.Running;

    /// <summary>当前配置快照。</summary>
    public PlatformConfig Config { get; private set; }

    /// <summary>配置存取器。</summary>
    public ConfigStore Store { get; }

    /// <summary>程序内容根目录（配置与默认数据目录的基准）。</summary>
    public string BaseDirectory { get; }

    /// <summary>设备管理器。</summary>
    public DeviceManager Manager => _manager;

    /// <summary>存储根目录（会话目录的父目录）。</summary>
    public string StorageRoot => Config.Storage.ResolveRoot(BaseDirectory);

    public event Action<DeviceConnection, DeviceSample, byte[]?, SampleSummary>? SampleParsed;
    public event Action<RelativeFrame>? RelativeFrameReady;
    public event Action<DeviceConnection>? StateChanged;
    public event Action<string>? LogLine;

    // ── 生命周期 ────────────────────────────────────────────────────────────

    public async Task StartAsync()
    {
        if (_manager.Running) return;
        await _manager.StartAsync();
        _logger.LogInformation("平台已启动，会话目录：{Dir}", _manager.Storage.SessionDirectory);
    }

    public async Task StopAsync()
    {
        if (!_manager.Running) return;
        await _manager.StopAsync();
        _logger.LogInformation("平台已停止。");
    }

    /// <summary>本次提交是否会改动与采集相关的段落（设备连接、存储、相对位置）。</summary>
    public bool AcquisitionChanged(PlatformConfig next) => !Config.SameAcquisitionAs(next);

    /// <summary>保存并热应用配置。</summary>
    public async Task<PlatformConfig> SaveConfigAsync(PlatformConfig config)
    {
        config.EnsureAllDevices();

        // 采集中、且只有与采集无关的部分变了（平台名称、界面显示选项）：照存不重启。
        // 走下面那条「停→应用→启」的路会把本次会话拦腰截断（存储换目录、设备重连、
        // 参考点重置）。真正的参数改动由 API 层在采集期间直接挡掉。
        if (_manager.Running && Config.SameAcquisitionAs(config))
        {
            Config = config;
            Store.Save(config);
            LogLine?.Invoke($"配置已保存（采集继续）：{Store.Path}");
            return Config;
        }

        Store.Save(config);
        Config = config;

        var restart = _manager.Running;
        if (restart) await _manager.StopAsync();
        _manager.ApplyConfig(config);
        if (restart) await _manager.StartAsync();

        LogLine?.Invoke($"配置已保存：{Store.Path}");
        return Config;
    }

    /// <summary>
    /// 只保存界面显示选项（显示目标 / 点云 / 轨迹、网格、点大小等）。
    /// 刻意不碰设备连接、存储与相对位置，也<b>不重启采集管线</b>：
    /// 界面开关与设备通讯无关，若走 <see cref="SaveConfigAsync"/> 的「停→应用→启」路径，
    /// 每勾一个复选框都会把正在跑的采集重启一遍，实时绘制会当场断掉。
    /// 入参是完整的界面选项对象（前端负责与现有值合并），避免缺省字段被默认值覆盖。
    /// </summary>
    public PlatformConfig SaveUiAsync(UiSettings ui)
    {
        Config.Ui = ui ?? new UiSettings();
        // 尾迹时长界面给了 0~600 的输入框，手输可能越界（或非数字被前端兜成 0），这里再夹一道
        Config.Ui.TrailSeconds = Math.Clamp(Config.Ui.TrailSeconds, 0, 600);
        // 后端轨迹保留窗口跟着尾迹走：这条路径不重启管线，所以要显式通知一次
        _manager.ApplyTrackRetention(Config.Ui.TrailSeconds);
        Store.Save(Config);
        return Config;
    }

    public async Task<TestResult> ReconnectAsync(DeviceKind kind)
    {
        var connection = _manager.Connection(kind);
        if (connection is null) return new TestResult { Ok = false, Message = "未找到该设备的连接。" };
        if (!_manager.Running) return new TestResult { Ok = false, Message = "平台未启动，请先点击「开始采集」。" };

        await connection.StopAsync();
        connection.Start();
        return new TestResult { Ok = true, Message = $"{connection.Name} 已触发重连。" };
    }

    // ── 快照 ────────────────────────────────────────────────────────────────

    public IReadOnlyList<SampleSummary> RecentSamples()
    {
        lock (_gate) return _samples.ToArray();
    }

    public PlatformSnapshot Snapshot()
    {
        var tracks = _manager.Tracks;
        return new PlatformSnapshot
        {
            Status = _manager.GetStatus(),
            Config = Config,
            Relative = _manager.CurrentFrame,
            Tracks = new TrackSnapshot
            {
                Drone = tracks.DroneTrack(),
                Targets = tracks.TargetTracks().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            },
            Samples = RecentSamples(),
            Logs = _manager.RecentLogs,
        };
    }

    /// <summary>列出存储根目录下的全部会话。</summary>
    public IReadOnlyList<SessionInfo> ListSessions()
    {
        var root = StorageRoot;
        if (!Directory.Exists(root)) return [];

        var active = _manager.Storage.SessionDirectory;
        var list = new List<SessionInfo>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                var subFolders = Directory.EnumerateDirectories(dir).Select(Path.GetFileName).OfType<string>().ToArray();
                list.Add(new SessionInfo
                {
                    Name = Path.GetFileName(dir),
                    Path = dir,
                    StartedAt = Directory.GetCreationTimeUtc(dir),
                    TotalBytes = files.Sum(f => new FileInfo(f).Length),
                    FileCount = files.Length,
                    Active = string.Equals(dir, active, StringComparison.OrdinalIgnoreCase),
                    SubFolders = subFolders,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取会话目录失败：{Dir}", dir);
            }
        }

        return list.OrderByDescending(s => s.Name).ToArray();
    }

    private void Append(SampleSummary summary)
    {
        lock (_gate)
        {
            _samples.AddLast(summary);
            while (_samples.Count > SampleBufferSize) _samples.RemoveFirst();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
    }
}
