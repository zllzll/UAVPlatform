using UavPlatform.Core.Models;

namespace UavPlatform.Tests;

// ==========================================================================================
// 采集中锁定采集参数（m04845）
//
// 用户的问题：开始采集之后，通讯参数、存储参数、相对位置这些还该不该能改？
// 答案是不该。这三段参数里任何一处改动，后端的热应用都是「停掉管线 → 应用配置 → 重新开始」：
//     存储：换会话目录 —— 落盘数据当场分成两段，事后没法当成一次采集来分析；
//     设备：断开重连 —— 采集中断，缓冲区里的字节丢失；
//     相对位置：参考点重置 —— 前后两段的坐标原点都可能不是同一个。
// 所以采集进行中只允许改这三段以外的内容（平台名称、界面显示选项），三段比对本文件负责。
//
// 判定用的是 PlatformConfig.SameAcquisitionAs。它被放在 Core 而不是 Api 的 PlatformService，
// 就是为了能在这里直接测：测试工程只引用 Core，拿不到 PlatformService。
// 调用方（backend\src\UavPlatform.Api\Program.cs 的 PUT /config）在 Running 且判定为「改了」
// 时直接返回 409，前端那边则是把整个参数区用 fieldset 锁灰。
// ==========================================================================================

/// <summary>
/// 「采集中不许改采集参数」的判定逻辑（<see cref="PlatformConfig.SameAcquisitionAs"/>）测试。
///
/// 每个用例的套路一致：拿一份默认配置当「已保存」，克隆一份改一处当「新提交」，
/// 再看两者是否被判为「与采集相关的变化」。克隆是深拷贝（PlatformConfig.Clone），
/// 所以改新配置不会连累旧配置——若哪天 Clone 退化成浅拷贝，下面的「真改动」用例会集体失败。
/// </summary>
public class ConfigLockTests
{
    /// <summary>克隆一份默认配置、改一处，返回它相对原配置是否算「改了采集参数」。</summary>
    private static bool IsAcquisitionChange(Action<PlatformConfig> mutate)
    {
        var saved = PlatformConfig.CreateDefault();
        var next = saved.Clone();
        mutate(next);
        return !saved.SameAcquisitionAs(next);
    }

