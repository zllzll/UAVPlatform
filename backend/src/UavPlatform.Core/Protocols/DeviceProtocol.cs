using UavPlatform.Core.Models;

namespace UavPlatform.Core.Protocols;

/// <summary>分帧扫描结果状态。</summary>
public enum FrameScanStatus
{
    /// <summary>数据不足，需要继续读取。</summary>
    NeedMore,

    /// <summary>已切出一帧，长度见 <see cref="FrameScan.Length"/>。</summary>
    Frame,

    /// <summary>无效数据，需要跳过 <see cref="FrameScan.Length"/> 个字节以重新同步。</summary>
    Skip,
}

/// <summary>分帧扫描结果。</summary>
public readonly record struct FrameScan(FrameScanStatus Status, int Length)
{
    public static readonly FrameScan NeedMore = new(FrameScanStatus.NeedMore, 0);

    public static FrameScan Frame(int length) => new(FrameScanStatus.Frame, length);

    public static FrameScan Skip(int count) => new(FrameScanStatus.Skip, count);
}

/// <summary>
/// 设备协议抽象：负责从字节流中切帧，并把帧解析为业务样本。
/// 所有实现必须做到：粘包 / 分包 / 残帧 / 坏帧重同步 均可容忍。
/// </summary>
public abstract class DeviceProtocol
{
    /// <summary>协议名称（日志用）。</summary>
    public abstract string Name { get; }

    /// <summary>
    /// 从缓冲区开头尝试切出一帧。
    /// 返回 <see cref="FrameScanStatus.Frame"/> 时，缓冲区开头恰好是一整帧（含校验和）。
    /// </summary>
    public abstract FrameScan Scan(ReadOnlySpan<byte> buffer);
}

/// <summary>小端 / 大端读取辅助。</summary>
internal static class Endian
{
    public static ushort U16(ReadOnlySpan<byte> span, int offset, bool bigEndian) =>
        bigEndian
            ? (ushort)((span[offset] << 8) | span[offset + 1])
            : (ushort)(span[offset] | (span[offset + 1] << 8));

    public static short I16(ReadOnlySpan<byte> span, int offset, bool bigEndian) =>
        unchecked((short)U16(span, offset, bigEndian));

    public static uint U32(ReadOnlySpan<byte> span, int offset, bool bigEndian) =>
        bigEndian
            ? ((uint)span[offset] << 24) | ((uint)span[offset + 1] << 16) | ((uint)span[offset + 2] << 8) | span[offset + 3]
            : span[offset] | ((uint)span[offset + 1] << 8) | ((uint)span[offset + 2] << 16) | ((uint)span[offset + 3] << 24);

    public static int I32(ReadOnlySpan<byte> span, int offset, bool bigEndian) =>
        unchecked((int)U32(span, offset, bigEndian));

    public static float F32(ReadOnlySpan<byte> span, int offset, bool littleEndian)
    {
        var bits = littleEndian
            ? span[offset] | (span[offset + 1] << 8) | (span[offset + 2] << 16) | (span[offset + 3] << 24)
            : (span[offset] << 24) | (span[offset + 1] << 16) | (span[offset + 2] << 8) | span[offset + 3];
        return BitConverter.Int32BitsToSingle(bits);
    }
}
