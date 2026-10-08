using UavPlatform.Core.Relative;

namespace UavPlatform.Tests;

/// <summary>
/// 地理坐标换算单元测试：WGS84 大地坐标 ↔ ECEF ↔ ENU，雷达极坐标 ↔ 直角坐标，以及雷达系 → ENU 的旋转。
/// 期望值全部由测试文件独立给出（部分为解析式，部分为公开的球面近似），不引用被测实现的常量。
/// </summary>
public class GeoMathTests
{
    /// <summary>北京参考点。</summary>
    private const double BeijingLat = 39.9041984;

    /// <inheritdoc cref="BeijingLat" />
    private const double BeijingLon = 116.4073984;

    /// <summary>WGS84 长半轴（米），测试独立重写。</summary>
    private const double Wgs84A = 6378137.0;

    /// <summary>WGS84 扁率倒数，测试独立重写。</summary>
    private const double Wgs84InvF = 298.257223563;

    /// <summary>往北 100 m 对应的纬度增量：任务书给的 0.000898（按 111320 m/度粗算）与椭球真值 0.0009006 都要容纳。</summary>
    private const double LatitudePer100Meters = 0.0009006;

    // ==================== 常量与角度工具 ====================

    [Fact]
    public void Wgs84常量应与标准一致()
    {
        Assert.Equal(Wgs84A, GeoMath.Wgs84SemiMajorAxis, 9);
        Assert.Equal(1.0 / Wgs84InvF, GeoMath.Wgs84Flattening, 15);
        Assert.Equal(111320.0, GeoMath.MetersPerDegreeLatitude, 9);

        var f = 1.0 / Wgs84InvF;
        Assert.Equal(f * (2 - f), GeoMath.Wgs84EccentricitySquared, 15);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(180.0, Math.PI)]
    [InlineData(-90.0, -Math.PI / 2)]
    public void ToRadians与ToDegrees应互为逆运算(double degrees, double radians)
    {
        Assert.Equal(radians, GeoMath.ToRadians(degrees), 12);
        Assert.Equal(degrees, GeoMath.ToDegrees(radians), 12);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(360.0, 0.0)]
    [InlineData(370.0, 10.0)]
    [InlineData(-10.0, 350.0)]
    [InlineData(720.0, 0.0)]
    [InlineData(359.9, 359.9)]
    public void Normalize360_应归一化到0到360(double input, double expected)
    {
        Assert.Equal(expected, GeoMath.Normalize360(input), 9);
    }

    // ==================== ENU 点结构 ====================

    [Fact]
    public void EnuPoint_零向量与距离运算()
    {
        Assert.Equal(0.0, EnuPoint.Zero.East);
        Assert.Equal(0.0, EnuPoint.Zero.North);
        Assert.Equal(0.0, EnuPoint.Zero.Up);

        var p = new EnuPoint(3, 4, 12);
        Assert.Equal(5.0, p.HorizontalDistance, 9);   // sqrt(3²+4²)
        Assert.Equal(13.0, p.Distance, 9);             // sqrt(3²+4²+12²)

        var sum = p + new EnuPoint(1, 1, 1);
        Assert.Equal(new EnuPoint(4, 5, 13), sum);

        var diff = p - new EnuPoint(1, 1, 2);
        Assert.Equal(new EnuPoint(2, 3, 10), diff);
    }

    // ==================== 大地坐标 ↔ ECEF ====================

    [Fact]
    public void GeodeticToEcef_赤道本初子午线应为长半轴()
    {
        var ecef = GeoMath.GeodeticToEcef(0, 0, 0);

        Assert.Equal(Wgs84A, ecef.X, 6);
        Assert.Equal(0.0, ecef.Y, 6);
        Assert.Equal(0.0, ecef.Z, 6);
    }

    [Fact]
    public void GeodeticToEcef_北极点Z应为短半轴()
    {
        var b = Wgs84A * (1 - 1.0 / Wgs84InvF);

        var ecef = GeoMath.GeodeticToEcef(90, 0, 0);

        Assert.Equal(0.0, ecef.X, 6);
        Assert.Equal(0.0, ecef.Y, 6);
        Assert.Equal(b, ecef.Z, 6);
    }

