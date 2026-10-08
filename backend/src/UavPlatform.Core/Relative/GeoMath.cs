namespace UavPlatform.Core.Relative;

/// <summary>地心地固坐标（ECEF，单位米）。</summary>
public readonly record struct EcefPoint(double X, double Y, double Z);

/// <summary>站心坐标（ENU：东、北、天，单位米）。</summary>
public readonly record struct EnuPoint(double East, double North, double Up)
{
    public static readonly EnuPoint Zero = new(0, 0, 0);

    public static EnuPoint operator +(EnuPoint a, EnuPoint b) => new(a.East + b.East, a.North + b.North, a.Up + b.Up);

    public static EnuPoint operator -(EnuPoint a, EnuPoint b) => new(a.East - b.East, a.North - b.North, a.Up - b.Up);

    /// <summary>水平距离（米）。</summary>
    public double HorizontalDistance => Math.Sqrt(East * East + North * North);

    /// <summary>三维距离（米）。</summary>
    public double Distance => Math.Sqrt(East * East + North * North + Up * Up);
}

/// <summary>
/// WGS84 大地坐标与 ENU 站心坐标的换算。
/// 参考点（原点）取基座的定位结果，从而把基座、雷达、无人机统一到同一个局部切平面坐标系。
/// </summary>
public static class GeoMath
{
    /// <summary>WGS84 长半轴（米）。</summary>
    public const double Wgs84SemiMajorAxis = 6378137.0;

    /// <summary>WGS84 扁率。</summary>
    public const double Wgs84Flattening = 1.0 / 298.257223563;

    /// <summary>WGS84 第一偏心率平方。</summary>
    public static readonly double Wgs84EccentricitySquared = Wgs84Flattening * (2.0 - Wgs84Flattening);

    /// <summary>每度纬度对应的米数（用于粗略校验）。</summary>
    public const double MetersPerDegreeLatitude = 111320.0;

    private const double DegreesToRadians = Math.PI / 180.0;
    private const double RadiansToDegrees = 180.0 / Math.PI;

    public static double ToRadians(double degrees) => degrees * DegreesToRadians;

    public static double ToDegrees(double radians) => radians * RadiansToDegrees;

    /// <summary>把角度归一化到 [0, 360)。</summary>
    public static double Normalize360(double degrees)
    {
        var value = degrees % 360.0;
        if (value < 0) value += 360.0;
        return value;
    }

    /// <summary>把角度差归一化到 (-180, 180]，用于比较两个方位角谁偏了多少。</summary>
    public static double Normalize180(double degrees)
    {
        var value = Normalize360(degrees);
        return value > 180.0 ? value - 360.0 : value;
    }

    /// <summary>大地坐标（度、度、米）→ ECEF。</summary>
    public static EcefPoint GeodeticToEcef(double latitudeDeg, double longitudeDeg, double heightMeters)
    {
        var lat = ToRadians(latitudeDeg);
        var lon = ToRadians(longitudeDeg);
        var sinLat = Math.Sin(lat);
        var cosLat = Math.Cos(lat);
        var sinLon = Math.Sin(lon);
        var cosLon = Math.Cos(lon);

        var n = Wgs84SemiMajorAxis / Math.Sqrt(1.0 - Wgs84EccentricitySquared * sinLat * sinLat);
        var x = (n + heightMeters) * cosLat * cosLon;
        var y = (n + heightMeters) * cosLat * sinLon;
        var z = (n * (1.0 - Wgs84EccentricitySquared) + heightMeters) * sinLat;
        return new EcefPoint(x, y, z);
    }

    /// <summary>ECEF → 大地坐标（Bowring 迭代法）。</summary>
    public static (double LatitudeDeg, double LongitudeDeg, double HeightMeters) EcefToGeodetic(EcefPoint point)
    {
        var a = Wgs84SemiMajorAxis;
        var e2 = Wgs84EccentricitySquared;
        var b = a * Math.Sqrt(1.0 - e2);
        var ep2 = (a * a - b * b) / (b * b);

        var p = Math.Sqrt(point.X * point.X + point.Y * point.Y);
        var theta = Math.Atan2(point.Z * a, p * b);
        var sinTheta = Math.Sin(theta);
        var cosTheta = Math.Cos(theta);

        var lat = Math.Atan2(point.Z + ep2 * b * sinTheta * sinTheta * sinTheta,
                             p - e2 * a * cosTheta * cosTheta * cosTheta);
        var lon = Math.Atan2(point.Y, point.X);

        var sinLat = Math.Sin(lat);
        var n = a / Math.Sqrt(1.0 - e2 * sinLat * sinLat);
        var height = p / Math.Cos(lat) - n;

        return (lat * RadiansToDegrees, lon * RadiansToDegrees, height);
    }

