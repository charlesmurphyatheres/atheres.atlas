namespace Atheres.Atlas.Scheduling.Abstractions;

/// <summary>
/// A bookable window returned by <see cref="ISchedulingProvider.GetAvailabilityAsync"/>.
/// The optional fields are populated when the provider exposes them
/// (Calendly returns the scheduling URL; Bookings returns a staff-member
/// id; Email/None leave them null).
/// </summary>
public sealed record AvailabilitySlot(
    DateTimeOffset Start,
    DateTimeOffset End,
    string? Label = null,
    string? ProviderRef = null);