    [Fact]
    public void GeodeticToEcef_海拔应沿椭球法线抬升()
    {
        var ground = GeoMath.GeodeticToEcef(BeijingLat, BeijingLon, 0);
        var high = GeoMath.GeodeticToEcef(BeijingLat, BeijingLon, 1000);

        // 高度沿椭球法线抬升，两点位移向量的模长应正好是 1000 m
        var dx = high.X - ground.X;
        var dy = high.Y - ground.Y;
        var dz = high.Z - ground.Z;
        Assert.Equal(1000.0, Math.Sqrt(dx * dx + dy * dy + dz * dz), 6);

        // 但地心距只增加约 999.995 m：椭球法线并不通过地心
        var r0 = Math.Sqrt(ground.X * ground.X + ground.Y * ground.Y + ground.Z * ground.Z);
        var r1 = Math.Sqrt(high.X * high.X + high.Y * high.Y + high.Z * high.Z);
        Assert.InRange(r1 - r0, 999.99, 1000.0);
    }

    [Theory]
    [InlineData(39.9041984, 116.4073984, 100.0)]
    [InlineData(0.0, 0.0, 0.0)]
    [InlineData(0.0, 179.5, 12.5)]
    [InlineData(-33.8688, 151.2093, 58.0)]
    [InlineData(64.1355, -21.8954, 15.0)]
    [InlineData(-89.9, 0.0, 1000.0)]
    public void Ecef往返_经纬高误差应小于百万分之一度(double latitude, double longitude, double height)
    {
        var ecef = GeoMath.GeodeticToEcef(latitude, longitude, height);

        var (lat2, lon2, h2) = GeoMath.EcefToGeodetic(ecef);

        Assert.InRange(Math.Abs(lat2 - latitude), 0.0, 1e-6);
        Assert.InRange(Math.Abs(lon2 - longitude), 0.0, 1e-6);
        Assert.InRange(Math.Abs(h2 - height), 0.0, 1e-3);
    }

    // ==================== 大地坐标 ↔ ENU ====================

    [Fact]
    public void GeodeticToEnu_参考点自身应为原点()
    {
        var enu = GeoMath.GeodeticToEnu(BeijingLat, BeijingLon, 50, BeijingLat, BeijingLon, 50);

        Assert.Equal(0.0, enu.East, 6);
        Assert.Equal(0.0, enu.North, 6);
        Assert.Equal(0.0, enu.Up, 6);
    }

    [Fact]
    public void GeodeticToEnu_正北方向应只有北分量()
    {
        var enu = GeoMath.GeodeticToEnu(BeijingLat + 0.001, BeijingLon, 0, BeijingLat, BeijingLon, 0);

        Assert.InRange(enu.North, 111.0, 112.0);            // 0.001° 纬度在南纬/北纬都约 111.2 m
        Assert.Equal(0.0, enu.East, 6);
        Assert.InRange(Math.Abs(enu.Up), 0.0, 0.05);        // 沿椭球面走会略微离开切平面
    }

    [Fact]
    public void GeodeticToEnu_正东方向应只有东分量()
    {
        var enu = GeoMath.GeodeticToEnu(BeijingLat, BeijingLon + 0.001, 0, BeijingLat, BeijingLon, 0);

        Assert.InRange(enu.East, 85.0, 86.0);               // 0.001° 经度 × cos(39.9°) ≈ 85.6 m
        // 同纬度两点之间是弦而非切向弧，弦略微偏向地心，故北分量有约 0.5 mm 的几何投影（非实现误差）
        Assert.InRange(Math.Abs(enu.North), 0.0, 0.001);
        Assert.InRange(Math.Abs(enu.Up), 0.0, 0.05);
    }

    [Fact]
    public void GeodeticToEnu_高度差应落在天分量()
    {
        var enu = GeoMath.GeodeticToEnu(BeijingLat, BeijingLon, 130, BeijingLat, BeijingLon, 30);

        Assert.Equal(100.0, enu.Up, 6);
        Assert.Equal(0.0, enu.East, 6);
        Assert.Equal(0.0, enu.North, 6);
    }

