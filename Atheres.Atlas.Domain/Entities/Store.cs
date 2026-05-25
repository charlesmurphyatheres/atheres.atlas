namespace Atheres.Atlas.Domain.Entities;

/// <summary>
/// A fixed delivery destination (e.g., dispensary).
/// Imported from reference data. Orders are delivered to stores.
/// </summary>
public class Store
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The warehouse/supplier this store is a customer of (e.g., "Echelon", "Six Labs").</summary>
    public string Customer       { get; set; } = string.Empty;
    public string Name           { get; set; } = string.Empty;
    public string Address        { get; set; } = string.Empty;
    public string City           { get; set; } = string.Empty;
    public string State          { get; set; } = "IL";
    public string Zip            { get; set; } = string.Empty;
    public string? County        { get; set; }
    public string? Region        { get; set; }
    public string? LicenseNumber { get; set; }
    public string? Email         { get; set; }
    public string? Phone         { get; set; }

    public double? Latitude  { get; set; }
    public double? Longitude { get; set; }
    public string? FormattedAddress { get; set; }

    /// <summary>Optional delivery zone assignment. Linked at import time via
    /// LicenseNumber → zones.csv. NULL when the store hasn't been mapped yet.</summary>
    public Guid? ZoneId { get; set; }

    public bool IsActive    { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    /// <summary>Companies that can dispatch to this store. A store may serve
    /// more than one carrier — e.g. a dispensary that buys from suppliers
    /// served by different delivery companies — so this is many-to-many.</summary>
    public ICollection<Company> Companies { get; set; } = [];
    public Zone?   Zone    { get; set; }
    public ICollection<Order> Orders { get; set; } = [];

    public string FullAddress => $"{Address}, {City}, {State} {Zip}";
}
