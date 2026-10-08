using UavPlatform.Core.Models;

namespace UavPlatform.Core.Protocols;

/// <summary>
/// NSR 雷达通信协议 V1.2.9（湖南纳雷科技）。
/// 帧格式：<c>A5 5A | 源地址 | 目的地址 | 命令码 | 参数长度N(2,小端) | 参数(N) | 校验和</c>，
/// 总帧长 = N + 8；校验和 = 从「源地址」到「参数最后一字节」的所有字节累加和 &amp; 0xFF。
/// 数字一律小端。
/// </summary>
public sealed class NsrRadarProtocol : DeviceProtocol
{
    /// <summary>协议固定起始码。</summary>
    public static readonly byte[] Header = [0xA5, 0x5A];

    /// <summary>单个目标 / 点云记录的字节数。</summary>
    public const int RecordSize = 68;

    /// <summary>帧头之后的固定开销：源地址 1 + 目的地址 1 + 命令码 1 + 参数长度 2 + 校验和 1。</summary>
    public const int Overhead = 8;

    public override string Name => "NSR 雷达 V1.2.9";

    // ---- 命令码（协议 §6.2）----
    public const byte CmdFactoryReset = 0x01;
    public const byte CmdAddCoordinate = 0x03;
    public const byte CmdSetNetwork = 0x04;
    public const byte CmdBroadcastNetwork = 0x05;
    public const byte CmdSetHeartbeatInterval = 0x09;
    public const byte CmdReadStatus = 0x0A;
    public const byte CmdSetAddress = 0x0B;
    public const byte CmdSetSystemTime = 0x24;
    public const byte CmdSaveParams = 0x88;
    public const byte CmdAck = 0xA2;
    public const byte CmdHeartbeat = 0xA4;
    public const byte CmdTargets = 0xA8;
    public const byte CmdPointCloud = 0xA9;
    public const byte CmdFirmwareUpdate = 0xCC;
    public const byte CmdReboot = 0xDB;
    public const byte CmdReadParam = 0xE0;
    public const byte CmdSetParam = 0xE1;
    public const byte CmdSetParamTemp = 0xE3;
    public const byte CmdDiscover = 0xFF;

    /// <summary>应答参数：命令执行成功。</summary>
    public const byte AckSuccess = 0x0F;

    /// <summary>应答参数：命令执行不成功。</summary>
    public const byte AckFailure = 0xF0;

    private readonly RadarProtocolSettings _settings;

    public NsrRadarProtocol(RadarProtocolSettings settings) => _settings = settings;

    /// <inheritdoc />
    public override FrameScan Scan(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 2) return FrameScan.NeedMore;

        if (buffer[0] != Header[0] || buffer[1] != Header[1])
        {
            // 重新同步：跳到下一个可能的起始码
            var next = buffer[1..].IndexOf(Header[0]);
            return FrameScan.Skip(next < 0 ? buffer.Length : next + 1);
        }

        if (buffer.Length < 7) return FrameScan.NeedMore; // 还需要读到参数长度

        int paramLength = buffer[5] | (buffer[6] << 8);
        int total = paramLength + Overhead;
        if (buffer.Length < total) return FrameScan.NeedMore;

        // 校验和覆盖范围：源地址(2) ~ 参数最后一字节(6+paramLength)
        byte sum = 0;
        for (var i = 2; i < 7 + paramLength; i++) sum += buffer[i];
        if (sum != buffer[7 + paramLength])
        {
            // 校验失败：丢弃起始码两字节，重新寻找帧头
            return FrameScan.Skip(2);
        }

