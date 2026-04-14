using Atheres.Atlas.Data;
using Atheres.Atlas.Data.Repositories;
using Atheres.Atlas.Domain.Enums;
using Atheres.Atlas.Domain.Messages;
using Atheres.Atlas.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Functions.Agents;

/// <summary>
/// Timer-based monitor that checks for missed confirmation deadlines every 15 minutes.
/// When a store hasn't confirmed 3 hours before delivery, the order is moved to
/// the next available business day and re-queued for route optimization.
/// Also handles reschedule messages published by other agents.
/// </summary>
public class ReschedulingAgent
{
    private readonly IOrderRepository _orders;
    private readonly IConfirmationRepository _confirmations;
    private readonly IUserSettingsRepository _settings;
    private readonly IServiceBusPublisher _bus;
    private readonly AtlasDbContext _db;
    private readonly ILogger<ReschedulingAgent> _logger;

    public ReschedulingAgent(
        IOrderRepository orders,
        IConfirmationRepository confirmations,
        IUserSettingsRepository settings,
        IServiceBusPublisher bus,
        AtlasDbContext db,
        ILogger<ReschedulingAgent> logger)
    {
        _orders = orders;
        _confirmations = confirmations;
        _settings = settings;
        _bus = bus;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Timer trigger: runs every 15 minutes to detect missed confirmation deadlines.
    /// Cron: every 15 minutes = "0 */15 * * * *"
    /// </summary>
    [Function(nameof(MonitorConfirmationDeadlines))]
    public async Task MonitorConfirmationDeadlines(
        [TimerTrigger("0 */15 * * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        _logger.LogInformation("Checking confirmation deadlines at {Time:u}", DateTime.UtcNow);

        var overdueOrders = await _orders.GetUnconfirmedPastDeadlineAsync(ct);
        _logger.LogInformation("Found {Count} orders past confirmation deadline", overdueOrders.Count);

        foreach (var order in overdueOrders)
        {
            var newDeliveryDate = NextBusinessDay(order.ExpectedDeliveryDate?.Date ?? DateTime.UtcNow.Date);

            await _bus.PublishAsync(ServiceBusQueues.Reschedule, new RescheduleRequestMessage(
                order.Id,
                order.CompanyId,
                order.StoreName,
                order.FormattedAddress ?? order.FullAddress,
                order.Email,
                order.Phone,
                order.District,
                order.Zone,
                order.ExpectedDeliveryDate ?? DateTime.UtcNow,
                newDeliveryDate,
                order.RescheduleCount + 1,
                "Confirmation deadline missed",
                DateTime.UtcNow), ct);
        }
    }

    /// <summary>
    /// Service Bus trigger: processes a reschedule request for a single order.
    /// Queue: atlas-reschedule
    /// </summary>
    [Function(nameof(ProcessReschedule))]
    public async Task ProcessReschedule(
        [ServiceBusTrigger(ServiceBusQueues.Reschedule, Connection = "ServiceBusConnection")]
        RescheduleRequestMessage message,
        CancellationToken ct)
    {
        _logger.LogInformation("Rescheduling order {OrderId} from {Old:yyyy-MM-dd} to {New:yyyy-MM-dd}",
            message.OrderId, message.OriginalDeliveryDate, message.NewDeliveryDate);

        var order = await _orders.GetByIdAsync(message.OrderId, ct);
        if (order is null)
        {
            _logger.LogError("Order {OrderId} not found for reschedule", message.OrderId);
            return;
        }

        var previousStatus = order.Status.ToString();

        // Expire any pending confirmations
        foreach (var conf in order.Confirmations.Where(c => c.Status != ConfirmationStatus.Confirmed))
        {
            conf.Status = ConfirmationStatus.Expired;
            await _confirmations.UpdateAsync(conf, ct);
        }

        // Remove from current route
        order.RouteId = null;
        order.StopSequence = null;
        order.ExpectedDeliveryDate = null;
        order.ConfirmationDeadline = null;
        order.ConfirmationToken = null;
        order.RescheduleCount = message.RescheduleCount;
        // Put back in Ordered pool — will be picked up by the next ReadyToPickup call
        order.Status = OrderStatus.Ordered;
        order.BatchId = null;

        await _orders.UpdateAsync(order, ct);
        await _confirmations.SaveChangesAsync(ct);
        await _orders.SaveChangesAsync(ct);

        await _bus.PublishAsync(ServiceBusQueues.Audit, new AuditMessage(
            AuditEventType.OrderRescheduled, nameof(ReschedulingAgent),
            message.CompanyId, order.Id, null,
            previousStatus, OrderStatus.Ordered.ToString(),
            $"Rescheduled from {message.OriginalDeliveryDate:yyyy-MM-dd}. Reason: {message.Reason}. Returned to Ordered pool.",
            true, null, DateTime.UtcNow), ct);

        await _bus.PublishAsync(ServiceBusQueues.Notifications, new NotificationMessage(
            Guid.NewGuid(), message.CompanyId, NotificationType.OrderRescheduled,
            "Order Rescheduled",
            $"{order.StoreName} returned to order pool. Reason: {message.Reason}",
            order.Id, null, null,
            new Dictionary<string, string>
            {
                ["originalDate"] = message.OriginalDeliveryDate.ToString("yyyy-MM-dd"),
                ["reason"] = message.Reason,
                ["rescheduleCount"] = message.RescheduleCount.ToString()
            }, DateTime.UtcNow), ct);
    }

    /// <summary>
    /// Returns the next Monday-Friday after the given date, skipping weekends.
    /// </summary>
    private static DateTime NextBusinessDay(DateTime after)
    {
        var next = after.AddDays(1);
        while (next.DayOfWeek == DayOfWeek.Saturday || next.DayOfWeek == DayOfWeek.Sunday)
            next = next.AddDays(1);
        return next.Date;
    }
}
