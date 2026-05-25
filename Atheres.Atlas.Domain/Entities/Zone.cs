namespace Atheres.Atlas.Domain.Entities;

/// <summary>
/// A delivery zone within a District. Zone codes are hierarchical strings such
/// as "1.0", "5.11", "17.4" — kept verbatim from the source data so sub-zones
/// (e.g., 5.11 vs 5.1) remain distinguishable. Seeded from data/zones.csv by
/// scripts/import-data.ps1.
/// </summary>
public class Zone
{
    public Guid   Id         { get; set; } = Guid.NewGuid();
    public Guid   CompanyId  { get; set; }
    public Guid   DistrictId { get; set; }

    /// <summary>Zone identifier as it appears in the source data ("1.0", "5.11", ...).</summary>
    public string Code       { get; set; } = string.Empty;

    public bool     IsActive  { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public District District { get; set; } = null!;
    public ICollection<Store> Stores { get; set; } = [];
}
