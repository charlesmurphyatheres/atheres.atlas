namespace Atheres.Atlas.Domain.Entities;

public class DeliveryRoute
{
    public Guid  Id        { get; set; } = Guid.NewGuid();
    public Guid  CompanyId { get; set; }
    public Guid? TruckId   { get; set; }

    /// <summary>Hub this route originates from and returns to.</summary>
    public Guid? HubId { get; set; }

    /// <summary>Warehouse where goods are picked up (null if all orders are already at hub).</summary>
    public Guid? WarehouseId { get; set; }

    public DateTime DeliveryDate { get; set; }

    public string StartAddress { get; set; } = string.Empty;
    public double StartLatitude { get; set; }
    public double StartLongitude { get; set; }

    public string EndAddress { get; set; } = string.Empty;
    public double EndLatitude { get; set; }
    public double EndLongitude { get; set; }

    public int TotalStops { get; set; }
    public double TotalDistanceMeters { get; set; }
    public int TotalDurationSeconds { get; set; }

    // JSON-serialized waypoint order from Google Maps optimization
    public string? OptimizedWaypointOrder { get; set; }

    // Google Maps polyline for map display
    public string? OverviewPolyline { get; set; }

    public bool IsOptimized { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public Company?               Company   { get; set; }
    public Truck?                 Truck     { get; set; }
    public Hub?                   Hub       { get; set; }
    public Warehouse?             Warehouse { get; set; }
    public ICollection<RouteStop> Stops   { get; set; } = new List<RouteStop>();
    public ICollection<Order>     Orders  { get; set; } = new List<Order>();

    public double TotalDistanceMiles => TotalDistanceMeters * 0.000621371;
    public TimeSpan TotalDuration => TimeSpan.FromSeconds(TotalDurationSeconds);
}
