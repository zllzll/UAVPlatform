using UavPlatform.Core.Models;
using UavPlatform.Core.Protocols;

namespace UavPlatform.Tests;

/// <summary>
/// UCM221（无人机 wifi 模块）上传流协议 V1.1.0 单元测试。
/// 帧由 <see cref="FrameBuilder"/> 按字节布局手工拼装；测试文件里另写了一份字面量起始码，
/// 刻意不引用 <c>Ucm221Protocol</c> 的常量，保证实现写错时测试不会跟着错。
/// </summary>
public class Ucm221ProtocolTests
{
    /// <summary>目标信息模块起始码（协议原文 A8 8A）。</summary>
    private static readonly byte[] TargetHeader = [0xA8, 0x8A];

    /// <summary>点云信息模块起始码（协议原文 A9 9A）。</summary>
    private static readonly byte[] PointCloudHeader = [0xA9, 0x9A];

    // 状态位掩码（协议 §汇总信息「数据包状态」）
    private const byte StatusDataValid = 0x01;
    private const byte StatusExtendedPresent = 0x02;
    private const byte StatusUavInfoPresent = 0x04;

    private static Ucm221Protocol NewProtocol(Ucm221ProtocolSettings? settings = null) =>
        new(settings ?? new Ucm221ProtocolSettings());

    /// <summary>汇总信息的 20 字节（时间固定为 2023-05-12 13:45:30.123，设备 ID 0x1234）。</summary>
    private static byte[] Summary(
        byte packetStatus = StatusDataValid,
        byte targetCount = 0,
        ushort pointCount = 0,
        ushort deviceId = 0x1234) =>
        FrameBuilder.Ucm221Summary(
            packetStatus, targetCount, pointCount,
            yearOffset: 123, month: 5, day: 12, hour: 13, minute: 45, second: 30,
            millisecond: 123, deviceId: deviceId);

    /// <summary>目标信息模块：5 字节模块头 + N1×14 字节记录。</summary>
    private static byte[] TargetModule(int count)
    {
        var records = new byte[count * 14];
        for (var i = 0; i < count; i++)
        {
            FrameBuilder.Ucm221Record(TargetHeader, (ushort)(i + 1)).CopyTo(records, i * 14);
        }

        return FrameBuilder.Ucm221Module(TargetHeader, records);
    }

    /// <summary>点云信息模块：5 字节模块头 + N2×14 字节记录。</summary>
    private static byte[] PointModule(int count)
    {
        var records = new byte[count * 14];
        for (var i = 0; i < count; i++)
        {
            FrameBuilder.Ucm221Record(PointCloudHeader, (ushort)(100 + i)).CopyTo(records, i * 14);
        }

        return FrameBuilder.Ucm221Module(PointCloudHeader, records);
    }

    // ==================== 帧结构与校验和 ====================

    [Fact]
    public void Scan_空帧应切出且长度字段等于负载长度()
    {
        var payload = FrameBuilder.Concat(Summary(), TargetModule(0), PointModule(0));
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);

        // 总长 = 数据总长度 L + 5（起始码 2 + 长度 2 + 校验和 1）
        Assert.Equal(payload.Length + 5, frame.Length);
        Assert.Equal(30, payload.Length); // 汇总 20 + 目标模块 5 + 点云模块 5

