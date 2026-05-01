using System.Net;
using Atheres.Atlas.Data.Repositories;
using Atheres.Atlas.Domain.Enums;
using Atheres.Atlas.Domain.Messages;
using Atheres.Atlas.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Functions.Agents;

/// <summary>
/// Sends delivery confirmation requests via email (SendGrid).
/// Exposes an HTTP endpoint that stores confirm when they click the link.
/// Queue: atlas-confirmations-send
/// </summary>
public class ConfirmationAgent
{
    private readonly IOrderRepository _orders;
    private readonly IConfirmationRepository _confirmations;
    private readonly IEmailService _email;
    private readonly IServiceBusPublisher _bus;
    private readonly ILogger<ConfirmationAgent> _logger;

    private static readonly string FunctionHostUrl =
        Environment.GetEnvironmentVariable("FunctionHostUrl") ?? "https://your-functions.azurewebsites.net";

    public ConfirmationAgent(
        IOrderRepository orders,
        IConfirmationRepository confirmations,
        IEmailService email,
        IServiceBusPublisher bus,
        ILogger<ConfirmationAgent> logger)
    {
        _orders = orders;
        _confirmations = confirmations;
        _email = email;
        _bus = bus;
        _logger = logger;
    }

    /// <summary>
    /// Service Bus trigger: sends email confirmation to the store via SendGrid.
    /// </summary>
    [Function(nameof(SendConfirmation))]
    public async Task SendConfirmation(
        [ServiceBusTrigger(ServiceBusQueues.ConfirmationsSend, Connection = "ServiceBusConnection")]
        ConfirmationRequestMessage message,
        CancellationToken ct)
    {
        _logger.LogInformation("Sending confirmation for order {OrderId} to {Email}",
            message.OrderId, message.Email);

        var order = await _orders.GetByIdAsync(message.OrderId, ct);
        if (order is null)
        {
            _logger.LogError("Order {OrderId} not found", message.OrderId);
            return;
        }

        // Create confirmation record
        var confirmation = new Domain.Entities.DeliveryConfirmation
        {
            Id = message.ConfirmationId,
            OrderId = message.OrderId,
            Token = message.ConfirmationToken,
            EmailSentTo = message.Email,
            ExpiresAt = message.ConfirmationDeadline,
            AttemptNumber = message.AttemptNumber,
            Status = ConfirmationStatus.Pending
        };

        await _confirmations.AddAsync(confirmation, ct);

        var confirmUrl = $"{FunctionHostUrl}/api/confirmations/confirm?token={message.ConfirmationToken}";
        var rejectUrl = $"{FunctionHostUrl}/api/confirmations/reject?token={message.ConfirmationToken}";
        var deliveryTime = message.ExpectedDelivery.ToString("ddd MMM dd 'at' h:mm tt");
        var deadline = message.ConfirmationDeadline.ToString("h:mm tt 'on' MMM dd");

        // Send email
        var emailHtml = BuildEmailHtml(message.StoreName, deliveryTime, deadline, confirmUrl, rejectUrl);
        var emailResult = await _email.SendAsync(new EmailRequest(
            message.Email,
            message.StoreName,
            $"[Action Required] Confirm Delivery for {deliveryTime}",
            emailHtml,
            $"Please confirm your delivery scheduled for {deliveryTime}. Confirm here: {confirmUrl}\nDeadline: {deadline}"),
            ct);

        if (emailResult)
        {
            confirmation.EmailSentAt = DateTime.UtcNow;
            confirmation.Status = ConfirmationStatus.SentEmail;
            await _bus.PublishAsync(ServiceBusQueues.Audit, new AuditMessage(
                AuditEventType.ConfirmationEmailSent, nameof(ConfirmationAgent),
                message.CompanyId, message.OrderId, null, null, null,
                $"Email sent to {message.Email}", true, null, DateTime.UtcNow), ct);
        }

        order.Status = OrderStatus.ConfirmationPending;
        await _orders.UpdateAsync(order, ct);
        await _confirmations.UpdateAsync(confirmation, ct);
        await _confirmations.SaveChangesAsync(ct);
        await _orders.SaveChangesAsync(ct);

        await _bus.PublishAsync(ServiceBusQueues.Notifications, new NotificationMessage(
            Guid.NewGuid(), message.CompanyId, NotificationType.ConfirmationSent,
            "Confirmation Sent",
            $"Confirmation sent to {message.StoreName} via email.",
            message.OrderId, null, null, null, DateTime.UtcNow), ct);
    }

