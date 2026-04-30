namespace Atheres.Atlas.Domain.Entities;

public class Company
{
    public Guid   Id           { get; set; } = Guid.NewGuid();
    public string Name         { get; set; } = string.Empty;
    /// <summary>URL-safe identifier used in API routes (e.g. "atlas-freight").</summary>
    public string Slug         { get; set; } = string.Empty;
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    /// <summary>IANA timezone name, e.g. "America/Chicago".</summary>
    public string Timezone     { get; set; } = "America/Chicago";
    public bool   IsActive     { get; set; } = true;
    public DateTime CreatedAt  { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt  { get; set; } = DateTime.UtcNow;

    // ---- Company-wide routing policies -----------------------------------
    // These values are policy decisions made at the company level (operating
    // hours, dispatch ceiling) rather than per-truck preferences. The Settings
    // page edits them via the company-scoped settings endpoint.

    /// <summary>Earliest time of day deliveries may be scheduled. Defaults to 08:00.</summary>
    public TimeSpan DeliveryWindowStart { get; set; } = new TimeSpan(8, 0, 0);

    /// <summary>Latest time of day deliveries may be scheduled. Defaults to 17:00.</summary>
    public TimeSpan DeliveryWindowEnd   { get; set; } = new TimeSpan(17, 0, 0);

    /// <summary>
    /// Hard cap on the number of delivery stops per route. The optimizer
    /// chunks ready orders into batches no larger than this. Server clamps
    /// the value to <see cref="UserRouteSettings.MaxStopsHardCap"/>.
    /// </summary>
    public int MaxStopsPerRoute { get; set; } = UserRouteSettings.DefaultMaxStops;

    // Navigation
    public ICollection<Truck>            Trucks         { get; set; } = [];
    public ICollection<Hub>              Hubs           { get; set; } = [];
    public ICollection<Store>             Stores         { get; set; } = [];
    public ICollection<Warehouse>        Warehouses     { get; set; } = [];
    public ICollection<OrderBatch>       Batches        { get; set; } = [];
    public ICollection<Order>            Orders         { get; set; } = [];
    public ICollection<DeliveryRoute>    Routes         { get; set; } = [];
    public ICollection<UserRouteSettings> RouteSettings  { get; set; } = [];
    public ICollection<AuditLog>         AuditLogs      { get; set; } = [];
}
