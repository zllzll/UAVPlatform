using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UavPlatform.Core.Devices;
using UavPlatform.Core.Relative;
using UavPlatform.Core.Models;

namespace UavPlatform.Core.Storage;

/// <summary>
/// 会话存储（需求②）。
/// 目录结构（按设备分文件夹存原始与解析数据，另有两份派生数据）：
/// <code>
/// {root}/{session}/
///     session.json            清单：配置快照 + 字段字典
///     01_base_station/  raw_0001.bin  parsed_0001.jsonl
///     02_radar/         raw_0001.bin  parsed_0001.jsonl
///     03_drone_gps/     raw_0001.bin  parsed_0001.jsonl
///     04_radar_base/    radar_base_0001.jsonl   雷达目标 → 以基座为原点的东北天坐标
///     05_frame/         frame_0001.jsonl        同一帧下三个设备一起的信息
/// </code>
/// 每一条记录都同时带**设备时间**与**本地 PC 时间**（<c>pcTime</c> 与 <c>deviceTime</c>），
/// 供后续算法把三个设备的数据对齐到同一时间轴。
/// 单个文件超过 <see cref="StorageConfig.MaxFileSizeMb"/> 后自动新建 <c>_0002</c> 续接。
/// <see cref="SessionFolderMode.PerRun"/>（默认）下 <c>{session}</c> 是「开始采集」那一刻的时间戳，
/// **每次开始采集都是新目录**，停止后再开始不会写进上一轮的目录；<see cref="SessionFolderMode.Fixed"/> 下
/// 固定为配置的目录名，同名文件按序号续接。
/// 本类线程安全（各写入器内部加锁，单条记录整体写出，不会交错）。
/// </summary>
public sealed class SessionStorage : IDisposable
{
    /// <summary>设备子目录名（固定 ASCII，避免各类工具编码问题；中文名写在清单里）。</summary>
    public static string FolderOf(DeviceKind kind) => kind switch
    {
        DeviceKind.BaseStation => "01_base_station",
        DeviceKind.Radar => "02_radar",
        DeviceKind.DroneGps => "03_drone_gps",
        _ => "09_other",
    };

