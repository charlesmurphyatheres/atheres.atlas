using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atheres.Atlas.Domain.Enums;
using Atheres.Atlas.Scheduling.Abstractions;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Scheduling.Providers;

/// <summary>
/// Calendly Scheduled Events API integration. Uses the store's Personal
/// Access Token to discover the configured event type by name, then asks
/// Calendly for available start times in the requested window.
///
/// <see cref="ScheduleAsync"/> is intentionally a stub today — Calendly's
/// public API does not expose programmatic booking on behalf of the
/// invitee. Real bookings happen via the scheduling URL Calendly returns
/// from the availability call, which the Functions caller can pass on to
/// the store recipient. Wiring that up is left for the business-logic
/// pass the user mentioned.
/// </summary>
public sealed class CalendlySchedulingProvider : ISchedulingProvider
{
    private const string CalendlyApiBase = "https://api.calendly.com";

    private readonly HttpClient _http;
    private readonly ILogger<CalendlySchedulingProvider> _log;

    public CalendlySchedulingProvider(HttpClient http, ILogger<CalendlySchedulingProvider> log)
    {
        _http = http;
        _log  = log;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(CalendlyApiBase);
    }

    public SchedulingMethod Method => SchedulingMethod.Calendly;

    public bool IsConfigured(StoreSchedulingSettings settings)
        => !string.IsNullOrWhiteSpace(settings.CalendlyAccessToken)
        && !string.IsNullOrWhiteSpace(settings.CalendlyCalendarName);

    public async Task<IReadOnlyList<AvailabilitySlot>> GetAvailabilityAsync(
        StoreSchedulingSettings settings,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        CancellationToken ct = default)
    {
        if (!IsConfigured(settings))
            throw new SchedulingConfigurationException(
                "Calendly scheduling requires both a Personal Access Token and a Calendar Name.");

        var me = await GetAsync<CalendlyResource<CalendlyUser>>(
            settings, "/users/me", "fetching the current Calendly user", ct).ConfigureAwait(false);
        if (me is null)
            throw new SchedulingConfigurationException("Calendly returned an empty /users/me response.");

        var eventTypesUrl = $"/event_types?user={Uri.EscapeDataString(me.Resource.Uri)}&active=true";
        var eventTypes = await GetAsync<CalendlyCollection<CalendlyEventType>>(
            settings, eventTypesUrl, "listing event types", ct).ConfigureAwait(false);

        var eventType = eventTypes?.Collection
            .FirstOrDefault(et => string.Equals(
                et.Name, settings.CalendlyCalendarName, StringComparison.OrdinalIgnoreCase))
            ?? throw new SchedulingConfigurationException(
                $"No active Calendly event type named '{settings.CalendlyCalendarName}' on this account.");

        // Calendly /event_type_available_times caps the range at 7 days; if
        // the caller asked for longer we clip — the admin UI's "Show
        // Availability" button uses exactly the next 7 days.
        var clippedEnd = rangeEnd > rangeStart.AddDays(7) ? rangeStart.AddDays(7) : rangeEnd;
        var slotsUrl = "/event_type_available_times"
                     + $"?event_type={Uri.EscapeDataString(eventType.Uri)}"
                     + $"&start_time={Uri.EscapeDataString(rangeStart.UtcDateTime.ToString("o"))}"
                     + $"&end_time={Uri.EscapeDataString(clippedEnd.UtcDateTime.ToString("o"))}";

        var page = await GetAsync<CalendlyCollection<CalendlySlot>>(
            settings, slotsUrl, "fetching availability", ct).ConfigureAwait(false);
        if (page is null) return Array.Empty<AvailabilitySlot>();

        var duration = TimeSpan.FromMinutes(eventType.Duration > 0 ? eventType.Duration : 30);
        return page.Collection
            .Select(s => new AvailabilitySlot(
                Start: s.StartTime,
                End:   s.StartTime + duration,
                Label: eventType.Name,
                ProviderRef: s.SchedulingUrl))
            .ToArray();
    }

    public Task<ScheduleResult> ScheduleAsync(
        StoreSchedulingSettings settings,
        ScheduleRequest request,
        CancellationToken ct = default)
    {
        // Calendly's public API doesn't book on behalf of the invitee —
        // bookings happen at the invitee-facing scheduling_url. Until the
        // business-logic pass decides how to surface that URL (email, push
        // to store dashboard, etc.) this just records intent.
        _log.LogInformation(
            "Calendly ScheduleAsync stub invoked for order {OrderId} (store {Store}). Business logic pending.",
            request.OrderId, settings.StoreName);

        return Task.FromResult(new ScheduleResult(
            Success: true,
            ResultingOrderStatus: OrderStatus.Tentative,
            Diagnostic: "Calendly booking flow not yet implemented — order held as Tentative."));
    }

    // --------------------------------------------------------------------
    private async Task<T?> GetAsync<T>(
        StoreSchedulingSettings settings,
        string url,
        string actionDescription,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.CalendlyAccessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new SchedulingConfigurationException(
                $"Calendly API error while {actionDescription}: {(int)res.StatusCode} {res.ReasonPhrase}. Body: {Truncate(body, 400)}");
        }
        return await res.Content.ReadFromJsonAsync<T>(JsonOpts, ct).ConfigureAwait(false);
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    // ---- Calendly DTO shapes (only the fields we read) ----
    private sealed record CalendlyResource<T>(T Resource);
    private sealed record CalendlyCollection<T>(IReadOnlyList<T> Collection);

    private sealed record CalendlyUser(
        [property: JsonPropertyName("uri")]  string Uri,
        [property: JsonPropertyName("name")] string Name);

    private sealed record CalendlyEventType(
        [property: JsonPropertyName("uri")]      string Uri,
        [property: JsonPropertyName("name")]     string Name,
        [property: JsonPropertyName("duration")] int Duration);

    private sealed record CalendlySlot(
        [property: JsonPropertyName("start_time")]     DateTimeOffset StartTime,
        [property: JsonPropertyName("scheduling_url")] string? SchedulingUrl);
}