        var scan = NewProtocol().Scan(frame);
        Assert.Equal(FrameScanStatus.Frame, scan.Status);
        Assert.Equal(frame.Length, scan.Length);
    }

    [Fact]
    public void Scan_数据不足应返回NeedMore()
    {
        var protocol = NewProtocol();

        Assert.Equal(FrameScanStatus.NeedMore, protocol.Scan([]).Status);
        Assert.Equal(FrameScanStatus.NeedMore, protocol.Scan([0xA5]).Status);
        // 只有 3 字节，还读不到长度字段
        Assert.Equal(FrameScanStatus.NeedMore, protocol.Scan([0xA5, 0x5A, 0x1E]).Status);
        // 长度字段齐全但负载不完整
        Assert.Equal(FrameScanStatus.NeedMore, protocol.Scan(FrameBuilder.Ucm221LengthHeader(30)).Status);
    }

    [Fact]
    public void Scan_长度字段小于汇总信息长度时应判为失步()
    {
        // L = 19 < 20，长度字段连汇总信息都装不下 → Skip(2)
        var scan = NewProtocol().Scan(FrameBuilder.Ucm221LengthHeader(19));

        Assert.Equal(FrameScanStatus.Skip, scan.Status);
        Assert.Equal(2, scan.Length);
    }

    [Fact]
    public void Scan_长度字段超过上限时应判为失步()
    {
        var scan = NewProtocol().Scan(FrameBuilder.Ucm221LengthHeader(60001));

        Assert.Equal(FrameScanStatus.Skip, scan.Status);
        Assert.Equal(2, scan.Length);
    }

    [Fact]
    public void Scan_校验和错误应丢弃两个字节()
    {
        var payload = FrameBuilder.Concat(Summary(), TargetModule(0), PointModule(0));
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);
        frame[^1] ^= 0xFF;

        var scan = NewProtocol().Scan(frame);

        Assert.Equal(FrameScanStatus.Skip, scan.Status);
        Assert.Equal(2, scan.Length);
    }

    [Fact]
    public void Scan_帧前垃圾字节应被跳过()
    {
        var payload = FrameBuilder.Concat(Summary(), TargetModule(0), PointModule(0));
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);
        var data = FrameBuilder.Concat([0x01, 0x02, 0x03], frame);

        var protocol = NewProtocol();
        var scan = protocol.Scan(data);

        Assert.Equal(FrameScanStatus.Skip, scan.Status);
        Assert.Equal(3, scan.Length);
        Assert.Equal(frame.Length, protocol.Scan(data.AsSpan(3)).Length);
    }

    // ==================== 汇总信息 ====================

    [Fact]
    public void Parse_汇总信息的每个字段应逐项对应()
    {
        var payload = FrameBuilder.Concat(
            Summary(StatusDataValid | StatusExtendedPresent, targetCount: 2, pointCount: 7, deviceId: 0xABCD),
            TargetModule(2),
            PointModule(7));
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);

        var result = NewProtocol().Parse(frame, sequence: 5, deviceName: "机载1");

        Assert.True(result.SummaryHeaderValid);
        Assert.Equal(StatusDataValid | StatusExtendedPresent, result.PacketStatus);
        Assert.Equal(2, result.TargetCount);
        Assert.Equal(14, result.TargetSize);
        Assert.Equal(7, result.PointCloudCount);
        Assert.Equal(14, result.PointSize);
        Assert.Equal(0xABCD, result.DeviceId);
        Assert.Equal("2023-05-12T13:45:30.123", result.DeviceTime);
        Assert.Equal(5, result.Sequence);
        Assert.Equal("机载1", result.DeviceName);
    }

    [Fact]
    public void Parse_汇总信息起始码错误时标志位为假()
    {
        var summary = Summary();
        summary[0] = 0x00; // 破坏 A7 7A
        var payload = FrameBuilder.Concat(summary, TargetModule(0), PointModule(0));
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);

        var result = NewProtocol().Parse(frame, 0, null);

        Assert.False(result.SummaryHeaderValid);
    }

    [Fact]
    public void Parse_月日非法时设备时间应为空串()
    {
        var summary = FrameBuilder.Ucm221Summary(
            StatusDataValid, 0, 0, 123, month: 13, day: 40, hour: 0, minute: 0, second: 0,
            millisecond: 0, deviceId: 1);
        var frame = FrameBuilder.Ucm221Frame(big: false, FrameBuilder.Concat(summary, TargetModule(0), PointModule(0)));

        Assert.Equal(string.Empty, NewProtocol().Parse(frame, 0, null).DeviceTime);
    }

    [Fact]
    public void Parse_年偏移以1900为基准()
    {
        var summary = FrameBuilder.Ucm221Summary(
            StatusDataValid, 0, 0, yearOffset: 0, month: 1, day: 2, hour: 3, minute: 4, second: 5,
            millisecond: 6, deviceId: 1);
        var frame = FrameBuilder.Ucm221Frame(big: false, FrameBuilder.Concat(summary, TargetModule(0), PointModule(0)));

        Assert.Equal("1900-01-02T03:04:05.006", NewProtocol().Parse(frame, 0, null).DeviceTime);
    }

    // ==================== 目标 / 点云模块定位 ====================

    [Fact]
    public void Parse_目标模块紧接汇总信息且起始码应被识别()
    {
        var payload = FrameBuilder.Concat(Summary(targetCount: 3), TargetModule(3), PointModule(0));
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);

        var result = NewProtocol().Parse(frame, 0, null);

        Assert.Equal(20, result.TargetInfoOffset);
        Assert.True(result.TargetInfoHeaderValid);
        // 点云模块偏移 = 20 + 5 + 14×N1
        Assert.Equal(20 + 5 + 14 * 3, result.PointCloudInfoOffset);
        Assert.True(result.PointCloudInfoHeaderValid);
        Assert.Equal(payload.Length - (result.PointCloudInfoOffset + 5), 0);
    }

    [Fact]
    public void Parse_目标模块起始码错误时标志位为假()
    {
        var target = TargetModule(1);
        target[0] = 0x00; // 破坏 A8 8A
        var payload = FrameBuilder.Concat(Summary(targetCount: 1), target, PointModule(0));
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);

        var result = NewProtocol().Parse(frame, 0, null);

        Assert.False(result.TargetInfoHeaderValid);
        Assert.True(result.PointCloudInfoHeaderValid);
        // 偏移仍按声明个数推进（协议要求单目标大小固定 14 字节）
        Assert.Equal(20 + 5 + 14, result.PointCloudInfoOffset);
    }

    [Fact]
    public void Parse_点云模块起始码错误时标志位为假()
    {
        var point = PointModule(2);
        point[1] = 0x00; // 破坏 A9 9A 的第二个字节
        var payload = FrameBuilder.Concat(Summary(pointCount: 2), TargetModule(0), point);
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);

        var result = NewProtocol().Parse(frame, 0, null);

        Assert.True(result.TargetInfoHeaderValid);
        Assert.False(result.PointCloudInfoHeaderValid);
    }

    // ==================== 扩展信息模块 0xA33A ====================

    /// <summary>一块内容确定的扩展信息模块：长度字段默认 45。</summary>
    private static byte[] ExtendedSample(int lengthField = 45) =>
        FrameBuilder.Ucm221Extended(
            lengthField,
            accel1: [100, -200, 300],
            gyro1: [1000, -2000, 3000],
            accel2: [-1, 2, -3],
            gyro2: [4, -5, 6],
            latitude: 399041984,     // 39.9041984°
            longitude: 1164073984,   // 116.4073984°
            altitude: 45600,         // 45.6 m
            speed: 1234,             // 12.34 m/s
            magneticDeclination: -350); // -3.5°

    [Fact]
    public void 扩展信息_各字段应按缩放因子换算()
    {
        var payload = FrameBuilder.Concat(
            Summary(StatusDataValid | StatusExtendedPresent), TargetModule(0), PointModule(0), ExtendedSample());
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);

        var result = NewProtocol().Parse(frame, 0, null);

        Assert.Equal(20 + 5 + 5, result.ExtendedOffset);
        var ext = Assert.IsType<Ucm221Extended>(result.Extended);

        Assert.Equal(100 * 0.004788, ext.Accel1[0], 9);
        Assert.Equal(-200 * 0.004788, ext.Accel1[1], 9);
        Assert.Equal(300 * 0.004788, ext.Accel1[2], 9);
        Assert.Equal(1000 * 0.001065, ext.Gyro1[0], 9);
        Assert.Equal(3000 * 0.001065, ext.Gyro1[2], 9);
        Assert.Equal(-1 * 0.004788, ext.Accel2[0], 9);
        Assert.Equal(6 * 0.001065, ext.Gyro2[2], 9);

        Assert.Equal(39.9041984, ext.Latitude!.Value, 7);
        Assert.Equal(116.4073984, ext.Longitude!.Value, 7);
        Assert.Equal(45.6, ext.AltitudeM!.Value, 6);
        Assert.Equal(12.34, ext.SpeedMps!.Value, 6);
        Assert.Equal(-3.5, ext.MagneticDeclinationDeg!.Value, 6);
    }

    [Theory]
    [InlineData(45)] // 长度字段 = 模块总字节数
    [InlineData(40)] // 长度字段 = 模块总字节数 − 5（起始码2 + 长度2 + 校验和1）
    public void 扩展信息_长度字段两种口径都应被接受(int lengthField)
    {
        var payload = FrameBuilder.Concat(
            Summary(StatusDataValid | StatusExtendedPresent), TargetModule(0), PointModule(0),
            ExtendedSample(lengthField));
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);

        Assert.NotNull(NewProtocol().Parse(frame, 0, null).Extended);
    }

    [Fact]
    public void 扩展信息_长度字段不符时应整体跳过()
    {
        var payload = FrameBuilder.Concat(
            Summary(StatusDataValid | StatusExtendedPresent), TargetModule(0), PointModule(0),
            ExtendedSample(lengthField: 99));
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);

        var result = NewProtocol().Parse(frame, 0, null);

        Assert.Equal(-1, result.ExtendedOffset);
        Assert.Null(result.Extended);
    }

    [Fact]
    public void 扩展信息_模块校验和错误时应整体跳过()
    {
        var extended = ExtendedSample();
        extended[^1] ^= 0xFF; // 破坏模块自身校验和
        var payload = FrameBuilder.Concat(
            Summary(StatusDataValid | StatusExtendedPresent), TargetModule(0), PointModule(0), extended);
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);

        var result = NewProtocol().Parse(frame, 0, null);

        Assert.Equal(-1, result.ExtendedOffset);
        Assert.Null(result.Extended);
    }

    [Fact]
    public void 扩展信息_状态位未置位时不解析()
    {
        // 模块在帧里，但数据包状态没有 Bit1
        var payload = FrameBuilder.Concat(
            Summary(StatusDataValid), TargetModule(0), PointModule(0), ExtendedSample());
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);

        var result = NewProtocol().Parse(frame, 0, null);

        Assert.Null(result.Extended);
    }

    [Fact]
    public void 扩展信息_开关关闭时不解析()
    {
        var payload = FrameBuilder.Concat(
            Summary(StatusDataValid | StatusExtendedPresent), TargetModule(0), PointModule(0), ExtendedSample());
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);

        var result = NewProtocol(new Ucm221ProtocolSettings { ParseExtendedInfo = false }).Parse(frame, 0, null);

        Assert.Null(result.Extended);
    }

    [Fact]
    public void 扩展信息_无效值32768与Int32最小值应解析为无效()
    {
        var extended = FrameBuilder.Ucm221Extended(
            45,
            accel1: [short.MinValue, 0, 0],
            gyro1: [short.MinValue, 0, 0],
            accel2: [0, 0, 0],
            gyro2: [0, 0, 0],
            latitude: int.MinValue,   // 无效
            longitude: int.MinValue,  // 无效
            altitude: int.MinValue,   // 无效
            speed: short.MinValue,    // 无效
            magneticDeclination: short.MinValue);
        var payload = FrameBuilder.Concat(
            Summary(StatusDataValid | StatusExtendedPresent), TargetModule(0), PointModule(0), extended);
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);

        var ext = NewProtocol().Parse(frame, 0, null).Extended!;

        // 加速度/角速度的无效值在源码里被写成 double.NaN
        Assert.True(double.IsNaN(ext.Accel1[0]));
        Assert.True(double.IsNaN(ext.Gyro1[0]));
        Assert.Null(ext.Latitude);
        Assert.Null(ext.Longitude);
        Assert.Null(ext.AltitudeM);
        Assert.Null(ext.SpeedMps);
        Assert.Null(ext.MagneticDeclinationDeg);
    }

    // ==================== 无人机信息模块 0xA22A ====================

    /// <summary>一块内容确定的无人机信息模块：长度字段默认 35。</summary>
    private static byte[] UavInfoSample(int lengthField = 35, ushort flags = 0x0003) =>
        FrameBuilder.Ucm221UavInfo(
            lengthField,
            flags,
            latitude: 399041984,
            longitude: 1164073984,
            altitude: 45600,
            relativeAltitude: 12300,   // 12.3 m
            velocityNorth: 150,        // 1.5 m/s
            velocityEast: -250,        // -2.5 m/s
            velocityDown: 30,          // 0.3 m/s
            roll: 1000,                // 0.1 rad
            pitch: -2000,              // -0.2 rad
            heading: 15708);           // ≈ π/2 rad

    [Fact]
    public void 无人机信息_标志位与各字段应按缩放因子换算()
    {
        var payload = FrameBuilder.Concat(
            Summary(StatusDataValid | StatusUavInfoPresent), TargetModule(0), PointModule(0), UavInfoSample());
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);

        var result = NewProtocol().Parse(frame, 0, null);

        Assert.Equal(30, result.UavInfoOffset);
        var uav = Assert.IsType<Ucm221UavInfo>(result.UavInfo);

        Assert.Equal(0x0003, uav.Flags);
        Assert.True(uav.PositionValid);
        Assert.True(uav.AttitudeValid);
        Assert.Equal(39.9041984, uav.Latitude!.Value, 7);
        Assert.Equal(116.4073984, uav.Longitude!.Value, 7);
        Assert.Equal(45.6, uav.AltitudeM!.Value, 6);
        Assert.Equal(12.3, uav.RelativeAltitudeM!.Value, 6);
        Assert.Equal(1.5, uav.VelocityNorthMps!.Value, 6);
        Assert.Equal(-2.5, uav.VelocityEastMps!.Value, 6);
        Assert.Equal(0.3, uav.VelocityDownMps!.Value, 6);
        Assert.Equal(0.1, uav.RollRad!.Value, 6);
        Assert.Equal(-0.2, uav.PitchRad!.Value, 6);
        Assert.Equal(Math.PI / 2, uav.HeadingRad!.Value, 4); // 角度单位是弧度，不是度
    }

    [Theory]
    [InlineData(0x0000, false, false)]
    [InlineData(0x0001, true, false)]
    [InlineData(0x0002, false, true)]
    [InlineData(0x0003, true, true)]
    public void 无人机信息_标志位Bit0与Bit1分别表示位置与姿态有效(ushort flags, bool positionValid, bool attitudeValid)
    {
        var payload = FrameBuilder.Concat(
            Summary(StatusDataValid | StatusUavInfoPresent), TargetModule(0), PointModule(0),
            UavInfoSample(flags: flags));
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);

        var uav = NewProtocol().Parse(frame, 0, null).UavInfo!;

        Assert.Equal(positionValid, uav.PositionValid);
        Assert.Equal(attitudeValid, uav.AttitudeValid);
    }

    [Theory]
    [InlineData(35)] // 长度字段 = 模块总字节数
    [InlineData(30)] // 长度字段 = 模块总字节数 − 5
    public void 无人机信息_长度字段两种口径都应被接受(int lengthField)
    {
        var payload = FrameBuilder.Concat(
            Summary(StatusDataValid | StatusUavInfoPresent), TargetModule(0), PointModule(0),
            UavInfoSample(lengthField));
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);

        Assert.NotNull(NewProtocol().Parse(frame, 0, null).UavInfo);
    }

    [Fact]
    public void 无人机信息_状态位未置位时不解析()
    {
        var payload = FrameBuilder.Concat(
            Summary(StatusDataValid), TargetModule(0), PointModule(0), UavInfoSample());
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);

        Assert.Null(NewProtocol().Parse(frame, 0, null).UavInfo);
    }

    [Fact]
    public void 无人机信息_角度无效值应为空()
    {
        var payload = FrameBuilder.Concat(
            Summary(StatusDataValid | StatusUavInfoPresent), TargetModule(0), PointModule(0),
            FrameBuilder.Ucm221UavInfo(35, 0x0003, 1, 2, 3, 4, 0, 0, 0,
                roll: short.MinValue, pitch: short.MinValue, heading: short.MinValue));
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);

        var uav = NewProtocol().Parse(frame, 0, null).UavInfo!;

        Assert.Null(uav.RollRad);
        Assert.Null(uav.PitchRad);
        Assert.Null(uav.HeadingRad);
    }

    [Fact]
    public void 两种模块同时存在时应各自定位()
    {
        var payload = FrameBuilder.Concat(
            Summary(StatusDataValid | StatusExtendedPresent | StatusUavInfoPresent),
            TargetModule(1), PointModule(3), ExtendedSample(), UavInfoSample());
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);

        var result = NewProtocol().Parse(frame, 0, null);

        Assert.Equal(20 + 5 + 14 + 5 + 14 * 3, result.ExtendedOffset);
        Assert.Equal(result.ExtendedOffset + 45, result.UavInfoOffset);
        Assert.NotNull(result.Extended);
        Assert.NotNull(result.UavInfo);
    }

    // ==================== ToSample ====================

    [Fact]
    public void ToSample_无人机信息的位置优先于扩展信息()
    {
        var payload = FrameBuilder.Concat(
            Summary(StatusDataValid | StatusExtendedPresent | StatusUavInfoPresent, targetCount: 2, pointCount: 4),
            TargetModule(2), PointModule(4), ExtendedSample(), UavInfoSample());
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);

        var sample = NewProtocol().Parse(frame, sequence: 9, deviceName: "机载").ToSample();

        Assert.Equal(DeviceKind.DroneGps, sample.Device);
        Assert.Equal(9, sample.Sequence);
        Assert.Equal("机载", sample.DeviceName);
        Assert.Equal("2023-05-12T13:45:30.123", sample.DeviceTime);
        Assert.Equal(StatusDataValid | StatusExtendedPresent | StatusUavInfoPresent, sample.PacketStatus);
        Assert.Equal(0x1234, sample.DeviceId);
        Assert.Equal(2, sample.TargetCount);
        Assert.Equal(4, sample.PointCloudCount);

        // UavInfo 与 Extended 的经纬度相同，这里用海拔区分来源：UavInfo 海拔 45.6，Extended 也是 45.6，
        // 因此改用 Assert 断言 Fix 来自 UavInfo —— 其 SpeedMps 为 null（UavInfo 不带 GPS 速度）。
        Assert.NotNull(sample.Fix);
        Assert.Equal(39.9041984, sample.Fix!.Latitude, 7);
        Assert.Equal(116.4073984, sample.Fix.Longitude, 7);
        Assert.True(sample.Fix.Valid);
        Assert.Null(sample.Fix.SpeedMps);

        // 扩展信息的 GPS 速度与磁偏角仍单独带出
        Assert.Equal(12.34, sample.GpsSpeedMps!.Value, 6);
        Assert.Equal(-3.5, sample.MagneticDeclinationDeg!.Value, 6);

        Assert.NotNull(sample.Attitude);
        Assert.Equal(0.1, sample.Attitude!.RollRad!.Value, 6);
        Assert.Equal(12.3, sample.Attitude.RelativeAltitudeM!.Value, 6);

        Assert.NotNull(sample.Imu);
        Assert.Equal(100 * 0.004788, sample.Imu!.Accel1![0], 9);
    }

    [Fact]
    public void ToSample_无无人机信息时回退到扩展信息的GPS()
    {
        var payload = FrameBuilder.Concat(
            Summary(StatusDataValid | StatusExtendedPresent), TargetModule(0), PointModule(0), ExtendedSample());
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);

        var sample = NewProtocol().Parse(frame, 0, null).ToSample();

        Assert.NotNull(sample.Fix);
        Assert.Equal(39.9041984, sample.Fix!.Latitude, 7);
        Assert.Equal(12.34, sample.Fix.SpeedMps!.Value, 6);       // 本次来自扩展信息
        Assert.Equal(-3.5, sample.Fix.MagneticVariationDeg!.Value, 6);
        Assert.Null(sample.Attitude);                              // 无 0xA22A
        Assert.NotNull(sample.Imu);
    }

    [Fact]
    public void ToSample_位置无效位时不应产出Fix()
    {
        var payload = FrameBuilder.Concat(
            Summary(StatusDataValid | StatusUavInfoPresent), TargetModule(0), PointModule(0),
            UavInfoSample(flags: 0x0002)); // 只有姿态有效
        var frame = FrameBuilder.Ucm221Frame(big: false, payload);

        var sample = NewProtocol().Parse(frame, 0, null).ToSample();

        Assert.Null(sample.Fix);       // 位置有效位（Bit0）为 0，扩展信息也不存在
        Assert.Null(sample.Imu);       // 无 0xA33A
        Assert.NotNull(sample.Attitude); // 姿态有效位（Bit1）为 1，姿态照常产出
        Assert.Equal(0.1, sample.Attitude!.RollRad!.Value, 6);
    }

    [Fact]
    public void ToSample_姿态无效位时不应产出姿态()
    {
        var payload = FrameBuilder.Concat(
            Summary(StatusDataValid | StatusUavInfoPresent), TargetModule(0), PointModule(0),
            UavInfoSample(flags: 0x0001)); // 只有位置有效

        var sample = NewProtocol().Parse(FrameBuilder.Ucm221Frame(big: false, payload), 0, null).ToSample();

        Assert.NotNull(sample.Fix);
        Assert.Equal(39.9041984, sample.Fix!.Latitude, 7);
        Assert.Null(sample.Attitude); // AttitudeValid 为假
        Assert.Null(sample.Imu);
    }

    // ==================== 大端选项 ====================

    [Fact]
    public void 大端模式下长度字段与数值字段都按大端解析()
    {
        var settings = new Ucm221ProtocolSettings { BigEndian = true };
        var payload = FrameBuilder.Concat(
            FrameBuilder.Ucm221Summary(StatusDataValid | StatusExtendedPresent, 0, 5, 123, 5, 12, 13, 45, 30, 123, 0x1234, big: true),
            FrameBuilder.Ucm221Module(TargetHeader, [], big: true),
            FrameBuilder.Ucm221Module(PointCloudHeader, [], big: true),
            ExtendedSample());
        // 扩展信息模块整体按大端重写
        var extended = FrameBuilder.Ucm221Extended(45, [1, 2, 3], [4, 5, 6], [7, 8, 9], [10, 11, 12],
            399041984, 1164073984, 45600, 1234, -350, big: true);
        var bigPayload = FrameBuilder.Concat(payload[..(20 + 5 + 5)], extended);
        var frame = FrameBuilder.Ucm221Frame(big: true, bigPayload);

        var protocol = NewProtocol(settings);
        Assert.Equal(FrameScanStatus.Frame, protocol.Scan(frame).Status);

        var result = protocol.Parse(frame, 0, null);

        Assert.Equal(5, result.PointCloudCount);
        Assert.Equal(39.9041984, result.Extended!.Latitude!.Value, 7);
        Assert.Equal(12.34, result.Extended.SpeedMps!.Value, 6);
    }

    // ==================== 分帧稳健性 ====================

    [Fact]
    public void 分帧_粘包分包与垃圾字节混合应切出全部帧()
    {
        var first = FrameBuilder.Ucm221Frame(big: false,
            FrameBuilder.Concat(Summary(targetCount: 1), TargetModule(1), PointModule(0)));
        var second = FrameBuilder.Ucm221Frame(big: false,
            FrameBuilder.Concat(Summary(pointCount: 2), TargetModule(0), PointModule(2)));
        var third = FrameBuilder.Ucm221Frame(big: false,
            FrameBuilder.Concat(Summary(), TargetModule(0), PointModule(0)));

        // 3 字节垃圾 + 三帧粘连，按 11 字节一块分包喂入
        var data = FrameBuilder.Concat([0x77, 0x66, 0x55], first, second, third);

        var frames = ScanPump.Drain(NewProtocol(), data, chunkSize: 11);

        Assert.Equal(3, frames.Count);
        Assert.Equal<byte>(first, frames[0]);
        Assert.Equal<byte>(second, frames[1]);
        Assert.Equal<byte>(third, frames[2]);

        var protocol = NewProtocol();
        Assert.Equal(1, protocol.Parse(frames[0], 1, null).TargetCount);
        Assert.Equal(2, protocol.Parse(frames[1], 2, null).PointCloudCount);
        Assert.Equal(0, protocol.Parse(frames[2], 3, null).TargetCount);
    }

    [Fact]
    public void 分帧_末尾残帧不应被切出()
    {
        var complete = FrameBuilder.Ucm221Frame(big: false,
            FrameBuilder.Concat(Summary(), TargetModule(0), PointModule(0)));
        var truncated = FrameBuilder.Ucm221Frame(big: false,
            FrameBuilder.Concat(Summary(targetCount: 2), TargetModule(2), PointModule(0)));
        var data = FrameBuilder.Concat(complete, truncated.AsSpan(0, truncated.Length - 7).ToArray());

        var frames = ScanPump.Drain(NewProtocol(), data);

        Assert.Single(frames);
        Assert.Equal<byte>(complete, frames[0]);
    }

    [Fact]
    public void 分帧_校验和错误的一帧应被丢弃且不影响后续帧()
    {
        var good = FrameBuilder.Ucm221Frame(big: false,
            FrameBuilder.Concat(Summary(pointCount: 1), TargetModule(0), PointModule(1)));
        var bad = (byte[])good.Clone();
        bad[^1] ^= 0xFF; // 破坏第二帧的校验和

        var data = FrameBuilder.Concat(good, bad, good);

        var frames = ScanPump.Drain(NewProtocol(), data);

        // 第二帧被拆成「跳过帧头」再重新同步，最终前后两帧仍能被完整切出
        Assert.Equal(2, frames.Count(f => f.SequenceEqual(good)));
    }
}
