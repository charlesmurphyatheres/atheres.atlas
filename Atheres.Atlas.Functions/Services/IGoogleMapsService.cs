namespace Atheres.Atlas.Functions.Services;

public record GeocodedAddress(
    double Latitude,
    double Longitude,
    string FormattedAddress
);

public record OptimizedRoute(
    IReadOnlyList<int> WaypointOrder,
    double TotalDistanceMeters,
    int TotalDurationSeconds,
    IReadOnlyList<RouteLeg> Legs,
    string OverviewPolyline
);

public record RouteLeg(
    double DistanceMeters,
    int DurationSeconds,
    string StartAddress,
    string EndAddress
);

public record DistanceMatrixEntry(
    string OriginAddress,
    string DestinationAddress,
    double DistanceMeters,
    int DurationSeconds
);

public interface IGoogleMapsService
{
    Task<GeocodedAddress?> GeocodeAsync(string address, CancellationToken ct = default);
    Task<OptimizedRoute?> GetOptimizedRouteAsync(
        string origin,
        string destination,
        IEnumerable<string> waypoints,
        CancellationToken ct = default);

    /// <summary>
    /// Gets travel distances/durations from multiple origins to multiple destinations.
    /// Returns a flat list of origin→destination entries.
    /// </summary>
    Task<IReadOnlyList<DistanceMatrixEntry>?> GetDistanceMatrixAsync(
        IEnumerable<string> origins,
        IEnumerable<string> destinations,
        CancellationToken ct = default);
}