    /// <summary>设备中文名，用于清单与界面。</summary>
    public static string LabelOf(DeviceKind kind) => kind switch
    {
        DeviceKind.BaseStation => "基座（UM982）",
        DeviceKind.Radar => "雷达（NSR）",
        DeviceKind.DroneGps => "无人机 GPS（UCM221）",
        _ => "未知设备",
    };

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }.AddNonFiniteHandling(); // NaN → null，与 REST / SignalR 的表示保持一致

    private static readonly JsonSerializerOptions ManifestOptions = new(JsonOptions) { WriteIndented = true };

    private readonly StorageConfig _config;
    private readonly string _root;
    private readonly object _gate = new();
    private readonly Dictionary<DeviceKind, RollingFileWriter> _raw = [];
    private readonly Dictionary<DeviceKind, RollingFileWriter> _parsed = [];
    private readonly HashSet<DeviceKind> _csvHeaderWritten = [];

    private RollingFileWriter? _radarBase;
    private RollingFileWriter? _frame;
    private long _rawRecords;
    private long _parsedRecords;
    private long _radarBaseRecords;
    private long _frameRecords;
    private bool _disposed;
    private DateTimeOffset _startedAt;

    public SessionStorage(StorageConfig config, string baseDirectory)
    {
        _config = config.Clone();
        _root = _config.ResolveRoot(baseDirectory);

        // 这里刻意不建目录：会话目录在每次 Open() 时才分配。
        // 以前在构造函数里建目录，导致两个问题：只构造不开始采集也会留下一个空目录；
        // 停止后再开始会沿用同一个目录，新一轮文件从 _0002 续号，两轮数据混在一起。
    }

    /// <summary>
    /// 本次会话的目录全路径。每次 <see cref="Open"/> 都分配一个新目录，所以开始采集之前是空串。
    /// </summary>
    public string SessionDirectory { get; private set; } = string.Empty;

    /// <summary>
    /// 给这一次采集分配一个全新的会话目录。
    /// <para>
    /// <see cref="SessionFolderMode.PerRun"/>（默认）：用当前时间戳建目录，**每次开始采集都是新目录**。
    /// 撞名时（同一秒内停止后又开始）依次尝试 <c>_2</c>、<c>_3</c>…，绝不与已有目录合并 ——
    /// 一旦合并，<see cref="RollingFileWriter"/> 会探测到已有文件并从 <c>_0002</c> 续号，
    /// 于是两轮数据混在同一个目录里，事后分不清哪条属于哪一轮。
    /// </para>
    /// <para>
    /// <see cref="SessionFolderMode.Fixed"/>：沿用配置里的固定目录名，同名文件按序号续接（长时间连续值守用）。
    /// </para>
    /// </summary>
    private string AllocateSessionDirectory()
    {
        if (_config.FolderMode == SessionFolderMode.Fixed)
        {
            var fixedDirectory = Path.Combine(_root, Sanitize(_config.FixedSessionName, "current"));
            Directory.CreateDirectory(fixedDirectory);
            return fixedDirectory;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(_root, stamp);
        var suffix = 2;
        while (Directory.Exists(candidate))
        {
            candidate = Path.Combine(_root, $"{stamp}_{suffix}");
            suffix++;
        }

        Directory.CreateDirectory(candidate);
        return candidate;
    }

    /// <summary>存储是否启用。</summary>
    public bool Enabled => _config.Enabled;

    /// <summary>本次会话已写出的原始记录条数。</summary>
    public long RawRecords => Interlocked.Read(ref _rawRecords);

    /// <summary>本次会话已写出的解析记录条数。</summary>
    public long ParsedRecords => Interlocked.Read(ref _parsedRecords);

    /// <summary>本次会话已写出的「雷达转换到基座系」记录条数。</summary>
    public long RadarBaseRecords => Interlocked.Read(ref _radarBaseRecords);

    /// <summary>本次会话已写出的「同一帧下三个设备一起」记录条数。</summary>
    public long FrameRecords => Interlocked.Read(ref _frameRecords);

    /// <summary>已写出的总字节数（原始 + 解析 + 雷达转基座系 + 三设备同帧）。</summary>
    public long TotalBytes
    {
        get
        {
            lock (_gate)
            {
                long total = 0;
                foreach (var w in _raw.Values) total += w.TotalBytes;
                foreach (var w in _parsed.Values) total += w.TotalBytes;
                if (_radarBase is not null) total += _radarBase.TotalBytes;
                if (_frame is not null) total += _frame.TotalBytes;
                return total;
            }
        }
    }

    /// <summary>各设备当前正在写入的原始 / 解析文件路径，供界面展示。</summary>
    public IReadOnlyList<(string Device, string RawPath, string ParsedPath)> CurrentFiles
    {
        get
        {
            lock (_gate)
            {
                var list = new List<(string, string, string)>();
                foreach (var kind in new[] { DeviceKind.BaseStation, DeviceKind.Radar, DeviceKind.DroneGps })
                {
                    _raw.TryGetValue(kind, out var rw);
                    _parsed.TryGetValue(kind, out var pw);
                    list.Add((LabelOf(kind), rw?.CurrentPath ?? string.Empty, pw?.CurrentPath ?? string.Empty));
                }
                // 后两份是派生数据，没有原始字节，所以 RawPath 留空（界面据此不画箭头）。
                list.Add(("雷达转基座系", _radarBase?.CurrentPath ?? string.Empty, string.Empty));
                list.Add(("三设备同帧", _frame?.CurrentPath ?? string.Empty, string.Empty));
                return list;
            }
        }
    }

    /// <summary>打开会话：建目录、建各设备写入器、写清单。</summary>
    public void Open(IEnumerable<DeviceConfig> devices, RelativeSettings? relativeSettings = null)
    {
        if (!_config.Enabled) return;

        _startedAt = DateTimeOffset.Now;
        var maxBytes = Math.Max(1, _config.MaxFileSizeMb) * 1024L * 1024L;
        var bufferBytes = Math.Max(4, _config.WriteBufferKb) * 1024;
        var flushMs = Math.Max(100, _config.FlushIntervalMs);
        var deviceList = devices.ToList();

        // 每次开始采集都换一个新目录（Fixed 模式除外），不再沿用上一轮的目录续号。
        // 必须在建写入器之前分配：RollingFileWriter 构造时会探测目录里已有文件并接着编号。
        SessionDirectory = AllocateSessionDirectory();

        lock (_gate)
        {
            // 重新开始采集前先把上一轮的写入器关掉。
            // 之前这里是直接覆盖字典项，旧写入器既不 Dispose 也不 Complete：
            // 它们持有的文件句柄和定时器会被永久泄漏 —— 每重启一轮采集漏 4 个句柄 + 4 个定时器，
            // 而且旧句柄还占着上一轮的文件，表现为「文件建出来了却一直是 0 字节」。
            // Dispose() 内部会先把缓冲刷盘，所以这里不会丢数据。
            foreach (var writer in _raw.Values) writer.Dispose();
            foreach (var writer in _parsed.Values) writer.Dispose();
            _radarBase?.Dispose();
            _frame?.Dispose();
            _raw.Clear();
            _parsed.Clear();
            _radarBase = null;
            _frame = null;

            foreach (var device in deviceList)
            {
                var dir = Path.Combine(SessionDirectory, FolderOf(device.Kind));
                Directory.CreateDirectory(dir);

                if (device.SaveRaw)
                {
                    _raw[device.Kind] = new RollingFileWriter(dir, "raw",
                        _config.RawFormat == RawFormat.Binary ? ".bin" : ".txt",
                        maxBytes, bufferBytes, flushMs);
                }

                if (device.SaveParsed)
                {
                    _parsed[device.Kind] = new RollingFileWriter(dir, "parsed",
                        _config.ParsedFormat == ParsedFormat.JsonLines ? ".jsonl" : ".csv",
                        maxBytes, bufferBytes, flushMs);
                }
            }

            // 二、雷达转换到基座系：每一帧雷达数据一条记录，目标换算到以基座为原点的东北天坐标。
            if (_config.SaveRadarBase)
            {
                var dir = Path.Combine(SessionDirectory, "04_radar_base");
                Directory.CreateDirectory(dir);
                _radarBase = new RollingFileWriter(dir, "radar_base", ".jsonl", maxBytes, bufferBytes, flushMs);
            }

            // 三、同一帧下三个设备一起的信息：每出一帧一条记录，含各自的设备时间与本机时间。
            if (_config.SaveFrame)
            {
                var dir = Path.Combine(SessionDirectory, "05_frame");
                Directory.CreateDirectory(dir);
                _frame = new RollingFileWriter(dir, "frame", ".jsonl", maxBytes, bufferBytes, flushMs);
            }
        }

        if (_config.WriteManifest) WriteManifest(deviceList, relativeSettings);
    }

    /// <summary>写入一段原始字节。</summary>
    public void WriteRaw(DeviceKind kind, in RawSegment segment)
    {
        if (!_config.Enabled || _disposed) return;

        RollingFileWriter? writer;
        lock (_gate)
        {
            if (!_raw.TryGetValue(kind, out writer)) return;
        }

        switch (_config.RawFormat)
        {
            case RawFormat.Binary:
            {
                // 记录格式：[int64 ticks][int32 长度][原始字节]，与参考项目一致，可逐帧回放。
                var header = new byte[12];
                BitConverter.TryWriteBytes(header.AsSpan(0, 8), segment.Timestamp.UtcTicks);
                BitConverter.TryWriteBytes(header.AsSpan(8, 4), segment.Data.Length);
                writer.Write(header);
                writer.Write(segment.Data);
                break;
            }

            case RawFormat.HexText:
            {
                var sb = new StringBuilder(segment.Data.Length * 3 + 48);
                sb.Append(segment.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                sb.Append(" [").Append(segment.Data.Length).Append("] ");
                foreach (var b in segment.Data) sb.Append(b.ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
                writer.WriteLine(sb.ToString());
                break;
            }

            case RawFormat.Text:
            {
                // 基座 NMEA 本身就是文本：原样写出，方便直接看。
                writer.Write(segment.Data);
                if (segment.Data.Length > 0 && segment.Data[^1] != (byte)'\n') writer.Write("\n"u8);
                break;
            }
        }

        Interlocked.Increment(ref _rawRecords);
    }

    /// <summary>写入一条解析后的样本。<paramref name="rawFrame"/> 仅在 <see cref="StorageConfig.EmbedRawInParsed"/> 为真时使用。</summary>
    public void WriteParsed(DeviceKind kind, DeviceSample sample, ReadOnlySpan<byte> rawFrame)
    {
        if (!_config.Enabled || _disposed) return;

        RollingFileWriter? writer;
        lock (_gate)
        {
            if (!_parsed.TryGetValue(kind, out writer)) return;
        }

        if (_config.ParsedFormat == ParsedFormat.JsonLines)
        {
            writer.WriteLine(BuildParsedJson(sample, rawFrame));
        }
        else
        {
            WriteParsedCsv(writer, kind, sample);
        }

        Interlocked.Increment(ref _parsedRecords);
    }

    /// <summary>
    /// 写入一条「雷达转换到基座系」记录：以基座为原点，把该帧雷达的每个目标换算到东北天坐标。
    /// </summary>
    public void WriteRadarBase(RadarToBaseFrame record)
    {
        if (!_config.Enabled || _disposed || !_config.SaveRadarBase) return;

        RollingFileWriter? writer;
        lock (_gate)
        {
            writer = _radarBase;
        }
        if (writer is null) return;

        // 这里刻意不限频：每一帧雷达数据都要留档，否则事后无法复算轨迹。
        writer.WriteLine(JsonSerializer.Serialize(record, JsonOptions));
        Interlocked.Increment(ref _radarBaseRecords);
    }

    /// <summary>
    /// 写入一条「同一帧下三个设备一起的信息」：基座定位与航向、雷达观测、无人机位置姿态，
    /// 各自带设备时间与本机接收时间。
    /// </summary>
    public void WriteFrame(RelativeFrame frame)
    {
        if (!_config.Enabled || _disposed || !_config.SaveFrame) return;

        RollingFileWriter? writer;
        lock (_gate)
        {
            writer = _frame;
        }
        if (writer is null) return;

        writer.WriteLine(JsonSerializer.Serialize(frame, JsonOptions));
        Interlocked.Increment(ref _frameRecords);
    }

    /// <summary>把各写入器缓冲刷入磁盘。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            foreach (var w in _raw.Values) w.Flush();
            foreach (var w in _parsed.Values) w.Flush();
            _radarBase?.Flush();
            _frame?.Flush();
        }
    }

    /// <summary>刷盘并关闭全部文件。</summary>
    public void Complete()
    {
        lock (_gate)
        {
            foreach (var w in _raw.Values) w.Complete();
            foreach (var w in _parsed.Values) w.Complete();
            _radarBase?.Complete();
            _frame?.Complete();
        }
        // 没开始过采集（SessionDirectory 为空）时不要写摘要：
        // Path.Combine("", "session-summary.json") 会落到进程当前目录，在仓库里丢一个野文件。
        if (_config.WriteManifest && SessionDirectory.Length > 0) WriteManifestSummary();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var w in _raw.Values) w.Dispose();
            foreach (var w in _parsed.Values) w.Dispose();
            _radarBase?.Dispose();
            _frame?.Dispose();
            _raw.Clear();
            _parsed.Clear();
            _radarBase = null;
            _frame = null;
        }
    }

    // ── 内部实现 ────────────────────────────────────────────────────────────

    private string BuildParsedJson(DeviceSample sample, ReadOnlySpan<byte> rawFrame)
    {
        // 序列化具体类型（DeviceSample 是抽象基类），保留各设备的全部字段。
        var node = JsonSerializer.SerializeToNode(sample, sample.GetType(), JsonOptions) as JsonObject
                   ?? new JsonObject();

        node["deviceKind"] = sample.Device.ToString();

        if (_config.EmbedRawInParsed && rawFrame.Length > 0)
        {
            node["rawLength"] = rawFrame.Length;
            node["rawHex"] = Convert.ToHexString(rawFrame);
        }

        return node.ToJsonString(JsonOptions);
    }

    private void WriteParsedCsv(RollingFileWriter writer, DeviceKind kind, DeviceSample sample)
    {
        lock (_gate)
        {
            if (_csvHeaderWritten.Add(kind))
            {
                writer.WriteLine(kind switch
                {
                    DeviceKind.BaseStation => "timestamp,deviceTime,latitude,longitude,altitudeM,speedMps,courseDeg,trueHeadingDeg,headingSource,satellites,fixQuality,hdop,geoidSeparationM,magneticVariationDeg,valid,sentences",
                    DeviceKind.Radar => "timestamp,isPointCloud,command,sourceAddress,declaredCount,targetId,targetType,targetTypeName,rangeM,azimuthDeg,elevationDeg,x,y,z,speedX,speedY,speedZ,speedMps,snr,peakEnergyDb,areaMask",
                    DeviceKind.DroneGps => "timestamp,deviceTime,latitude,longitude,altitudeM,relativeAltitudeM,velocityNorthMps,velocityEastMps,velocityDownMps,rollDeg,pitchDeg,headingDeg,gpsSpeedMps,magneticDeclinationDeg,packetStatus,deviceId,targetCount,pointCloudCount,positionValid,attitudeValid",
                    _ => "timestamp",
                });
            }
        }

        switch (sample)
        {
            case GnssSample gnss:
                writer.WriteLine(string.Join(',',
                    Ts(gnss.Timestamp),
                    Csv(gnss.DeviceTime),
                    Num(gnss.Fix.Latitude, 9), Num(gnss.Fix.Longitude, 9),
                    Num(gnss.Fix.AltitudeM, 3), Num(gnss.Fix.SpeedMps, 3), Num(gnss.Fix.CourseDeg, 3),
                    Num(gnss.TrueHeadingDeg, 3), Csv(gnss.HeadingSource),
                    Num(gnss.Fix.Satellites, 0), Num(gnss.Fix.FixQuality, 0), Num(gnss.Fix.Hdop, 3),
                    Num(gnss.Fix.GeoidSeparationM, 3), Num(gnss.Fix.MagneticVariationDeg, 3),
                    gnss.Fix.Valid ? "1" : "0",
                    Csv(string.Join(' ', gnss.Sentences))));
                break;

            case RadarSample radar:
                if (radar.Targets.Length == 0)
                {
                    writer.WriteLine(string.Join(',',
                        Ts(radar.Timestamp), radar.IsPointCloud ? "1" : "0",
                        radar.Command.ToString(CultureInfo.InvariantCulture), radar.SourceAddress.ToString(CultureInfo.InvariantCulture),
                        radar.DeclaredCount.ToString(CultureInfo.InvariantCulture)));
                    break;
                }
                foreach (var t in radar.Targets)
                {
                    writer.WriteLine(string.Join(',',
                        Ts(radar.Timestamp), radar.IsPointCloud ? "1" : "0",
                        radar.Command.ToString(CultureInfo.InvariantCulture), radar.SourceAddress.ToString(CultureInfo.InvariantCulture),
                        radar.DeclaredCount.ToString(CultureInfo.InvariantCulture),
                        t.Id.ToString(CultureInfo.InvariantCulture), t.Type.ToString(CultureInfo.InvariantCulture), Csv(t.TypeName),
                        Num(t.Range, 3), Num(t.AzimuthDeg, 3), Num(t.ElevationDeg, 3),
                        Num(t.X, 3), Num(t.Y, 3), Num(t.Z, 3),
                        Num(t.SpeedX, 3), Num(t.SpeedY, 3), Num(t.SpeedZ, 3), Num(t.SpeedMps, 3),
                        Num(t.Snr, 3), Num(t.PeakEnergyDb, 3), t.AreaMask.ToString(CultureInfo.InvariantCulture)));
                }
                break;

            case DroneGpsSample drone:
                writer.WriteLine(string.Join(',',
                    Ts(drone.Timestamp), Csv(drone.DeviceTime),
                    Num(drone.Fix?.Latitude, 9), Num(drone.Fix?.Longitude, 9), Num(drone.Fix?.AltitudeM, 3),
                    Num(drone.Attitude?.RelativeAltitudeM, 3),
                    Num(drone.Attitude?.VelocityNorthMps, 3), Num(drone.Attitude?.VelocityEastMps, 3), Num(drone.Attitude?.VelocityDownMps, 3),
                    Num(Deg(drone.Attitude?.RollRad), 3), Num(Deg(drone.Attitude?.PitchRad), 3), Num(Deg(drone.Attitude?.HeadingRad), 3),
                    Num(drone.GpsSpeedMps, 3), Num(drone.MagneticDeclinationDeg, 3),
                    drone.PacketStatus.ToString(CultureInfo.InvariantCulture), drone.DeviceId.ToString(CultureInfo.InvariantCulture),
                    drone.TargetCount.ToString(CultureInfo.InvariantCulture), drone.PointCloudCount.ToString(CultureInfo.InvariantCulture),
                    drone.Fix?.Valid == true ? "1" : "0",
                    drone.Attitude is not null ? "1" : "0"));
                break;
        }
    }

    private static string Ts(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static string Num(double? value, int digits) =>
        value is null ? string.Empty : Math.Round(value.Value, digits).ToString("0.######", CultureInfo.InvariantCulture);

    private static string Num(int? value, int digits) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static double? Deg(double? rad) => rad is null ? null : rad.Value * 180.0 / Math.PI;

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Contains(',') || value.Contains('"')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private void WriteManifest(IEnumerable<DeviceConfig> devices, RelativeSettings? relativeSettings)
    {
        try
        {
            var manifest = new JsonObject
            {
                ["schema"] = "uav-platform/session",
                ["schemaVersion"] = 1,
                ["startedAt"] = _startedAt.ToString("O"),
                ["sessionDirectory"] = SessionDirectory,
                ["storage"] = JsonSerializer.SerializeToNode(_config, ManifestOptions),
                ["devices"] = new JsonArray(devices.Select(d => (JsonNode)new JsonObject
                {
                    ["kind"] = d.Kind.ToString(),
                    ["kindLabel"] = LabelOf(d.Kind),
                    ["name"] = d.Name,
                    ["enabled"] = d.Enabled,
                    ["folder"] = FolderOf(d.Kind),
                    ["transport"] = d.Transport.ToString(),
                    ["transportDescription"] = d.TransportSettings.Describe(d.Transport),
                    ["saveRaw"] = d.SaveRaw,
                    ["saveParsed"] = d.SaveParsed,
                }).ToArray()),
                ["fieldDictionary"] = BuildFieldDictionary(),
            };

            if (relativeSettings is not null)
            {
                manifest["relativeSettings"] = JsonSerializer.SerializeToNode(relativeSettings, ManifestOptions);
            }

            var path = Path.Combine(SessionDirectory, "session.json");
            File.WriteAllText(path, manifest.ToJsonString(ManifestOptions), new UTF8Encoding(false));
        }
        catch
        {
            // 清单写失败不影响数据记录
        }
    }

    private void WriteManifestSummary()
    {
        try
        {
            var summary = new JsonObject
            {
                ["finishedAt"] = DateTimeOffset.Now.ToString("O"),
                ["rawRecords"] = RawRecords,
                ["parsedRecords"] = ParsedRecords,
                ["radarBaseRecords"] = RadarBaseRecords,
                ["frameRecords"] = FrameRecords,
                ["totalBytes"] = TotalBytes,
                ["files"] = new JsonArray(CurrentFiles.Select(f => (JsonNode)new JsonObject
                {
                    ["device"] = f.Device,
                    ["raw"] = f.RawPath,
                    ["parsed"] = f.ParsedPath,
                }).ToArray()),
            };

            var path = Path.Combine(SessionDirectory, "session-summary.json");
            File.WriteAllText(path, summary.ToJsonString(ManifestOptions), new UTF8Encoding(false));
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>解析记录里各字段的中文说明，配合 session.json 使用。</summary>
    private static JsonObject BuildFieldDictionary()
    {
        static JsonObject O(params (string Key, string Value)[] pairs)
        {
            var o = new JsonObject();
            foreach (var (k, v) in pairs) o[k] = v;
            return o;
        }

        return new JsonObject
        {
            ["note"] = "parsed 目录下每行一条 JSON（JSON Lines）；CSV 格式时数值列为空表示该字段未收到。",
            ["common"] = O(
                ("device", "设备类型 BaseStation/Radar/DroneGps"),
                ("deviceKind", "同 device，便于外部工具识别"),
                ("timestamp", "上位机收到该帧的本机时间（ISO 8601 带时区）"),
                ("sequence", "该设备自本次连接以来的帧序号"),
                ("deviceTime", "设备自带的时间字符串")),
            ["baseStation.gnss"] = O(
                ("fix.latitude", "WGS84 纬度（度）"),
                ("fix.longitude", "WGS84 经度（度）"),
                ("fix.altitudeM", "海拔高（米）"),
                ("fix.speedMps", "对地速度（米/秒）"),
                ("fix.courseDeg", "对地航向（度，0~360）"),
                ("fix.satellites", "参与解算卫星数"),
                ("fix.fixQuality", "定位质量：0 无效/1 单点/2 差分/4 RTK 固定/5 RTK 浮动"),
                ("fix.hdop", "水平精度因子"),
                ("fix.geoidSeparationM", "大地水准面差距（米）"),
                ("fix.magneticVariationDeg", "磁偏角（度，东偏为正）"),
                ("fix.valid", "该定位是否有效"),
                ("trueHeadingDeg", "真航向（度，0~360），来自 THS 双天线定向"),
                ("headingSource", "航向来源：THS/RMC/VTG"),
                ("sentences", "合成该样本所依据的 NMEA 语句类型")),
            ["radar.targets"] = O(
                ("isPointCloud", "true 表示来自点云传输（命令 0xA9），false 表示目标传输（0xA8）"),
                ("command", "命令码"),
                ("sourceAddress", "源地址编码"),
                ("declaredCount", "帧内声明的目标/点云个数"),
                ("targets[].id", "目标 ID"),
                ("targets[].type", "目标类型：0 未识别/1 人/2 车/3 树/4 船/6 小船/7 中船/8 大船/65535 已删除"),
                ("targets[].x,y,z", "雷达本体坐标系（米）：+Y 为探测方向，+X 向右，+Z 向上"),
                ("targets[].range", "测量距离（米）"),
                ("targets[].azimuthDeg", "方位角（度）"),
                ("targets[].elevationDeg", "俯仰角（度）"),
                ("targets[].speedX,speedY,speedZ", "X/Y/Z 方向速度（米/秒）"),
                ("targets[].snr", "信噪比"),
                ("targets[].peakEnergyDb", "峰值能量（dB）"),
                ("targets[].areaMask", "报警区域位掩码，低字节在前，bit0 = 第一防区")),
            ["droneGps"] = O(
                ("fix", "GPS 定位，来自无人机信息模块（0xA22A）或扩展信息模块（0xA33A）"),
                ("attitude", "姿态：rollRad/pitchRad/headingRad 为弧度，航向顺时针为正"),
                ("attitude.velocityNorthMps/EastMps/DownMps", "飞行速度（米/秒）"),
                ("attitude.relativeAltitudeM", "相对地面高度（米）"),
                ("imu", "IMU 双组加速度/角速度"),
                ("gpsSpeedMps", "GPS 对地速度（米/秒）"),
                ("magneticDeclinationDeg", "磁偏角（度，东偏为正）"),
                ("packetStatus", "汇总信息里的数据包状态位掩码：bit0 数据有效，bit1 扩展信息有效，bit2 无人机信息有效")),
            ["radarBase"] = O(
                ("pcTime", "上位机收到该帧雷达数据的本机时间（ISO 8601 带时区）"),
                ("deviceTime", "该帧数据里设备自带的时间（若协议携带）"),
                ("sequence", "该设备自本次连接以来的帧序号"),
                ("trigger", "触发本记录的原因"),
                ("baseResolved", "是否已由基座定位确定原点；false 时以下坐标不可用"),
                ("origin", "原点（基座）的 WGS84 坐标"),
                ("boresightDeg", "解算出的雷达正前方绝对方位角（度）"),
                ("boresightFromBaseline", "true 表示由双天线基线航向 + 夹角解出，false 表示用配置里的固定值"),
                ("baselineHeadingDeg", "双天线基线真航向（度，来自 THS）"),
                ("radarEast/North/Up", "雷达在以基座为原点的东北天坐标系里的位置（米）"),
                ("isPointCloud", "true 表示来自点云传输（命令 0xA9），false 表示目标传输（0xA8）"),
                ("command", "命令码"),
                ("declaredCount", "帧内声明的目标/点云个数"),
                ("targets[].east/north/up", "目标在以基座为原点的东北天坐标系里的位置（米）"),
                ("targets[].rangeM/azimuthDeg/elevationDeg", "相对雷达的距离与角度（保持原始观测量，便于核对）"),
                ("targets[].distanceToDroneM", "目标与无人机的三维距离（米）"),
                ("targets[].heightAboveDroneM", "目标相对无人机的垂直高度（米）")),
            ["frame"] = O(
                ("pcTime", "本帧的生成时间（本机时间，ISO 8601 带时区）"),
                ("trigger", "触发本帧的原因"),
                ("origin", "显示坐标系原点：BaseStation=0，Radar=1"),
                ("originName", "原点对应的设备名"),
                ("reference", "参考点 WGS84 坐标（local 坐标的零点）"),
                ("referenceResolved", "参考点是否已确定；false 时坐标不可用"),
                ("baseStation/radar/drone.pcTime", "该设备这一帧数据被本机收到的时刻（跨设备对齐用）"),
                ("baseStation/radar/drone.deviceTime", "该设备这一帧数据自带的时间（若协议携带）"),
                ("baseStation/radar/drone.sequence", "该设备这一帧数据的序号"),
                ("baseStation/radar/drone.ageMs", "相对本帧生成时刻的数据年龄（毫秒）"),
                ("baseStation/radar/drone.position", "显示坐标系下的位置（米，east/north/up），原点即 (0,0,0)"),
                ("targets[]", "该帧的雷达目标，换算规则同 04_radar_base（字段说明见 radarBase 段）"),
                ("droneDistanceToBaseM", "无人机与基座的水平距离（米）"),
                ("droneHeightAboveBaseM", "无人机相对基座的高度差（米）"),
                ("radarBaseLineM", "雷达与基座之间的基线长度（米）"),
                ("baseToDroneSkewMs", "基座定位与无人机定位之间的时间差（毫秒）"),
                ("droneComparison", "无人机 RTK 位置与雷达最近目标的偏差（雷达 − RTK）")),
        };
    }

    private static string Sanitize(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars);
        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }
}
