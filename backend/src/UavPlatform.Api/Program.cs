using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using UavPlatform.Api.Contracts;
using UavPlatform.Api.Hubs;
using UavPlatform.Api.Services;
using UavPlatform.Core.Devices;
using UavPlatform.Core.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddSingleton<PlatformService>();
builder.Services.AddHostedService<LiveBroadcaster>();

// REST 与 SignalR 共用一套 JSON 约定：camelCase + 字符串枚举（与 config/platform.json 一致）。
static void ApplyJson(JsonSerializerOptions o)
{
    o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.PropertyNameCaseInsensitive = true;
    o.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true));
    o.AddNonFiniteHandling(); // NaN / Infinity → null，避免设备无效值把响应打成 500
}

builder.Services.ConfigureHttpJsonOptions(o => ApplyJson(o.SerializerOptions));
builder.Services.AddSignalR(o => o.EnableDetailedErrors = true)
    .AddJsonProtocol(o => ApplyJson(o.PayloadSerializerOptions));

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .SetIsOriginAllowed(origin =>
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        return uri.IsLoopback;
    })
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/health", (PlatformService s) => Results.Ok(new
{
    ok = true,
    running = s.Running,
    clients = 0,
    configPath = s.Store.Path,
    sessionDirectory = s.Manager.Storage.SessionDirectory,
    storageRoot = s.StorageRoot,
    time = DateTimeOffset.Now,
}));

api.MapGet("/status", (PlatformService s) => s.Manager.GetStatus());
api.MapGet("/snapshot", (PlatformService s) => s.Snapshot());
api.MapGet("/relative", (PlatformService s) => s.Manager.CurrentFrame);
api.MapGet("/sessions", (PlatformService s) => s.ListSessions());
api.MapGet("/logs", (PlatformService s) => s.Manager.RecentLogs);

api.MapGet("/tracks", (PlatformService s) =>
{
    var tracks = s.Manager.Tracks;
    return new TrackSnapshot
    {
        Drone = tracks.DroneTrack(),
        Targets = tracks.TargetTracks().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
    };
});

// ── 配置 ────────────────────────────────────────────────────────────────────
api.MapGet("/config", (PlatformService s) => s.Config);
api.MapGet("/config/default", () => PlatformConfig.CreateDefault());
api.MapPut("/config", async (PlatformConfig config, PlatformService s) =>
{
    // 采集中挡掉通讯 / 存储 / 相对位置的改动：这三段一变，热应用就要把管线停掉重建，
    // 本次会话的落盘数据当场分成两段（存储换目录、设备重连、参考点重置）。
    // 前端已把面板锁灰，这里再兜一道——旧标签页、脚本、直连 curl 都绕不过去。
    if (s.Running && s.AcquisitionChanged(config))
        return Results.Conflict(new TestResult { Ok = false, Message = "采集中不能修改通讯、存储与相对位置参数，请先停止采集。" });

    return Results.Ok(await s.SaveConfigAsync(config));
});

// 界面显示选项单独一个入口：只改 ui 段、不重启采集管线，供「显示目标/点云/轨迹」这类开关使用。
api.MapPut("/config/ui", (UiSettings ui, PlatformService s) => s.SaveUiAsync(ui));

// ── 串口 ────────────────────────────────────────────────────────────────────
// 枚举本机可用串口供前端下拉框使用；无串口 / 非 Windows / 驱动异常时返回空数组，不返回 500。
api.MapGet("/serial-ports", () => new { ports = SerialPortEnumerator.GetPortNames() });

// ── 运行控制 ────────────────────────────────────────────────────────────────
api.MapPost("/platform/start", async (PlatformService s) =>
{
    if (s.Running) return Results.Ok(new TestResult { Ok = true, Message = "平台已在运行。" });
    await s.StartAsync();
    return Results.Ok(new TestResult { Ok = true, Message = $"已开始采集，会话目录：{s.Manager.Storage.SessionDirectory}" });
});

api.MapPost("/platform/stop", async (PlatformService s) =>
{
    if (!s.Running) return Results.Ok(new TestResult { Ok = true, Message = "平台已停止。" });
    await s.StopAsync();
    return Results.Ok(new TestResult { Ok = true, Message = "已停止采集，数据已刷盘。" });
});

api.MapPost("/devices/{kind}/reconnect", async (string kind, PlatformService s) =>
    Enum.TryParse<DeviceKind>(kind, ignoreCase: true, out var k)
        ? Results.Ok(await s.ReconnectAsync(k))
        : Results.BadRequest(new TestResult { Ok = false, Message = $"未知设备类型：{kind}" }));

api.MapPost("/devices/{kind}/send", async (string kind, DeviceCommandRequest request, PlatformService s) =>
{
    if (!Enum.TryParse<DeviceKind>(kind, ignoreCase: true, out var k))
        return Results.BadRequest(new TestResult { Ok = false, Message = $"未知设备类型：{kind}" });

    var connection = s.Manager.Connection(k);
    if (connection is null) return Results.Ok(new TestResult { Ok = false, Message = "未找到该设备的连接。" });
    if (connection.State != LinkState.Connected)
        return Results.Ok(new TestResult { Ok = false, Message = $"{connection.Name} 未连接，无法发送。" });

    try
    {
        if (!string.IsNullOrEmpty(request.Hex))
        {
            var clean = new string(request.Hex.Where(c => !char.IsWhiteSpace(c) && c != '-' && c != ',').ToArray());
            if (clean.Length % 2 != 0) return Results.Ok(new TestResult { Ok = false, Message = "十六进制长度必须为偶数。" });
            var bytes = Convert.FromHexString(clean);
            await connection.SendAsync(bytes);
            return Results.Ok(new TestResult { Ok = true, Message = $"已发送 {bytes.Length} 字节到 {connection.Name}。" });
        }

        await connection.SendTextAsync(request.Text ?? string.Empty);
        return Results.Ok(new TestResult { Ok = true, Message = $"已发送到 {connection.Name}。" });
    }
    catch (Exception ex)
    {
        return Results.Ok(new TestResult { Ok = false, Message = $"发送失败：{ex.Message}" });
    }
});

// ── 相对位置 ────────────────────────────────────────────────────────────────────
api.MapPost("/relative/reset-reference", (PlatformService s) =>
{
    s.Manager.Relative.ResetReference();
    return Results.Ok(new TestResult { Ok = true, Message = "已重新解算显示坐标系原点。" });
});

api.MapPost("/relative/force", (PlatformService s) =>
{
    var frame = s.Manager.ForceRebuild("api");
    return Results.Ok(frame is null
        ? new TestResult { Ok = false, Message = "暂无可用数据，无法相对位置。" }
        : new TestResult { Ok = true, Message = $"已相对位置：原点 {frame.OriginName}，目标 {frame.Targets.Length} 个。" });
});

api.MapPost("/tracks/clear", (PlatformService s) =>
{
    s.Manager.ClearTracks();
    return Results.Ok(new TestResult { Ok = true, Message = "已清空轨迹。" });
});

app.MapHub<PlatformHub>("/hubs/platform");

// 前端已构建时直接托管（run.ps1 只用开一个端口）。
var indexFile = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "index.html");
if (File.Exists(indexFile)) app.MapFallbackToFile("index.html");

app.Run();

/// <summary>设备下行数据请求体。</summary>
internal sealed record DeviceCommandRequest(string? Text, string? Hex);
