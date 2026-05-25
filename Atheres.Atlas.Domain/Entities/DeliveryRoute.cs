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

    /// <summary>Classifies this route's role in the pickup → hub-sort → zone-delivery
    /// pipeline. <see cref="RouteType.Legacy"/> is the default for pre-existing rows.</summary>
    public RouteType RouteType { get; set; } = RouteType.Legacy;

    /// <summary>For <see cref="RouteType.ZonedDelivery"/> and <see cref="RouteType.DirectDelivery"/>,
    /// the single zone every stop on this route lives in. Null for pickup-only and legacy routes.</summary>
    public Guid? ZoneId { get; set; }

    /// <summary>Estimated wall-clock time the pickup van will arrive at the hub. Populated
    /// when a Pickup route is created so paired ZonedDelivery routes can compute their
    /// own start time (= HubArrivalTime + Hub.SortingWaitMinutes).</summary>
    public DateTime? HubArrivalTime { get; set; }

    /// <summary>For delivery routes that depart after a hub sort, the earliest moment
    /// the delivery van may leave (= paired pickup's HubArrivalTime + sort wait). For
    /// <see cref="RouteType.DirectDelivery"/> this equals the warehouse pickup time.</summary>
    public DateTime? ScheduledDepartTime { get; set; }

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
