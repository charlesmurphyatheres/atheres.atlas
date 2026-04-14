namespace Atheres.Atlas.Domain.Entities;

public class RouteStop
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RouteId { get; set; }
    public Guid OrderId { get; set; }

    public int Sequence { get; set; }
    public string Address { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string StoreName { get; set; } = string.Empty;

    // Estimated times (calculated from Google Maps leg durations)
    public DateTime? EstimatedArrival { get; set; }
    public DateTime? EstimatedDeparture { get; set; }
    public int ServiceTimeMinutes { get; set; } = 15; // default unload time

    // Leg details from start to this stop
    public double LegDistanceMeters { get; set; }
    public int LegDurationSeconds { get; set; }

    // Navigation properties
    public DeliveryRoute? Route { get; set; }
    public Order? Order { get; set; }
}
