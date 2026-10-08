namespace UavPlatform.Core.Relative;

/// <summary>轨迹上的一个点（显示坐标系，米）。</summary>
public readonly record struct TrackPoint(DateTimeOffset Timestamp, double East, double North, double Up);

/// <summary>
/// 轨迹存储（需求③「更好的展现整个平台的轨迹」）。
/// 保留无人机轨迹与各雷达目标的拖尾，供前端刷新页面后仍能回看历史轨迹。
/// 全部操作在锁内完成，容量有上限，长时间运行不会无限增长。
/// </summary>
public sealed class TrackStore
{
    private readonly object _gate = new();
    private readonly LinkedList<TrackPoint> _drone = new();
    private readonly Dictionary<uint, LinkedList<TrackPoint>> _targets = [];
    private int _retentionSeconds;
    private int _maxPointsPerTrack;
    private int _maxTargets;

    public TrackStore(int retentionSeconds = 600, int maxPointsPerTrack = 2000, int maxTargets = 300)
    {
        _retentionSeconds = Math.Max(0, retentionSeconds);
        _maxPointsPerTrack = Math.Max(10, maxPointsPerTrack);
        _maxTargets = Math.Max(1, maxTargets);
    }

    /// <summary>调整保留策略（配置热更新时调用）。</summary>
    public void Configure(int retentionSeconds, int maxPointsPerTrack, int maxTargets)
    {
        lock (_gate)
        {
            _retentionSeconds = Math.Max(0, retentionSeconds);
            _maxPointsPerTrack = Math.Max(10, maxPointsPerTrack);
            _maxTargets = Math.Max(1, maxTargets);
        }
    }

    /// <summary>无人机轨迹点数。</summary>
    public int DronePointCount
    {
        get { lock (_gate) return _drone.Count; }
    }

    /// <summary>正在跟踪的目标数。</summary>
    public int TrackedTargetCount
    {
        get { lock (_gate) return _targets.Count; }
    }

    /// <summary>追加一帧相对位置结果的轨迹点。</summary>
    /// <remarks>
    /// **点云帧不建目标轨迹**（只记无人机轨迹）。理由：点云的「目标 ID」是每帧的点序号
    /// （NSR 0xA9 与 UCM221 点云记录都给的是逐点 ID，不是持续跟踪的身份），一帧几千个点会
    /// 瞬间冲爆 <c>_maxTargets</c>，把真正的跟踪目标轨迹全部挤掉；实测一帧 800 个点云点时
    /// <c>TrackedTargetCount</c> 会长期顶在上限并伴随每帧上百次淘汰扫描。
    /// 真实跟踪目标走 0xA8（NSR）/ 目标信息（UCM221），它们才有稳定 ID。
    /// 前端 <c>frontend/src/services/live.ts</c> 用的是同一条规则。
    /// </remarks>
    public void Append(RelativeFrame frame)
    {
        var cutoff = _retentionSeconds > 0
            ? frame.Timestamp.AddSeconds(-_retentionSeconds)
            : DateTimeOffset.MinValue;

        lock (_gate)
        {
            if (frame.Drone.Latitude is not null)
            {
                _drone.AddLast(new TrackPoint(frame.Timestamp, frame.Drone.Position.East,
                    frame.Drone.Position.North, frame.Drone.Position.Up));
                Prune(_drone, cutoff);
            }

            if (frame.IsPointCloud) return;

            var seen = frame.Targets.Length > 0 ? new HashSet<uint>() : null;
            if (seen is not null)
            {
                foreach (var t in frame.Targets)
                {
                    seen!.Add(t.Id);
                    if (!_targets.TryGetValue(t.Id, out var list))
                    {
                        if (_targets.Count >= _maxTargets) EvictOldest();
                        list = new LinkedList<TrackPoint>();
                        _targets[t.Id] = list;
                    }
                    list.AddLast(new TrackPoint(frame.Timestamp, t.East, t.North, t.Up));
                    Prune(list, cutoff);
                }

                // 本帧未出现的目标，若已超时则移除
                if (_retentionSeconds > 0 && _targets.Count > 0)
                {
                    foreach (var id in _targets.Where(kv => kv.Value.Last is null ||
                                 kv.Value.Last.Value.Timestamp < cutoff).Select(kv => kv.Key).ToList())
                    {
                        _targets.Remove(id);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 淘汰最久未更新的目标轨迹。必须在锁内调用。
    /// 注意：这里**不能**用「oldest.Key != 0 才删」的写法——目标 ID 0 是合法值，
    /// 一旦最久未更新的恰好是 ID 0，容量就再也降不下来，字典会无限增长。
    /// </summary>
    private void EvictOldest()
    {
        uint? oldestId = null;
        var oldestAt = DateTimeOffset.MaxValue;
        foreach (var kv in _targets)
        {
            var at = kv.Value.Last?.Value.Timestamp ?? DateTimeOffset.MinValue;
            if (at < oldestAt)
            {
                oldestAt = at;
                oldestId = kv.Key;
            }
        }
        if (oldestId is not null) _targets.Remove(oldestId.Value);
    }

    /// <summary>取无人机轨迹快照。</summary>
    public TrackPoint[] DroneTrack()
    {
        lock (_gate) return _drone.ToArray();
    }

    /// <summary>取全部目标拖尾快照。</summary>
    public Dictionary<uint, TrackPoint[]> TargetTracks()
    {
        lock (_gate) return _targets.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }

    /// <summary>清空。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _drone.Clear();
            _targets.Clear();
        }
    }

    private void Prune(LinkedList<TrackPoint> list, DateTimeOffset cutoff)
    {
        while (list.Count > _maxPointsPerTrack) list.RemoveFirst();
        if (_retentionSeconds > 0)
        {
            while (list.First is not null && list.First.Value.Timestamp < cutoff) list.RemoveFirst();
        }
    }
}
