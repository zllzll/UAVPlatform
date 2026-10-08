using System.Globalization;
using UavPlatform.Core.Devices;
using UavPlatform.Core.Models;
using UavPlatform.Core.Storage;

namespace UavPlatform.Tests;

/// <summary>
/// 会话目录分配测试。
/// 重点是把「同一进程内停止采集后再开始」的行为钉死：默认模式下必须换一个**新目录**，
/// 而不是沿用上一轮目录让 <see cref="RollingFileWriter"/> 从 _0002 接着编号 ——
/// 那样两轮数据会混在同一个目录里，事后分不清哪条属于哪一轮。
/// </summary>
public class SessionStorageTests : IDisposable
{
    private readonly string _root;

    public SessionStorageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "UavPlatformTests", "session-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响断言结果
        }

        GC.SuppressFinalize(this);
    }

    private StorageConfig PerRunConfig() => new()
    {
        Enabled = true,
        RootPath = _root,
        FolderMode = SessionFolderMode.PerRun,
        SaveRadarBase = false,   // 本组只关心目录分配，不需要派生数据写入器
        SaveFrame = false,
        WriteManifest = true,
    };

    private static DeviceConfig[] Devices() =>
    [
        DeviceConfig.CreateDefault(DeviceKind.Radar),
        DeviceConfig.CreateDefault(DeviceKind.DroneGps),
    ];

    private string RawPath(string sessionDirectory, DeviceKind kind, string fileName) =>
        Path.Combine(sessionDirectory, SessionStorage.FolderOf(kind), fileName);

    [Fact]
    public void 未开始采集时会话目录应为空且不应凭空建目录()
    {
        using var storage = new SessionStorage(PerRunConfig(), _root);

        Assert.Equal(string.Empty, storage.SessionDirectory);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void 每次开始采集都应新建会话目录_停止后再开始不沿用旧目录()
    {
        using var storage = new SessionStorage(PerRunConfig(), _root);
        var devices = Devices();

        storage.Open(devices);
        var first = storage.SessionDirectory;
        Assert.True(Directory.Exists(first));
        storage.WriteRaw(DeviceKind.Radar, new RawSegment(DateTimeOffset.Now, [0x01, 0x02, 0x03]));
        storage.Complete();

        // 停止采集后再开始：必须落在另一个目录
        storage.Open(devices);
        var second = storage.SessionDirectory;
        Assert.True(Directory.Exists(second));
        Assert.NotEqual(first, second);

        storage.WriteRaw(DeviceKind.Radar, new RawSegment(DateTimeOffset.Now, [0x04, 0x05]));
        storage.Complete();

        // 两个目录各自从 _0001 开始：第二轮没有因为探测到第一轮的文件而续号
        var firstRaw = RawPath(first, DeviceKind.Radar, "raw_0001.bin");
        var secondRaw = RawPath(second, DeviceKind.Radar, "raw_0001.bin");
        Assert.True(File.Exists(firstRaw), $"第一轮应写入 {firstRaw}");
        Assert.True(File.Exists(secondRaw), $"第二轮应写入 {secondRaw}");
        Assert.False(File.Exists(RawPath(second, DeviceKind.Radar, "raw_0002.bin")));

        // 每个文件只带自己那一轮的载荷：第一轮没被第二轮追加，第二轮也没重新写上第一轮的数据
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, ReadSingleRawPayload(firstRaw));
        Assert.Equal(new byte[] { 0x04, 0x05 }, ReadSingleRawPayload(secondRaw));

        // 第一轮的目录只属于第一轮：原始数据只有一个分片（同目录下还有 parsed_*.jsonl，所以只数 raw_*）
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(firstRaw)!, "raw_*"));
    }

    /// <summary>读回二进制原始记录（[int64 ticks][int32 长度][原始字节]）里唯一的载荷，并确认没有多余追加。</summary>
    private static byte[] ReadSingleRawPayload(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 12, $"{path} 连 12 字节记录头都不够");
        var length = BitConverter.ToInt32(bytes, 8);
        Assert.Equal(12 + length, bytes.Length);   // 正好一条记录，多一个字节都说明被追加过
        return bytes[12..];
    }

    [Fact]
    public void 同一秒内停止后又开始_会话目录应加序号后缀而不是合并()
    {
        // 预先把「当前这一秒」会分到的目录占掉，模拟同一秒内停止后又开始
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var occupied = Path.Combine(_root, stamp);
        Directory.CreateDirectory(occupied);

        using var storage = new SessionStorage(PerRunConfig(), _root);
        storage.Open(Devices());

        var allocated = storage.SessionDirectory;
        Assert.NotEqual(occupied, allocated);
        Assert.True(Directory.Exists(allocated));

        // 已存在的目录一个字节都不许写进去，否则两轮数据就混了
        Assert.Empty(Directory.GetFileSystemEntries(occupied));

        // 若还落在同一秒，必须退让到 _2；跨秒了就是普通新目录，不断言后缀
        var name = Path.GetFileName(allocated);
        if (name.StartsWith(stamp, StringComparison.Ordinal))
        {
            Assert.Equal(stamp + "_2", name);
        }
    }

    [Fact]
    public void 固定目录模式应沿用同一目录并在第二次开始时从_0002续接()
    {
        var config = PerRunConfig();
        config.FolderMode = SessionFolderMode.Fixed;
        config.FixedSessionName = "fixed";

        using var storage = new SessionStorage(config, _root);
        var devices = Devices();
        var expected = Path.Combine(_root, "fixed");

        storage.Open(devices);
        Assert.Equal(expected, storage.SessionDirectory);
        storage.WriteRaw(DeviceKind.Radar, new RawSegment(DateTimeOffset.Now, [0x01]));
        storage.Complete();

        storage.Open(devices);
        Assert.Equal(expected, storage.SessionDirectory);   // 固定模式：仍是同一个目录
        storage.WriteRaw(DeviceKind.Radar, new RawSegment(DateTimeOffset.Now, [0x02]));
        storage.Complete();

        Assert.True(File.Exists(RawPath(expected, DeviceKind.Radar, "raw_0001.bin")));
        Assert.True(File.Exists(RawPath(expected, DeviceKind.Radar, "raw_0002.bin")));   // 续号，不覆盖
    }

    [Fact]
    public void 未开始采集就完成不应在进程当前目录里丢下摘要文件()
    {
        var straySummary = Path.Combine(Directory.GetCurrentDirectory(), "session-summary.json");
        var existedBefore = File.Exists(straySummary);

        using (var storage = new SessionStorage(PerRunConfig(), _root))
        {
            storage.Complete();   // 从未 Open 过
        }

        Assert.Equal(existedBefore, File.Exists(straySummary));
        Assert.False(Directory.Exists(_root));
    }
}