    [Fact]
    public void EnuToGeodetic_往北100米的纬度增量应约等于0点0009度()
    {
        var (lat, lon, height) = GeoMath.EnuToGeodetic(
            new EnuPoint(0, 100, 0), BeijingLat, BeijingLon, 0);

        // 任务书按 111320 m/度 粗算得 0.000898；WGS84 子午圈曲率半径给出 0.0009006，
        // 两者都要容纳，故容差取 1e-5 量级。
        Assert.InRange(lat - BeijingLat, 0.00089, 0.00091);
        Assert.Equal(BeijingLat + LatitudePer100Meters, lat, 4);
        Assert.Equal(BeijingLon, lon, 9);                  // 经度不应改变
        Assert.InRange(Math.Abs(height), 0.0, 0.01);       // 沿椭球面北移，天向高度基本不变
    }

    [Fact]
    public void EnuToGeodetic_往东100米应主要改变经度()
    {
        var (lat, lon, _) = GeoMath.EnuToGeodetic(
            new EnuPoint(100, 0, 0), BeijingLat, BeijingLon, 0);

        Assert.Equal(BeijingLat, lat, 6);
        Assert.InRange(lon - BeijingLon, 0.00116, 0.00118); // 100 / (111320·cos39.9°) ≈ 0.001169
    }

    [Theory]
    [InlineData(0.0, 100.0, 0.0)]
    [InlineData(100.0, 0.0, 0.0)]
    [InlineData(30.0, -45.0, 12.0)]
    [InlineData(-200.0, 300.0, -25.0)]
    public void Enu往返_误差应小于0点01米(double east, double north, double up)
    {
        var enu = new EnuPoint(east, north, up);

        var (lat, lon, h) = GeoMath.EnuToGeodetic(enu, BeijingLat, BeijingLon, 55.0);
        var back = GeoMath.GeodeticToEnu(lat, lon, h, BeijingLat, BeijingLon, 55.0);

        Assert.InRange(Math.Abs(back.East - east), 0.0, 0.01);
        Assert.InRange(Math.Abs(back.North - north), 0.0, 0.01);
        Assert.InRange(Math.Abs(back.Up - up), 0.0, 0.01);
    }

    [Fact]
    public void Enu往返_与原大地坐标的球面距离应小于1厘米()
    {
        const double targetLat = BeijingLat + 0.0035;
        const double targetLon = BeijingLon - 0.0021;
        const double targetHeight = 88.0;

        var enu = GeoMath.GeodeticToEnu(targetLat, targetLon, targetHeight, BeijingLat, BeijingLon, 30.0);
        var (lat, lon, height) = GeoMath.EnuToGeodetic(enu, BeijingLat, BeijingLon, 30.0);

        Assert.InRange(GeoMath.HaversineMeters(targetLat, targetLon, lat, lon), 0.0, 0.01);
        Assert.InRange(Math.Abs(height - targetHeight), 0.0, 0.01);
    }

    [Fact]
    public void EcefToEnu与EnuToEcef应互为逆运算()
    {
        var origin = GeoMath.GeodeticToEcef(BeijingLat, BeijingLon, 40);
        var target = GeoMath.GeodeticToEcef(BeijingLat + 0.002, BeijingLon + 0.003, 120);

        var enu = GeoMath.EcefToEnu(target, origin, BeijingLat, BeijingLon);
        var back = GeoMath.EnuToEcef(enu, origin, BeijingLat, BeijingLon);

        Assert.Equal(target.X, back.X, 6);
        Assert.Equal(target.Y, back.Y, 6);
        Assert.Equal(target.Z, back.Z, 6);
    }

    [Fact]
    public void EcefToEnu_与GeodeticToEnu应给出一致结果()
    {
        const double targetLat = BeijingLat + 0.0015;
        const double targetLon = BeijingLon + 0.0012;
        const double targetHeight = 77.0;

        var viaGeodetic = GeoMath.GeodeticToEnu(targetLat, targetLon, targetHeight, BeijingLat, BeijingLon, 20.0);
        var viaEcef = GeoMath.EcefToEnu(
            GeoMath.GeodeticToEcef(targetLat, targetLon, targetHeight),
            GeoMath.GeodeticToEcef(BeijingLat, BeijingLon, 20.0),
            BeijingLat,
            BeijingLon);

        Assert.Equal(viaGeodetic.East, viaEcef.East, 6);
        Assert.Equal(viaGeodetic.North, viaEcef.North, 6);
        Assert.Equal(viaGeodetic.Up, viaEcef.Up, 6);
    }

