using BLite.Core.Indexing.Internal;

namespace BLite.Core.Indexing;

public static class SpatialMath
{
    private const double EarthRadiusKm = 6371.0;

    /// <summary>
    /// Calculates distance between two points on Earth using Haversine formula.
    /// Result in kilometers.
    /// </summary>
    internal static double DistanceKm(GeoPoint p1, GeoPoint p2) => DistanceKm(p1.Latitude, p1.Longitude, p2.Latitude, p2.Longitude);

    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = ToRadians(lat2 - lat1);
        double dLon = ToRadians(lon2 - lon1);

        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusKm * c;
    }

    /// <summary>
    /// Boxes that together cover every point within <paramref name="radiusKm"/> of the center, for
    /// an R-Tree search whose hits are then checked against the exact distance.
    /// <list type="bullet">
    ///   <item>A circle that contains a pole reaches every longitude, so its box spans all
    ///   longitudes from the circle's far edge up to the pole.</item>
    ///   <item>Otherwise the longitude half-width is the circle's widest point,
    ///   asin(sin r / cos lat), which lies poleward of the center; a box that crosses the ±180°
    ///   meridian is split into one box on each side.</item>
    /// </list>
    /// </summary>
    internal static GeoBox[] NearQueryBoxes(double lat, double lon, double radiusKm)
    {
        double angularRadius = radiusKm / EarthRadiusKm;
        double dLat = ToDegrees(angularRadius);
        double minLat = lat - dLat;
        double maxLat = lat + dLat;

        if (maxLat >= 90 || minLat <= -90)
            return [new GeoBox(Math.Max(minLat, -90), -180, Math.Min(maxLat, 90), 180)];

        double dLon = ToDegrees(Math.Asin(Math.Sin(angularRadius) / Math.Cos(ToRadians(lat))));
        double minLon = lon - dLon;
        double maxLon = lon + dLon;

        if (minLon < -180)
            return [new GeoBox(minLat, minLon + 360, maxLat, 180), new GeoBox(minLat, -180, maxLat, maxLon)];
        if (maxLon > 180)
            return [new GeoBox(minLat, minLon, maxLat, 180), new GeoBox(minLat, -180, maxLat, maxLon - 360)];
        return [new GeoBox(minLat, minLon, maxLat, maxLon)];
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
    private static double ToDegrees(double radians) => radians * 180.0 / Math.PI;
}
