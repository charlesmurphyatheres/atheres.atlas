using Atheres.Atlas.Domain.Enums;
using Atheres.Atlas.Scheduling.Abstractions;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;

namespace Atheres.Atlas.Scheduling.Providers;

/// <summary>
/// Microsoft Bookings integration via Microsoft Graph. Each store supplies
/// its own Client Identifier, Client Secret, and Calendar Name (the
/// Bookings business display name).
///
/// <para>
/// The Azure AD tenant the credentials live in is read from the
/// app-wide <see cref="BookingsProviderOptions.TenantId"/>. The intended
/// deployment model is one tenant per Atlas customer, with separate Azure
/// app registrations per store inside it — the spec the user gave only
/// lists three per-store fields, so the tenant is configured once at the
/// host level rather than per-row.
/// </para>
///
/// <para>
/// <see cref="GetAvailabilityAsync"/> currently returns each weekday's
/// <c>businessHours</c> as a single slot per day. Subtracting booked
/// appointments to produce true free-time windows is left for the
/// business-logic pass.
/// </para>
/// </summary>
public sealed class BookingsSchedulingProvider : ISchedulingProvider
{
    private readonly BookingsProviderOptions _options;
    private readonly ILogger<BookingsSchedulingProvider> _log;

    public BookingsSchedulingProvider(
        IOptions<BookingsProviderOptions> options,
        ILogger<BookingsSchedulingProvider> log)
    {
        _options = options.Value;
        _log     = log;
    }

    public SchedulingMethod Method => SchedulingMethod.Booking;

    public bool IsConfigured(StoreSchedulingSettings settings)
        => !string.IsNullOrWhiteSpace(settings.BookingClientId)
        && !string.IsNullOrWhiteSpace(settings.BookingClientSecret)
        && !string.IsNullOrWhiteSpace(settings.BookingCalendarName);

    public async Task<IReadOnlyList<AvailabilitySlot>> GetAvailabilityAsync(
        StoreSchedulingSettings settings,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        CancellationToken ct = default)
    {
        if (!IsConfigured(settings))
            throw new SchedulingConfigurationException(
                "Microsoft Bookings scheduling requires Client Identifier, Client Secret, and Calendar Name.");

        if (string.IsNullOrWhiteSpace(_options.TenantId))
            throw new SchedulingConfigurationException(
                "Microsoft Bookings tenant is not configured. Set the 'BookingsTenantId' application setting.");

        var graph = BuildClient(settings);

        var businessesPage = await graph.Solutions.BookingBusinesses
            .GetAsync(cancellationToken: ct)
            .ConfigureAwait(false);

        var business = businessesPage?.Value?
            .FirstOrDefault(b => string.Equals(
                b.DisplayName, settings.BookingCalendarName, StringComparison.OrdinalIgnoreCase))
            ?? throw new SchedulingConfigurationException(
                $"No Microsoft Bookings calendar named '{settings.BookingCalendarName}' is visible to the supplied app credentials.");

        var hours = business.BusinessHours ?? new List<Microsoft.Graph.Models.BookingWorkHours>();
        var slots = new List<AvailabilitySlot>();

        for (var day = rangeStart.Date; day <= rangeEnd.Date; day = day.AddDays(1))
        {
            var match = hours.FirstOrDefault(h => DayOfWeekMatch(h.Day, day.DayOfWeek));
            if (match?.TimeSlots is null) continue;

            foreach (var window in match.TimeSlots)
            {
                if (window.StartTime is null || window.EndTime is null) continue;
                var start = day.Add(ToTimeSpan(window.StartTime.Value));
                var end   = day.Add(ToTimeSpan(window.EndTime.Value));
                slots.Add(new AvailabilitySlot(
                    Start: new DateTimeOffset(start, rangeStart.Offset),
                    End:   new DateTimeOffset(end,   rangeStart.Offset),
                    Label: business.DisplayName));
            }
        }

        return slots;
    }

    public Task<ScheduleResult> ScheduleAsync(
        StoreSchedulingSettings settings,
        ScheduleRequest request,
        CancellationToken ct = default)
    {
        // Real bookingAppointment creation lands in the business-logic pass.
        _log.LogInformation(
            "Microsoft Bookings ScheduleAsync stub invoked for order {OrderId} (store {Store}). Business logic pending.",
            request.OrderId, settings.StoreName);

        return Task.FromResult(new ScheduleResult(
            Success: true,
            ResultingOrderStatus: OrderStatus.Tentative,
            Diagnostic: "Microsoft Bookings appointment creation not yet implemented — order held as Tentative."));
    }

    // --------------------------------------------------------------------
    private GraphServiceClient BuildClient(StoreSchedulingSettings settings)
    {
        var credential = new ClientSecretCredential(
            tenantId:     _options.TenantId,
            clientId:     settings.BookingClientId,
            clientSecret: settings.BookingClientSecret);
        return new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
    }

    // Microsoft.Kiota.Abstractions.Time exposes Hour/Minute/Second but no
    // direct TimeSpan conversion — bridge by hand.
    private static TimeSpan ToTimeSpan(Microsoft.Kiota.Abstractions.Time t)
        => new(t.Hour, t.Minute, t.Second);

    private static bool DayOfWeekMatch(Microsoft.Graph.Models.DayOfWeekObject? graphDay, DayOfWeek systemDay)
        => graphDay switch
        {
            Microsoft.Graph.Models.DayOfWeekObject.Sunday    => systemDay == DayOfWeek.Sunday,
            Microsoft.Graph.Models.DayOfWeekObject.Monday    => systemDay == DayOfWeek.Monday,
            Microsoft.Graph.Models.DayOfWeekObject.Tuesday   => systemDay == DayOfWeek.Tuesday,
            Microsoft.Graph.Models.DayOfWeekObject.Wednesday => systemDay == DayOfWeek.Wednesday,
            Microsoft.Graph.Models.DayOfWeekObject.Thursday  => systemDay == DayOfWeek.Thursday,
            Microsoft.Graph.Models.DayOfWeekObject.Friday    => systemDay == DayOfWeek.Friday,
            Microsoft.Graph.Models.DayOfWeekObject.Saturday  => systemDay == DayOfWeek.Saturday,
            _ => false,
        };
}

/// <summary>
/// Host-level configuration for the Bookings provider. Bound to the
/// "Scheduling:Bookings" configuration section, with the tenant ID also
/// readable from the raw "BookingsTenantId" app setting (Functions host
/// flat-key convention).
/// </summary>
public sealed class BookingsProviderOptions
{
    public string? TenantId { get; set; }
}
