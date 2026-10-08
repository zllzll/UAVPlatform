namespace UavPlatform.Core.Protocols;

/// <summary>
/// 可增长的字节队列，用于在解码循环里累积粘包/分包数据。
/// 内部使用单块数组 + 读写游标，消费后自动前移，避免每帧分配。
/// </summary>
public sealed class ByteQueue
{
    private byte[] _buffer;
    private int _start;
    private int _end;

    public ByteQueue(int capacity = 8192) => _buffer = new byte[Math.Max(64, capacity)];

    /// <summary>当前可读字节数。</summary>
    public int Count => _end - _start;

    /// <summary>可读区域（不含已消费部分）。</summary>
    public ReadOnlySpan<byte> Span => _buffer.AsSpan(_start, _end - _start);

    /// <summary>追加数据。</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        EnsureCapacity(data.Length);
        data.CopyTo(_buffer.AsSpan(_end));
        _end += data.Length;
    }

    /// <summary>消费开头 <paramref name="count"/> 个字节。</summary>
    public void Consume(int count)
    {
        if (count <= 0) return;
        if (count >= Count)
        {
            _start = 0;
            _end = 0;
            return;
        }
        _start += count;
    }

    /// <summary>复制并消费开头 <paramref name="count"/> 个字节（用于保存原始帧）。</summary>
    public byte[] Take(int count)
    {
        var result = Span[..count].ToArray();
        Consume(count);
        return result;
    }

    /// <summary>清空。</summary>
    public void Clear()
    {
        _start = 0;
        _end = 0;
    }

    private void EnsureCapacity(int extra)
    {
        if (_end + extra <= _buffer.Length) return;

        // 先尝试把已消费空间腾出来
        var live = Count;
        if (live + extra <= _buffer.Length)
        {
            Span.CopyTo(_buffer);
            _start = 0;
            _end = live;
            return;
        }

        var size = _buffer.Length;
        while (size < live + extra) size *= 2;
        var next = new byte[size];
        Span.CopyTo(next);
        _buffer = next;
        _start = 0;
        _end = live;
    }
}