    /// <summary>大地坐标 → 以参考点为原点的 ENU。</summary>
    public static EnuPoint GeodeticToEnu(double latitudeDeg, double longitudeDeg, double heightMeters,
        double referenceLatitudeDeg, double referenceLongitudeDeg, double referenceHeightMeters)
    {
        var target = GeodeticToEcef(latitudeDeg, longitudeDeg, heightMeters);
        var origin = GeodeticToEcef(referenceLatitudeDeg, referenceLongitudeDeg, referenceHeightMeters);
        return EcefToEnu(target, origin, referenceLatitudeDeg, referenceLongitudeDeg);
    }

    /// <summary>ECEF 差值 → ENU（以参考点经纬度定义的切平面）。</summary>
    public static EnuPoint EcefToEnu(EcefPoint target, EcefPoint origin, double referenceLatitudeDeg, double referenceLongitudeDeg)
    {
        var dx = target.X - origin.X;
        var dy = target.Y - origin.Y;
        var dz = target.Z - origin.Z;

        var lat = ToRadians(referenceLatitudeDeg);
        var lon = ToRadians(referenceLongitudeDeg);
        var sinLat = Math.Sin(lat);
        var cosLat = Math.Cos(lat);
        var sinLon = Math.Sin(lon);
        var cosLon = Math.Cos(lon);

        var east = -sinLon * dx + cosLon * dy;
        var north = -sinLat * cosLon * dx - sinLat * sinLon * dy + cosLat * dz;
        var up = cosLat * cosLon * dx + cosLat * sinLon * dy + sinLat * dz;
        return new EnuPoint(east, north, up);
    }

    /// <summary>ENU → ECEF 绝对坐标（用于把相对位置结果反算回经纬度）。</summary>
    public static EcefPoint EnuToEcef(EnuPoint enu, EcefPoint origin, double referenceLatitudeDeg, double referenceLongitudeDeg)
    {
        var lat = ToRadians(referenceLatitudeDeg);
        var lon = ToRadians(referenceLongitudeDeg);
        var sinLat = Math.Sin(lat);
        var cosLat = Math.Cos(lat);
        var sinLon = Math.Sin(lon);
        var cosLon = Math.Cos(lon);

        var dx = -sinLon * enu.East - sinLat * cosLon * enu.North + cosLat * cosLon * enu.Up;
        var dy = cosLon * enu.East - sinLat * sinLon * enu.North + cosLat * sinLon * enu.Up;
        var dz = cosLat * enu.North + sinLat * enu.Up;

        return new EcefPoint(origin.X + dx, origin.Y + dy, origin.Z + dz);
    }

    /// <summary>ENU → 大地坐标（把相对位置结果换算回经纬度，供热力图与地图使用）。</summary>
    public static (double LatitudeDeg, double LongitudeDeg, double HeightMeters) EnuToGeodetic(
        EnuPoint enu, double referenceLatitudeDeg, double referenceLongitudeDeg, double referenceHeightMeters)
    {
        var origin = GeodeticToEcef(referenceLatitudeDeg, referenceLongitudeDeg, referenceHeightMeters);
        var ecef = EnuToEcef(enu, origin, referenceLatitudeDeg, referenceLongitudeDeg);
        return EcefToGeodetic(ecef);
    }