    /// <summary>按名字改一处采集相关参数（名字来自下面两个 Theory 的 InlineData）。</summary>
    private static void Mutate(PlatformConfig c, string what)
    {
        var baseStation = c.Device(DeviceKind.BaseStation) ?? throw new InvalidOperationException("缺少基座配置");
        var radar = c.Device(DeviceKind.Radar) ?? throw new InvalidOperationException("缺少雷达配置");
        var drone = c.Device(DeviceKind.DroneGps) ?? throw new InvalidOperationException("缺少无人机配置");

        switch (what)
        {
            // ---- 通讯参数：链路怎么建 ----
            case "串口名": baseStation.TransportSettings.SerialPort = "COM7"; break;
            case "波特率": baseStation.TransportSettings.BaudRate = 115200; break;
            case "数据位": baseStation.TransportSettings.DataBits = 7; break;
            case "校验位": baseStation.TransportSettings.Parity = "Even"; break;
            case "停止位": baseStation.TransportSettings.StopBits = "Two"; break;
            case "流控": baseStation.TransportSettings.Handshake = "RTS"; break;
            case "读超时": baseStation.TransportSettings.ReadTimeoutMs = 1500; break;
            case "传输方式": radar.Transport = TransportKind.Udp; break;
            case "雷达 IP": radar.TransportSettings.Host = "192.168.10.129"; break;
            case "雷达端口": radar.TransportSettings.Port = 50001; break;
            case "无人机监听端口": drone.TransportSettings.LocalPort = 8200; break;
            case "设备启用开关": radar.Enabled = false; break;
            case "断线重连": radar.AutoReconnect = false; break;
            case "重连间隔": radar.ReconnectDelayMs = 5000; break;
            case "原始字节落盘": radar.SaveRaw = !radar.SaveRaw; break;
            case "解析结果落盘": radar.SaveParsed = !radar.SaveParsed; break;

            // ---- 协议解析：字节怎么读 ----
            case "雷达本机地址": radar.Radar.LocalAddress = 0x11; break;
            case "雷达输出模式": radar.Radar.OutputMode = RadarOutputMode.PointCloud; break;
            case "雷达心跳": radar.Radar.SendHeartbeat = false; break;
            case "雷达心跳周期": radar.Radar.HeartbeatIntervalSec = 10; break;
            case "回令解析": radar.Radar.ParseAcks = false; break;
            case "UM982 的 THS 解析": baseStation.Um982.ParseThs = false; break;
            case "UM982 真航向": baseStation.Um982.UseTrueHeading = false; break;
            case "UM982 初始化指令": baseStation.Um982.SendInitCommands = false; break;
            case "UM982 输出间隔": baseStation.Um982.OutputIntervalSec = 0.1; break;
            case "UCM221 扩展信息": drone.Ucm221.ParseExtendedInfo = false; break;
            case "UCM221 字节序": drone.Ucm221.BigEndian = true; break;

            // ---- 存储参数 ----
            case "存储总开关": c.Storage.Enabled = false; break;
            case "存储根目录": c.Storage.RootPath = @"D:\uav-data"; break;
            case "会话目录模式": c.Storage.FolderMode = SessionFolderMode.Fixed; break;
            case "固定会话名": c.Storage.FixedSessionName = "demo"; break;
            case "单文件上限": c.Storage.MaxFileSizeMb = 128; break;
            case "原始数据格式": c.Storage.RawFormat = RawFormat.HexText; break;
            case "解析数据格式": c.Storage.ParsedFormat = ParsedFormat.Csv; break;
            case "雷达转基座系落盘": c.Storage.SaveRadarBase = false; break;
            case "三设备同帧落盘": c.Storage.SaveFrame = false; break;
            case "解析里嵌原始字节": c.Storage.EmbedRawInParsed = true; break;
            case "写缓冲": c.Storage.WriteBufferKb = 512; break;
            case "刷盘间隔": c.Storage.FlushIntervalMs = 2000; break;
            case "清单文件": c.Storage.WriteManifest = false; break;

            // ---- 相对位置参数 ----
            case "相对位置总开关": c.Relative.Enabled = false; break;
            case "参考点模式": c.Relative.ReferenceMode = ReferencePointMode.Manual; break;
            case "手动参考点经度": c.Relative.ManualLongitude = 120.0; break;
            case "跟随参考点漂移": c.Relative.FollowReferenceDrift = true; break;
            case "雷达安装方式": c.Relative.Radar.Mode = RadarPlacementMode.OffsetFromBase; break;
            case "雷达安装偏置": c.Relative.Radar.OffsetEastM = 1.5; break;
            case "雷达安装姿态": c.Relative.Radar.PitchDeg = 2.5; break;
            case "雷达朝向来源": c.Relative.Radar.YawSource = RadarYawSource.ManualAbsolute; break;
            case "雷达安装夹角": c.Relative.Radar.YawOffsetFromBaselineDeg = -90; break;
            case "雷达绝对朝向": c.Relative.Radar.YawDeg = 87.3; break;
            case "过滤最大距离": c.Relative.Filter.MaxRangeM = 500; break;
            case "过滤信噪比": c.Relative.Filter.MinSnr = 3; break;
            case "过滤已删除目标": c.Relative.Filter.DropDeleted = false; break;
            case "目标数上限": c.Relative.Filter.MaxTargets = 100; break;
            case "对比开关": c.Relative.Comparison.Enabled = false; break;
            case "对比匹配半径": c.Relative.Comparison.MatchRadiusM = 8; break;
            case "重建间隔": c.Relative.RelativeIntervalMs = 200; break;
            case "陈旧超时": c.Relative.StaleTimeoutMs = 5000; break;

            default: throw new InvalidOperationException($"未定义的参数名：{what}");
        }
    }

