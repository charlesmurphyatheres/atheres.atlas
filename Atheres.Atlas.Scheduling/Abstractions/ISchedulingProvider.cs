using Atheres.Atlas.Domain.Enums;

namespace Atheres.Atlas.Scheduling.Abstractions;

/// <summary>
/// Facade over a single per-store scheduling method (Microsoft Bookings,
/// Calendly, Email, or None). One implementation per <see cref="SchedulingMethod"/>
/// value; <see cref="ISchedulingProviderFactory"/> picks the right one
/// from a <see cref="StoreSchedulingSettings"/> snapshot.
///
/// Business logic (when to schedule, batching, retry policy) lives in the
/// Functions project that calls into these providers. This contract is
/// deliberately narrow: "can the store take a delivery at time X, and if
/// I commit to it, will you reflect that on their calendar?"
/// </summary>
public interface ISchedulingProvider
{
    /// <summary>Which method this provider implements.</summary>
    SchedulingMethod Method { get; }

    /// <summary>
    /// True if the supplied settings have the credentials this provider
    /// needs. Used to gate the "Show Availability" button in the admin UI
    /// without making the user save first.
    /// </summary>
    bool IsConfigured(StoreSchedulingSettings settings);

    /// <summary>
    /// Returns available delivery windows between <paramref name="rangeStart"/>
    /// and <paramref name="rangeEnd"/>. Implementations may return an empty
    /// list when the calendar is fully booked; throw
    /// <see cref="SchedulingConfigurationException"/> when credentials are
    /// missing or rejected.
    /// </summary>
    Task<IReadOnlyList<AvailabilitySlot>> GetAvailabilityAsync(
        StoreSchedulingSettings settings,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        CancellationToken ct = default);

    /// <summary>
    /// Books / records / sends the supplied slot. The Functions side calls
    /// this when a route is finalized. Returns a provider-specific result
    /// (booking id, calendly event uri, email message id, etc.) plus the
    /// status the order should land in.
    /// </summary>
    Task<ScheduleResult> ScheduleAsync(
        StoreSchedulingSettings settings,
        ScheduleRequest request,
        CancellationToken ct = default);
}
