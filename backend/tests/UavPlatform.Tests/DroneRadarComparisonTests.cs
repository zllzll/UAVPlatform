using UavPlatform.Core.Relative;
using UavPlatform.Core.Models;

namespace UavPlatform.Tests;

// ==========================================================================================
// 「雷达探测 vs 无人机 RTK」比对 + 雷达正前方解算
//
// 现场模型（用户更正后）：基座与雷达安装在一起，基座两根天线构成一条基线，主天线给位置，
// 双天线定向（UM982 的 THS）给基线航向；雷达夹在两根天线中间，于是
//     雷达正前方 = 基线航向 + 安装夹角
// 无人机本身就是天上飞的一个运动目标：雷达按自己的坐标系探到它，RTK 给出它相对基座的真实
// 位置，两者换算到同一个显示坐标系后同框比对，就是雷达探测精度的直接证据。
//
// 期望值来源：本文件刻意**不手抄 ENU 数值**（那属于 GeoMathTests / EndToEndPipelineTests 的
// 职责），而是从相对位置帧自己给出的无人机 RTK 位置反推极坐标，再让雷达「如实」或「带已知偏差」
// 上报。这样断言的是比对逻辑本身：最近目标匹配、偏差正负号、极坐标 ↔ 直角坐标的回环。
// ==========================================================================================

/// <summary>
/// 雷达朝向解算（双天线基线 + 安装夹角）与无人机/雷达比对结果的单元测试。
/// </summary>
public class DroneRadarComparisonTests
{
    // ---- 场景常量：与 EndToEndFixtures 同场景，便于交叉对照 ----

    private const double BaseLatitudeDeg = 30.0;
    private const double BaseLongitudeDeg = 120.0;
    private const double BaseAltitudeM = 50.0;
    private const double DroneLatitudeDeg = 30.0;
    private const double DroneLongitudeDeg = 120.001;
    private const double DroneAltitudeM = 120.0;

    /// <summary>双天线基线真航向（度），来自 UM982 的 THS 语句。</summary>
    private const double BaselineHeadingDeg = 57.3;

    /// <summary>雷达正前方相对双天线基线的安装夹角（度，顺时针为正）。</summary>
    private const double BoresightOffsetDeg = 30.0;

    /// <summary>浮点回环容差。整条链路都是闭式三角运算，1e-9 只是防浮点噪声。</summary>
    private const double Eps = 1e-9;

    // ======================================================================================
    // 一、雷达朝向解算：基线航向 + 安装夹角
    // ======================================================================================

    [Theory]
    [InlineData("THS")]
    [InlineData("ths")]
    [InlineData("Ths")]
    public void 朝向解算_双天线THS有效时应为基线航向加安装夹角(string headingSource)
    {
        var placement = new RadarPlacementSettings
        {
            YawSource = RadarYawSource.BaseHeadingPlusOffset,
            YawOffsetFromBaselineDeg = BoresightOffsetDeg,
            YawDeg = 999.0,   // 刻意给一个荒谬的兜底值：证明这条支路根本没碰它
        };

        var (yaw, fromBaseline) = placement.ResolveYawDeg(BaselineHeadingDeg, headingSource);

        Assert.True(fromBaseline, "航向来源是 THS 双天线定向，应判定为「由基线推算」。");
        AssertNear(BaselineHeadingDeg + BoresightOffsetDeg, yaw, Eps, "雷达正前方");
    }

    [Theory]
    [InlineData("RMC")]
    [InlineData("VTG")]
    public void 朝向解算_基线航向来自航迹向时应回落到手动绝对角(string headingSource)
    {
        // 基座是静止的，RMC/VTG 报的是航迹向（对地速度方向），静止时本质是噪声。
        // 若拿它去转雷达，所有目标会被系统性转偏，所以这里只认 THS 双天线定向。
        var placement = new RadarPlacementSettings
        {
            YawSource = RadarYawSource.BaseHeadingPlusOffset,
            YawOffsetFromBaselineDeg = BoresightOffsetDeg,
            YawDeg = 90.0,
        };

        var (yaw, fromBaseline) = placement.ResolveYawDeg(57.3, headingSource);

        Assert.False(fromBaseline, $"{headingSource} 是航迹向，不能当作基线航向使用。");
        AssertNear(90.0, yaw, Eps, "雷达正前方（应回落到手动绝对角）");
    }

