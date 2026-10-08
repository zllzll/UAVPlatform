using UavPlatform.Core.Models;
using UavPlatform.Core.Protocols;

namespace UavPlatform.Tests;

/// <summary>
/// NSR 雷达协议 V1.2.9（TCP，小端）单元测试。
/// 全部帧由 <see cref="FrameBuilder"/> 按协议字节布局手工拼装，不依赖被测实现的构造方法。
/// </summary>
public class NsrRadarProtocolTests
{
    private static NsrRadarProtocol NewProtocol(RadarProtocolSettings? settings = null) =>
        new(settings ?? new RadarProtocolSettings());

    // ==================== 帧结构与校验和 ====================

    [Fact]
    public void 心跳帧_手工字节序列应与协议一致()
    {
        // A5 5A | 源 10 | 目的 FF | 命令 A4 | 长度 01 00 | 参数 05 | 校验和
        // 校验和覆盖源地址..参数末字节：10 + FF + A4 + 01 + 00 + 05 = 0x1B9 → 0xB9
        // 总长 = 参数长度 1 + 8 = 9
        var frame = FrameBuilder.Nsr(0xA4, [0x05], source: 0x10, destination: 0xFF);

        Assert.Equal<byte>([0xA5, 0x5A, 0x10, 0xFF, 0xA4, 0x01, 0x00, 0x05, 0xB9], frame);

        var scan = NewProtocol().Scan(frame);
        Assert.Equal(FrameScanStatus.Frame, scan.Status);
        Assert.Equal(9, scan.Length);
    }

    [Fact]
    public void 心跳帧_应解析出心跳间隔与源地址()
    {
        var frame = FrameBuilder.Nsr(0xA4, [0x07], source: 0x03);
        var result = NewProtocol().Parse(frame, DeviceKind.Radar, "雷达1", sequence: 42);

        Assert.Equal(RadarFrameKind.Heartbeat, result.Kind);
        Assert.Equal(7, result.HeartbeatIntervalSec);
        Assert.Equal(0x03, result.SourceAddress);
        Assert.Equal(0xA4, result.Command);
        Assert.Equal(42, result.Sequence);
        Assert.Equal("雷达1", result.DeviceName);
    }

    [Fact]
    public void 应答帧_参数为0x0F时表示成功()
    {
        var frame = FrameBuilder.Nsr(0xA2, [0x88, 0x0F]);
        var result = NewProtocol().Parse(frame, DeviceKind.Radar, null, 0);

        Assert.Equal(RadarFrameKind.Ack, result.Kind);
        Assert.NotNull(result.Ack);
        Assert.Equal(0x88, result.Ack!.Command);
        Assert.True(result.Ack.Success);
    }

    [Fact]
    public void 应答帧_参数为0xF0时表示不成功()
    {
        var frame = FrameBuilder.Nsr(0xA2, [0x88, 0xF0]);
        var result = NewProtocol().Parse(frame, DeviceKind.Radar, null, 0);

        Assert.Equal(RadarFrameKind.Ack, result.Kind);
        Assert.False(result.Ack!.Success);
    }

    // ==================== 目标信息 0xA8 ====================

