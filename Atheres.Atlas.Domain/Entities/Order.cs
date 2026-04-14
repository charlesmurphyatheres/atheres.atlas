using Atheres.Atlas.Domain.Enums;

namespace Atheres.Atlas.Domain.Entities;

public class Order
{
    public Guid Id        { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }

    // License-number lookups (used to match Warehouse and Store on ingestion)
    public string? WarehouseLicenseNumber { get; set; }
    public string? StoreLicenseNumber { get; set; }

    // Store details (denormalized from Store entity on ingestion)
    public string StoreName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Zip { get; set; } = string.Empty;
    public string County { get; set; } = string.Empty;
    public string LicenseNumber { get; set; } = string.Empty;
    public string District { get; set; } = string.Empty;
    public string Zone { get; set; } = string.Empty;
    public DateTime OrderDate { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }

    // Geocoded coordinates (populated from Store on ingestion)
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? FormattedAddress { get; set; }

    // Delivery logistics
    public OrderStatus Status { get; set; } = OrderStatus.Ordered;
    public DateTime? ExpectedDeliveryDate { get; set; }
    public DateTime? ConfirmationDeadline { get; set; }
    public int RescheduleCount { get; set; } = 0;

    // Confirmation tracking
    public string? ConfirmationToken { get; set; }
    public DateTime? ConfirmedAt { get; set; }

    // Store (delivery destination)
    public Guid? StoreId { get; set; }

    // Batch assignment
    public Guid? BatchId { get; set; }

    // Source warehouse (where goods are picked up — null if already at hub)
    public Guid? WarehouseId { get; set; }

    /// <summary>True when the order is already staged at the hub and does not need a warehouse pickup.</summary>
    public bool IsAtHub { get; set; } = false;

    /// <summary>Set when an undelivered order is brought back to the hub for next-day delivery.</summary>
    public DateTime? DeferredAt { get; set; }

    /// <summary>The route that originally carried this order before it was deferred.</summary>
    public Guid? OriginalRouteId { get; set; }

    // Route assignment
    public Guid? RouteId { get; set; }
    public int? StopSequence { get; set; }

    // Metadata
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? ValidationErrors { get; set; }
    public string? Notes { get; set; }

    // Navigation properties
    public Company?       Company   { get; set; }
    public Store?         Store     { get; set; }
    public OrderBatch?    Batch     { get; set; }
    public Warehouse?     Warehouse { get; set; }
    public DeliveryRoute? Route     { get; set; }
    public ICollection<OrderItem> Items { get; set; } = new List<OrderItem>();
    public ICollection<DeliveryConfirmation> Confirmations { get; set; } = new List<DeliveryConfirmation>();
    public ICollection<AuditLog> AuditLogs { get; set; } = new List<AuditLog>();

    public string FullAddress => $"{Address}, {City}, {State} {Zip}";
}
