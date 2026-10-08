using System.Text.Json;
using System.Text.Json.Serialization;

namespace UavPlatform.Core.Models;

/// <summary>界面显示选项（后端不需要，但集中存放便于一键导出配置）。</summary>
public sealed class UiSettings
{
    /// <summary>三维视图默认显示内容。</summary>
    public bool ShowTargets { get; set; } = true;

    /// <summary>是否显示点云（0xA9）。</summary>
    public bool ShowPointCloud { get; set; } = true;

    /// <summary>是否显示无人机本身（图标 + 航向箭头 + 光晕 + RTK 位置标记）。</summary>
    public bool ShowDrone { get; set; } = true;

    /// <summary>是否显示无人机轨迹。</summary>
    public bool ShowDroneTrack { get; set; } = true;

    /// <summary>是否显示目标拖尾。</summary>
    public bool ShowTargetTrails { get; set; } = true;

    /// <summary>在三维/二维视图中显示无人机到基座、雷达的测距连线（测距辅助线开关）。</summary>
    public bool ShowLinks { get; set; } = true;

    /// <summary>尾迹时长（秒，0~600）：超过这个时长的轨迹点逐点消失，数据断流整条清空；0 表示不画尾迹。
    /// 后端的轨迹保留窗口也由它推出（见 DeviceManager.ApplyTrackRetention），不再单独配置。</summary>
    public int TrailSeconds { get; set; } = 60;

    /// <summary>点云点大小。</summary>
    public double PointSize { get; set; } = 1.0;

    /// <summary>网格范围（米）。</summary>
    public double GridSizeM { get; set; } = 200;

    public UiSettings Clone() => (UiSettings)MemberwiseClone();
}

/// <summary>
/// 平台总配置：三台设备的通讯参数 + 存储 + 相对位置显示 + 界面选项。
/// 这是 <c>config/platform.json</c> 的根对象，也是「三个设备通讯参数可配置」的落点。
/// </summary>
public sealed class PlatformConfig
{
    /// <summary>配置结构版本，便于后续兼容升级。</summary>
    public int Version { get; set; } = 1;

    /// <summary>平台名称（仅展示）。</summary>
    public string Name { get; set; } = "无人机 + RTK 平台";

    /// <summary>三台设备。</summary>
    public List<DeviceConfig> Devices { get; set; } = [];

    /// <summary>存储配置。</summary>
    public StorageConfig Storage { get; set; } = new();

    /// <summary>相对位置与三维显示配置。</summary>
    public RelativeSettings Relative { get; set; } = new();

    /// <summary>界面选项。</summary>
    public UiSettings Ui { get; set; } = new();

    /// <summary>创建带默认值的配置（基座 COM3 / 雷达 192.168.10.128:50000 / 无人机 0.0.0.0:50000）。</summary>
    public static PlatformConfig CreateDefault()
    {
        var config = new PlatformConfig();
        foreach (var kind in new[] { DeviceKind.BaseStation, DeviceKind.Radar, DeviceKind.DroneGps })
        {
            config.Devices.Add(DeviceConfig.CreateDefault(kind));
        }
        return config;
    }

    /// <summary>按设备类型取配置。</summary>
    public DeviceConfig? Device(DeviceKind kind) => Devices.FirstOrDefault(d => d.Kind == kind);

    /// <summary>补齐缺失的设备项（手工改过配置文件时使用）。</summary>
    public void EnsureAllDevices()
    {
        foreach (var kind in new[] { DeviceKind.BaseStation, DeviceKind.Radar, DeviceKind.DroneGps })
        {
            if (Device(kind) is null) Devices.Add(DeviceConfig.CreateDefault(kind));
        }
        Devices.Sort((a, b) => a.Kind.CompareTo(b.Kind));
    }