    [Fact]
    public void 目标帧_单目标的全部字段应逐项对应()
    {
        var record = FrameBuilder.NsrRecord(
            id: 0x01020304,
            type: 2,
            xSpeed: 1.5f,
            ySpeed: -2.5f,
            zSpeed: 0.25f,
            x: 10f,
            y: 100f,
            z: -3f,
            length: 100.5f,
            azimuth: 5.5f,
            elevation: -1.25f,
            snr: 20f,
            peakEnergy: 33.5f,
            area: 0x0005);

        // 0xA8 参数 = 目标个数(1 字节) + N×68 字节
        var parameters = FrameBuilder.Concat([0x01], record);
        var frame = FrameBuilder.Nsr(0xA8, parameters);

        Assert.Equal(77, frame.Length); // 参数 68 + 1 = 69 字节，加帧开销 8 = 77
        var scan = NewProtocol().Scan(frame);
        Assert.Equal(FrameScanStatus.Frame, scan.Status);
        Assert.Equal(77, scan.Length);

        var result = NewProtocol().Parse(frame, DeviceKind.Radar, null, 0);

        Assert.Equal(RadarFrameKind.Targets, result.Kind);
        Assert.False(result.IsPointCloud);
        Assert.Equal(1, result.DeclaredCount);

        var target = Assert.Single(result.Targets);
        Assert.Equal(0x01020304u, target.Id);
        Assert.Equal(2, target.Type);
        Assert.Equal("车", target.TypeName);
        Assert.Equal(1.5, target.SpeedX, 1e-6);
        Assert.Equal(-2.5, target.SpeedY, 1e-6);
        Assert.Equal(0.25, target.SpeedZ, 1e-6);
        Assert.Equal(10.0, target.X, 1e-6);
        Assert.Equal(100.0, target.Y, 1e-6);
        Assert.Equal(-3.0, target.Z, 1e-6);
        Assert.Equal(100.5, target.Range, 1e-6);
        Assert.Equal(5.5, target.AzimuthDeg, 1e-6);
        Assert.Equal(-1.25, target.ElevationDeg, 1e-6);
        Assert.Equal(20.0, target.Snr, 1e-6);
        Assert.Equal(33.5, target.PeakEnergyDb, 1e-6);
        Assert.Equal(0x0005, target.AreaMask);
    }

    [Fact]
    public void 目标帧_报警区域掩码低字节在前()
    {
        // area = 0x0102 → r[52] = 0x02（低字节）、r[53] = 0x01（高字节）→ AreaMask = 0x0102
        var record = FrameBuilder.NsrRecord(id: 1, type: 1, area: 0x0102);
        var frame = FrameBuilder.Nsr(0xA8, FrameBuilder.Concat([0x01], record));

        var result = NewProtocol().Parse(frame, DeviceKind.Radar, null, 0);

        var target = Assert.Single(result.Targets);
        Assert.Equal(0x02, record[52]);
        Assert.Equal(0x01, record[53]);
        Assert.Equal(0x0102, target.AreaMask);
    }

    [Fact]
    public void 目标帧_已删除目标的类型为0xFFFF()
    {
        // 已删除标记：type 字段为 0x0000FFFF，按 int32 读取仍是 65535（正数）
        var record = FrameBuilder.NsrRecord(id: 9, type: 0xFFFF);
        var frame = FrameBuilder.Nsr(0xA8, FrameBuilder.Concat([0x01], record));

        var target = Assert.Single(NewProtocol().Parse(frame, DeviceKind.Radar, null, 0).Targets);

        Assert.Equal(0xFFFF, target.Type);
        Assert.Equal("已删除", target.TypeName);
    }

    [Fact]
    public void 目标帧_多目标应按顺序解析()
    {
        var first = FrameBuilder.NsrRecord(id: 11, type: 1, x: 1f, y: 11f);
        var second = FrameBuilder.NsrRecord(id: 22, type: 2, x: 2f, y: 22f);
        var third = FrameBuilder.NsrRecord(id: 33, type: 3, x: 3f, y: 33f);
        var frame = FrameBuilder.Nsr(0xA8, FrameBuilder.Concat([0x03], first, second, third));

        var result = NewProtocol().Parse(frame, DeviceKind.Radar, null, 0);

        Assert.Equal(3, result.DeclaredCount);
        Assert.Equal(3, result.Targets.Length);
        Assert.Equal(new uint[] { 11, 22, 33 }, result.Targets.Select(t => t.Id));
        Assert.Equal(new double[] { 11, 22, 33 }, result.Targets.Select(t => t.Y));
        Assert.Equal(["人", "车", "树"], result.Targets.Select(t => t.TypeName));
    }

