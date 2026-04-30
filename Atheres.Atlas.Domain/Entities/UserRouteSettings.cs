namespace Atheres.Atlas.Domain.Entities;

/// <summary>
/// Per-truck routing preferences. Company-wide values (delivery window,
/// max stops per route) live on <see cref="Company"/> instead — those are
/// policy decisions made for the whole fleet rather than per-truck.
/// Start/end physical locations are not stored here either; every route
/// begins and ends at the truck's home hub.
/// </summary>
public class UserRouteSettings
{
    public Guid   Id        { get; set; } = Guid.NewGuid();
    public Guid   CompanyId { get; set; }

    /// <summary>Logical key — matches Truck.Id.ToString() for truck-specific settings.</summary>
    public string UserId { get; set; } = "default";

    // Confirmation deadline offset in hours before expected delivery
    public int ConfirmationDeadlineHours { get; set; } = 3;

    /// <summary>
    /// Extra idle minutes the van spends per delivery stop (driver break,
    /// paperwork, dock waiting), added to the per-stop service time when
    /// computing ETAs. Does NOT apply to the route's start or end at the
    /// home hub — those are not stored as RouteStop rows.
    /// </summary>
    public int WaitMinutesPerStop { get; set; } = 0;

    /// <summary>Absolute maximum stops per route enforced server-side.</summary>
    public const int MaxStopsHardCap = 20;

    /// <summary>Default cap applied to a new <see cref="Company"/> row.</summary>
    public const int DefaultMaxStops = 12;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Company? Company { get; set; }
}
