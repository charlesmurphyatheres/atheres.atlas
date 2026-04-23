namespace Atheres.Atlas.Domain.Entities;

/// <summary>
/// Per-truck routing preferences. Start/end physical locations are no longer
/// stored here — every route begins and ends at the truck's home hub.
/// </summary>
public class UserRouteSettings
{
    public Guid   Id        { get; set; } = Guid.NewGuid();
    public Guid   CompanyId { get; set; }

    /// <summary>Logical key — matches Truck.Id.ToString() for truck-specific settings.</summary>
    public string UserId { get; set; } = "default";

    // Delivery window
    public TimeSpan DeliveryWindowStart { get; set; } = new TimeSpan(8, 0, 0);  // 8:00 AM
    public TimeSpan DeliveryWindowEnd { get; set; } = new TimeSpan(17, 0, 0);   // 5:00 PM

    // Confirmation deadline offset in hours before expected delivery
    public int ConfirmationDeadlineHours { get; set; } = 3;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Company? Company { get; set; }
}