    /// <summary>
    /// 两份配置里「与采集相关的三段」（设备连接、存储、相对位置）是否等价。
    ///
    /// 采集进行中只允许改这三段以外的内容（平台名称、界面显示选项）：这三段任一改动，
    /// 热应用都会把采集管线停掉重建——存储换会话目录、设备重连、参考点重置，
    /// 落盘数据当场分成两段，事后没法当成一次采集来分析。
    /// 按 JSON 逐字段比：配置对象嵌套多层，手写 Equals 早晚会漏字段。
    /// </summary>
    public bool SameAcquisitionAs(PlatformConfig other)
    {
        EnsureAllDevices();
        other.EnsureAllDevices();
        return SameJson(Devices, other.Devices)
            && SameJson(Storage, other.Storage)
            && SameJson(Relative, other.Relative);
    }

    /// <summary>按配置文件同一套序列化规则，比两段配置是否逐字段一致。</summary>
    private static bool SameJson<T>(T left, T right) =>
        JsonSerializer.Serialize(left, ConfigJson.Options) == JsonSerializer.Serialize(right, ConfigJson.Options);

    public PlatformConfig Clone() => new()
    {
        Version = Version,
        Name = Name,
        Devices = Devices.Select(d => d.Clone()).ToList(),
        Storage = Storage.Clone(),
        Relative = Relative.Clone(),
        Ui = Ui.Clone(),
    };
}

/// <summary>配置 JSON 的序列化选项（枚举写成字符串，便于人工编辑与差错）。</summary>
public static class ConfigJson
{
    public static readonly JsonSerializerOptions Options = new JsonSerializerOptions()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }.AddNonFiniteHandling();

    /// <summary>网络传输用（不缩进，其余与 <see cref="Options"/> 一致）。</summary>
    public static readonly JsonSerializerOptions WireOptions = new JsonSerializerOptions()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }.AddNonFiniteHandling();

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>配置文件的读写（默认 <c>{程序目录}/config/platform.json</c>）。</summary>
public sealed class ConfigStore
{
    private readonly object _gate = new();

    public ConfigStore(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    /// <summary>配置文件全路径。</summary>
    public string Path { get; }

    /// <summary>配置发生变化（保存成功）时触发。</summary>
    public event Action<PlatformConfig>? Changed;

    /// <summary>读取配置；文件不存在或损坏时返回默认配置并落盘。</summary>
    public PlatformConfig Load()
    {
        lock (_gate)
        {
            PlatformConfig config;
            if (File.Exists(Path))
            {
                try
                {
                    var json = File.ReadAllText(Path);
                    config = ConfigJson.Deserialize<PlatformConfig>(json) ?? PlatformConfig.CreateDefault();
                }
                catch
                {
                    // 配置损坏：备份原文件后用默认配置启动，避免程序起不来
                    try { File.Copy(Path, Path + ".bad", overwrite: true); } catch { /* 忽略 */ }
                    config = PlatformConfig.CreateDefault();
                    PersistDefault(config);
                }
            }
            else
            {
                config = PlatformConfig.CreateDefault();
                PersistDefault(config);
            }

            config.EnsureAllDevices();
            return config;
        }
    }

    /// <summary>
    /// 把默认配置写盘，让「文件不存在」这条路径下也真的留下一份可读的配置。
    ///
    /// 写失败（目录只读等）只忽略：这里的目标是补齐落盘，不能因为写不进去就让程序起不来。
    /// </summary>
    private void PersistDefault(PlatformConfig config)
    {
        try
        {
            File.WriteAllText(Path, ConfigJson.Serialize(config), new System.Text.UTF8Encoding(false));
        }
        catch
        {
            // 忽略：读取阶段的兜底不能升级成启动失败
        }
    }

    /// <summary>写入配置。</summary>
    public void Save(PlatformConfig config)
    {
        lock (_gate)
        {
            var json = ConfigJson.Serialize(config);
            File.WriteAllText(Path, json, new System.Text.UTF8Encoding(false));
        }
        Changed?.Invoke(config);
    }
}