    [Fact]
    public void 目标帧_声明个数大于实际记录数时以实际为准()
    {
        var record = FrameBuilder.NsrRecord(id: 1, type: 1);
        // 声明 5 个目标，实际只给了 1 条 68 字节记录
        var frame = FrameBuilder.Nsr(0xA8, FrameBuilder.Concat([0x05], record));

        var result = NewProtocol().Parse(frame, DeviceKind.Radar, null, 0);

        Assert.Equal(5, result.DeclaredCount);
        Assert.Single(result.Targets);
    }

    [Fact]
    public void 目标帧_参数不足一个记录时应返回空列表()
    {
        // 参数只有个数 + 10 字节残片，凑不满 68 字节
        var frame = FrameBuilder.Nsr(0xA8, FrameBuilder.Concat([0x01], new byte[10]));

        var result = NewProtocol().Parse(frame, DeviceKind.Radar, null, 0);

        Assert.Equal(1, result.DeclaredCount);
        Assert.Empty(result.Targets);
    }

    // ==================== 点云 0xA9 ====================

    [Fact]
    public void 点云帧_目标个数占4字节小端()
    {
        var first = FrameBuilder.NsrRecord(id: 1001, type: 0, x: 1f, y: 50f);
        var second = FrameBuilder.NsrRecord(id: 1002, type: 0, x: 2f, y: 60f);
        // 0xA9 参数 = 点云个数(uint32 小端) + N×68 字节
        var frame = FrameBuilder.Nsr(0xA9, FrameBuilder.Concat(FrameBuilder.U32Le(2), first, second));

        var scan = NewProtocol().Scan(frame);
        Assert.Equal(FrameScanStatus.Frame, scan.Status);
        Assert.Equal(4 + 68 * 2 + 8, scan.Length);

        var result = NewProtocol().Parse(frame, DeviceKind.Radar, null, 0);

        Assert.Equal(RadarFrameKind.PointCloud, result.Kind);
        Assert.True(result.IsPointCloud);
        Assert.Equal(2, result.DeclaredCount);
        Assert.Equal(2, result.Targets.Length);
        Assert.Equal(1001u, result.Targets[0].Id);
        Assert.Equal(60.0, result.Targets[1].Y, 1e-6);
    }

    [Fact]
    public void 点云帧_个数的高字节参与解析()
    {
        // 个数 0x00000100 = 256：小端字节为 00 01 00 00，说明确实读了 4 字节
        var record = FrameBuilder.NsrRecord(id: 1, type: 0);
        var frame = FrameBuilder.Nsr(0xA9, FrameBuilder.Concat(FrameBuilder.U32Le(256), record));

        var result = NewProtocol().Parse(frame, DeviceKind.Radar, null, 0);

        Assert.Equal(256, result.DeclaredCount);
        Assert.Single(result.Targets); // 实际只有 1 条，按实际为准
    }

    [Fact]
    public void 点云帧_type为单字节后跟3字节保留_仍应读出type()
    {
        // 点云记录的 type 只占 1 字节（0x06 小船），后 3 字节为零填充
        var record = FrameBuilder.NsrRecord(id: 7, type: 6, x: 1f, y: 2f, singleByteType: true);
        Assert.Equal(0x06, record[4]);
        Assert.Equal(0x00, record[5]);

        var frame = FrameBuilder.Nsr(0xA9, FrameBuilder.Concat(FrameBuilder.U32Le(1), record));
        var target = Assert.Single(NewProtocol().Parse(frame, DeviceKind.Radar, null, 0).Targets);

        Assert.Equal(6, target.Type);
        Assert.Equal("小船", target.TypeName);
    }

