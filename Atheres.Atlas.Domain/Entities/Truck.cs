namespace Atheres.Atlas.Domain.Entities;

/// <summary>
/// A delivery vehicle belonging to a company.
/// Each truck has its own depot settings and can be assigned a driver.
/// </summary>
public class Truck
{
    public Guid   Id        { get; set; } = Guid.NewGuid();
    public Guid   CompanyId { get; set; }
    public string Name      { get; set; } = string.Empty; // e.g. "Ford Transit 2500"

    /// <summary>License plate number.</summary>
    public string? LicensePlate { get; set; }

    /// <summary>Hub where this truck is based.</summary>
    public Guid? HubId { get; set; }

    /// <summary>Last-known or manually-set physical location of the vehicle.
    /// Separate from HubId — a van may be on the road or parked away from its home hub.</summary>
    public string?   CurrentLocationAddress    { get; set; }
    public double?   CurrentLocationLatitude   { get; set; }
    public double?   CurrentLocationLongitude  { get; set; }
    public DateTime? CurrentLocationUpdatedAt  { get; set; }

    /// <summary>FK to AspNetUsers — the driver currently assigned to this truck.</summary>
    public string? AssignedDriverId { get; set; }

    /// <summary>FK to UserRouteSettings — depot and delivery window for this truck.</summary>
    public Guid? RouteSettingsId { get; set; }

    public bool IsActive    { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Company            Company       { get; set; } = null!;
    public Hub?               Hub           { get; set; }
    public UserRouteSettings? RouteSettings { get; set; }
    public ICollection<DeliveryRoute> Routes { get; set; } = [];
}
