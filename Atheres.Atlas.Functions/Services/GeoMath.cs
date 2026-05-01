namespace Atheres.Atlas.Functions.Services;

/// <summary>
/// Small set of pure geometry helpers used by the routing pipeline so we
/// don't have to call Google Maps for trivial distance comparisons (e.g.
/// "which of these 3 hubs is closest to this warehouse").
/// </summary>
public static class GeoMath
{
    private const double EarthRadiusMeters = 6_371_000;

    /// <summary>
    /// Great-circle distance between two lat/lng pairs in meters.
    /// Accurate to within ~0.5% over typical road-network distances —
    /// fine for picking the nearest hub from a small candidate list.
    /// </summary>
    public static double HaversineMeters(double lat1, double lng1, double lat2, double lng2)
    {
        var dLat = ToRadians(lat2 - lat1);
        var dLng = ToRadians(lng2 - lng1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2))
              * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusMeters * c;
    }

    private static double ToRadians(double deg) => deg * Math.PI / 180.0;
}
