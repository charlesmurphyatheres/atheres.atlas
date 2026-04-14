using System.Text.Json;
using Atheres.Atlas.Domain.Messages;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Spectre.Console;

namespace Atheres.Atlas.TestBed.Helpers;

public static class ServiceBusHelper
{
    public static async Task TriggerRouteOptimization(string connectionString, Guid companyId)
    {
        var deliveryDate = AnsiConsole.Ask<DateTime>("Delivery date (yyyy-MM-dd):", DateTime.UtcNow.Date.AddDays(1));
        var orderCountInput = AnsiConsole.Ask<string>("Comma-separated order GUIDs (or press Enter for a dummy test):", "");

        List<Guid> orderIds;
        if (string.IsNullOrWhiteSpace(orderCountInput))
        {
            orderIds = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
            AnsiConsole.MarkupLine("[yellow]Using dummy order IDs — agent will skip unfound orders.[/]");
        }
        else
        {
            orderIds = orderCountInput.Split(',')
                .Select(s => Guid.TryParse(s.Trim(), out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToList();
        }

        var message = new RouteOptimizationRequestMessage(
            Guid.NewGuid(), companyId, null, deliveryDate, orderIds, DateTime.UtcNow);

        await PublishAsync(connectionString, ServiceBusQueues.RoutesOptimize, message);
        AnsiConsole.MarkupLine($"[green]✓[/] Route optimization requested for [bold]{deliveryDate:yyyy-MM-dd}[/] with {orderIds.Count} order(s).");
    }

    public static async Task SimulateConfirmation(string connectionString)
    {
        var tokenInput = AnsiConsole.Ask<string>("Confirmation token (or press Enter for test token):", "");
        var token = string.IsNullOrWhiteSpace(tokenInput) ? Guid.NewGuid().ToString("N") : tokenInput;
        var orderId = Guid.NewGuid();

        var message = new ConfirmationResponseMessage(
            Guid.NewGuid(), orderId, token, "sms", DateTime.UtcNow);

        await PublishAsync(connectionString, ServiceBusQueues.ConfirmationsReceived, message);
        AnsiConsole.MarkupLine($"[green]✓[/] Simulated SMS confirmation for order [bold]{orderId}[/].");
    }

    public static async Task TriggerReschedulingCheck(string connectionString, Guid companyId)
    {
        var orderId = Guid.NewGuid();
        var originalDate = DateTime.UtcNow.Date;
        var newDate = NextBusinessDay(originalDate);

        var message = new RescheduleRequestMessage(
            orderId, companyId, "Test Store", "123 Test St, Denver, CO 80201",
            "test@store.com", "+13035551234",
            "District 1", "Zone A",
            originalDate, newDate, 1, "Manual TestBed trigger", DateTime.UtcNow);

        await PublishAsync(connectionString, ServiceBusQueues.Reschedule, message);
        AnsiConsole.MarkupLine($"[green]✓[/] Reschedule queued: [bold]{originalDate:yyyy-MM-dd}[/] → [bold]{newDate:yyyy-MM-dd}[/].");
    }

    public static async Task ShowQueueDepths(string connectionString)
    {
        try
        {
            var adminClient = new ServiceBusAdministrationClient(connectionString);
            var queues = new[]
            {
                ServiceBusQueues.OrdersIngest,
                ServiceBusQueues.RoutesOptimize,
                ServiceBusQueues.ConfirmationsSend,
                ServiceBusQueues.ConfirmationsReceived,
                ServiceBusQueues.Reschedule,
                ServiceBusQueues.Notifications,
                ServiceBusQueues.Audit
            };

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumns("Queue", "Active", "Dead Letter", "Scheduled");

            foreach (var queueName in queues)
            {
                try
                {
                    var props = await adminClient.GetQueueRuntimePropertiesAsync(queueName);
                    table.AddRow(
                        queueName,
                        props.Value.ActiveMessageCount.ToString(),
                        props.Value.DeadLetterMessageCount.ToString(),
                        props.Value.ScheduledMessageCount.ToString());
                }
                catch
                {
                    table.AddRow(queueName, "[red]N/A[/]", "[red]N/A[/]", "[red]N/A[/]");
                }
            }

            AnsiConsole.Write(table);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error reading queue depths: {ex.Message}[/]");
        }
    }

    private static async Task PublishAsync<T>(string connectionString, string queue, T message) where T : class
    {
        await using var client = new ServiceBusClient(connectionString);
        await using var sender = client.CreateSender(queue);
        var json = JsonSerializer.Serialize(message);
        await sender.SendMessageAsync(new ServiceBusMessage(json)
        {
            ContentType = "application/json",
            Subject = typeof(T).Name
        });
    }

    private static DateTime NextBusinessDay(DateTime after)
    {
        var next = after.AddDays(1);
        while (next.DayOfWeek == DayOfWeek.Saturday || next.DayOfWeek == DayOfWeek.Sunday)
            next = next.AddDays(1);
        return next;
    }
}
