using System.Text;
using System.Threading;

namespace UavPlatform.Core.Storage;

/// <summary>
/// 按大小滚动的追加式文件写入器。
/// 单文件写满 <see cref="MaxBytes"/> 字节后自动新建 <c>前缀_0002.后缀</c> 续接，不丢数据、不覆盖。
/// </summary>
/// <remarks>
/// 写入走内存缓冲 + 定时刷盘，调用方（解码循环）不会被磁盘 I/O 阻塞。
/// 本类线程安全；<see cref="Dispose"/> / <see cref="Flush"/> 前请确保已调用 <see cref="Complete"/>。
/// </remarks>
public sealed class RollingFileWriter : IDisposable
{
    private readonly string _directory;
    private readonly string _prefix;
    private readonly string _extension;
    private readonly long _maxBytes;
    private readonly int _bufferBytes;
    private readonly int _flushIntervalMs;
    private readonly object _gate = new();

    private FileStream? _stream;
    private byte[] _buffer;
    private int _buffered;
    private int _index;
    private long _currentBytes;
    private DateTime _lastFlush = DateTime.UtcNow;
    private bool _disposed;
    private readonly Timer? _flushTimer;

    public RollingFileWriter(string directory, string prefix, string extension, long maxBytes,
        int bufferBytes = 256 * 1024, int flushIntervalMs = 1000)
    {
        _directory = directory;
        _prefix = prefix;
        _extension = extension;
        _maxBytes = Math.Max(1024, maxBytes);
        _bufferBytes = Math.Max(4096, bufferBytes);
        _flushIntervalMs = Math.Max(100, flushIntervalMs);
        _buffer = new byte[_bufferBytes];

        Directory.CreateDirectory(_directory);
        _index = ProbeNextIndex();
        OpenNewFile();

        // 这里必须是真的定时器，不能只靠 Write() 尾巴上的 MaybeTimedFlush()。
        // MaybeTimedFlush() 的语义是「下一次写入时顺便看看该不该刷」，数据一旦停下来
        // （设备静默、一段突发刚写完、只写了几百字节就没了），缓冲里那不足一个
        // bufferBytes 的数据就会一直留在内存里，直到 Roll()/Complete()/Dispose() 才落盘。
        // 默认 256 KB 缓冲下，这意味着最多 256 KB 的数据「已计数但未落盘」——
        // 采集途中崩溃就会丢掉这一段。
        _flushTimer = new Timer(
            static state => ((RollingFileWriter)state!).OnFlushTimer(),
            this,
            _flushIntervalMs,
            _flushIntervalMs);
    }

    /// <summary>定时器回调：把缓冲真正刷到磁盘。</summary>
    /// <remarks>
    /// 这里用 <c>Flush(true)</c>（＝同时做 FlushFileBuffers）而不是 <c>Flush(false)</c>，有两个原因：
    /// <list type="number">
    /// <item>Flush(false) 只把数据写进系统缓存，<b>不更新 NTFS 目录项里的文件大小</b>。
    /// 于是资源管理器 / <c>Get-ChildItem</c> / <c>dir</c> 会一直显示 0 字节，而文件其实已经有数据——
    /// 排查「到底存没存」时极易误判（本项目就因此误报过一次）。</item>
    /// <item>掉电或进程被强杀时，只有 fsync 过的数据才保证还在盘上，符合「实时保存」的要求。</item>
    /// </list>
    /// 代价是每个写入器每秒一次 fsync，对 7 个写入器来说可以忽略。
    /// </remarks>
    private void OnFlushTimer()
    {
        lock (_gate)
        {
            if (_disposed || _stream is null) return;

            try
            {
                FlushBuffer();
                _stream.Flush(true);
                _lastFlush = DateTime.UtcNow;
            }
            catch (ObjectDisposedException)
            {
                // 与 Complete()/Dispose() 赛跑：文件已关闭，定时器马上也会被关掉。
            }
            catch (IOException)
            {
                // 后台刷盘失败不向上抛：写入路径不该因为磁盘抖动而中断。
            }
        }
    }

    /// <summary>当前正在写入的文件全路径。</summary>
    public string CurrentPath { get; private set; } = string.Empty;