    [Theory]
    [InlineData(4, "船")]
    [InlineData(6, "小船")]
    [InlineData(7, "中船")]
    [InlineData(8, "大船")]
    public void 点云帧_船只类型描述的映射(int type, string expected)
    {
        var record = FrameBuilder.NsrRecord(id: 1, type: type, singleByteType: true);
        var frame = FrameBuilder.Nsr(0xA9, FrameBuilder.Concat(FrameBuilder.U32Le(1), record));

        var target = Assert.Single(NewProtocol().Parse(frame, DeviceKind.Radar, null, 0).Targets);

        Assert.Equal(type, target.Type);
        Assert.Equal(expected, target.TypeName);
    }

    // ==================== 其他命令与命令构造 ====================

    [Fact]
    public void 读取状态响应_应保留原始参数()
    {
        var frame = FrameBuilder.Nsr(0x0A, [0x01, 0x02, 0x03, 0x04]);
        var result = NewProtocol().Parse(frame, DeviceKind.Radar, null, 0);

        Assert.Equal(RadarFrameKind.Other, result.Kind);
        Assert.Equal<byte>([0x01, 0x02, 0x03, 0x04], result.Parameters!);
    }

    [Fact]
    public void 读取参数命令_参数编码应为2字节小端()
    {
        var protocol = NewProtocol();
        var frame = protocol.BuildReadParameter(0x1234);

        Assert.Equal(0xA5, frame[0]);
        Assert.Equal(0x5A, frame[1]);
        Assert.Equal(0x10, frame[2]);      // 上位机地址固定 0x10
        Assert.Equal(0xE0, frame[4]);      // 读取参数命令码
        Assert.Equal<byte>([0x34, 0x12], frame[7..9]);

        // 自造命令帧也应能被自己的 Scan/Parse 读回
        Assert.Equal(FrameScanStatus.Frame, protocol.Scan(frame).Status);
        Assert.Equal<byte>([0x34, 0x12], protocol.Parse(frame, DeviceKind.Radar, null, 0).Parameters!);
    }

    [Fact]
    public void 设备发现命令_目的地址应为广播()
    {
        var frame = NewProtocol().BuildDiscover();

        Assert.Equal(0xFF, frame[3]);
        Assert.Equal(0xFF, frame[4]);
        Assert.Equal(FrameScanStatus.Frame, NewProtocol().Scan(frame).Status);
    }

    [Fact]
    public void 心跳命令构造_间隔秒数应被限制在0到255()
    {
        var protocol = NewProtocol();

        Assert.Equal(0xFF, protocol.BuildHeartbeat(300)[7]);
        Assert.Equal(0x00, protocol.BuildHeartbeat(-5)[7]);
        Assert.Equal(0x05, protocol.BuildHeartbeat(5)[7]);
    }

    // ==================== 分帧稳健性 ====================

    [Fact]
    public void Scan_数据不足应返回NeedMore()
    {
        var protocol = NewProtocol();

        Assert.Equal(FrameScanStatus.NeedMore, protocol.Scan([]).Status);
        Assert.Equal(FrameScanStatus.NeedMore, protocol.Scan([0xA5]).Status);
        Assert.Equal(FrameScanStatus.NeedMore, protocol.Scan([0xA5, 0x5A, 0x10, 0xFF, 0xA4, 0x01]).Status);
        // 已读到长度字段（N=1，总长应为 9），但只有 8 字节
        Assert.Equal(FrameScanStatus.NeedMore, protocol.Scan([0xA5, 0x5A, 0x10, 0xFF, 0xA4, 0x01, 0x00, 0x05]).Status);
    }

    [Fact]
    public void Scan_帧前垃圾字节应被跳过且帧头保留在缓冲区首位()
    {
        var protocol = NewProtocol();
        var frame = FrameBuilder.Nsr(0xA4, [0x05]);
        var data = FrameBuilder.Concat([0x11, 0x22, 0x33], frame);

        var scan = protocol.Scan(data);

        Assert.Equal(FrameScanStatus.Skip, scan.Status);
        Assert.Equal(3, scan.Length);
        // 跳过后缓冲区首位正是帧头，下一次 Scan 应按完整帧切出
        var second = protocol.Scan(data.AsSpan(scan.Length));
        Assert.Equal(FrameScanStatus.Frame, second.Status);
        Assert.Equal(frame.Length, second.Length);
    }

