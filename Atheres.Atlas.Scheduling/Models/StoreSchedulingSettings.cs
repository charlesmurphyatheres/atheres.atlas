using Atheres.Atlas.Domain.Enums;

namespace Atheres.Atlas.Scheduling.Abstractions;

/// <summary>
/// Snapshot of the scheduling-relevant fields on a Store, passed in to
/// every provider call. A separate record (rather than the Store entity
/// itself) so the Scheduling project doesn't take a Data layer reference
/// and so callers can construct fakes in tests without an EF context.
/// </summary>
public sealed record StoreSchedulingSettings(
    Guid StoreId,
    string StoreName,
    SchedulingMethod Method,
    string? BookingClientId,
    string? BookingClientSecret,
    string? BookingCalendarName,
    string? CalendlyAccessToken,
    string? CalendlyCalendarName,
    string? EmailRecipientsCsv,
    string? StoreEmail,
    string? StoreTimeZoneId = null)
{
    /// <summary>Split the email recipients CSV into trimmed addresses.</summary>
    public IReadOnlyList<string> EmailRecipients =>
        string.IsNullOrWhiteSpace(EmailRecipientsCsv)
            ? Array.Empty<string>()
            : EmailRecipientsCsv
                .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim())
                .Where(e => e.Length > 0)
                .ToArray();
}