    [Fact]
    public void 朝向解算_拿不到双天线定向时应回落到手动绝对角()
    {
        var placement = new RadarPlacementSettings
        {
            YawSource = RadarYawSource.BaseHeadingPlusOffset,
            YawOffsetFromBaselineDeg = BoresightOffsetDeg,
            YawDeg = 123.5,
        };

        // 四种「这条支路不该走」的输入：完全没航向、有航向但没来源、有来源但没航向、航向是 NaN。
        var cases = new (double? Heading, string? Source)[]
        {
            (null, null),
            (null, "THS"),
            (57.3, null),
            (double.NaN, "THS"),
        };

        foreach (var (heading, source) in cases)
        {
            var (yaw, fromBaseline) = placement.ResolveYawDeg(heading, source);

            Assert.False(fromBaseline, $"heading={heading} source={source} 不应判定为基线推算。");
            AssertNear(123.5, yaw, Eps, $"heading={heading} source={source} 的兜底角");
        }
    }

    [Fact]
    public void 朝向解算_手动模式应忽略基线航向()
    {
        var placement = new RadarPlacementSettings
        {
            YawSource = RadarYawSource.ManualAbsolute,
            YawOffsetFromBaselineDeg = BoresightOffsetDeg,
            YawDeg = 210.0,
        };

        // 即使拿到了一条完美的 THS 定向，手动模式也必须用手填的绝对角。
        var (yaw, fromBaseline) = placement.ResolveYawDeg(BaselineHeadingDeg, "THS");

        Assert.False(fromBaseline);
        AssertNear(210.0, yaw, Eps, "雷达正前方");
    }

    [Fact]
    public void 朝向解算_结果应归一化到0到360()
    {
        var placement = new RadarPlacementSettings
        {
            YawSource = RadarYawSource.BaseHeadingPlusOffset,
            YawOffsetFromBaselineDeg = 20.0,
        };

        var (fromBaselineYaw, fromBaseline) = placement.ResolveYawDeg(350.0, "THS");
        Assert.True(fromBaseline);
        AssertNear(10.0, fromBaselineYaw, Eps, "350° + 20° 应归一化成 10°");

        placement.YawSource = RadarYawSource.ManualAbsolute;
        placement.YawDeg = -30.0;
        var (manualYaw, _) = placement.ResolveYawDeg(null, null);
        AssertNear(330.0, manualYaw, Eps, "−30° 应归一化成 330°");
    }

    [Fact]
    public void 朝向解算_默认配置就应走双天线基线这条支路()
    {
        // 现场模型就是「雷达正前方 = 双天线基线 + 安装夹角」，所以默认值必须落在这一支；
        // 安装夹角默认 0 时，默认行为退化为「正前方 = 基线航向」。
        var placement = new RadarPlacementSettings();

        Assert.Equal(RadarYawSource.BaseHeadingPlusOffset, placement.YawSource);
        Assert.Equal(0.0, placement.YawOffsetFromBaselineDeg);

        var (yaw, fromBaseline) = placement.ResolveYawDeg(BaselineHeadingDeg, "THS");

        Assert.True(fromBaseline);
        AssertNear(BaselineHeadingDeg, yaw, Eps, "安装夹角为 0 时正前方就等于基线航向");
    }

