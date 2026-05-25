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

    public bool     IsActive  { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Zone> Zones { get; set; } = [];
}