    /// <summary>两个经纬度之间的水平距离（米，Haversine 公式）。</summary>
    public static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadius = 6371008.8;
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthRadius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>
    /// 把雷达本体系向量（+Y 为探测方向、+X 向右、+Z 向上）按安装姿态旋转到 ENU。
    /// 旋转顺序：先绕本体 Y 轴滚转（Roll），再绕本体 X 轴俯仰（Pitch），最后绕天向轴偏航（Yaw）。
    /// 偏航角定义为雷达探测方向相对**正北**的夹角，顺时针为正。
    /// </summary>
    public static EnuPoint RotateRadarToEnu(double x, double y, double z, double yawDeg, double pitchDeg, double rollDeg)
    {
        var yaw = ToRadians(yawDeg);
        var pitch = ToRadians(pitchDeg);
        var roll = ToRadians(rollDeg);

        // 1) 绕本体 Y 轴（探测方向）滚转
        var cosRoll = Math.Cos(roll);
        var sinRoll = Math.Sin(roll);
        var x1 = x * cosRoll + z * sinRoll;
        var y1 = y;
        var z1 = -x * sinRoll + z * cosRoll;

        // 2) 绕本体 X 轴（右向）俯仰
        var cosPitch = Math.Cos(pitch);
        var sinPitch = Math.Sin(pitch);
        var x2 = x1;
        var y2 = y1 * cosPitch - z1 * sinPitch;
        var z2 = y1 * sinPitch + z1 * cosPitch;

        // 3) 绕天向轴偏航：本体 +Y → 正北，本体 +X → 正东
        var cosYaw = Math.Cos(yaw);
        var sinYaw = Math.Sin(yaw);
        var east = x2 * cosYaw + y2 * sinYaw;
        var north = -x2 * sinYaw + y2 * cosYaw;
        var up = z2;

        return new EnuPoint(east, north, up);
    }

    /// <summary>
    /// <see cref="RotateRadarToEnu"/> 的逆变换：把 ENU 向量转回雷达本体系（+Y 探测方向、+X 向右、+Z 向上）。
    /// 用于把无人机 RTK 位置换算到雷达极坐标，与雷达实测的距离/方位/俯仰直接比对。
    /// 反解顺序与正变换相反：先绕天向轴 −Yaw，再绕本体 X 轴 −Pitch，最后绕本体 Y 轴 −Roll。
    /// </summary>
    public static (double X, double Y, double Z) EnuToRadarBody(double east, double north, double up,
        double yawDeg, double pitchDeg, double rollDeg)
    {
        var yaw = ToRadians(yawDeg);
        var pitch = ToRadians(pitchDeg);
        var roll = ToRadians(rollDeg);

        // 反向第 3 步：绕天向轴 −Yaw
        var cosYaw = Math.Cos(yaw);
        var sinYaw = Math.Sin(yaw);
        var x2 = east * cosYaw - north * sinYaw;
        var y2 = east * sinYaw + north * cosYaw;
        var z2 = up;

        // 反向第 2 步：绕本体 X 轴（右向）−Pitch
        var cosPitch = Math.Cos(pitch);
        var sinPitch = Math.Sin(pitch);
        var x1 = x2;
        var y1 = y2 * cosPitch + z2 * sinPitch;
        var z1 = -y2 * sinPitch + z2 * cosPitch;

        // 反向第 1 步：绕本体 Y 轴（探测方向）−Roll
        var cosRoll = Math.Cos(roll);
        var sinRoll = Math.Sin(roll);
        var x = x1 * cosRoll - z1 * sinRoll;
        var y = y1;
        var z = x1 * sinRoll + z1 * cosRoll;

        return (x, y, z);
    }

    /// <summary>雷达极坐标（方位角、俯仰角、距离，角度单位为度）→ 本体直角坐标。</summary>
    public static (double X, double Y, double Z) RadarPolarToCartesian(double azimuthDeg, double elevationDeg, double rangeMeters)
    {
        var az = ToRadians(azimuthDeg);
        var el = ToRadians(elevationDeg);
        var horizontal = rangeMeters * Math.Cos(el);
        return (horizontal * Math.Sin(az), horizontal * Math.Cos(az), rangeMeters * Math.Sin(el));
    }

    /// <summary>本体直角坐标 → 雷达极坐标。</summary>
    public static (double AzimuthDeg, double ElevationDeg, double RangeMeters) CartesianToRadarPolar(double x, double y, double z)
    {
        var range = Math.Sqrt(x * x + y * y + z * z);
        if (range < 1e-9) return (0, 0, 0);
        var azimuth = ToDegrees(Math.Atan2(x, y));
        var elevation = ToDegrees(Math.Asin(Math.Clamp(z / range, -1.0, 1.0)));
        return (azimuth, elevation, range);
    }
}