    // ==================== Haversine ====================

    [Fact]
    public void HaversineMeters_同一点距离应为零()
    {
        Assert.Equal(0.0, GeoMath.HaversineMeters(BeijingLat, BeijingLon, BeijingLat, BeijingLon), 9);
    }

    [Fact]
    public void HaversineMeters_一个纬度差应约等于111公里()
    {
        // 球面近似：R = 6371008.8 m，1° = π/180 rad
        var expected = 6371008.8 * Math.PI / 180.0;

        Assert.Equal(expected, GeoMath.HaversineMeters(0, 0, 1, 0), 3);
        Assert.Equal(expected, GeoMath.HaversineMeters(0, 0, 0, 1), 3); // 赤道上一个经度差同理
        Assert.Equal(expected, GeoMath.HaversineMeters(39.9, 116.4, 40.9, 116.4), 3);
    }

    [Fact]
    public void HaversineMeters_已知小距离应约等于100米()
    {
        // 用 EnuToGeodetic 生成真北 100 m 的点，再用 Haversine 量回来（球面近似，误差在 1 m 内）
        var (lat, lon, _) = GeoMath.EnuToGeodetic(new EnuPoint(0, 100, 0), BeijingLat, BeijingLon, 0);

        Assert.InRange(GeoMath.HaversineMeters(BeijingLat, BeijingLon, lat, lon), 99.5, 100.5);
    }

    [Fact]
    public void HaversineMeters_应对称()
    {
        var ab = GeoMath.HaversineMeters(31.2304, 121.4737, 22.5431, 114.0579);
        var ba = GeoMath.HaversineMeters(22.5431, 114.0579, 31.2304, 121.4737);

        Assert.Equal(ab, ba, 9);
        Assert.InRange(ab, 1_200_000.0, 1_250_000.0); // 上海 ↔ 深圳 陆路直线约 1210 km
    }

    // ==================== 雷达极坐标 ↔ 直角坐标 ====================

    [Fact]
    public void RadarPolarToCartesian_方位角以正北为零且顺时针为正()
    {
        var north = GeoMath.RadarPolarToCartesian(0, 0, 100);
        Assert.Equal(0.0, north.X, 9);
        Assert.Equal(100.0, north.Y, 9);   // 雷达探测方向为 Y 轴正向 = 正北
        Assert.Equal(0.0, north.Z, 9);

        var east = GeoMath.RadarPolarToCartesian(90, 0, 100);
        Assert.Equal(100.0, east.X, 9);
        Assert.Equal(0.0, east.Y, 9);

        var up = GeoMath.RadarPolarToCartesian(0, 90, 100);
        Assert.Equal(0.0, up.X, 9);
        Assert.Equal(0.0, up.Y, 9);
        Assert.Equal(100.0, up.Z, 9);
    }

    [Fact]
    public void RadarPolarToCartesian_俯仰应缩短水平距离()
    {
        var p = GeoMath.RadarPolarToCartesian(0, 30, 100);

        Assert.Equal(100.0 * Math.Cos(Math.PI / 6), p.Y, 9);
        Assert.Equal(50.0, p.Z, 9);
    }

    [Theory]
    [InlineData(0.0, 0.0, 100.0)]
    [InlineData(90.0, 0.0, 250.0)]
    [InlineData(45.0, 10.0, 1000.0)]
    [InlineData(-135.0, -20.0, 33.5)]
    [InlineData(180.0, 15.0, 12.0)]
    public void 雷达极坐标往返_方位俯仰距离应还原(double azimuth, double elevation, double range)
    {
        var (x, y, z) = GeoMath.RadarPolarToCartesian(azimuth, elevation, range);

        var (az2, el2, r2) = GeoMath.CartesianToRadarPolar(x, y, z);

        Assert.Equal(range, r2, 9);
        Assert.Equal(elevation, el2, 9);
        // 方位角在 ±180 处会翻转符号，换算差值后再比较
        var delta = Math.Abs(GeoMath.Normalize360(az2) - GeoMath.Normalize360(azimuth));
        Assert.InRange(Math.Min(delta, 360.0 - delta), 0.0, 1e-9);
    }

