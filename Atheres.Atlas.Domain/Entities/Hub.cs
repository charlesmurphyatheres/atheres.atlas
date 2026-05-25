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

    public bool IsActive    { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Company Company { get; set; } = null!;
    public ICollection<DeliveryRoute> Routes { get; set; } = [];

    public string FullAddress => $"{Address}, {City}, {State} {Zip}";
}