        return FrameScan.Frame(total);
    }

    /// <summary>
    /// 解析一整帧（<paramref name="frame"/> 必须是 <see cref="Scan"/> 认可的完整帧）。
    /// </summary>
    public RadarFrameResult Parse(ReadOnlySpan<byte> frame, DeviceKind device, string? deviceName, long sequence)
    {
        var now = DateTimeOffset.Now;
        var paramLength = frame[5] | (frame[6] << 8);
        var source = frame[2];
        var command = frame[4];
        var parameters = frame.Slice(7, paramLength);

        var result = new RadarFrameResult
        {
            Command = command,
            SourceAddress = source,
            Timestamp = now,
            Sequence = sequence,
            DeviceName = deviceName,
        };

        switch (command)
        {
            case CmdTargets:
                result.Targets = ParseRecords(parameters, targetCountOffset: 1, out var declaredTargets);
                result.DeclaredCount = declaredTargets;
                result.IsPointCloud = false;
                result.Kind = RadarFrameKind.Targets;
                break;

            case CmdPointCloud:
                result.Targets = ParseRecords(parameters, targetCountOffset: 4, out var declaredPoints);
                result.DeclaredCount = declaredPoints;
                result.IsPointCloud = true;
                result.Kind = RadarFrameKind.PointCloud;
                break;

            case CmdAck when paramLength >= 2:
                result.Kind = RadarFrameKind.Ack;
                result.Ack = new RadarAck
                {
                    Command = parameters[0],
                    Success = parameters[1] == AckSuccess,
                };
                break;

            case CmdAck:
                result.Kind = RadarFrameKind.Ack;
                result.Ack = new RadarAck { Command = 0, Success = false };
                break;

            case CmdHeartbeat:
                result.Kind = RadarFrameKind.Heartbeat;
                result.HeartbeatIntervalSec = paramLength >= 1 ? parameters[0] : (byte)0;
                break;

            default:
                // 读取雷达状态（0x0A）等响应，原样保留参数供界面展示
                result.Kind = RadarFrameKind.Other;
                result.Parameters = parameters.ToArray();
                break;
        }

        return result;
    }

    /// <summary>解析目标 / 点云记录列表。</summary>
    private static RadarTarget[] ParseRecords(ReadOnlySpan<byte> parameters, int targetCountOffset, out int declaredCount)
    {
        declaredCount = 0;
        if (parameters.Length < targetCountOffset) return [];

        declaredCount = targetCountOffset == 1
            ? parameters[0]                                             // 0xA8：个数占 1 字节
            : (int)Endian.U32(parameters, 0, bigEndian: false);          // 0xA9：个数占 4 字节（上限 65535）

        var available = (parameters.Length - targetCountOffset) / RecordSize;
        var count = Math.Min(declaredCount, available);
        if (count <= 0) return [];

        var targets = new RadarTarget[count];
        for (var i = 0; i < count; i++)
        {
            var r = parameters.Slice(targetCountOffset + i * RecordSize, RecordSize);
            targets[i] = new RadarTarget
            {
                Id = Endian.U32(r, 0, bigEndian: false),
                Type = Endian.I32(r, 4, bigEndian: false),
                SpeedX = Endian.F32(r, 8, littleEndian: true),
                SpeedY = Endian.F32(r, 12, littleEndian: true),
                SpeedZ = Endian.F32(r, 16, littleEndian: true),
                X = Endian.F32(r, 20, littleEndian: true),
                Y = Endian.F32(r, 24, littleEndian: true),
                Z = Endian.F32(r, 28, littleEndian: true),
                Range = Endian.F32(r, 32, littleEndian: true),
                AzimuthDeg = Endian.F32(r, 36, littleEndian: true),
                ElevationDeg = Endian.F32(r, 40, littleEndian: true),
                Snr = Endian.F32(r, 44, littleEndian: true),
                PeakEnergyDb = Endian.F32(r, 48, littleEndian: true),
                // area 为 2 字节，低字节在前、高字节在后，共 16 bit，bit0 = 第一防区
                AreaMask = r[52] | (r[53] << 8),
            };
        }

        return targets;
    }

    // ---- 命令构造 ----

    /// <summary>构造任意命令帧。</summary>
    public byte[] BuildCommand(byte command, ReadOnlySpan<byte> parameters, byte? destination = null)
    {
        var dest = destination ?? _settings.RadarAddress;
        var frame = new byte[Overhead + parameters.Length];
        frame[0] = Header[0];
        frame[1] = Header[1];
        frame[2] = _settings.LocalAddress; // 源地址：上位机固定 0x10
        frame[3] = dest;
        frame[4] = command;
        frame[5] = (byte)(parameters.Length & 0xFF);
        frame[6] = (byte)((parameters.Length >> 8) & 0xFF);
        parameters.CopyTo(frame.AsSpan(7));

        byte sum = 0;
        for (var i = 2; i < 7 + parameters.Length; i++) sum += frame[i];
        frame[7 + parameters.Length] = sum;
        return frame;
    }

    /// <summary>心跳包（0xA4）：参数为心跳间隔秒数（0~255，默认 5）。</summary>
    public byte[] BuildHeartbeat(int intervalSeconds)
    {
        var clamped = (byte)Math.Clamp(intervalSeconds, 0, 255);
        return BuildCommand(CmdHeartbeat, [clamped]);
    }

    /// <summary>读取雷达状态（0x0A）：返回地址编码、固件版本号、雷达型号等。</summary>
    public byte[] BuildReadStatus() => BuildCommand(CmdReadStatus, []);

    /// <summary>保存参数（0x88）。</summary>
    public byte[] BuildSaveParams() => BuildCommand(CmdSaveParams, []);

    /// <summary>设备发现（0xFF），目的地址固定为广播。</summary>
    public byte[] BuildDiscover() => BuildCommand(CmdDiscover, [], destination: 0xFF);

    /// <summary>重启系统（0xDB）。</summary>
    public byte[] BuildReboot() => BuildCommand(CmdReboot, []);

    /// <summary>读取参数（0xE0）：参数为要读取的参数编码（2 字节小端）。</summary>
    public byte[] BuildReadParameter(ushort parameterCode) =>
        BuildCommand(CmdReadParam, [(byte)(parameterCode & 0xFF), (byte)(parameterCode >> 8)]);

    /// <summary>设置参数（0xE1）。</summary>
    public byte[] BuildSetParameter(ushort parameterCode, ReadOnlySpan<byte> value)
    {
        var parameters = new byte[2 + value.Length];
        parameters[0] = (byte)(parameterCode & 0xFF);
        parameters[1] = (byte)(parameterCode >> 8);
        value.CopyTo(parameters.AsSpan(2));
        return BuildCommand(CmdSetParam, parameters);
    }
}

