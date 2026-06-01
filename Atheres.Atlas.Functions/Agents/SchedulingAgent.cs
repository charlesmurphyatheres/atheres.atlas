using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atheres.Atlas.Data;
using Atheres.Atlas.Domain.Enums;
using Atheres.Atlas.Scheduling.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Functions.Agents;

/// <summary>
/// Endpoints powering the per-store Scheduling sub-interface in the Admin
/// Panel plus the email-link callback that flips an order between
/// <see cref="OrderStatus.Confirmed"/> and <see cref="OrderStatus.Standby"/>
/// when a store recipient clicks Confirm or Cancel in a scheduling email.
///
/// <para>
/// Availability is queried against credentials passed in the request body
/// (not against persisted Store columns) so an operator can test their
/// inputs without saving them first.
/// </para>
/// </summary>
public class SchedulingAgent
{
    private readonly AtlasDbContext _db;
    private readonly ISchedulingProviderFactory _factory;
    private readonly ILogger<SchedulingAgent> _log;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public SchedulingAgent(
        AtlasDbContext db,
        ISchedulingProviderFactory factory,
        ILogger<SchedulingAgent> log)
    {
        _db      = db;
        _factory = factory;
        _log     = log;
    }

    /// <summary>
    /// POST /api/stores/{id}/scheduling/availability — preview the next
    /// 7 days of available delivery windows using the credentials in the
    /// request body. Returns 200 with the slot list, 400 for missing
    /// credentials, 502 for upstream provider errors.
    /// </summary>
    [Function("scheduling-availability")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> Availability(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "stores/{id:guid}/scheduling/availability")]
        HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        AvailabilityRequestDto? dto;
        try { dto = await JsonSerializer.DeserializeAsync<AvailabilityRequestDto>(req.Body, _json, ct); }
        catch { return new BadRequestObjectResult(new { error = "Invalid JSON." }); }
        if (dto is null) return new BadRequestObjectResult(new { error = "Empty body." });

        var store = await _db.Stores.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (store is null) return new NotFoundObjectResult(new { error = "Store not found." });

        var settings = new StoreSchedulingSettings(
            StoreId:               store.Id,
            StoreName:             store.Name,
            Method:                dto.Method,
            BookingClientId:       dto.BookingClientId,
            BookingClientSecret:   dto.BookingClientSecret,
            BookingCalendarName:   dto.BookingCalendarName,
            CalendlyAccessToken:   dto.CalendlyAccessToken,
            CalendlyCalendarName:  dto.CalendlyCalendarName,
            EmailRecipientsCsv:    dto.EmailRecipients,
            StoreEmail:            store.Email);

        var provider = _factory.Get(settings.Method);
        var now = DateTimeOffset.UtcNow;

        try
        {
            var slots = await provider.GetAvailabilityAsync(settings, now, now.AddDays(7), ct);
            return new OkObjectResult(new
            {
                method = settings.Method.ToString(),
                slots = slots.Select(s => new
                {
                    start = s.Start,
                    end   = s.End,
                    label = s.Label,
                    providerRef = s.ProviderRef,
                }),
            });
        }
        catch (SchedulingConfigurationException ex)
        {
            return new BadRequestObjectResult(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Availability lookup failed for store {Store} via {Method}", store.Id, settings.Method);
            return new ObjectResult(new { error = $"Provider error: {ex.Message}" })
            { StatusCode = (int)HttpStatusCode.BadGateway };
        }
    }

    /// <summary>
    /// GET /api/orders/{id}/scheduling-response?token=X&amp;action=confirm|cancel —
    /// the click target for the Confirm/Cancel buttons in the email-method
    /// scheduling notice. Validates the per-order token, flips status, and
    /// returns a minimal HTML acknowledgement.
    /// </summary>
    [Function("scheduling-email-respond")]
    public async Task<IActionResult> EmailRespond(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "orders/{id:guid}/scheduling-response")]
        HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        var token  = req.Query["token"].ToString();
        var action = req.Query["action"].ToString().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(token) || (action is not "confirm" and not "cancel"))
            return HtmlResult(HttpStatusCode.BadRequest,
                "Invalid link. Required query parameters: token and action=confirm|cancel.");

        // Tenant-filter is bypassed deliberately: the email recipient does
        // not carry a JWT and the per-order token is the credential.
        var order = await _db.Orders.IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null) return HtmlResult(HttpStatusCode.NotFound, "Order not found.");

        if (string.IsNullOrEmpty(order.ConfirmationToken)
            || !string.Equals(order.ConfirmationToken, token, StringComparison.Ordinal))
            return HtmlResult(HttpStatusCode.Unauthorized, "This response link is no longer valid.");

        // Idempotency: a second click on the same link is a no-op rather
        // than an error — recipients commonly refresh / forward emails.
        var nextStatus = action == "confirm" ? OrderStatus.Confirmed : OrderStatus.Standby;
        if (order.Status == nextStatus)
            return HtmlResult(HttpStatusCode.OK,
                $"Thanks — this delivery is already marked <strong>{nextStatus}</strong>.");

        order.Status      = nextStatus;
        order.ConfirmedAt = action == "confirm" ? DateTime.UtcNow : order.ConfirmedAt;
        order.UpdatedAt   = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Order {Order} flipped to {Status} via scheduling-email click ({Action}).",
            order.Id, nextStatus, action);

        var verb = action == "confirm" ? "confirmed" : "cancelled";
        return HtmlResult(HttpStatusCode.OK,
            $"Thanks — this delivery has been <strong>{verb}</strong>.");
    }

    private static IActionResult HtmlResult(HttpStatusCode status, string innerHtml)
    {
        var html = $@"<!doctype html><html><body style=""font-family:system-ui,Segoe UI,Arial;padding:40px;color:#111"">
<p style=""font-size:16px"">{innerHtml}</p>
</body></html>";
        return new ContentResult { Content = html, ContentType = "text/html", StatusCode = (int)status };
    }
}

public class AvailabilityRequestDto
{
    public SchedulingMethod Method { get; set; } = SchedulingMethod.None;
    public string? BookingClientId      { get; set; }
    public string? BookingClientSecret  { get; set; }
    public string? BookingCalendarName  { get; set; }
    public string? CalendlyAccessToken  { get; set; }
    public string? CalendlyCalendarName { get; set; }
    public string? EmailRecipients      { get; set; }
}
