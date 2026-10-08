using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace UavPlatform.Tests;

/// <summary>
/// 独立于被测解析器的「手工造帧」工具。
/// <para>
/// 所有字段都按协议文档的字节布局逐字节拼装，<b>不引用 <c>UavPlatform.Core</c> 的任何常量或解析代码</b>，
/// 以保证测试与被测实现相互独立——实现写错时测试不会跟着一起错。
/// </para>
/// </summary>
internal static class FrameBuilder
{
    // ==================== 通用 ====================

    /// <summary>按顺序拼接若干字节数组。</summary>
    public static byte[] Concat(params byte[][] parts)
    {
        var total = 0;
        foreach (var part in parts) total += part.Length;

        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    /// <summary>字节累加和 &amp; 0xFF（三套协议里的「校验和」都是这个算法）。</summary>
    public static byte Sum8(ReadOnlySpan<byte> bytes)
    {
        byte sum = 0;
        foreach (var b in bytes) sum += b;
        return sum;
    }

    private static void WriteU16(Span<byte> span, ushort value, bool big)
    {
        if (big) BinaryPrimitives.WriteUInt16BigEndian(span, value);
        else BinaryPrimitives.WriteUInt16LittleEndian(span, value);
    }

    private static void WriteI16(Span<byte> span, short value, bool big)
    {
        if (big) BinaryPrimitives.WriteInt16BigEndian(span, value);
        else BinaryPrimitives.WriteInt16LittleEndian(span, value);
    }

    private static void WriteI32(Span<byte> span, int value, bool big)
    {
        if (big) BinaryPrimitives.WriteInt32BigEndian(span, value);
        else BinaryPrimitives.WriteInt32LittleEndian(span, value);
    }

    // ==================== NSR 雷达（小端） ====================

    /// <summary>单个目标 / 点云记录的字节数。</summary>
    public const int NsrRecordSize = 68;

    /// <summary>4 字节小端无符号整数（0xA9 的个数域）。</summary>
    public static byte[] U32Le(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    /// <summary>
    /// NSR 通用帧：<c>A5 5A | 源地址 | 目的地址 | 命令码 | 参数长度N(2,小端) | 参数(N) | 校验和</c>；
    /// 总长 = N + 8；校验和 = 从「源地址」到「参数最后一字节」的累加和 &amp; 0xFF。
    /// </summary>
    public static byte[] Nsr(byte command, ReadOnlySpan<byte> parameters, byte source = 0x10, byte destination = 0x01)
    {
        var frame = new byte[parameters.Length + 8];
        frame[0] = 0xA5;
        frame[1] = 0x5A;
        frame[2] = source;
        frame[3] = destination;
        frame[4] = command;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(5), (ushort)parameters.Length);
        parameters.CopyTo(frame.AsSpan(7));
        // 校验和覆盖源地址..参数最后一字节，即下标 2..(6 + N)
        frame[7 + parameters.Length] = Sum8(frame.AsSpan(2, 5 + parameters.Length));
        return frame;
    }

    /// <summary>
    /// 单条 68 字节目标 / 点云记录的协议布局：
    /// ID(uint32,0) / type(4) / x_speed(8) / y_speed(12) / z_speed(16) / x_axes(20) / y_axes(24) / z_axes(28) /
    /// length(32) / azimuth_angle(36) / elevation_angle(40) / SNR(44) / Peak_energy(48) / area(uint16,52) / 保留(54..67)。
    /// </summary>
    /// <param name="singleByteType">
    /// true 时 type 只占 1 字节（0xA9 点云），后 3 字节保留置 0；false 时按 int32 写入（0xA8 目标）。
    /// </param>
    public static byte[] NsrRecord(
        uint id,
        int type,
        float xSpeed = 0f,
        float ySpeed = 0f,
        float zSpeed = 0f,
        float x = 0f,
        float y = 0f,
        float z = 0f,
        float length = 0f,
        float azimuth = 0f,
        float elevation = 0f,
        float snr = 0f,
        float peakEnergy = 0f,
        ushort area = 0,
        bool singleByteType = false)
    {
        var record = new byte[NsrRecordSize];
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0), id);