    // ======================================================================================
    // 二、极坐标回环：GeoMath.Normalize180 / EnuToRadarBody
    // ======================================================================================

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(180.0, 180.0)]
    [InlineData(-180.0, 180.0)]
    [InlineData(190.0, -170.0)]
    [InlineData(-190.0, 170.0)]
    [InlineData(360.0, 0.0)]
    [InlineData(540.0, 180.0)]
    public void Normalize180_应归一化到正负180(double input, double expected)
    {
        AssertNear(expected, GeoMath.Normalize180(input), Eps, $"Normalize180({input})");
    }

    [Theory]
    [InlineData(0.0, 0.0, 0.0)]
    [InlineData(37.5, 0.0, 0.0)]
    [InlineData(0.0, 12.0, 0.0)]
    [InlineData(0.0, 0.0, -8.0)]
    [InlineData(123.4, 5.5, -3.25)]
    [InlineData(-47.0, -6.75, 9.5)]
    public void EnuToRadarBody_应与RotateRadarToEnu互为逆变换(double yaw, double pitch, double roll)
    {
        // 雷达本体系里的一个任意目标（+Y 是探测方向）
        var (x, y, z) = (12.5, 88.25, -7.75);

        var enu = GeoMath.RotateRadarToEnu(x, y, z, yaw, pitch, roll);
        var back = GeoMath.EnuToRadarBody(enu.East, enu.North, enu.Up, yaw, pitch, roll);

        AssertNear(x, back.X, 1e-8, "本体 X");
        AssertNear(y, back.Y, 1e-8, "本体 Y");
        AssertNear(z, back.Z, 1e-8, "本体 Z");
    }

    [Fact]
    public void EnuToRadarBody_偏航90度应把探测方向转到正东()
    {
        // 本体 +Y 是探测方向。偏航 90° 表示雷达正前方指向正东，于是正前方 100 m 应落在 ENU 的东向。
        var enu = GeoMath.RotateRadarToEnu(0, 100, 0, yawDeg: 90, pitchDeg: 0, rollDeg: 0);
        AssertNear(100.0, enu.East, Eps, "东");
        AssertNear(0.0, enu.North, Eps, "北");
        AssertNear(0.0, enu.Up, Eps, "天");

        // 逆变换：正东 100 m 应还原成「正前方 100 m」
        var back = GeoMath.EnuToRadarBody(100, 0, 0, yawDeg: 90, pitchDeg: 0, rollDeg: 0);
        AssertNear(0.0, back.X, Eps, "本体 X（右向）");
        AssertNear(100.0, back.Y, Eps, "本体 Y（探测方向）");
        AssertNear(0.0, back.Z, Eps, "本体 Z（上）");
    }

    [Fact]
    public void 对比设置_默认应开启且匹配半径为20米()
    {
        var settings = new RelativeSettings();

        Assert.True(settings.Comparison.Enabled);
        Assert.Equal(20.0, settings.Comparison.MatchRadiusM);
    }

    [Fact]
    public void 对比设置_相对位置设置克隆后应互不影响()
    {
        // RelativeSettings.Clone() 必须深拷一层 Comparison，否则「克隆一份改测试参数」会污染原对象。
        var original = new RelativeSettings();
        original.Comparison.MatchRadiusM = 20.0;

        var clone = original.Clone();
        clone.Comparison.MatchRadiusM = 5.0;

        Assert.Equal(20.0, original.Comparison.MatchRadiusM);
        Assert.Equal(5.0, clone.Comparison.MatchRadiusM);
    }

    // ======================================================================================
    // 三、比对：无人机作为运动靶标校核雷达精度
    // ======================================================================================

    [Fact]
    public void 对比_雷达如实报出无人机位置时各项偏差都应为零()
    {
        // 第一步：空跑一帧，拿到相对位置给出的无人机 RTK 在显示坐标系里的位置。
        var probe = BuildFrame(Settings(), RadarSampleWith());
        var rtk = probe.Drone.Position;
        Assert.NotNull(probe.Drone.Latitude);

        // 第二步：把 RTK 位置反推成雷达极坐标，让雷达「如实」上报同一个点。
        //   （安装角全 0 + 手动绝对角 0° 时，雷达本体系与 ENU 完全重合，
        //     方位角 = atan2(东, 北)，俯仰角 = asin(天 / 距离)。）
        var (az, el, range) = PolarFromEnu(rtk);
        var frame = BuildFrame(Settings(), RadarSampleWith(PolarTarget(7, az, el, range)));

        var cmp = frame.DroneComparison;
        Assert.NotNull(cmp);
        var c = cmp!;

        Assert.True(c.Matched, "雷达报的就是无人机所在的位置，必须判为命中。");
        Assert.Equal(7u, c.TargetId);
        Assert.Equal(20.0, c.MatchRadiusM);

        // 位置：三个分量与三维/水平偏差都应该是零
        AssertNear(rtk.East, c.RtkEast, Eps, "RTK 东");
        AssertNear(rtk.North, c.RtkNorth, Eps, "RTK 北");
        AssertNear(rtk.Up, c.RtkUp, Eps, "RTK 天");
        AssertNear(c.RadarEast, c.RtkEast, 1e-6, "雷达东 − RTK 东");
        AssertNear(c.RadarNorth, c.RtkNorth, 1e-6, "雷达北 − RTK 北");
        AssertNear(c.RadarUp, c.RtkUp, 1e-6, "雷达天 − RTK 天");
        AssertNear(0.0, c.DeltaEastM, 1e-6, "东向偏差");
        AssertNear(0.0, c.DeltaNorthM, 1e-6, "北向偏差");
        AssertNear(0.0, c.DeltaUpM, 1e-6, "天向偏差");
        AssertNear(0.0, c.DeltaDistanceM, 1e-6, "三维偏差");
        AssertNear(0.0, c.DeltaHorizontalM, 1e-6, "水平偏差");

        // 极坐标：雷达实测值与我反推的真值都应回到同一组数
        AssertNear(range, c.RadarRangeM, 1e-6, "雷达实测距离");
        AssertNear(range, c.RtkRangeM, 1e-6, "RTK 换算真值距离");
        AssertNear(az, c.RadarAzimuthDeg, 1e-6, "雷达实测方位角");
        AssertNear(az, c.RtkAzimuthDeg, 1e-6, "RTK 换算真值方位角");
        AssertNear(el, c.RadarElevationDeg, 1e-6, "雷达实测俯仰角");
        AssertNear(el, c.RtkElevationDeg, 1e-6, "RTK 换算真值俯仰角");
        AssertNear(0.0, c.DeltaRangeM, 1e-6, "距离差");
        AssertNear(0.0, c.DeltaAzimuthDeg, 1e-6, "方位差");
        AssertNear(0.0, c.DeltaElevationDeg, 1e-6, "俯仰差");
    }

    [Fact]
    public void 对比_距离差的正负号应为雷达减RTK()
    {
        var probe = BuildFrame(Settings(), RadarSampleWith());
        var (az, el, range) = PolarFromEnu(probe.Drone.Position);

        // 雷达把距离报大 5 m：目标沿同一条探测射线外推，三维偏差必然正好是 5 m。
        var frame = BuildFrame(Settings(matchRadiusM: 50), RadarSampleWith(PolarTarget(7, az, el, range + 5)));

        var c = frame.DroneComparison!;

        Assert.True(c.Matched);
        AssertNear(5.0, c.DeltaRangeM, 1e-6, "距离差（雷达 − RTK）");
        AssertNear(5.0, c.DeltaDistanceM, 1e-6, "三维偏差");
        AssertNear(range + 5, c.RadarRangeM, 1e-6, "雷达实测距离");
        AssertNear(range, c.RtkRangeM, 1e-6, "RTK 换算真值距离");

        // 角度没动，角度偏差应仍为零
        AssertNear(0.0, c.DeltaAzimuthDeg, 1e-6, "方位差");
        AssertNear(0.0, c.DeltaElevationDeg, 1e-6, "俯仰差");
        // 沿射线外推不产生横向位移，但会抬高 5·sin(俯仰角)
        AssertNear(5.0 * Math.Sin(GeoMath.ToRadians(el)), c.DeltaUpM, 1e-6, "天向偏差");
    }

    [Fact]
    public void 对比_方位差的正负号与横向位移应可手算核对()
    {
        var probe = BuildFrame(Settings(), RadarSampleWith());
        var rtk = probe.Drone.Position;
        var (az, el, range) = PolarFromEnu(rtk);

        // 雷达把方位角报大 2°。
        var frame = BuildFrame(Settings(matchRadiusM: 50), RadarSampleWith(PolarTarget(7, az + 2, el, range)));

        var c = frame.DroneComparison!;

        Assert.True(c.Matched);
        AssertNear(2.0, c.DeltaAzimuthDeg, 1e-6, "方位差（雷达 − RTK）");
        AssertNear(0.0, c.DeltaRangeM, 1e-6, "距离差");

        // 距离不变、方位偏 2° → 目标绕雷达转了一个小角度，横向位移 = 2·水平距离·sin(1°)。
        var horizontal = Math.Sqrt(rtk.East * rtk.East + rtk.North * rtk.North);
        var expected = 2.0 * horizontal * Math.Sin(GeoMath.ToRadians(1.0));

        AssertNear(expected, c.DeltaHorizontalM, 1e-6, "水平偏差");
        AssertNear(0.0, c.DeltaUpM, 1e-6, "天向偏差（方位角变化不影响高度）");
    }

    [Fact]
    public void 对比_应取离无人机最近的目标而不是列表里的第一个()
    {
        var probe = BuildFrame(Settings(), RadarSampleWith());
        var (az, el, range) = PolarFromEnu(probe.Drone.Position);

        // 远处的 900 号排在前面、近处的 901 号排在后面：必须选中 901。
        var frame = BuildFrame(Settings(matchRadiusM: 1), RadarSampleWith(
            PolarTarget(900, az, el, range + 60),
            PolarTarget(901, az, el, range + 3)));

        var c = frame.DroneComparison!;

        Assert.Equal(901u, c.TargetId);
        AssertNear(3.0, c.DeltaDistanceM, 1e-6, "最近目标的三维偏差");
        Assert.Equal(1.0, c.MatchRadiusM);
        Assert.False(c.Matched, "最近目标也有 3 m，超出 1 m 的匹配半径，应判为未命中。");
    }

    [Fact]
    public void 对比_超出匹配半径时仍应给出最近目标供人判断()
    {
        var probe = BuildFrame(Settings(), RadarSampleWith());
        var (az, el, range) = PolarFromEnu(probe.Drone.Position);
        var radar = RadarSampleWith(PolarTarget(901, az, el, range + 3));

        // 同一份数据，只改匹配半径：1 m 判未命中，20 m 判命中。
        var tight = BuildFrame(Settings(matchRadiusM: 1), radar).DroneComparison!;
        var loose = BuildFrame(Settings(matchRadiusM: 20), radar).DroneComparison!;

        Assert.False(tight.Matched);
        Assert.True(loose.Matched);

        // 无论命中与否，都要能报「最近的那个目标是谁、差多少」，否则操作员无从判断雷达是漏探还是探偏。
        Assert.Equal(901u, tight.TargetId);
        Assert.Equal(901u, loose.TargetId);
        AssertNear(3.0, tight.DeltaDistanceM, 1e-6, "未命中时的最近距离");
        AssertNear(3.0, loose.DeltaDistanceM, 1e-6, "命中时的三维偏差");
    }

    [Fact]
    public void 对比_关闭开关时应不产生比对结果()
    {
        var probe = BuildFrame(Settings(), RadarSampleWith());
        var (az, el, range) = PolarFromEnu(probe.Drone.Position);

        var frame = BuildFrame(Settings(comparisonEnabled: false), RadarSampleWith(PolarTarget(7, az, el, range)));

        // 目标照样换算出来，只是不做比对
        Assert.Single(frame.Targets);
        Assert.Null(frame.DroneComparison);
    }

    [Fact]
    public void 对比_缺少无人机或雷达目标时应不产生比对结果()
    {
        var probe = BuildFrame(Settings(), RadarSampleWith());
        var (az, el, range) = PolarFromEnu(probe.Drone.Position);

        // 有目标但没无人机
        var noDrone = BuildFrame(Settings(), RadarSampleWith(PolarTarget(7, az, el, range)), withDrone: false);
        Assert.Null(noDrone.DroneComparison);

        // 有无人机但雷达一个目标都没报
        var noTarget = BuildFrame(Settings(), RadarSampleWith());
        Assert.Empty(noTarget.Targets);
        Assert.Null(noTarget.DroneComparison);
    }

    // ======================================================================================
    // 四、朝向解算结果要真的流进相对位置帧
    // ======================================================================================

    [Fact]
    public void 相对位置帧_双天线THS有效时正前方应等于基线航向加安装夹角()
    {
        var settings = Settings(yawSource: RadarYawSource.BaseHeadingPlusOffset);
        settings.Radar.YawOffsetFromBaselineDeg = BoresightOffsetDeg;

        var frame = BuildFrame(settings, RadarSampleWith());

        Assert.True(frame.RadarBoresightFromBaseline);
        Assert.NotNull(frame.RadarBaselineHeadingDeg);
        AssertNear(BaselineHeadingDeg, frame.RadarBaselineHeadingDeg!.Value, Eps, "基线航向");
        AssertNear(BaselineHeadingDeg + BoresightOffsetDeg, frame.RadarBoresightDeg, Eps, "雷达正前方");
        AssertNear(BaselineHeadingDeg + BoresightOffsetDeg, frame.Radar.HeadingDeg!.Value, Eps, "雷达节点航向");
        Assert.Equal("航向来自 THS", frame.BaseStation.Note);
    }

    [Fact]
    public void 相对位置帧_基线航向来自航迹向时应回落到手动绝对角但基线仍照实上报()
    {
        // 基座静止时 RMC 给的是航迹向；拿它转雷达会把所有目标系统性转偏，因此正前方要用兜底角。
        // 但「基座自己报告的航向是多少」仍要原样透出，否则界面上看不出为什么回落了。
        var settings = Settings(yawSource: RadarYawSource.BaseHeadingPlusOffset);
        settings.Radar.YawOffsetFromBaselineDeg = BoresightOffsetDeg;
        settings.Radar.YawDeg = 90.0;

        var relative = new RelativeService(settings);
        relative.OnGnss(BaseSample(headingSource: "RMC"));
        relative.OnDrone(DroneSample());
        relative.OnRadar(RadarSampleWith());

        var frame = relative.Build("test", force: true);
        Assert.NotNull(frame);
        var f = frame!;

        Assert.False(f.RadarBoresightFromBaseline);
        AssertNear(90.0, f.RadarBoresightDeg, Eps, "雷达正前方（手动兜底）");
        Assert.NotNull(f.RadarBaselineHeadingDeg);
        AssertNear(BaselineHeadingDeg, f.RadarBaselineHeadingDeg!.Value, Eps, "基线航向（航迹向）应照实上报");
        Assert.Equal("航向来自 RMC", f.BaseStation.Note);
    }

    [Fact]
    public void 相对位置帧_没有双天线定向时基线航向应为空且正前方回落到手动角()
    {
        var settings = Settings(yawSource: RadarYawSource.BaseHeadingPlusOffset);
        settings.Radar.YawOffsetFromBaselineDeg = BoresightOffsetDeg;
        settings.Radar.YawDeg = 135.0;

        var relative = new RelativeService(settings);
        relative.OnGnss(BaseSample(headingSource: null, headingDeg: null));
        relative.OnDrone(DroneSample());
        relative.OnRadar(RadarSampleWith());

        var frame = relative.Build("test", force: true);
        Assert.NotNull(frame);
        var f = frame!;

        Assert.False(f.RadarBoresightFromBaseline);
        Assert.Null(f.RadarBaselineHeadingDeg);
        AssertNear(135.0, f.RadarBoresightDeg, Eps, "雷达正前方（手动兜底）");
    }

    [Fact]
    public void 相对位置帧_目标方位应由解算出的正前方决定而不是手填角()
    {
        // 雷达正前方由「基座双天线基线航向 + 安装夹角」实时解算，手填的 YawDeg 只作兜底。
        // 这里手填角刻意设成 0：若被误用，目标会落在正北，而不是解算出的 87.3° 方向。
        var settings = Settings(yawSource: RadarYawSource.BaseHeadingPlusOffset);
        settings.Radar.YawOffsetFromBaselineDeg = BoresightOffsetDeg;   // 正前方 = 57.3 + 30 = 87.3°
        settings.Radar.YawDeg = 0.0;

        // 本体正前方 100 m 处的目标：本体系 (0, 100, 0)
        var frame = BuildFrame(settings, RadarSampleWith(PolarTarget(7, 0, 0, 100)));

        Assert.True(frame.RadarBoresightFromBaseline);
        AssertNear(BaselineHeadingDeg + BoresightOffsetDeg, frame.RadarBoresightDeg, Eps, "雷达正前方");

        var boresight = BaselineHeadingDeg + BoresightOffsetDeg;
        var target = Assert.Single(frame.Targets);

        // 基座系（世界 ENU 系）里，目标应落在正前方 87.3° 方向、100 m 处。
        var rad = boresight * Math.PI / 180.0;
        AssertNear(100.0 * Math.Sin(rad), target.East, 1e-6, "东");
        AssertNear(100.0 * Math.Cos(rad), target.North, 1e-6, "北");
    }

    // ======================================================================================
    // 夹具
    // ======================================================================================

    /// <summary>
    /// 场景设置：原点取基座、雷达与基座共址、安装角全 0。
    /// <para>
    /// 默认用<b>手动绝对角 0°</b> 而不是双天线基线，是为了把「朝向解算」这条支路摘出去，
    /// 让比对的数值可以直接用闭式三角核对（安装角全 0 时雷达本体系与 ENU 完全重合）。
    /// 朝向解算本身由本文件第一节与第四节单独覆盖。
    /// </para>
    /// </summary>
    private static RelativeSettings Settings(double matchRadiusM = 20, bool comparisonEnabled = true,
        RadarYawSource yawSource = RadarYawSource.ManualAbsolute)
    {
        var settings = new RelativeSettings
        {
            Enabled = true,
            ReferenceMode = ReferencePointMode.Auto,
            RelativeIntervalMs = 0,   // 关掉节流，保证每次 Build 都产帧
            StaleTimeoutMs = 3000,
            FollowReferenceDrift = false,
        };

        settings.Radar.Mode = RadarPlacementMode.CoLocatedWithBase;
        settings.Radar.YawSource = yawSource;
        settings.Radar.YawDeg = 0;
        settings.Radar.PitchDeg = 0;
        settings.Radar.RollDeg = 0;

        settings.Comparison.Enabled = comparisonEnabled;
        settings.Comparison.MatchRadiusM = matchRadiusM;

        return settings;
    }

    /// <summary>基座样本：主天线位置 + 双天线定向（默认 THS 真航向）。</summary>
    private static GnssSample BaseSample(string? headingSource = "THS", double? headingDeg = BaselineHeadingDeg) => new()
    {
        Device = DeviceKind.BaseStation,
        DeviceName = "基座(UM982)",
        // 必须给时间戳：DeviceSample.Timestamp 默认是 0001-01-01，不给就会被判成「数据已超时」，
        // Note 也就变成超时提示而不是「航向来自 THS」。
        Timestamp = DateTimeOffset.Now,
        Fix = new GnssFix
        {
            Latitude = BaseLatitudeDeg,
            Longitude = BaseLongitudeDeg,
            AltitudeM = BaseAltitudeM,
            FixQuality = 4,       // RTK 固定解
            Satellites = 24,
            Valid = true,
        },
        TrueHeadingDeg = headingDeg,
        HeadingSource = headingSource,
    };

    /// <summary>无人机样本：RTK 定位（UDP 链路回传的遥测）。</summary>
    private static DroneGpsSample DroneSample() => new()
    {
        Device = DeviceKind.DroneGps,
        DeviceName = "无人机 GPS(UCM221)",
        Timestamp = DateTimeOffset.Now,
        Fix = new GnssFix
        {
            Latitude = DroneLatitudeDeg,
            Longitude = DroneLongitudeDeg,
            AltitudeM = DroneAltitudeM,
            FixQuality = 4,
            Valid = true,
        },
    };

    private static RadarSample RadarSampleWith(params RadarTarget[] targets) => new()
    {
        Device = DeviceKind.Radar,
        DeviceName = "雷达(NSR)",
        Timestamp = DateTimeOffset.Now,
        Targets = targets,
    };

    /// <summary>
    /// 按雷达极坐标造一个目标。X/Y/Z 全 0 是刻意的：逼 <c>RelativeService.BuildTargets</c> 走
    /// <c>RadarPolarToCartesian</c> 的回填支路，即「雷达只报距离/方位/俯仰」这一真实情况。
    /// </summary>
    private static RadarTarget PolarTarget(uint id, double azimuthDeg, double elevationDeg, double rangeM) => new()
    {
        Id = id,
        Type = 1,             // 人
        AzimuthDeg = azimuthDeg,
        ElevationDeg = elevationDeg,
        Range = rangeM,
        X = 0,
        Y = 0,
        Z = 0,
    };

    private static RelativeFrame BuildFrame(RelativeSettings settings, RadarSample radar, bool withDrone = true)
    {
        var relative = new RelativeService(settings);
        relative.OnGnss(BaseSample());
        if (withDrone) relative.OnDrone(DroneSample());
        relative.OnRadar(radar);

        var frame = relative.Build("test", force: true);

        Assert.NotNull(frame);
        return frame!;
    }

    /// <summary>
    /// 把一个显示坐标系（ENU）里的点反推成雷达本体系极坐标。
    /// 仅在安装角全 0、正前方为 0° 时成立——此时雷达本体系与 ENU 完全重合：
    /// 方位角 = atan2(东, 北)（本体 +Y 为探测方向、+X 为右，即东），俯仰角 = asin(天 / 距离)。
    /// </summary>
    private static (double AzimuthDeg, double ElevationDeg, double RangeM) PolarFromEnu(EnuPoint point)
    {
        var range = Math.Sqrt(point.East * point.East + point.North * point.North + point.Up * point.Up);
        Assert.True(range > 1.0, $"无人机离雷达只有 {range} m，场景不成立。");

        var azimuth = GeoMath.Normalize360(GeoMath.ToDegrees(Math.Atan2(point.East, point.North)));
        var elevation = GeoMath.ToDegrees(Math.Asin(point.Up / range));

        return (azimuth, elevation, range);
    }

    /// <summary>
    /// 带上下文的浮点断言：失败信息里直接给出「期望 / 实际 / 差多少 / 容差」，
    /// 免得回头只看到一个干巴巴的 Assert.True(false)。
    /// </summary>
    private static void AssertNear(double expected, double actual, double tolerance, string what)
    {
        var delta = Math.Abs(expected - actual);
        Assert.True(delta <= tolerance,
            $"{what}：期望 {expected}，实际 {actual}，相差 {delta}，超出容差 {tolerance}。");
    }
}