    /// <summary>当前文件已写入字节数。</summary>
    public long CurrentBytes
    {
        get { lock (_gate) return _currentBytes; }
    }

    /// <summary>本写入器累计写出的字节数（含已滚动文件）。</summary>
    public long TotalBytes { get; private set; }

    /// <summary>已滚动生成的文件数（含当前文件）。</summary>
    public int FileCount
    {
        get { lock (_gate) return _index; }
    }

    /// <summary>写入一段字节。</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;

        lock (_gate)
        {
            if (_disposed) return;

            var offset = 0;
            while (offset < data.Length)
            {
                // 单个文件写满则滚动
                if (_currentBytes >= _maxBytes && _buffered == 0)
                {
                    Roll();
                }

                var space = _bufferBytes - _buffered;
                var take = Math.Min(space, data.Length - offset);
                data.Slice(offset, take).CopyTo(_buffer.AsSpan(_buffered));
                _buffered += take;
                offset += take;
                _currentBytes += take;
                TotalBytes += take;

                if (_buffered == _bufferBytes)
                {
                    FlushBuffer();
                }
            }

            MaybeTimedFlush();
        }
    }

    /// <summary>写入一行文本（自动补 LF）。</summary>
    public void WriteLine(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        Write(bytes);
    }

    /// <summary>把缓冲刷入磁盘。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            FlushBuffer();
            _stream?.Flush(true);
            _lastFlush = DateTime.UtcNow;
        }
    }

    /// <summary>刷盘并关闭当前文件。</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _flushTimer?.Dispose();
            FlushBuffer();
            _stream?.Flush(true);
            _stream?.Dispose();
            _stream = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _flushTimer?.Dispose();
            try
            {
                FlushBuffer();
                _stream?.Flush(true);
            }
            catch
            {
                // 关闭阶段的刷盘失败不再向上抛
            }
            _stream?.Dispose();
            _stream = null;
        }
    }

    private void FlushBuffer()
    {
        if (_buffered == 0) return;

        // _stream 为 null 表示当前文件已被 Complete() / Dispose() 关闭。
        // 这种情况下必须照样清空缓冲：否则 _buffered 会永久停在 _bufferBytes，
        // 下一次 Write 里 space 恒为 0、take 恒为 0，while 循环再也无法推进，
        // 调用线程会在持有 _gate 的情况下死转，整个写入器彻底卡死。
        if (_stream is null)
        {
            _buffered = 0;
            return;
        }

        _stream.Write(_buffer, 0, _buffered);
        _buffered = 0;
    }

    private void MaybeTimedFlush()
    {
        if ((DateTime.UtcNow - _lastFlush).TotalMilliseconds < _flushIntervalMs) return;
        FlushBuffer();
        _stream?.Flush(false);
        _lastFlush = DateTime.UtcNow;
    }

    private void Roll()
    {
        FlushBuffer();
        _stream?.Flush(true);
        _stream?.Dispose();
        _stream = null;
        _currentBytes = 0;
        OpenNewFile();
    }

    private void OpenNewFile()
    {
        while (true)
        {
            _index++;
            var name = $"{_prefix}_{_index:D4}{_extension}";
            var path = Path.Combine(_directory, name);

            try
            {
                _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                    _bufferBytes, FileOptions.None);
                CurrentPath = path;
                _currentBytes = 0;
                _lastFlush = DateTime.UtcNow;
                return;
            }
            catch (IOException) when (File.Exists(path))
            {
                // 已存在（例如同一秒内重启）：换下一个序号
                if (_index > 100000) throw;
            }
        }
    }

    /// <summary>找出目录里已有的最大序号，避免固定目录模式下覆盖历史文件。</summary>
    private int ProbeNextIndex()
    {
        try
        {
            var pattern = $"{_prefix}_*{_extension}";
            var max = 0;
            foreach (var file in Directory.EnumerateFiles(_directory, pattern))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var underscore = name.LastIndexOf('_');
                if (underscore < 0 || underscore + 1 >= name.Length) continue;
                if (int.TryParse(name[(underscore + 1)..], out var value) && value > max) max = value;
            }
            return max;
        }
        catch
        {
            return 0;
        }
    }
}
