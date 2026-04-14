namespace Atheres.Atlas.Domain.Entities;

/// <summary>
/// A fixed delivery destination (e.g., dispensary).
/// Imported from reference data. Orders are delivered to stores.
/// </summary>
public class Store
{
    public Guid   Id        { get; set; } = Guid.NewGuid();
    public Guid   CompanyId { get; set; }

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

    public bool IsActive    { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Company Company { get; set; } = null!;
    public ICollection<Order> Orders { get; set; } = [];

    public string FullAddress => $"{Address}, {City}, {State} {Zip}";
}
