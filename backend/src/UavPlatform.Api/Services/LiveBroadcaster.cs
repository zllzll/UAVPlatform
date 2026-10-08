using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace UavPlatform.Api.Services;

/// <summary>在线客户端计数：没有客户端时跳过序列化与广播，避免无谓开销。</summary>
public sealed class PresenceTracker
{
    private int _count;
    private long _totalConnections;

    /// <summary>当前在线连接数。</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>是否至少有一个客户端。</summary>
    public bool Any => Volatile.Read(ref _count) > 0;

    /// <summary>累计连接次数。</summary>
    public long TotalConnections => Interlocked.Read(ref _totalConnections);

    internal void Add()
    {
        Interlocked.Increment(ref _count);
        Interlocked.Increment(ref _totalConnections);
    }

    internal void Remove() => Interlocked.Decrement(ref _count);
}

/// <summary>SignalR 实时广播器：把设备样本、相对位置帧、状态与日志按节流后的节奏推给前端。</summary>
public sealed class LiveBroadcaster : BackgroundService
{
    private static readonly TimeSpan RelativeInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PointCloudInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(500);

    private readonly PlatformService _service;
    private readonly IHubContext<Hubs.PlatformHub> _hub;
    private readonly PresenceTracker _presence;
    private readonly ILogger<LiveBroadcaster> _logger;

    private readonly ConcurrentQueue<Contracts.SampleSummary> _samples = new();
    private readonly ConcurrentQueue<string> _logs = new();

    private RelativeFrameBox _relative = new(null, 0);
    private long _relativeSentVersion;
    private DateTimeOffset _lastPointCloudAt = DateTimeOffset.MinValue;

    public LiveBroadcaster(
        PlatformService service,
        IHubContext<Hubs.PlatformHub> hub,
        PresenceTracker presence,
        ILogger<LiveBroadcaster> logger)
    {
        _service = service;
        _hub = hub;
        _presence = presence;
        _logger = logger;

        _service.RelativeFrameReady += OnRelative;
        _service.SampleParsed += (_, _, _, summary) => Enqueue(_samples, summary, 2000);
        _service.LogLine += message => Enqueue(_logs, message, 500);
    }

    private void OnRelative(Core.Relative.RelativeFrame frame) =>
        Interlocked.Exchange(ref _relative, new RelativeFrameBox(frame, _relative.Version + 1));

    private static void Enqueue<T>(ConcurrentQueue<T> queue, T item, int cap)
    {
        queue.Enqueue(item);
        while (queue.Count > cap && queue.TryDequeue(out _)) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("实时广播器已启动。");

        var lastRelative = DateTimeOffset.MinValue;
        var lastSample = DateTimeOffset.MinValue;
        var lastStatus = DateTimeOffset.MinValue;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (!_presence.Any) continue;
                var now = DateTimeOffset.Now;

                // ── 相对位置帧 ──
                var box = Volatile.Read(ref _relative);
                if (box.Frame is { } frame &&
                    box.Version != Volatile.Read(ref _relativeSentVersion) &&
                    now - lastRelative >= RelativeInterval)
                {
                    var isPointCloud = frame.IsPointCloud;
                    if (!isPointCloud || now - _lastPointCloudAt >= PointCloudInterval)
                    {
                        lastRelative = now;
                        if (isPointCloud) _lastPointCloudAt = now;
                        Volatile.Write(ref _relativeSentVersion, box.Version);
                        await SafeAsync(() => _hub.Clients.All.SendAsync("relative", frame, stoppingToken)).ConfigureAwait(false);
                    }
                }

                // ── 解析数据摘要（滚动表格）──
                if (now - lastSample >= SampleInterval && !_samples.IsEmpty)
                {
                    lastSample = now;
                    var batch = Drain(_samples, 40);
                    if (batch.Count > 0)
                    {
                        await SafeAsync(() => _hub.Clients.All.SendAsync("samples", batch, stoppingToken)).ConfigureAwait(false);
                    }
                }

                // ── 状态 / 日志 ──
                if (now - lastStatus >= StatusInterval)
                {
                    lastStatus = now;
                    var status = _service.Manager.GetStatus();
                    await SafeAsync(() => _hub.Clients.All.SendAsync("status", status, stoppingToken)).ConfigureAwait(false);

                    var logs = Drain(_logs, 50);
                    if (logs.Count > 0)
                    {
                        await SafeAsync(() => _hub.Clients.All.SendAsync("logs", logs, stoppingToken)).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }

        _logger.LogInformation("实时广播器已停止。");
    }

    private async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停机中
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "广播失败。");
        }
    }

    private static List<T> Drain<T>(ConcurrentQueue<T> queue, int max)
    {
        var list = new List<T>(Math.Min(max, queue.Count));
        while (list.Count < max && queue.TryDequeue(out var item)) list.Add(item);
        return list;
    }

    private sealed record RelativeFrameBox(Core.Relative.RelativeFrame? Frame, long Version);
}
