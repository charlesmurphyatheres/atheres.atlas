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
