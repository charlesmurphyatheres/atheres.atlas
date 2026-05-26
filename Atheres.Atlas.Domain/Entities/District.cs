namespace Atheres.Atlas.Domain.Entities;

/// <summary>
/// A geographic district within a company (e.g., "Bloomington" #1, "Peoria" #10).
/// Districts group one or more Zones; each Store belongs to a Zone, and through
/// it a District. Seeded from data/zones.csv by scripts/import-data.ps1.
/// </summary>
public class District
{
    public Guid   Id        { get; set; } = Guid.NewGuid();
    public Guid   CompanyId { get; set; }

    /// <summary>District code as it appears in the source data (e.g., 1, 10, 15).</summary>
    public int    Number    { get; set; }
    public string Name      { get; set; } = string.Empty;

    /// <summary>
    /// True when this district sits inside the Chicago-Naperville-Elgin MSA
    /// (historically District 5). Drives the order-side ChicagoLand check
    /// through Order → Store → Zone → District: a batch whose deliveries all
    /// resolve to IsChicagoLand=false districts is eligible for the
    /// transfer-site bypass when paired with a non-ChicagoLand warehouse.
    /// Stored as a real column (not derived from Number) so administrators
    /// can adjust MSA membership without a code change.
    /// </summary>
    public bool IsChicagoLand { get; set; } = false;

    public bool     IsActive  { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Zone> Zones { get; set; } = [];
}
