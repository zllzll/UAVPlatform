using Microsoft.AspNetCore.SignalR;
using UavPlatform.Api.Contracts;
using UavPlatform.Api.Services;
using UavPlatform.Core.Relative;
using UavPlatform.Core.Models;

namespace UavPlatform.Api.Hubs;

/// <summary>
/// 平台实时通道。服务端→客户端事件：
/// <c>relative</c>（<see cref="RelativeFrame"/>）、<c>samples</c>（<see cref="SampleSummary"/>[]）、
/// <c>status</c>（<see cref="PlatformStatus"/>）、<c>logs</c>（string[]）、<c>notice</c>（string）。
/// </summary>
public sealed class PlatformHub : Hub
{
    private readonly PlatformService _service;
    private readonly PresenceTracker _presence;
    private readonly ILogger<PlatformHub> _logger;

    public PlatformHub(PlatformService service, PresenceTracker presence, ILogger<PlatformHub> logger)
    {
        _service = service;
        _presence = presence;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _presence.Add();
        _logger.LogInformation("客户端 {Id} 已连接，当前在线 {Count}。", Context.ConnectionId, _presence.Count);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _presence.Remove();
        _logger.LogInformation("客户端 {Id} 已断开，当前在线 {Count}。", Context.ConnectionId, _presence.Count);
        await base.OnDisconnectedAsync(exception);
    }

    // ── 查询 ────────────────────────────────────────────────────────────────

    /// <summary>连接建立后的完整快照（状态 + 配置 + 当前相对位置帧 + 轨迹 + 最近样本 + 日志）。</summary>
    public PlatformSnapshot GetSnapshot() => _service.Snapshot();

    /// <summary>当前配置。</summary>
    public PlatformConfig GetConfig() => _service.Config;

    /// <summary>平台状态。</summary>
    public PlatformStatus GetStatus() => _service.Manager.GetStatus();

    /// <summary>轨迹快照。</summary>
    public TrackSnapshot GetTracks()
    {
        var tracks = _service.Manager.Tracks;
        return new TrackSnapshot
        {
            Drone = tracks.DroneTrack(),
            Targets = tracks.TargetTracks().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
        };
    }

    /// <summary>最近若干条解析数据摘要。</summary>
    public IReadOnlyList<SampleSummary> GetSamples() => _service.RecentSamples();

    /// <summary>存储根目录下的会话列表。</summary>
    public IReadOnlyList<SessionInfo> GetSessions() => _service.ListSessions();

    // ── 配置 ────────────────────────────────────────────────────────────────

    /// <summary>保存并热应用配置。</summary>
    public async Task<PlatformConfig> SaveConfig(PlatformConfig config)
    {
        var saved = await _service.SaveConfigAsync(config);
        await Clients.All.SendAsync("notice", "配置已保存并生效。");
        return saved;
    }

    /// <summary>恢复出厂默认配置（不落盘，需再调用 SaveConfig）。</summary>
    public PlatformConfig GetDefaultConfig() => PlatformConfig.CreateDefault();

    // ── 运行控制 ────────────────────────────────────────────────────────────

    /// <summary>开始采集。</summary>
    public async Task<TestResult> StartPlatform()
    {
        if (_service.Running) return new TestResult { Ok = true, Message = "平台已在运行。" };
        await _service.StartAsync();
        return new TestResult { Ok = true, Message = $"已开始采集，会话目录：{_service.Manager.Storage.SessionDirectory}" };
    }

    /// <summary>停止采集。</summary>
    public async Task<TestResult> StopPlatform()
    {
        if (!_service.Running) return new TestResult { Ok = true, Message = "平台已停止。" };
        await _service.StopAsync();
        return new TestResult { Ok = true, Message = "已停止采集，数据已刷盘。" };
    }

    /// <summary>重连某台设备。</summary>
    public async Task<TestResult> Reconnect(string kind)
    {
        if (!TryParseKind(kind, out var deviceKind)) return BadKind(kind);
        return await _service.ReconnectAsync(deviceKind);
    }