    // ======================================================================================
    // 一、这些改动不该被拦：改了不影响本次采集，采集中照样允许保存
    // ======================================================================================

    [Fact]
    public void 只改平台名称不算改采集参数()
    {
        Assert.False(IsAcquisitionChange(c => c.Name = "换个名字"));
    }

    [Fact]
    public void 只改界面显示开关不算改采集参数()
    {
        // 界面开关走的是 PUT /config/ui（不重启管线）这条独立通道，
        // 但即便整份配置一起提交上来，也不该被拦——它们不影响落盘与设备。
        Assert.False(IsAcquisitionChange(c =>
        {
            c.Ui.ShowTargets = false;
            c.Ui.ShowPointCloud = false;
            c.Ui.ShowDrone = false;
            c.Ui.ShowDroneTrack = false;
            c.Ui.ShowTargetTrails = false;
            c.Ui.ShowLinks = false;
            c.Ui.TrailSeconds = 120;
            c.Ui.PointSize = 2.5;
            c.Ui.GridSizeM = 500;
        }));
    }

    [Fact]
    public void 版本号变化不算改采集参数()
    {
        Assert.False(IsAcquisitionChange(c => c.Version = 2));
    }

    [Fact]
    public void 两份完全相同的配置不算改采集参数()
    {
        var saved = PlatformConfig.CreateDefault();
        // SameAcquisitionAs 返回 true 表示「三段等价」，不是「改了」。
        Assert.True(saved.SameAcquisitionAs(saved.Clone()));
    }

    [Fact]
    public void 设备顺序不同但内容相同不算改采集参数()
    {
        // 前端把整份配置 PUT 回来时数组顺序不保证，比对前必须按 Kind 归一化，
        // 否则「顺序变了」会被误判成「改了设备参数」而被 409 拦下。
        Assert.False(IsAcquisitionChange(c => c.Devices.Reverse()));
    }

    // ======================================================================================
    // 二、这些改动必须被拦：任何一处都会让热应用重启管线、把本次采集截成两段
    // ======================================================================================

    [Theory]
    [InlineData("串口名")]
    [InlineData("波特率")]
    [InlineData("数据位")]
    [InlineData("校验位")]
    [InlineData("停止位")]
    [InlineData("流控")]
    [InlineData("读超时")]
    [InlineData("传输方式")]
    [InlineData("雷达 IP")]
    [InlineData("雷达端口")]
    [InlineData("无人机监听端口")]
    [InlineData("设备启用开关")]
    [InlineData("断线重连")]
    [InlineData("重连间隔")]
    [InlineData("原始字节落盘")]
    [InlineData("解析结果落盘")]
    public void 改通讯参数算改采集参数(string what)
    {
        Assert.True(IsAcquisitionChange(c => Mutate(c, what)), $"{what} 应该被判为改了采集参数");
    }

    [Theory]
    [InlineData("雷达本机地址")]
    [InlineData("雷达输出模式")]
    [InlineData("雷达心跳")]
    [InlineData("雷达心跳周期")]
    [InlineData("回令解析")]
    [InlineData("UM982 的 THS 解析")]
    [InlineData("UM982 真航向")]
    [InlineData("UM982 初始化指令")]
    [InlineData("UM982 输出间隔")]
    [InlineData("UCM221 扩展信息")]
    [InlineData("UCM221 字节序")]
    public void 改协议解析参数算改采集参数(string what)
    {
        // 协议参数决定「同一段原始字节被解析成什么」，改了之后的解析结果与之前不可比，
        // 所以与通讯参数同等对待，采集中一起锁。
        Assert.True(IsAcquisitionChange(c => Mutate(c, what)), $"{what} 应该被判为改了采集参数");
    }

