namespace UavPlatform.Core.Models;

/// <summary>会话（一次运行）的建目录方式。</summary>
public enum SessionFolderMode
{
    /// <summary>每次启动新建一个带时间戳的会话目录，数据互不覆盖。</summary>
    PerRun = 0,

    /// <summary>固定目录，同名文件按序号续接，便于长时间连续值守。</summary>
    Fixed = 1,
}

/// <summary>解析后数据的落盘格式。</summary>
public enum ParsedFormat
{
    /// <summary>每行一条 JSON（JSON Lines）。字段最完整，便于程序与 python/pandas 处理。</summary>
    JsonLines = 0,

    /// <summary>逗号分隔，体积最小，便于 Excel 直接打开。</summary>
    Csv = 1,
}

/// <summary>
/// 存储配置（需求②）。分四层落盘：
/// <list type="number">
/// <item>分设备的**原始字节**与**解析后数据**（各设备一个文件夹）；</item>
/// <item>**雷达转换到基座系**（每帧雷达数据一条，目标换算到以基座为原点的东北天坐标）；</item>
/// <item>**同一帧下三个设备一起的信息**（每出一帧一条，含各自的设备时间与本机时间）；</item>
/// </list>
/// 单文件超过上限自动另创文件续接。任何一条记录都同时带**设备时间**与**本地 PC 时间**，便于事后对齐。
/// </summary>
public sealed class StorageConfig
{
    /// <summary>存储总开关。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>存储根路径。留空时使用 <c>{程序目录}/data</c>。</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>会话目录方式。</summary>
    public SessionFolderMode FolderMode { get; set; } = SessionFolderMode.PerRun;

    /// <summary>会话目录名（<see cref="SessionFolderMode.Fixed"/> 时使用）。</summary>
    public string FixedSessionName { get; set; } = "current";

    /// <summary>单个文件大小上限（MB）。超过后新建 <c>xxx_0002</c> 继续写入。</summary>
    public int MaxFileSizeMb { get; set; } = 64;

    /// <summary>原始数据的落盘格式。</summary>
    public RawFormat RawFormat { get; set; } = RawFormat.Binary;

    /// <summary>解析数据的落盘格式。</summary>
    public ParsedFormat ParsedFormat { get; set; } = ParsedFormat.JsonLines;

    /// <summary>
    /// 是否落盘「雷达转换到基座系」（每收到一帧雷达数据写一条：目标由雷达本体坐标换算到
    /// 以基座为原点的东北天坐标，同时保留距离/方位/俯仰观测量）。
    /// </summary>
    public bool SaveRadarBase { get; set; } = true;

    /// <summary>
    /// 是否落盘「同一帧下三个设备一起的信息」（每出一帧写一条：基座、雷达、无人机各自的位置、
    /// 姿态、设备时间与本机时间，外加该帧的雷达目标）。
    /// </summary>
    public bool SaveFrame { get; set; } = true;

    /// <summary>是否把原始字节一并嵌入解析记录（便于逐帧对账，体积会翻倍）。</summary>
    public bool EmbedRawInParsed { get; set; }

    /// <summary>写入缓冲（字节），达到即刷盘。</summary>
    public int WriteBufferKb { get; set; } = 256;

    /// <summary>强制刷盘间隔（毫秒），保证异常断电时已落盘。</summary>
    public int FlushIntervalMs { get; set; } = 1000;

    /// <summary>是否在会话目录写一份 <c>session.json</c>（记录配置与解析后的字段字典）。</summary>
    public bool WriteManifest { get; set; } = true;

    public StorageConfig Clone() => (StorageConfig)MemberwiseClone();

    /// <summary>解析出实际使用的根路径。</summary>
    public string ResolveRoot(string baseDirectory) =>
        string.IsNullOrWhiteSpace(RootPath) ? Path.Combine(baseDirectory, "data") : RootPath;
}