    /// <summary>重新解算并锁定显示原点。</summary>
    public TestResult ResetReference()
    {
        _service.Manager.Relative.ResetReference();
        return new TestResult { Ok = true, Message = "已重新解算显示坐标系原点。" };
    }

    /// <summary>清空轨迹。</summary>
    public TestResult ClearTracks()
    {
        _service.Manager.ClearTracks();
        return new TestResult { Ok = true, Message = "已清空无人机与目标的轨迹。" };
    }

    /// <summary>立即产生一帧相对位置数据。</summary>
    public TestResult ForceRebuild()
    {
        var frame = _service.Manager.ForceRebuild("ui");
        return frame is null
            ? new TestResult { Ok = false, Message = "暂无可用数据，无法相对位置（请确认至少一台设备已有定位）。" }
            : new TestResult { Ok = true, Message = $"已相对位置：原点 {frame.OriginName}，目标 {frame.Targets.Length} 个。" };
    }

    // ── 设备下行 ────────────────────────────────────────────────────────────

    /// <summary>向设备发送文本（基座 NMEA 配置命令用）。</summary>
    public Task<TestResult> SendText(string kind, string text)
    {
        if (!TryParseKind(kind, out var deviceKind)) return Task.FromResult(BadKind(kind));
        return SendAsync(deviceKind, ct => _service.Manager.Connection(deviceKind)!.SendTextAsync(text, ct));
    }

    /// <summary>向设备发送十六进制字节串（雷达命令用，如 <c>A5 5A 10 60 88 00 00 F8</c>）。</summary>
    public Task<TestResult> SendHex(string kind, string hex)
    {
        if (!TryParseKind(kind, out var deviceKind)) return Task.FromResult(BadKind(kind));
        if (!TryParseHex(hex, out var bytes, out var error)) return Task.FromResult(new TestResult { Ok = false, Message = error });
        return SendAsync(deviceKind, ct => _service.Manager.Connection(deviceKind)!.SendAsync(bytes, ct));
    }

    private async Task<TestResult> SendAsync(DeviceKind kind, Func<CancellationToken, ValueTask> send)
    {
        var connection = _service.Manager.Connection(kind);
        if (connection is null) return new TestResult { Ok = false, Message = "未找到该设备的连接。" };
        if (connection.State != LinkState.Connected) return new TestResult { Ok = false, Message = $"{connection.Name} 未连接，无法发送。" };

        try
        {
            await send(CancellationToken.None);
            return new TestResult { Ok = true, Message = $"已发送到 {connection.Name}。" };
        }
        catch (Exception ex)
        {
            return new TestResult { Ok = false, Message = $"发送失败：{ex.Message}" };
        }
    }

    // ── 工具 ────────────────────────────────────────────────────────────────

    private static bool TryParseKind(string? text, out DeviceKind kind) =>
        Enum.TryParse(text, ignoreCase: true, out kind) && Enum.IsDefined(kind);

    private static TestResult BadKind(string? kind) =>
        new() { Ok = false, Message = $"未知设备类型「{kind}」，应为 baseStation / radar / droneGps。" };

    private static bool TryParseHex(string hex, out byte[] bytes, out string error)
    {
        bytes = [];
        error = string.Empty;
        var clean = new string(hex.Where(c => !char.IsWhiteSpace(c) && c != '-' && c != ',').ToArray());
        if (clean.Length == 0) { error = "十六进制内容为空。"; return false; }
        if (clean.Length % 2 != 0) { error = "十六进制长度必须为偶数。"; return false; }

        var buffer = new byte[clean.Length / 2];
        for (var i = 0; i < buffer.Length; i++)
        {
            if (!byte.TryParse(clean.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out buffer[i]))
            {
                error = $"「{clean.Substring(i * 2, 2)}」不是合法的十六进制字节。";
                return false;
            }
        }

        bytes = buffer;
        return true;
    }
}
