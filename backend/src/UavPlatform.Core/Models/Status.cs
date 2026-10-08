using UavPlatform.Core.Relative;

namespace UavPlatform.Core.Models;

/// <summary>单台设备的运行状态快照（供界面展示）。</summary>
public sealed record DeviceStatus
{
    public string Kind { get; init; } = string.Empty;
    public string KindLabel { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool Enabled { get; init; }

    /// <summary>链路状态：Disabled / Disconnected / Connecting / Connected / Faulted。</summary>
    public string State { get; init; } = nameof(LinkState.Disabled);

    public string Transport { get; init; } = string.Empty;

    /// <summary>通讯参数的文字描述，例如 <c>TCP 客户端 192.168.1.10:50000</c>。</summary>
    public string TransportDescription { get; init; } = string.Empty;

    public string Protocol { get; init; } = string.Empty;
    public string? RemoteEndPoint { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? LastDataAt { get; init; }
    public double IdleMs { get; init; }
    public int Reconnects { get; init; }

    public long BytesReceived { get; init; }
    public long FramesReceived { get; init; }
    public long ParseErrors { get; init; }
    public long SkippedBytes { get; init; }
    public long Samples { get; init; }
    public long TargetCount { get; init; }

    public bool SaveRaw { get; init; }
    public bool SaveParsed { get; init; }
}

/// <summary>存储运行状态快照。</summary>
public sealed record StorageStatus
{
    public bool Enabled { get; init; }
    public string SessionDirectory { get; init; } = string.Empty;
    public long RawRecords { get; init; }
    public long ParsedRecords { get; init; }

    /// <summary>已写出的「雷达转换到基座系」记录条数（04_radar_base）。</summary>
    public long RadarBaseRecords { get; init; }

    /// <summary>已写出的「同一帧下三个设备一起的信息」记录条数（05_frame）。</summary>
    public long FrameRecords { get; init; }
    public long TotalBytes { get; init; }
    public IReadOnlyList<StorageFileStatus> Files { get; init; } = [];
}

/// <summary>某个设备正在写入的文件。</summary>
public sealed record StorageFileStatus
{
    public string Device { get; init; } = string.Empty;
    public string RawPath { get; init; } = string.Empty;
    public string ParsedPath { get; init; } = string.Empty;
}

/// <summary>平台整体状态快照（REST /api/status 与 SignalR 状态推送）。</summary>
public sealed record PlatformStatus
{
    public string Name { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public bool Running { get; init; }
    /// <summary>显示坐标系原点的设备名。本项目恒为基座（世界 ENU 系）。</summary>
    public string OriginName { get; init; } = "基座";
    public bool ReferenceResolved { get; init; }
    public GeoReference Reference { get; init; } = new();
    public IReadOnlyList<DeviceStatus> Devices { get; init; } = [];
    public StorageStatus Storage { get; init; } = new();
    public long RelativeFrames { get; init; }
    public long DroppedRelativeFrames { get; init; }
    public int DroneTrackPoints { get; init; }
    public int TrackedTargets { get; init; }
    public RelativeNode? Drone { get; init; }
    public RelativeNode? BaseStation { get; init; }
    public RelativeNode? Radar { get; init; }
    public double? DroneDistanceToBaseM { get; init; }
    public double? DroneHeightAboveBaseM { get; init; }
    public double? DroneDistanceToRadarM { get; init; }
}
