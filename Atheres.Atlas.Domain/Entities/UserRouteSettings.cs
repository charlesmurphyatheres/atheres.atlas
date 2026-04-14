namespace Atheres.Atlas.Domain.Entities;

public class UserRouteSettings
{
    public Guid   Id        { get; set; } = Guid.NewGuid();
    public Guid   CompanyId { get; set; }

    /// <summary>Logical key — matches Truck.Id.ToString() for truck-specific settings.</summary>
    public string UserId { get; set; } = "default";

    // Depot / start location
    public string StartAddress { get; set; } = string.Empty;
    public string StartCity { get; set; } = string.Empty;
    public string StartState { get; set; } = string.Empty;
    public string StartZip { get; set; } = string.Empty;
    public double? StartLatitude { get; set; }
    public double? StartLongitude { get; set; }

    // Return / end location (can match start for round-trip)
    public string EndAddress { get; set; } = string.Empty;
    public string EndCity { get; set; } = string.Empty;
    public string EndState { get; set; } = string.Empty;
    public string EndZip { get; set; } = string.Empty;
    public double? EndLatitude { get; set; }
    public double? EndLongitude { get; set; }

    // Delivery window
    public TimeSpan DeliveryWindowStart { get; set; } = new TimeSpan(8, 0, 0);  // 8:00 AM
    public TimeSpan DeliveryWindowEnd { get; set; } = new TimeSpan(17, 0, 0);   // 5:00 PM

    // Confirmation deadline offset in hours before expected delivery
    public int ConfirmationDeadlineHours { get; set; } = 3;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Company? Company { get; set; }

    public string StartFullAddress => $"{StartAddress}, {StartCity}, {StartState} {StartZip}";
    public string EndFullAddress => $"{EndAddress}, {EndCity}, {EndState} {EndZip}";
}
