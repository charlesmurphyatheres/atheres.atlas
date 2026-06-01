namespace Atheres.Atlas.Scheduling.Abstractions;

/// <summary>
/// Inputs to <see cref="ISchedulingProvider.ScheduleAsync"/>. The Functions
/// caller assembles this from the finalized Route + Order data.
/// </summary>
public sealed record ScheduleRequest(
    Guid OrderId,
    Guid CompanyId,
    DateTimeOffset RequestedStart,
    DateTimeOffset RequestedEnd,
    string Subject,
    string Body,
    /// <summary>
    /// Tokenized Confirm / Cancel URLs used by the Email provider. Bookings
    /// and Calendly ignore them. Caller is responsible for token generation
    /// + persistence so the receiver-endpoint can authenticate the click.
    /// </summary>
    string? ConfirmUrl = null,
    string? CancelUrl  = null);