        if (singleByteType) record[4] = (byte)type;
        else BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), type);

        BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(8), xSpeed);
        BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(12), ySpeed);
        BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(16), zSpeed);
        BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(20), x);
        BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(24), y);
        BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(28), z);
        BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(32), length);
        BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(36), azimuth);
        BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(40), elevation);
        BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(44), snr);
        BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(48), peakEnergy);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(52), area);
        // 54..67 保留，保持 0
        return record;
    }

    // ==================== UCM221（默认小端） ====================

    /// <summary>汇总信息固定长度。</summary>
    public const int Ucm221SummarySize = 20;

    /// <summary>目标 / 点云单条记录长度。</summary>
    public const int Ucm221RecordSize = 14;

    /// <summary>扩展信息（IMU + GPS，0xA33A）模块总长度。</summary>
    public const int Ucm221ExtendedSize = 45;

    /// <summary>无人机信息（0xA22A）模块总长度：起始码2 + 长度2 + 标志2 + 4×int32 + 6×int16 + 校验和1。</summary>
    public const int Ucm221UavInfoSize = 35;

    /// <summary>
    /// 汇总信息 20 字节：起始码 <c>A7 7A</c>(2) / 长度(1) / 数据包状态(1) / 目标个数N1(1) / 单目标大小(1) /
    /// 年偏移(1) / 月(1) / 日(1) / 时(1) / 分(1) / 秒(1) / 毫秒(uint16) / 设备ID(uint16) / 点云个数N2(uint16) /
    /// 单点云大小(1) / 校验和(1)。
    /// </summary>
    public static byte[] Ucm221Summary(
        byte packetStatus,
        byte targetCount,
        ushort pointCount,
        byte yearOffset,
        byte month,
        byte day,
        byte hour,
        byte minute,
        byte second,
        ushort millisecond,
        ushort deviceId,
        byte recordSize = 14,
        bool big = false)
    {
        var summary = new byte[Ucm221SummarySize];
        summary[0] = 0xA7;
        summary[1] = 0x7A;
        summary[2] = Ucm221SummarySize;
        summary[3] = packetStatus;
        summary[4] = targetCount;
        summary[5] = recordSize;
        summary[6] = yearOffset;
        summary[7] = month;
        summary[8] = day;
        summary[9] = hour;
        summary[10] = minute;
        summary[11] = second;
        WriteU16(summary.AsSpan(12), millisecond, big);
        WriteU16(summary.AsSpan(14), deviceId, big);
        WriteU16(summary.AsSpan(16), pointCount, big);
        summary[18] = recordSize;
        summary[19] = Sum8(summary.AsSpan(0, 19)); // 汇总信息自带校验和，本项目解析时不校验
        return summary;
    }

    /// <summary>目标 / 点云信息模块：起始码(2) + 长度(2) + 内容 + 校验和(1)。长度为 0 时仍占 5 字节。</summary>
    public static byte[] Ucm221Module(byte[] header, byte[] records, bool big = false)
    {
        var module = new byte[5 + records.Length];
        module[0] = header[0];
        module[1] = header[1];
        WriteU16(module.AsSpan(2), (ushort)records.Length, big);
        records.CopyTo(module, 4);
        module[^1] = Sum8(module.AsSpan(0, module.Length - 1));
        return module;
    }

    /// <summary>目标 / 点云 14 字节记录：起始码(2) + 点云 ID(uint16) + 其余字段（本项目不解析内容）。</summary>
    public static byte[] Ucm221Record(byte[] header, ushort id, bool big = false)
    {
        var record = new byte[Ucm221RecordSize];
        record[0] = header[0];
        record[1] = header[1];
        WriteU16(record.AsSpan(2), id, big);
        return record;
    }

    /// <summary>
    /// 扩展信息模块（0xA33A，共 45 字节）：起始码(2) / 长度(2) / 加速度X1Y1Z1(3×int16) / 角速度X1Y1Z1(3×int16) /
    /// 加速度X2Y2Z2(3×int16) / 角速度X2Y2Z2(3×int16) / GPS纬度(int32) / GPS经度(int32) / GPS海拔(int32) /
    /// GPS速度(int16) / GPS磁偏角(int16) / 校验和(1)。
    /// </summary>
    public static byte[] Ucm221Extended(
        int lengthField,
        short[] accel1,
        short[] gyro1,
        short[] accel2,
        short[] gyro2,
        int latitude,
        int longitude,
        int altitude,
        short speed,
        short magneticDeclination,
        bool big = false)
    {
        var module = new byte[Ucm221ExtendedSize];
        module[0] = 0xA3;
        module[1] = 0x3A;
        WriteU16(module.AsSpan(2), (ushort)lengthField, big);

        WriteI16(module.AsSpan(4), accel1[0], big);
        WriteI16(module.AsSpan(6), accel1[1], big);
        WriteI16(module.AsSpan(8), accel1[2], big);
        WriteI16(module.AsSpan(10), gyro1[0], big);
        WriteI16(module.AsSpan(12), gyro1[1], big);
        WriteI16(module.AsSpan(14), gyro1[2], big);
        WriteI16(module.AsSpan(16), accel2[0], big);
        WriteI16(module.AsSpan(18), accel2[1], big);
        WriteI16(module.AsSpan(20), accel2[2], big);
        WriteI16(module.AsSpan(22), gyro2[0], big);
        WriteI16(module.AsSpan(24), gyro2[1], big);
        WriteI16(module.AsSpan(26), gyro2[2], big);

        WriteI32(module.AsSpan(28), latitude, big);
        WriteI32(module.AsSpan(32), longitude, big);
        WriteI32(module.AsSpan(36), altitude, big);
        WriteI16(module.AsSpan(40), speed, big);
        WriteI16(module.AsSpan(42), magneticDeclination, big);

        module[44] = Sum8(module.AsSpan(0, 44));
        return module;
    }

    /// <summary>
    /// 无人机信息模块（0xA22A，共 35 字节）：起始码(2) / 长度(2) / 标志(uint16) / GPS纬度(int32) / GPS经度(int32) /
    /// GPS海拔(int32) / 相对高度(int32) / 飞行速度VN,VE,VD(3×int16) / 横滚角,俯仰角,航向角(3×int16) / 校验和(1)。
    /// </summary>
    public static byte[] Ucm221UavInfo(
        int lengthField,
        ushort flags,
        int latitude,
        int longitude,
        int altitude,
        int relativeAltitude,
        short velocityNorth,
        short velocityEast,
        short velocityDown,
        short roll,
        short pitch,
        short heading,
        bool big = false)
    {
        var module = new byte[Ucm221UavInfoSize];
        module[0] = 0xA2;
        module[1] = 0x2A;
        WriteU16(module.AsSpan(2), (ushort)lengthField, big);
        WriteU16(module.AsSpan(4), flags, big);

        WriteI32(module.AsSpan(6), latitude, big);
        WriteI32(module.AsSpan(10), longitude, big);
        WriteI32(module.AsSpan(14), altitude, big);
        WriteI32(module.AsSpan(18), relativeAltitude, big);

        WriteI16(module.AsSpan(22), velocityNorth, big);
        WriteI16(module.AsSpan(24), velocityEast, big);
        WriteI16(module.AsSpan(26), velocityDown, big);
        WriteI16(module.AsSpan(28), roll, big);
        WriteI16(module.AsSpan(30), pitch, big);
        WriteI16(module.AsSpan(32), heading, big);

        module[34] = Sum8(module.AsSpan(0, 34));
        return module;
    }

    /// <summary>
    /// UCM221 完整帧：<c>A5 5A | 数据总长度L(2) | 负载(L) | 校验和(1)</c>，总长 = L + 5；
    /// 校验和 = 负载全部字节累加和 &amp; 0xFF。
    /// </summary>
    public static byte[] Ucm221Frame(bool big, params byte[][] payloadParts)
    {
        var payload = Concat(payloadParts);
        var frame = new byte[payload.Length + 5];
        frame[0] = 0xA5;
        frame[1] = 0x5A;
        WriteU16(frame.AsSpan(2), (ushort)payload.Length, big);
        payload.CopyTo(frame, 4);
        frame[^1] = Sum8(payload);
        return frame;
    }

    /// <summary>只构造 4 字节帧头 + 长度字段，用于分帧边界测试。</summary>
    public static byte[] Ucm221LengthHeader(ushort length, bool big = false)
    {
        var header = new byte[] { 0xA5, 0x5A, 0, 0 };
        WriteU16(header.AsSpan(2), length, big);
        return header;
    }

    // ==================== NMEA 0183 ====================

    /// <summary>把字符串按 ASCII 编码为字节（NMEA 全是 ASCII）。</summary>
    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    /// <summary>
    /// 拼一条 NMEA 语句：<c>$body*hh\r\n</c>；校验和 = <c>$</c> 与 <c>*</c> 之间所有字符的异或。
    /// </summary>
    /// <param name="checksumDelta">给正确校验和加的偏移量，用于故意造出错误校验和。</param>
    /// <param name="withChecksum">false 时完全不写 <c>*hh</c>。</param>
    /// <param name="crlf">false 时不写行尾 CR/LF。</param>
    public static string Nmea(string body, int checksumDelta = 0, bool withChecksum = true, bool crlf = true)
    {
        byte checksum = 0;
        foreach (var ch in body) checksum ^= (byte)ch;
        checksum = (byte)(checksum + checksumDelta);

        var text = "$" + body;
        if (withChecksum) text += "*" + checksum.ToString("X2", CultureInfo.InvariantCulture);
        if (crlf) text += "\r\n";
        return text;
    }

    /// <summary>按 <c>Scan</c> 切出的「帧」形态给字节：含行尾 CR、不含 LF。</summary>
    public static byte[] NmeaLine(string body, int checksumDelta = 0, bool withChecksum = true) =>
        Ascii(Nmea(body, checksumDelta, withChecksum, crlf: false) + "\r");

    /// <summary>去掉整条语句的尾部 LF，得到 <c>Scan</c> 会切出的那一帧。</summary>
    public static byte[] WithoutTrailingLf(string nmeaWithCrLf) =>
        Ascii(nmeaWithCrLf[..^1]);
}