    [Theory]
    [InlineData("存储总开关")]
    [InlineData("存储根目录")]
    [InlineData("会话目录模式")]
    [InlineData("固定会话名")]
    [InlineData("单文件上限")]
    [InlineData("原始数据格式")]
    [InlineData("解析数据格式")]
    [InlineData("雷达转基座系落盘")]
    [InlineData("三设备同帧落盘")]
    [InlineData("解析里嵌原始字节")]
    [InlineData("写缓冲")]
    [InlineData("刷盘间隔")]
    [InlineData("清单文件")]
    public void 改存储参数算改采集参数(string what)
    {
        Assert.True(IsAcquisitionChange(c => Mutate(c, what)), $"{what} 应该被判为改了采集参数");
    }

    [Theory]
    [InlineData("相对位置总开关")]
    [InlineData("参考点模式")]
    [InlineData("手动参考点经度")]
    [InlineData("跟随参考点漂移")]
    [InlineData("雷达安装方式")]
    [InlineData("雷达安装偏置")]
    [InlineData("雷达安装姿态")]
    [InlineData("雷达朝向来源")]
    [InlineData("雷达安装夹角")]
    [InlineData("雷达绝对朝向")]
    [InlineData("过滤最大距离")]
    [InlineData("过滤信噪比")]
    [InlineData("过滤已删除目标")]
    [InlineData("目标数上限")]
    [InlineData("对比开关")]
    [InlineData("对比匹配半径")]
    [InlineData("重建间隔")]
    [InlineData("陈旧超时")]
    public void 改相对位置参数算改采集参数(string what)
    {
        Assert.True(IsAcquisitionChange(c => Mutate(c, what)), $"{what} 应该被判为改了采集参数");
    }

    // ======================================================================================
    // 三、边界：设备补齐与多字段同时改动
    // ======================================================================================

    [Fact]
    public void 两端都缺同一台设备时判定仍然成立()
    {
        // EnsureAllDevices 会把缺的设备按默认值补回来，两端补出来的内容一致，所以不算改动。
        var saved = PlatformConfig.CreateDefault();
        var next = saved.Clone();
        saved.Devices.RemoveAll(d => d.Kind == DeviceKind.DroneGps);
        next.Devices.RemoveAll(d => d.Kind == DeviceKind.DroneGps);

        Assert.True(saved.SameAcquisitionAs(next));
        // 补设备是就地改，比对完两边都该是完整的三台
        Assert.Equal(3, saved.Devices.Count);
        Assert.Equal(3, next.Devices.Count);
    }

    [Fact]
    public void 删掉一台已按现场改过参数的设备算改采集参数()
    {
        // 现场把雷达 IP 改过之后，删掉雷达再提交：补齐逻辑补回来的是**默认** IP，
        // 与现场值不同，所以仍然判为改动——不会因为「自动补回默认」把这次改动放过去。
        var saved = PlatformConfig.CreateDefault();
        var radar = saved.Device(DeviceKind.Radar) ?? throw new InvalidOperationException("缺少雷达配置");
        radar.TransportSettings.Host = "192.168.10.200";

        var next = saved.Clone();
        next.Devices.RemoveAll(d => d.Kind == DeviceKind.Radar);

        Assert.False(saved.SameAcquisitionAs(next));
    }

    [Fact]
    public void 采集参数与界面开关一起改仍然算改采集参数()
    {
        // 前端一旦回退成「整份配置一起 PUT」，改动会被混在界面开关里送上来，不能因此漏判。
        Assert.True(IsAcquisitionChange(c =>
        {
            c.Ui.ShowTargetTrails = false;
            c.Name = "顺手改个名字";
            c.Storage.FlushIntervalMs = 2000;
        }));
    }

    [Fact]
    public void 判定不修改任一份配置的采集参数内容()
    {
        // SameAcquisitionAs 内部会调 EnsureAllDevices 归一化顺序，但两个入参的
        // 采集参数本身必须原样保留——它被 API 在保存前调用，不能有副作用。
        var saved = PlatformConfig.CreateDefault();
        var next = saved.Clone();
        var before = ConfigJson.Serialize(saved);

        Assert.True(saved.SameAcquisitionAs(next));

        Assert.Equal(before, ConfigJson.Serialize(saved));
        Assert.Equal(before, ConfigJson.Serialize(next));
    }
}
