namespace Atheres.Atlas.Domain.Entities;

/// <summary>
/// A client / supplier location where goods are picked up.
/// Typically a few dozen per company. May appear as the first
/// stop on a route for truck loading.
/// </summary>
public class Warehouse
{
    public Guid   Id        { get; set; } = Guid.NewGuid();
    public Guid   CompanyId { get; set; }

    public string BusinessName       { get; set; } = string.Empty;
    public string? AlternateName     { get; set; }
    public string Address            { get; set; } = string.Empty;
    public string City               { get; set; } = string.Empty;
    public string State              { get; set; } = string.Empty;
    public string Zip                { get; set; } = string.Empty;
    public string? LicenseNumber     { get; set; }
    public string? LegacyLicenseNumber { get; set; }

    public double? Latitude  { get; set; }
    public double? Longitude { get; set; }
    public string? FormattedAddress { get; set; }

    // Tentative weekly pickup schedule (nullable = no standing pickup that day)
    public TimeSpan? MondayPickupTime    { get; set; }
    public TimeSpan? TuesdayPickupTime   { get; set; }
    public TimeSpan? WednesdayPickupTime { get; set; }
    public TimeSpan? ThursdayPickupTime  { get; set; }
    public TimeSpan? FridayPickupTime    { get; set; }
    public TimeSpan? SaturdayPickupTime  { get; set; }
    public TimeSpan? SundayPickupTime    { get; set; }

    /// <summary>Returns the tentative pickup time for a given day, or null if none scheduled.</summary>
    public TimeSpan? GetPickupTimeForDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday    => MondayPickupTime,
        DayOfWeek.Tuesday   => TuesdayPickupTime,
        DayOfWeek.Wednesday => WednesdayPickupTime,
        DayOfWeek.Thursday  => ThursdayPickupTime,
        DayOfWeek.Friday    => FridayPickupTime,
        DayOfWeek.Saturday  => SaturdayPickupTime,
        DayOfWeek.Sunday    => SundayPickupTime,
        _ => null
    };

    public bool IsActive    { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Company Company { get; set; } = null!;
    public ICollection<Order> Orders { get; set; } = [];

    public string FullAddress => $"{Address}, {City}, {State} {Zip}";
}
