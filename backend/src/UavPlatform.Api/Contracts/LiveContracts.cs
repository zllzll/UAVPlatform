using UavPlatform.Core.Relative;
using UavPlatform.Core.Models;

namespace UavPlatform.Api.Contracts;

/// <summary>一条解析数据的摘要（用于界面的实时数据表，不做全字段展开）。</summary>
public sealed record SampleSummary
{
    public string Device { get; init; } = string.Empty;
    public string DeviceLabel { get; init; } = string.Empty;
    public string DeviceKind { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
    public long Sequence { get; init; }

    /// <summary>表格「内容」列：把该设备最关键的字段压成一行文字。</summary>
    public string Summary { get; init; } = string.Empty;

    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? AltitudeM { get; init; }
    public double? SpeedMps { get; init; }
    public double? HeadingDeg { get; init; }
    public int? Satellites { get; init; }
    public int? FixQuality { get; init; }

    public int TargetCount { get; init; }
    public bool IsPointCloud { get; init; }
    public double? RangeM { get; init; }
    public double? AzimuthDeg { get; init; }
    public double? ElevationDeg { get; init; }
    public double? Snr { get; init; }
    public uint? TargetId { get; init; }
    public string? TargetType { get; init; }

    /// <summary>构造摘要。</summary>
    public static SampleSummary From(DeviceSample sample, string deviceLabel)
    {
        var summary = new SampleSummary
        {
            Device = sample.DeviceName ?? string.Empty,
            DeviceLabel = deviceLabel,
            DeviceKind = sample.Device.ToString(),
            Timestamp = sample.Timestamp,
            Sequence = sample.Sequence,
        };

        switch (sample)
        {
            case GnssSample gnss:
                return summary with
                {
                    Latitude = gnss.Fix.Latitude,
                    Longitude = gnss.Fix.Longitude,
                    AltitudeM = gnss.Fix.AltitudeM,
                    SpeedMps = gnss.Fix.SpeedMps,
                    HeadingDeg = gnss.TrueHeadingDeg ?? gnss.Fix.CourseDeg,
                    Satellites = gnss.Fix.Satellites,
                    FixQuality = gnss.Fix.FixQuality,
                    Summary = $"{(gnss.Fix.Valid ? "有效" : "无效")} · {gnss.Fix.Latitude:F7}, {gnss.Fix.Longitude:F7} · " +
                              $"海拔 {gnss.Fix.AltitudeM:F2} m · 卫 {gnss.Fix.Satellites} · 质量 {gnss.Fix.FixQuality}" +
                              (gnss.TrueHeadingDeg is { } h ? $" · 真航向 {h:F2}°" : string.Empty),
                };

            case RadarSample radar:
            {
                var first = radar.Targets.Length > 0 ? radar.Targets[0] : null;
                var kind = radar.IsPointCloud ? "点云" : "目标";
                return summary with
                {
                    TargetCount = radar.Targets.Length,
                    IsPointCloud = radar.IsPointCloud,
                    RangeM = first?.Range,
                    AzimuthDeg = first?.AzimuthDeg,
                    ElevationDeg = first?.ElevationDeg,
                    Snr = first?.Snr,
                    TargetId = first?.Id,
                    TargetType = first?.TypeName,
                    Summary = $"{kind} 命令 0x{radar.Command:X2} · {radar.Targets.Length} 个" +
                              (first is null
                                  ? " · 本帧无数据"
                                  : $" · 最近 #{first.Id}({first.TypeName}) {first.Range:F1} m " +
                                    $"方位 {first.AzimuthDeg:F1}° 俯仰 {first.ElevationDeg:F1}° SNR {first.Snr:F1}"),
                };
            }

            case DroneGpsSample drone:
                return summary with
                {
                    Latitude = drone.Fix?.Latitude,
                    Longitude = drone.Fix?.Longitude,
                    AltitudeM = drone.Fix?.AltitudeM,
                    SpeedMps = drone.GpsSpeedMps,
                    HeadingDeg = drone.Attitude?.HeadingRad is { } rad ? GeoMath.Normalize360(GeoMath.ToDegrees(rad)) : null,
                    Satellites = drone.Fix?.Satellites,
                    FixQuality = drone.Fix?.FixQuality,
                    TargetCount = drone.TargetCount,
                    IsPointCloud = drone.PointCloudCount > 0,
                    Summary = (drone.Fix is null
                                  ? "无 GPS"
                                  : $"{drone.Fix.Latitude:F7}, {drone.Fix.Longitude:F7} · 海拔 {drone.Fix.AltitudeM:F2} m") +
                              (drone.Attitude is null
                                  ? string.Empty
                                  : $" · 相对高 {drone.Attitude.RelativeAltitudeM:F2} m" +
                                    $" · 航向 {GeoMath.Normalize360(GeoMath.ToDegrees(drone.Attitude.HeadingRad ?? 0)):F1}°") +
                              $" · 状态 0x{drone.PacketStatus:X2}",
                };

            default:
                return summary with { Summary = sample.GetType().Name };
        }
    }
}

/// <summary>一次连接建立时下发的完整快照。</summary>
public sealed record PlatformSnapshot
{
    public PlatformStatus Status { get; init; } = new();
    public PlatformConfig Config { get; init; } = new();
    public RelativeFrame? Relative { get; init; }
    public TrackSnapshot Tracks { get; init; } = new();
    public IReadOnlyList<SampleSummary> Samples { get; init; } = [];
    public IReadOnlyList<string> Logs { get; init; } = [];
}

/// <summary>轨迹快照。</summary>
public sealed record TrackSnapshot
{
    public TrackPoint[] Drone { get; init; } = [];
    public Dictionary<string, TrackPoint[]> Targets { get; init; } = [];
}

/// <summary>存储会话（已完成或正在写入）的浏览条目。</summary>
public sealed record SessionInfo
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public DateTimeOffset? StartedAt { get; init; }
    public long TotalBytes { get; init; }
    public int FileCount { get; init; }
    public bool Active { get; init; }
    public IReadOnlyList<string> SubFolders { get; init; } = [];
}

/// <summary>设备连通性测试的返回。</summary>
public sealed record TestResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = string.Empty;
    public double ElapsedMs { get; init; }
}
