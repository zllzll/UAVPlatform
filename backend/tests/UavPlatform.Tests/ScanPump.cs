using UavPlatform.Core.Protocols;

namespace UavPlatform.Tests;

/// <summary>
/// 按 <c>DeviceConnection.DrainQueue</c> 的真实语义驱动 <see cref="DeviceProtocol.Scan"/>：
/// <c>NeedMore</c> 停下等数据、<c>Skip</c> 丢弃指定字节、<c>Frame</c> 取出并消费一整帧。
/// <para>用于验证粘包 / 分包 / 垃圾字节 / 残帧下的分帧稳健性。</para>
/// </summary>
internal static class ScanPump
{
    /// <summary>
    /// 把 <paramref name="data"/> 按 <paramref name="chunkSize"/> 切块依次喂入协议，
    /// 返回按顺序切出的全部完整帧（不含被 Skip 掉的字节）。
    /// </summary>
    /// <param name="chunkSize">每次喂入的字节数；小于等于 0 表示一次性全部喂入。</param>
    public static List<byte[]> Drain(DeviceProtocol protocol, byte[] data, int chunkSize = 0)
    {
        var queue = new ByteQueue();
        var frames = new List<byte[]>();
        var offset = 0;

        do
        {
            var take = chunkSize <= 0 ? data.Length - offset : Math.Min(chunkSize, data.Length - offset);
            if (take > 0) queue.Append(data.AsSpan(offset, take));
            offset += take;

            while (queue.Count > 0)
            {
                var scan = protocol.Scan(queue.Span);

                switch (scan.Status)
                {
                    case FrameScanStatus.NeedMore:
                        goto NextChunk;

                    case FrameScanStatus.Skip:
                        queue.Consume(scan.Length);
                        break;

                    case FrameScanStatus.Frame:
                        frames.Add(queue.Take(scan.Length));
                        break;
                }
            }

        NextChunk:
            ;
        }
        while (offset < data.Length);

        return frames;
    }
}
