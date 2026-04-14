using Atheres.Atlas.Domain.Enums;

namespace Atheres.Atlas.Domain.Entities;

/// <summary>
/// A batch groups orders for pickup from a warehouse and delivery from a hub.
/// When a pickup date is assigned, route optimization runs automatically
/// and creates a delivery itinerary.
/// </summary>
public class OrderBatch
{
    public Guid   Id        { get; set; } = Guid.NewGuid();
    public Guid   CompanyId { get; set; }
    public string Name      { get; set; } = string.Empty; // e.g. "ABC Dist - Apr 10"

    public Guid   HubId       { get; set; }
    public Guid   WarehouseId { get; set; }
    public Guid?  TruckId     { get; set; }

    public BatchStatus Status { get; set; } = BatchStatus.Open;

    /// <summary>When set, triggers route optimization and itinerary creation.</summary>
    public DateTime? PickupDate { get; set; }

    /// <summary>FK to the delivery route created after optimization.</summary>
    public Guid? RouteId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Company       Company   { get; set; } = null!;
    public Hub           Hub       { get; set; } = null!;
    public Warehouse     Warehouse { get; set; } = null!;
    public Truck?        Truck     { get; set; }
    public DeliveryRoute? Route    { get; set; }
    public ICollection<Order> Orders { get; set; } = [];
}
