using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Functions.Services;

public class GoogleMapsService : IGoogleMapsService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ILogger<GoogleMapsService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public GoogleMapsService(HttpClient http, ILogger<GoogleMapsService> logger)
    {
        _http = http;
        _logger = logger;
        _apiKey = Environment.GetEnvironmentVariable("GoogleMapsApiKey")
                  ?? throw new InvalidOperationException("GoogleMapsApiKey is not configured.");
    }

    public async Task<GeocodedAddress?> GeocodeAsync(string address, CancellationToken ct = default)
    {
        var encoded = Uri.EscapeDataString(address);
        var url = $"https://maps.googleapis.com/maps/api/geocode/json?address={encoded}&key={_apiKey}";

        try
        {
            var response = await _http.GetFromJsonAsync<GeocodeResponse>(url, JsonOptions, ct);
            if (response?.Status != "OK" || response.Results.Length == 0)
            {
                _logger.LogWarning("Geocode failed for address '{Address}': {Status}", address, response?.Status);
                return null;
            }

            var result = response.Results[0];
            return new GeocodedAddress(
                result.Geometry.Location.Lat,
                result.Geometry.Location.Lng,
                result.FormattedAddress
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Geocode request failed for '{Address}'", address);
            return null;
        }
    }

    public async Task<OptimizedRoute?> GetOptimizedRouteAsync(
        string origin,
        string destination,
        IEnumerable<string> waypoints,
        CancellationToken ct = default)
    {
        var waypointList = waypoints.ToList();
        if (waypointList.Count == 0)
        {
            _logger.LogWarning("No waypoints provided for route optimization.");
            return null;
        }

        var waypointParam = "optimize:true|" + string.Join("|", waypointList.Select(Uri.EscapeDataString));
        var url = $"https://maps.googleapis.com/maps/api/directions/json" +
                  $"?origin={Uri.EscapeDataString(origin)}" +
                  $"&destination={Uri.EscapeDataString(destination)}" +
                  $"&waypoints={waypointParam}" +
                  $"&key={_apiKey}";

        try
        {
            var response = await _http.GetFromJsonAsync<DirectionsResponse>(url, JsonOptions, ct);
            if (response?.Status != "OK" || response.Routes.Length == 0)
            {
                _logger.LogWarning("Directions API failed: {Status}", response?.Status);
                return null;
            }

            var route = response.Routes[0];
            var legs = route.Legs.Select(l => new RouteLeg(
                l.Distance.Value,
                l.Duration.Value,
                l.StartAddress,
                l.EndAddress
            )).ToList();

            double totalDistance = legs.Sum(l => l.DistanceMeters);
            int totalDuration = legs.Sum(l => l.DurationSeconds);

            return new OptimizedRoute(
                route.WaypointOrder,
                totalDistance,
                totalDuration,
                legs,
                route.OverviewPolyline.Points
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Directions API request failed");
            return null;
        }
    }

    public async Task<IReadOnlyList<DistanceMatrixEntry>?> GetDistanceMatrixAsync(
        IEnumerable<string> origins,
        IEnumerable<string> destinations,
        CancellationToken ct = default)
    {
        var originList = origins.ToList();
        var destList = destinations.ToList();

        var originsParam = string.Join("|", originList.Select(Uri.EscapeDataString));
        var destsParam = string.Join("|", destList.Select(Uri.EscapeDataString));

        var url = $"https://maps.googleapis.com/maps/api/distancematrix/json" +
                  $"?origins={originsParam}" +
                  $"&destinations={destsParam}" +
                  $"&key={_apiKey}";

        try
        {
            var response = await _http.GetFromJsonAsync<DistanceMatrixResponse>(url, JsonOptions, ct);
            if (response?.Status != "OK")
            {
                _logger.LogWarning("Distance Matrix API failed: {Status}", response?.Status);
                return null;
            }

            var entries = new List<DistanceMatrixEntry>();
            for (int o = 0; o < response.Rows.Length && o < originList.Count; o++)
            {
                var row = response.Rows[o];
                for (int d = 0; d < row.Elements.Length && d < destList.Count; d++)
                {
                    var el = row.Elements[d];
                    if (el.Status == "OK")
                    {
                        entries.Add(new DistanceMatrixEntry(
                            originList[o], destList[d],
                            el.Distance.Value, el.Duration.Value));
                    }
                }
            }

            return entries;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Distance Matrix API request failed");
            return null;
        }
    }

    // ---- Internal deserialization models ----

    private record GeocodeResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("results")] GeocodeResult[] Results
    );

    private record GeocodeResult(
        [property: JsonPropertyName("formatted_address")] string FormattedAddress,
        [property: JsonPropertyName("geometry")] GeocodeGeometry Geometry
    );

    private record GeocodeGeometry(
        [property: JsonPropertyName("location")] LatLng Location
    );

    private record LatLng(
        [property: JsonPropertyName("lat")] double Lat,
        [property: JsonPropertyName("lng")] double Lng
    );

    private record DirectionsResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("routes")] DirectionsRoute[] Routes
    );

    private record DirectionsRoute(
        [property: JsonPropertyName("waypoint_order")] IReadOnlyList<int> WaypointOrder,
        [property: JsonPropertyName("legs")] DirectionsLeg[] Legs,
        [property: JsonPropertyName("overview_polyline")] DirectionsPolyline OverviewPolyline
    );

    private record DirectionsLeg(
        [property: JsonPropertyName("start_address")] string StartAddress,
        [property: JsonPropertyName("end_address")] string EndAddress,
        [property: JsonPropertyName("distance")] DirectionsValue Distance,
        [property: JsonPropertyName("duration")] DirectionsValue Duration
    );

    private record DirectionsValue(
        [property: JsonPropertyName("value")] int Value,
        [property: JsonPropertyName("text")] string Text
    );

    private record DirectionsPolyline(
        [property: JsonPropertyName("points")] string Points
    );

    private record DistanceMatrixResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("rows")] DistanceMatrixRow[]  Rows
    );

    private record DistanceMatrixRow(
        [property: JsonPropertyName("elements")] DistanceMatrixElement[] Elements
    );

    private record DistanceMatrixElement(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("distance")] DirectionsValue Distance,
        [property: JsonPropertyName("duration")] DirectionsValue Duration
    );
}
