namespace Atheres.Atlas.Domain.Entities;

/// <summary>
/// A company home base / operations center. Routes start and end at a hub.
/// Orders that cannot be delivered are brought back to the hub for next-day routing.
/// A company can have multiple hubs (e.g., regional locations).
/// </summary>
public class Hub
{
    public Guid   Id        { get; set; } = Guid.NewGuid();
    public Guid   CompanyId { get; set; }
    public string Name      { get; set; } = string.Empty; // e.g. "Downtown Hub", "East Regional"

    public string Address   { get; set; } = string.Empty;
    public string City      { get; set; } = string.Empty;
    public string State     { get; set; } = string.Empty;
    public string Zip       { get; set; } = string.Empty;

    public double? Latitude  { get; set; }
    public double? Longitude { get; set; }
    public string? FormattedAddress { get; set; }

    /// <summary>
    /// Minutes that elapse between a pickup van's arrival at this hub and the
    /// dispatch of the per-zone delivery vans. Models the time hub operators
    /// need to break down a pickup load and stage it by zone. Configurable
    /// per hub because larger / busier hubs sort more slowly. Default 30.
    /// </summary>
    public int SortingWaitMinutes { get; set; } = 30;

    /// <summary>
    /// True for the single hub that acts as the consolidation / transfer site
    /// for the company. Pickup vans bring warehouse loads here to be sorted by
    /// zone before per-zone delivery vans dispatch. Only transfer-site hubs may
    /// receive warehouse pickups when a batch spans multiple delivery zones —
    /// every other hub is delivery-only and cannot perform the warehouse →
    /// hub-sort leg. Exactly one hub per company is expected to be flagged.
    /// </summary>
    public bool IsTransferSite { get; set; } = false;

    /// <summary>
    /// Whether this hub physically sits within the Chicago-Naperville-Elgin
    /// MSA. Drives the non-ChicagoLand bypass: a pickup at a non-ChicagoLand
    /// warehouse whose orders all land in non-ChicagoLand zones skips the
    /// transfer site entirely (it is too far away to be worth the detour).
    /// Future routing rules may also consult this flag — keep accurate per
    /// hub even though the transfer-site flag is the only consumer today.
    /// </summary>
    public bool IsChicagoLand { get; set; } = false;

    public bool IsActive    { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Company Company { get; set; } = null!;
    public ICollection<DeliveryRoute> Routes { get; set; } = [];

    public string FullAddress => $"{Address}, {City}, {State} {Zip}";
}