/// <summary>雷达帧的类别。</summary>
public enum RadarFrameKind
{
    /// <summary>未特别处理的帧（如读取状态响应）。</summary>
    Other = 0,

    /// <summary>目标信息传输（0xA8）。</summary>
    Targets = 1,

    /// <summary>点云传输（0xA9）。</summary>
    PointCloud = 2,

    /// <summary>通用应答（0xA2）。</summary>
    Ack = 3,

    /// <summary>心跳包（0xA4）。</summary>
    Heartbeat = 4,
}

/// <summary>一帧雷达数据的解析结果。</summary>
public sealed class RadarFrameResult
{
    public RadarFrameKind Kind { get; set; }

    /// <summary>触发本帧的命令码。</summary>
    public byte Command { get; set; }

    /// <summary>雷达源地址编码。</summary>
    public byte SourceAddress { get; set; }

    /// <summary>是否来自点云传输（0xA9）。</summary>
    public bool IsPointCloud { get; set; }

    /// <summary>帧中声明的目标个数。</summary>
    public int DeclaredCount { get; set; }

    /// <summary>目标 / 点云列表（雷达坐标系）。</summary>
    public RadarTarget[] Targets { get; set; } = [];

    /// <summary>通用应答内容。</summary>
    public RadarAck? Ack { get; set; }

    /// <summary>心跳间隔秒数（收到心跳包时）。</summary>
    public int HeartbeatIntervalSec { get; set; }

    /// <summary>未特别处理帧的原始参数。</summary>
    public byte[]? Parameters { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public long Sequence { get; set; }

    public string? DeviceName { get; set; }

    /// <summary>转换为落盘 / 推送用的样本。</summary>
    public RadarSample ToSample() => new()
    {
        Device = DeviceKind.Radar,
        Timestamp = Timestamp,
        Sequence = Sequence,
        DeviceName = DeviceName,
        DeviceTime = null,
        IsPointCloud = IsPointCloud,
        Command = Command,
        SourceAddress = SourceAddress,
        Targets = Targets,
        DeclaredCount = DeclaredCount,
    };
}
