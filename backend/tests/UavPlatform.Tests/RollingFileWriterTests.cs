using System.Text;
using UavPlatform.Core.Storage;

namespace UavPlatform.Tests;

/// <summary>
/// 滚动文件写入器单元测试。
/// 注意：<see cref="RollingFileWriter"/> 只在「缓冲区已排空」且「当前文件已达上限」时才滚动，
/// 因此滚动类测试必须显式传入很小的 bufferBytes、极大的 flushIntervalMs，并在每次 Write 后主动 Flush，
/// 否则默认 256 KB 缓冲区会让文件一直增长而不滚动。
/// </summary>
public class RollingFileWriterTests : IDisposable
{
    private readonly string _directory;

    public RollingFileWriterTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "UavPlatformTests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响断言结果
        }

        GC.SuppressFinalize(this);
    }

    private static byte[] Chunk(byte value, int length)
    {
        var data = new byte[length];
        Array.Fill(data, value);
        return data;
    }

    private static string[] Files(string directory) =>
        Directory.GetFiles(directory).Select(Path.GetFileName).OrderBy(x => x).ToArray()!;

    [Fact]
    public void 构造时应立即建立第一个分片文件()
    {
        using var writer = new RollingFileWriter(_directory, "telemetry", ".jsonl", maxBytes: 2048, bufferBytes: 4096, flushIntervalMs: 3_600_000);

        Assert.True(File.Exists(writer.CurrentPath));
        Assert.Equal("telemetry_0001.jsonl", Path.GetFileName(writer.CurrentPath));
        Assert.Equal(0, writer.CurrentBytes);
        Assert.Equal(0, writer.TotalBytes);
        Assert.Equal(1, writer.FileCount);
    }

    [Fact]
    public void 达到上限后应滚动并保证文件总字节数等于写入量()
    {
        const int chunkSize = 512;
        const int chunkCount = 10;                       // 共 5120 字节
        using (var writer = new RollingFileWriter(_directory, "telemetry", ".jsonl", maxBytes: 2048, bufferBytes: 4096, flushIntervalMs: 3_600_000))
        {
            for (var i = 0; i < chunkCount; i++)
            {
                writer.Write(Chunk((byte)(i + 1), chunkSize));
                writer.Flush();                          // 排空缓冲区，使滚动点确定
            }

            Assert.Equal((long)chunkSize * chunkCount, writer.TotalBytes);
            Assert.Equal(3, writer.FileCount);
        }

        var files = Files(_directory);
        Assert.Equal(["telemetry_0001.jsonl", "telemetry_0002.jsonl", "telemetry_0003.jsonl"], files);

        var sizes = files.Select(f => new FileInfo(Path.Combine(_directory, f)).Length).ToArray();
        Assert.Equal([2048L, 2048L, 1024L], sizes);
        Assert.Equal(chunkSize * chunkCount, sizes.Sum());   // 总字节数等于写入量

        // 每个分片的内容应是连续且未被覆盖的块
        var first = File.ReadAllBytes(Path.Combine(_directory, "telemetry_0001.jsonl"));
        Assert.Equal(Chunk(1, 512), first[..512]);
        Assert.Equal(Chunk(2, 512), first[512..1024]);
        Assert.Equal(Chunk(3, 512), first[1024..1536]);
        Assert.Equal(Chunk(4, 512), first[1536..2048]);
    }

    [Fact]
    public void 新建实例写同一目录应续号且不覆盖已有关闭的文件()
    {
        const int chunkSize = 512;
        byte[] firstRoundSnapshot;

        using (var writer = new RollingFileWriter(_directory, "telemetry", ".jsonl", maxBytes: 2048, bufferBytes: 4096, flushIntervalMs: 3_600_000))
        {
            for (var i = 0; i < 10; i++)
            {
                writer.Write(Chunk((byte)(i + 1), chunkSize));
                writer.Flush();
            }

            writer.Complete();
        }

        var before = Files(_directory);
        Assert.Equal(3, before.Length);
        firstRoundSnapshot = File.ReadAllBytes(Path.Combine(_directory, "telemetry_0001.jsonl"));
        var sizesBefore = before.Select(f => new FileInfo(Path.Combine(_directory, f)).Length).ToArray();

        // 第二轮：同一目录再建一个实例，应接着 _0004 写
        using (var writer = new RollingFileWriter(_directory, "telemetry", ".jsonl", maxBytes: 1024 * 1024, bufferBytes: 4096, flushIntervalMs: 3_600_000))
        {
            Assert.Equal("telemetry_0004.jsonl", Path.GetFileName(writer.CurrentPath));
            Assert.Equal(4, writer.FileCount);
            writer.Write(Chunk(0xAB, 100));
            writer.Flush();
            Assert.Equal(100, writer.CurrentBytes);
        }

        var after = Files(_directory);
        Assert.Equal(4, after.Length);
        Assert.Equal(["telemetry_0001.jsonl", "telemetry_0002.jsonl", "telemetry_0003.jsonl", "telemetry_0004.jsonl"], after);

        // 已有关闭的文件长度与内容都不变
        Assert.Equal(sizesBefore, after.Take(3).Select(f => new FileInfo(Path.Combine(_directory, f)).Length).ToArray());
        Assert.Equal(firstRoundSnapshot, File.ReadAllBytes(Path.Combine(_directory, "telemetry_0001.jsonl")));

        // 新分片只包含第二轮写入的内容
        var appended = File.ReadAllBytes(Path.Combine(_directory, "telemetry_0004.jsonl"));
        Assert.Equal(Chunk(0xAB, 100), appended);
    }

    [Fact]
    public void WriteLine应按UTF8写入并补换行()
    {
        using (var writer = new RollingFileWriter(_directory, "log", ".txt", maxBytes: 4096, bufferBytes: 4096, flushIntervalMs: 3_600_000))
        {
            writer.WriteLine("高度=100.5米");
            writer.Flush();
            writer.WriteLine("second");
            writer.Complete();
        }

        var bytes = File.ReadAllBytes(Path.Combine(_directory, "log_0001.txt"));
        var expected = Encoding.UTF8.GetBytes("高度=100.5米\n").Concat(Encoding.UTF8.GetBytes("second\n")).ToArray();
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void 写入空数据不应改变计数()
    {
        using var writer = new RollingFileWriter(_directory, "log", ".txt", maxBytes: 4096, bufferBytes: 4096, flushIntervalMs: 3_600_000);

        writer.Write([]);
        writer.Flush();

        Assert.Equal(0, writer.CurrentBytes);
        Assert.Equal(0, writer.TotalBytes);
        Assert.Equal(0, new FileInfo(writer.CurrentPath).Length);
    }

    [Fact]
    public void TotalBytes应跨分片累加()
    {
        using var writer = new RollingFileWriter(_directory, "log", ".txt", maxBytes: 1024, bufferBytes: 4096, flushIntervalMs: 3_600_000);

        for (var i = 0; i < 5; i++)
        {
            writer.Write(Chunk((byte)i, 512));   // 1024 上限 → 每 2 块滚一次
            writer.Flush();
        }

        Assert.Equal(2560, writer.TotalBytes);
        Assert.Equal(3, writer.FileCount);
        Assert.Equal(512, writer.CurrentBytes);   // 第 5 块落进 _0003
    }

    [Fact]
    public void 超过缓冲区的大块写入应被拆开且不丢字节()
    {
        using (var writer = new RollingFileWriter(_directory, "log", ".bin", maxBytes: 1L << 30, bufferBytes: 4096, flushIntervalMs: 3_600_000))
        {
            writer.Write(Chunk(0x5A, 10_000));   // 大于缓冲区，必须被分片搬运
            writer.Flush();
        }

        var bytes = File.ReadAllBytes(Path.Combine(_directory, "log_0001.bin"));
        Assert.Equal(10_000, bytes.Length);
        Assert.All(bytes, b => Assert.Equal(0x5A, b));
    }

    [Fact]
    public void 上限小于1024时应被钳制到1024()
    {
        using (var writer = new RollingFileWriter(_directory, "log", ".bin", maxBytes: 1, bufferBytes: 4096, flushIntervalMs: 3_600_000))
        {
            for (var i = 0; i < 3; i++)
            {
                writer.Write(Chunk((byte)i, 1024));
                writer.Flush();
            }

            // 钳制后上限为 1024：第 1 块填满 _0001，第 2、3 块各进一个分片
            Assert.Equal(3, writer.FileCount);
            Assert.Equal(1024, writer.CurrentBytes);
        }

        Assert.Equal(3, Files(_directory).Length);
    }

    [Fact]
    public void 前缀中的下划线不应干扰续号探测()
    {
        using (var first = new RollingFileWriter(_directory, "imu_raw", ".bin", maxBytes: 4096, bufferBytes: 4096, flushIntervalMs: 3_600_000))
        {
            first.Write(Chunk(1, 10));
            first.Flush();
            Assert.Equal("imu_raw_0001.bin", Path.GetFileName(first.CurrentPath));
        }

        using var second = new RollingFileWriter(_directory, "imu_raw", ".bin", maxBytes: 4096, bufferBytes: 4096, flushIntervalMs: 3_600_000);
        Assert.Equal("imu_raw_0002.bin", Path.GetFileName(second.CurrentPath));
    }

    [Fact]
    public void 目录不存在时应自动创建()
    {
        var nested = Path.Combine(_directory, "a", "b", "c");

        using (var writer = new RollingFileWriter(nested, "log", ".txt", maxBytes: 4096, bufferBytes: 4096, flushIntervalMs: 3_600_000))
        {
            writer.Write(Chunk(1, 8));
            writer.Flush();
            Assert.True(File.Exists(Path.Combine(nested, "log_0001.txt")));
        }
    }

    [Fact]
    public void Complete之后继续写入不应死锁()
    {
        // 回归用例：Complete() 只把 _stream 置空并关闭文件，并不置 _disposed。
        // 旧实现的 FlushBuffer() 在 _stream 为 null 时直接 return，连 _buffered 都不清，
        // 于是缓冲永久停在 _bufferBytes；下一次 Write 里 space/take 恒为 0，
        // while (offset < data.Length) 再也无法推进 —— 调用线程在持有 _gate 的情况下死转。
        //
        // 本用例故意不用 using：一旦回归，写入线程会永久持有 _gate，
        // Dispose() 也会跟着阻塞，整个测试进程会卡死而不是干净地失败。
        var writer = new RollingFileWriter(_directory, "raw", ".bin",
            maxBytes: 1L << 30, bufferBytes: 4096, flushIntervalMs: 3_600_000);

        writer.Complete();

        const int chunkSize = 1024;
        const int count = 200;

        var task = Task.Run(() =>
        {
            for (var i = 0; i < count; i++) writer.Write(Chunk((byte)i, chunkSize));
        });

        // 这里必须用带超时的阻塞等待，不能 await：本用例要断言的正是「调用线程会不会永远不返回」，
        // await 一个死锁的 Task 会让测试自己也挂住，xUnit1031 的通用建议在此不适用。
#pragma warning disable xUnit1031
        var finished = task.Wait(TimeSpan.FromSeconds(10));
#pragma warning restore xUnit1031

        Assert.True(finished, "Complete() 之后继续写入发生死锁：缓冲写满却无法推进。");
        Assert.Equal((long)chunkSize * count, writer.TotalBytes);
    }

    [Fact]
    public void 缓冲未写满时定时刷盘也应把数据落到磁盘上()
    {
        // 这条用例回答一个很实际的问题：**采集正在进行的时候**，磁盘上到底有没有数据？
        // 默认配置是 bufferBytes = 256 KB、flushIntervalMs = 1000，也就是即使每秒只写进来
        // 几百字节，也应该在 1 秒内落盘；而不是把数据一直攒在内存里、等停止采集才一次性写出
        // （那样中途崩溃就全丢了，也不符合「实时保存」）。
        var line = new string('x', 200); // WriteLine 会补 '\n'，每行 201 字节

        using var writer = new RollingFileWriter(_directory, "parsed", ".jsonl",
            maxBytes: 64L * 1024 * 1024, bufferBytes: 256 * 1024, flushIntervalMs: 1000);

        // 只写约 98 KB：故意不到 256 KB 缓冲区的一半，逼数据只能靠定时刷盘出去。
        for (var i = 0; i < 500; i++) writer.WriteLine(line);

        var path = writer.CurrentPath;
        var written = writer.TotalBytes;

        // 轮询而不是死等，避免慢机器上假失败；最多等 4 秒（4 个刷盘周期）。
        long onDisk = 0;
        for (var i = 0; i < 40 && onDisk == 0; i++)
        {
            Thread.Sleep(100);
            onDisk = new FileInfo(path).Length;
        }

        Assert.True(onDisk > 0,
            $"写入 {written} 字节后等了 4 秒，磁盘上 {Path.GetFileName(path)} 仍是 {onDisk} 字节：" +
            "定时刷盘没有生效，采集期间的数据只留在内存里。");
    }

    [Fact]
    public void 缓冲写满应立即把数据交给磁盘()
    {
        // 与上一条用例互补：上一条排除「缓冲写满」，本条排除「定时刷盘」。
        // 定时刷盘设成 1 小时，逼数据只能靠「缓冲写满」这条路径出去。
        var line = new string('x', 200); // 201 B/行

        using var writer = new RollingFileWriter(_directory, "parsed", ".jsonl",
            maxBytes: 64L * 1024 * 1024, bufferBytes: 256 * 1024, flushIntervalMs: 3_600_000);

        // 写约 301 KB，明确越过一次 256 KB 缓冲。
        for (var i = 0; i < 1500; i++) writer.WriteLine(line);

        var onDisk = new FileInfo(writer.CurrentPath).Length;
        Assert.True(onDisk > 0,
            $"累计写入 {writer.TotalBytes} 字节、缓冲已写满，磁盘上 {Path.GetFileName(writer.CurrentPath)} " +
            $"仍是 {onDisk} 字节：FlushBuffer 没能把数据交给磁盘。");
    }
}
