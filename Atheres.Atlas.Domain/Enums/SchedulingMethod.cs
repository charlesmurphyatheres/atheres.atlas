namespace Atheres.Atlas.Domain.Enums;

/// <summary>
/// How the system should arrange the delivery time with a store once a route
/// for it is built. Selected per-store in the Admin Panel; the Scheduling
/// project's provider factory dispatches on this value.
/// </summary>
public enum SchedulingMethod
{
    /// <summary>No outbound scheduling. Optimized routes are assumed
    /// confirmed until an administrator changes them manually.</summary>
    None = 0,

    /// <summary>Microsoft Bookings via Microsoft Graph. Requires per-store
    /// Client Identifier, Client Secret, and Calendar Name.</summary>
    Booking = 1,

    /// <summary>Calendly Scheduled Events API. Requires per-store Personal
    /// Access Token and Calendar Name.</summary>
    Calendly = 2,

    /// <summary>Email-based confirmation. Routes assumed confirmed
    /// (Tentative status) until the recipient clicks Confirm or Cancel
    /// in the email body.</summary>
    Email = 3,
}