    [Fact]
    public void Scan_缓冲区无帧头时应一次跳过全部字节()
    {
        var scan = NewProtocol().Scan([0x11, 0x22, 0x33]);

        Assert.Equal(FrameScanStatus.Skip, scan.Status);
        Assert.Equal(3, scan.Length);
    }

    [Fact]
    public void Scan_校验和错误应丢弃两个字节后重新寻找帧头()
    {
        var protocol = NewProtocol();
        var frame = FrameBuilder.Nsr(0xA4, [0x05]);
        frame[^1] ^= 0xFF; // 破坏校验和

        var scan = protocol.Scan(frame);

        Assert.Equal(FrameScanStatus.Skip, scan.Status);
        Assert.Equal(2, scan.Length);
    }

    [Fact]
    public void 分帧_粘包分包与垃圾字节混合应切出全部帧()
    {
        var targets = FrameBuilder.Nsr(0xA8, FrameBuilder.Concat([0x02], FrameBuilder.NsrRecord(id: 1, type: 1, y: 10f), FrameBuilder.NsrRecord(id: 2, type: 2, y: 20f)));
        var heartbeat = FrameBuilder.Nsr(0xA4, [0x05]);
        var ack = FrameBuilder.Nsr(0xA2, [0x88, 0x0F]);
        // 2 字节垃圾 + 三帧粘连，且按 7 字节一块分包喂入
        var data = FrameBuilder.Concat([0x00, 0x7F], targets, heartbeat, ack);

        var frames = ScanPump.Drain(NewProtocol(), data, chunkSize: 7);

        Assert.Equal(3, frames.Count);
        Assert.Equal<byte>(targets, frames[0]);
        Assert.Equal<byte>(heartbeat, frames[1]);
        Assert.Equal<byte>(ack, frames[2]);

        var protocol = NewProtocol();
        var first = protocol.Parse(frames[0], DeviceKind.Radar, null, 1);
        Assert.Equal(2, first.Targets.Length);
        Assert.Equal(new double[] { 10, 20 }, first.Targets.Select(t => t.Y));
        Assert.Equal(RadarFrameKind.Heartbeat, protocol.Parse(frames[1], DeviceKind.Radar, null, 2).Kind);
        Assert.True(protocol.Parse(frames[2], DeviceKind.Radar, null, 3).Ack!.Success);
    }

    [Fact]
    public void 分帧_末尾残帧不应被切出()
    {
        var complete = FrameBuilder.Nsr(0xA4, [0x05]);
        var truncated = FrameBuilder.Nsr(0xA8, FrameBuilder.Concat([0x01], FrameBuilder.NsrRecord(id: 1, type: 1)));
        var data = FrameBuilder.Concat(complete, truncated.AsSpan(0, truncated.Length - 3).ToArray());

        var frames = ScanPump.Drain(NewProtocol(), data);

        Assert.Single(frames);
        Assert.Equal<byte>(complete, frames[0]);
    }

    [Fact]
    public void ToSample_应带出命令码与目标列表()
    {
        var frame = FrameBuilder.Nsr(0xA9, FrameBuilder.Concat(FrameBuilder.U32Le(1), FrameBuilder.NsrRecord(id: 5, type: 0, y: 77f)));
        var result = NewProtocol().Parse(frame, DeviceKind.Radar, "雷达A", 8);

        var sample = result.ToSample();

        Assert.Equal(DeviceKind.Radar, sample.Device);
        Assert.Equal(0xA9, sample.Command);
        Assert.True(sample.IsPointCloud);
        Assert.Equal(1, sample.DeclaredCount);
        Assert.Equal(8, sample.Sequence);
        Assert.Equal("雷达A", sample.DeviceName);
        Assert.Equal(77.0, Assert.Single(sample.Targets).Y, 1e-6);
    }
}
