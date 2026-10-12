namespace BLite.Core;

public static class GeoSpatialExtensions
{
    /// <summary>
    /// Performs a geospatial proximity search (Near) on a coordinate tuple property.
    /// The LINQ query provider serves it from an R-Tree index when one exists; otherwise it is
    /// evaluated per document using the great-circle (Haversine) distance.
    /// </summary>
    /// <param name="point">The coordinate tuple (Latitude, Longitude) property of the entity.</param>
    /// <param name="center">The center point (Latitude, Longitude) for the proximity search.</param>
    /// <param name="radiusKm">The radius in kilometers.</param>
    /// <returns>True if the point is within the specified radius.</returns>
    public static bool Near(this (double Latitude, double Longitude) point, (double Latitude, double Longitude) center, double radiusKm)
    {
        return Indexing.SpatialMath.DistanceKm(point.Latitude, point.Longitude, center.Latitude, center.Longitude) <= radiusKm;
    }

    /// <summary>
    /// Performs a geospatial bounding box search (Within) on a coordinate tuple property.
    /// The LINQ query provider serves it from an R-Tree index when one exists; otherwise it is
    /// evaluated per document. The box includes its edges and does not wrap across the ±180°
    /// meridian: a <paramref name="min"/> longitude greater than the <paramref name="max"/>
    /// longitude matches nothing.
    /// </summary>
    /// <param name="point">The coordinate tuple (Latitude, Longitude) property of the entity.</param>
    /// <param name="min">The minimum (Latitude, Longitude) of the bounding box.</param>
    /// <param name="max">The maximum (Latitude, Longitude) of the bounding box.</param>
    /// <returns>True if the point is within the specified bounding box.</returns>
    public static bool Within(this (double Latitude, double Longitude) point, (double Latitude, double Longitude) min, (double Latitude, double Longitude) max)
    {
        return point.Latitude >= min.Latitude && point.Latitude <= max.Latitude &&
               point.Longitude >= min.Longitude && point.Longitude <= max.Longitude;
    }
}