    /// <summary>
    /// HTTP GET: store clicks link in email to confirm delivery.
    /// GET /api/confirmations/confirm?token={token}
    ///
    /// Intentionally anonymous: the caller is a dispensary employee clicking
    /// a one-time link from email, not a logged-in app user. The URL token
    /// (Order.ConfirmationToken) is the credential and is single-use.
    /// </summary>
    [Function(nameof(ConfirmDeliveryByEmail))]
    public async Task<HttpResponseData> ConfirmDeliveryByEmail(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "confirmations/confirm")] HttpRequestData req,
        CancellationToken ct)
    {
        var token = req.Query["token"];
        if (string.IsNullOrWhiteSpace(token))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Missing confirmation token.", ct);
            return bad;
        }

        return await ProcessConfirmation(req, token, "email", ct);
    }

    /// <summary>
    /// HTTP GET: store clicks reject link in email to decline delivery.
    /// GET /api/confirmations/reject?token={token}
    ///
    /// Intentionally anonymous for the same reason as ConfirmDeliveryByEmail:
    /// the URL token authenticates the click, not a JWT.
    /// </summary>
    [Function(nameof(RejectDelivery))]
    public async Task<HttpResponseData> RejectDelivery(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "confirmations/reject")] HttpRequestData req,
        CancellationToken ct)
    {
        var token = req.Query["token"];
        if (string.IsNullOrWhiteSpace(token))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Missing confirmation token.", ct);
            return bad;
        }

        var confirmation = await _confirmations.GetByTokenAsync(token, ct);
        if (confirmation is null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Confirmation not found.", ct);
            return notFound;
        }

        if (confirmation.Status == ConfirmationStatus.Confirmed)
        {
            var already = req.CreateResponse(HttpStatusCode.OK);
            await already.WriteStringAsync("This delivery has already been confirmed.", ct);
            return already;
        }

        confirmation.Status = ConfirmationStatus.Failed;
        confirmation.ConfirmedBy = "rejected";
        confirmation.ConfirmedAt = DateTime.UtcNow;
        await _confirmations.UpdateAsync(confirmation, ct);
        await _confirmations.SaveChangesAsync(ct);

        var order = await _orders.GetByIdAsync(confirmation.OrderId, ct);
        if (order is not null)
        {
            order.Status = OrderStatus.Rejected;
            order.UpdatedAt = DateTime.UtcNow;
            await _orders.UpdateAsync(order, ct);
            await _orders.SaveChangesAsync(ct);

            await _bus.PublishAsync(ServiceBusQueues.Audit, new AuditMessage(
                AuditEventType.ConfirmationRejected, nameof(ConfirmationAgent),
                order.CompanyId, confirmation.OrderId, null, null, null,
                "Delivery rejected by store", true, null, DateTime.UtcNow), ct);

            await _bus.PublishAsync(ServiceBusQueues.Notifications, new NotificationMessage(
                Guid.NewGuid(), order.CompanyId, NotificationType.ConfirmationReceived,
                "Delivery Rejected",
                $"{order.StoreName} rejected the delivery.",
                confirmation.OrderId, null, null, null, DateTime.UtcNow), ct);
        }

        var ok = req.CreateResponse(HttpStatusCode.OK);
        ok.Headers.Add("Content-Type", "text/html");
        await ok.WriteStringAsync(
            "<html><body><h2>Delivery Declined</h2><p>You have declined this delivery. The dispatcher has been notified.</p></body></html>", ct);
        return ok;
    }

    private async Task<HttpResponseData> ProcessConfirmation(
        HttpRequestData req, string token, string confirmedBy, CancellationToken ct)
    {
        var confirmation = await _confirmations.GetByTokenAsync(token, ct);
        if (confirmation is null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Confirmation not found.", ct);
            return notFound;
        }

        if (confirmation.IsExpired)
        {
            var expired = req.CreateResponse(HttpStatusCode.Gone);
            await expired.WriteStringAsync("This confirmation link has expired. Your order may have been rescheduled.", ct);
            return expired;
        }

        if (confirmation.Status == ConfirmationStatus.Confirmed)
        {
            var already = req.CreateResponse(HttpStatusCode.OK);
            await already.WriteStringAsync("This delivery has already been confirmed. Thank you!", ct);
            return already;
        }

        confirmation.Status = ConfirmationStatus.Confirmed;
        confirmation.ConfirmedAt = DateTime.UtcNow;
        confirmation.ConfirmedBy = confirmedBy;
        await _confirmations.UpdateAsync(confirmation, ct);
        await _confirmations.SaveChangesAsync(ct);

        // Load order to get CompanyId and update status
        var order = await _orders.GetByIdAsync(confirmation.OrderId, ct);
        if (order is not null)
        {
            order.Status = OrderStatus.Confirmed;
            order.ConfirmedAt = DateTime.UtcNow;
            await _orders.UpdateAsync(order, ct);
            await _orders.SaveChangesAsync(ct);
        }

        // Publish confirmation response
        await _bus.PublishAsync(ServiceBusQueues.ConfirmationsReceived, new ConfirmationResponseMessage(
            confirmation.Id, confirmation.OrderId, token, confirmedBy, DateTime.UtcNow), ct);

        if (order is not null)
        {
            await _bus.PublishAsync(ServiceBusQueues.Audit, new AuditMessage(
                AuditEventType.ConfirmationReceived, nameof(ConfirmationAgent),
                order.CompanyId, confirmation.OrderId, null, null, null,
                $"Confirmed by {confirmedBy}", true, null, DateTime.UtcNow), ct);

            await _bus.PublishAsync(ServiceBusQueues.Notifications, new NotificationMessage(
                Guid.NewGuid(), order.CompanyId, NotificationType.ConfirmationReceived,
                "Delivery Confirmed",
                $"Store confirmed delivery via {confirmedBy}.",
                confirmation.OrderId, null, null, null, DateTime.UtcNow), ct);
        }

        var ok = req.CreateResponse(HttpStatusCode.OK);
        ok.Headers.Add("Content-Type", "text/html");
        await ok.WriteStringAsync(
            "<html><body><h2>✓ Delivery Confirmed</h2><p>Thank you! Your delivery has been confirmed.</p></body></html>", ct);
        return ok;
    }

    /// <summary>
    /// Service Bus trigger: updates order status when a confirmation response comes in.
    /// Queue: atlas-confirmations-received
    /// </summary>
    [Function(nameof(HandleConfirmationResponse))]
    public async Task HandleConfirmationResponse(
        [ServiceBusTrigger(ServiceBusQueues.ConfirmationsReceived, Connection = "ServiceBusConnection")]
        ConfirmationResponseMessage message,
        CancellationToken ct)
    {
        var order = await _orders.GetByIdAsync(message.OrderId, ct);
        if (order is null) return;

        order.Status = OrderStatus.Confirmed;
        order.ConfirmedAt = message.ConfirmedAt;
        await _orders.UpdateAsync(order, ct);
        await _orders.SaveChangesAsync(ct);

        _logger.LogInformation("Order {OrderId} confirmed by {By} at {At}",
            message.OrderId, message.ConfirmedBy, message.ConfirmedAt);
    }

    private static string BuildEmailHtml(string storeName, string deliveryTime, string deadline, string confirmUrl, string rejectUrl) =>
        $$"""
        <!DOCTYPE html>
        <html>
        <head><style>
            body { font-family: Arial, sans-serif; max-width: 600px; margin: 0 auto; padding: 20px; }
            .header { background: #1a56db; color: white; padding: 20px; border-radius: 8px 8px 0 0; }
            .body { background: #f9fafb; padding: 24px; border: 1px solid #e5e7eb; }
            .btn { display: inline-block; color: white; padding: 14px 28px;
                    text-decoration: none; border-radius: 6px; font-size: 16px; margin: 16px 8px 16px 0; }
            .btn-confirm { background: #1a56db; }
            .btn-reject { background: #dc2626; }
            .note { color: #6b7280; font-size: 13px; margin-top: 16px; }
            .deadline { color: #dc2626; font-weight: bold; }
        </style></head>
        <body>
            <div class="header"><h2 style="margin:0">Delivery Confirmation Required</h2></div>
            <div class="body">
                <p>Hello <strong>{{storeName}}</strong>,</p>
                <p>A delivery has been scheduled for your location:</p>
                <p><strong>Estimated Arrival:</strong> {{deliveryTime}}</p>
                <p class="deadline">⚠ Please respond by: {{deadline}}</p>
                <p>If you do not respond, your delivery will be automatically rescheduled.</p>
                <a href="{{confirmUrl}}" class="btn btn-confirm">Confirm Delivery</a>
                <a href="{{rejectUrl}}" class="btn btn-reject">Decline Delivery</a>
                <p class="note">If the buttons don't work, copy and paste this link to confirm:<br>{{confirmUrl}}</p>
            </div>
        </body>
        </html>
        """;
}
