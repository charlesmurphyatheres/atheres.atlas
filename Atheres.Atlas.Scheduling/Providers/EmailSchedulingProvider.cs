using Atheres.Atlas.Domain.Enums;
using Atheres.Atlas.Scheduling.Abstractions;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Scheduling.Providers;

/// <summary>
/// Email-based scheduling. <see cref="GetAvailabilityAsync"/> has no real
/// API to query, so it returns a synthetic "any business-hours weekday"
/// preview the admin UI can render while editing settings.
///
/// <see cref="ScheduleAsync"/> hands the email payload to <see cref="ISchedulingEmailSender"/>
/// (implemented in the Functions project on top of SendGrid) and reports
/// the order as <see cref="OrderStatus.Tentative"/> — the route runs as
/// planned unless / until a recipient clicks the Cancel link in the email
/// (which flips it to <see cref="OrderStatus.Standby"/> via the
/// /api/orders/{id}/respond endpoint).
/// </summary>
public sealed class EmailSchedulingProvider : ISchedulingProvider
{
    private readonly ISchedulingEmailSender _sender;
    private readonly ILogger<EmailSchedulingProvider> _log;

    public EmailSchedulingProvider(
        ISchedulingEmailSender sender,
        ILogger<EmailSchedulingProvider> log)
    {
        _sender = sender;
        _log    = log;
    }

    public SchedulingMethod Method => SchedulingMethod.Email;

    public bool IsConfigured(StoreSchedulingSettings settings)
        => settings.EmailRecipients.Count > 0;

    public Task<IReadOnlyList<AvailabilitySlot>> GetAvailabilityAsync(
        StoreSchedulingSettings settings,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        CancellationToken ct = default)
    {
        // Email scheduling has no remote calendar to inspect. Surface a
        // weekday business-hours preview so the admin UI's "Show
        // Availability" button still has something useful to display.
        var slots = new List<AvailabilitySlot>();
        for (var day = rangeStart.Date; day <= rangeEnd.Date; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var open  = new DateTimeOffset(day.AddHours(9),  rangeStart.Offset);
            var close = new DateTimeOffset(day.AddHours(17), rangeStart.Offset);
            slots.Add(new AvailabilitySlot(open, close, Label: "Business hours (email)"));
        }
        return Task.FromResult<IReadOnlyList<AvailabilitySlot>>(slots);
    }

    public async Task<ScheduleResult> ScheduleAsync(
        StoreSchedulingSettings settings,
        ScheduleRequest request,
        CancellationToken ct = default)
    {
        var recipients = settings.EmailRecipients;
        if (recipients.Count == 0)
            throw new SchedulingConfigurationException(
                $"Store '{settings.StoreName}' is set to Email scheduling but has no recipient addresses configured.");

        if (string.IsNullOrWhiteSpace(request.ConfirmUrl) || string.IsNullOrWhiteSpace(request.CancelUrl))
            throw new SchedulingConfigurationException(
                "Email scheduling requires both ConfirmUrl and CancelUrl on the ScheduleRequest.");

        var html = BuildHtml(settings, request);
        await _sender.SendAsync(
            recipients,
            request.Subject,
            html,
            ct).ConfigureAwait(false);

        _log.LogInformation(
            "Email scheduling notice sent to {Count} recipient(s) for order {OrderId} at store {Store}.",
            recipients.Count, request.OrderId, settings.StoreName);

        return new ScheduleResult(
            Success: true,
            ResultingOrderStatus: OrderStatus.Tentative,
            Diagnostic: $"Email sent to {recipients.Count} recipient(s); awaiting Confirm/Cancel click.");
    }

    private static string BuildHtml(StoreSchedulingSettings settings, ScheduleRequest request)
    {
        var when = $"{request.RequestedStart:ddd, MMM d, yyyy h:mm tt} – {request.RequestedEnd:h:mm tt}";
        return $@"
<p>Hello,</p>
<p>A delivery to <strong>{settings.StoreName}</strong> has been scheduled for <strong>{when}</strong>.</p>
<p>{request.Body}</p>
<p>
  <a href=""{request.ConfirmUrl}"" style=""display:inline-block;padding:10px 16px;background:#16a34a;color:#fff;text-decoration:none;border-radius:6px;margin-right:8px"">Confirm</a>
  <a href=""{request.CancelUrl}""  style=""display:inline-block;padding:10px 16px;background:#dc2626;color:#fff;text-decoration:none;border-radius:6px"">Cancel</a>
</p>
<p style=""font-size:12px;color:#666"">
  This delivery is assumed confirmed until you click Confirm or Cancel.
</p>";
    }
}

/// <summary>
/// Implementation-agnostic email sender the Email provider depends on. The
/// Functions project wires this onto its existing SendGrid IEmailService
/// so the Scheduling project doesn't need a SendGrid reference.
/// </summary>
public interface ISchedulingEmailSender
{
    Task SendAsync(
        IReadOnlyList<string> recipients,
        string subject,
        string htmlBody,
        CancellationToken ct = default);
}