    [Fact]
    public void CartesianToRadarPolar_原点应返回全零()
    {
        var (az, el, range) = GeoMath.CartesianToRadarPolar(0, 0, 0);

        Assert.Equal(0.0, az, 9);
        Assert.Equal(0.0, el, 9);
        Assert.Equal(0.0, range, 9);
    }

    [Fact]
    public void CartesianToRadarPolar_方位角应以Y轴为零点()
    {
        var (az, _, _) = GeoMath.CartesianToRadarPolar(1, 0, 0);
        Assert.Equal(90.0, az, 9);

        var (az2, _, _) = GeoMath.CartesianToRadarPolar(0, 1, 0);
        Assert.Equal(0.0, az2, 9);

        var (az3, _, _) = GeoMath.CartesianToRadarPolar(-1, 0, 0);
        Assert.Equal(-90.0, az3, 9);
    }

    // ==================== 雷达系 → ENU ====================

    [Fact]
    public void RotateRadarToEnu_无姿态时正前方100米应正好是正北100米()
    {
        var enu = GeoMath.RotateRadarToEnu(0, 100, 0, yawDeg: 0, pitchDeg: 0, rollDeg: 0);

        Assert.Equal(0.0, enu.East, 9);
        Assert.Equal(100.0, enu.North, 9);
        Assert.Equal(0.0, enu.Up, 9);
    }

    [Fact]
    public void RotateRadarToEnu_偏航90度时正前方应指向正东()
    {
        var enu = GeoMath.RotateRadarToEnu(0, 100, 0, yawDeg: 90, pitchDeg: 0, rollDeg: 0);

        Assert.Equal(100.0, enu.East, 9);
        Assert.InRange(Math.Abs(enu.North), 0.0, 1e-9);
        Assert.Equal(0.0, enu.Up, 9);
    }

    [Fact]
    public void RotateRadarToEnu_偏航90度时本体右方应指向正南()
    {
        // 航向 90°（东）时，本体 +X（右侧）落在正南
        var enu = GeoMath.RotateRadarToEnu(100, 0, 0, yawDeg: 90, pitchDeg: 0, rollDeg: 0);

        Assert.InRange(Math.Abs(enu.East), 0.0, 1e-9);
        Assert.Equal(-100.0, enu.North, 9);
    }

    [Fact]
    public void RotateRadarToEnu_俯仰向上应把正前方抬到天向()
    {
        var enu = GeoMath.RotateRadarToEnu(0, 100, 0, yawDeg: 0, pitchDeg: 90, rollDeg: 0);

        Assert.InRange(Math.Abs(enu.East), 0.0, 1e-9);
        Assert.InRange(Math.Abs(enu.North), 0.0, 1e-9);
        Assert.Equal(100.0, enu.Up, 9);
    }

    [Fact]
    public void RotateRadarToEnu_横滚右正应把本体右方压低()
    {
        // 本体 +X 为右侧；横滚右正（右翼下沉）时它应落到天向负方向
        var enu = GeoMath.RotateRadarToEnu(100, 0, 0, yawDeg: 0, pitchDeg: 0, rollDeg: 90);

        Assert.InRange(Math.Abs(enu.East), 0.0, 1e-9);
        Assert.InRange(Math.Abs(enu.North), 0.0, 1e-9);
        Assert.Equal(-100.0, enu.Up, 9);
    }

    [Fact]
    public void RotateRadarToEnu_应保持向量长度()
    {
        var enu = GeoMath.RotateRadarToEnu(30, 20, 10, yawDeg: 137.5, pitchDeg: -12.25, rollDeg: 6.75);

        var original = Math.Sqrt(30 * 30 + 20 * 20 + 10 * 10);
        Assert.Equal(original, enu.Distance, 9);
    }

    [Fact]
    public void RotateRadarToEnu_三点零姿态应与极坐标换算一致()
    {
        var (x, y, z) = GeoMath.RadarPolarToCartesian(30, 20, 500);

        var enu = GeoMath.RotateRadarToEnu(x, y, z, 0, 0, 0);

        Assert.Equal(x, enu.East, 9);
        Assert.Equal(y, enu.North, 9);
        Assert.Equal(z, enu.Up, 9);
        Assert.Equal(500.0, enu.Distance, 9);
    }
}
