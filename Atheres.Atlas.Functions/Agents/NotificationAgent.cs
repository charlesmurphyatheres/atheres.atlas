using System.Net;
using System.Text.Json;
using Atheres.Atlas.Domain.Messages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Functions.Agents;

/// <summary>
/// Receives notification messages from the Service Bus and broadcasts them
/// to connected frontend clients via Azure SignalR Service.
/// Also exposes the SignalR negotiate endpoint required by the client SDK.
/// Queue: atlas-notifications
/// </summary>
public class NotificationAgent
{
    private readonly ILogger<NotificationAgent> _logger;

    public NotificationAgent(ILogger<NotificationAgent> logger) => _logger = logger;

    /// <summary>
    /// Required negotiate endpoint — returns a SignalR connection token to
    /// the React client. Authenticated callers only; the client sends the
    /// access token in the Authorization header during the negotiate POST.
    /// POST /api/negotiate
    /// </summary>
    [Function(nameof(Negotiate))]
    [Authorize]
    public async Task<HttpResponseData> Negotiate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "negotiate")] HttpRequestData req,
        [SignalRConnectionInfoInput(HubName = "atlashub", ConnectionStringSetting = "AzureSignalRConnectionString")] SignalRConnectionInfo connectionInfo)
    {
        // [Authorize] doesn't always fire on HttpRequestData triggers in the
        // isolated worker, so we check the JWT-populated principal explicitly.
        // The JwtAuthMiddleware sets httpContext.User when an Authorization
        // header is present and valid; anonymous callers fall through here.
        var httpContext = req.FunctionContext.GetHttpContext();
        if (httpContext?.User?.Identity?.IsAuthenticated != true)
            return req.CreateResponse(HttpStatusCode.Unauthorized);

        _logger.LogDebug("SignalR negotiate requested from {IP}", req.Headers.TryGetValues("X-Forwarded-For", out var ips) ? ips.FirstOrDefault() : "unknown");

        // @microsoft/signalr expects lowercase "url" / "accessToken". The isolated
        // worker serializer defaults to PascalCase here, which the client silently
        // ignores → falls through with no transports → "None of the transports
        // supported by the server" on the browser side.
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        var payload = JsonSerializer.Serialize(
            new { url = connectionInfo.Url, accessToken = connectionInfo.AccessToken });
        await response.WriteStringAsync(payload);
        return response;
    }

    /// <summary>
    /// Service Bus trigger: broadcasts a notification to all SignalR hub clients.
    /// </summary>
    [Function(nameof(BroadcastNotification))]
    [SignalROutput(HubName = "atlashub", ConnectionStringSetting = "AzureSignalRConnectionString")]
    public SignalRMessageAction BroadcastNotification(
        [ServiceBusTrigger(ServiceBusQueues.Notifications, Connection = "ServiceBusConnection")]
        NotificationMessage message,
        CancellationToken ct)
    {
        _logger.LogInformation("Broadcasting {Type} notification: {Title}", message.Type, message.Title);

        return new SignalRMessageAction("notification")
        {
            GroupName = message.CompanyId.ToString(),
            Arguments = new object[]
            {
                new
                {
                    id = message.NotificationId,
                    companyId = message.CompanyId,
                    type = message.Type.ToString(),
                    title = message.Title,
                    body = message.Body,
                    orderId = message.OrderId,
                    routeId = message.RouteId,
                    metadata = message.Metadata,
                    occurredAt = message.OccurredAt
                }
            }
        };
    }
}
