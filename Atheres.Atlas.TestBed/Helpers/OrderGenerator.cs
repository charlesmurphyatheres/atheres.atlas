using System.Text.Json;
using Atheres.Atlas.Domain.DTOs;
using Atheres.Atlas.Domain.Messages;
using Azure.Messaging.ServiceBus;
using Spectre.Console;

namespace Atheres.Atlas.TestBed.Helpers;

public static class OrderGenerator
{
    private static readonly string[] StoreNames =
    [
        "Green Valley Pharmacy", "Sunrise Medical Supply", "Lakewood Drug Store",
        "Mountain Health Pharmacy", "Riverside Rx", "Central Compounding",
        "Heritage Health Mart", "Pinnacle Pharmacy", "Main Street Drugs"
    ];

    private static readonly (string Address, string City, string State, string Zip, string County)[] Locations =
    [
        ("123 Oak Street", "Denver", "CO", "80201", "Denver"),
        ("456 Elm Avenue", "Aurora", "CO", "80010", "Arapahoe"),
        ("789 Pine Road", "Lakewood", "CO", "80215", "Jefferson"),
        ("321 Maple Drive", "Boulder", "CO", "80301", "Boulder"),
        ("654 Cedar Lane", "Arvada", "CO", "80002", "Jefferson"),
        ("987 Birch Boulevard", "Westminster", "CO", "80021", "Adams"),
        ("147 Willow Way", "Thornton", "CO", "80229", "Adams"),
        ("258 Ash Court", "Commerce City", "CO", "80022", "Adams"),
        ("369 Spruce Street", "Englewood", "CO", "80110", "Arapahoe"),
    ];

    public static async Task SendSingleOrder(string connectionString, Guid companyId)
    {
        var order = GenerateOrder();
        await using var client = new ServiceBusClient(connectionString);
        await using var sender = client.CreateSender(ServiceBusQueues.OrdersIngest);

        // Mimic what the HTTP endpoint does — create entity and publish ingest message
        var message = new OrderIngestedMessage(
            Guid.NewGuid(),
            companyId,
            order.StoreName,
            $"{order.Address}, {order.City}, {order.State} {order.Zip}",
            order.Email,
            order.Phone,
            order.District,
            order.Zone,
            order.OrderDate,
            DateTime.UtcNow);

        var json = JsonSerializer.Serialize(message);
        await sender.SendMessageAsync(new ServiceBusMessage(json)
        {
            ContentType = "application/json",
            Subject = nameof(OrderIngestedMessage)
        });

        AnsiConsole.MarkupLine($"[green]✓[/] Sent order for [bold]{order.StoreName}[/] → {ServiceBusQueues.OrdersIngest}");
        PrintOrder(order);
    }

    public static async Task SendBatchOrders(string connectionString, int count, Guid companyId)
    {
        await using var client = new ServiceBusClient(connectionString);
        await using var sender = client.CreateSender(ServiceBusQueues.OrdersIngest);

        var batch = await sender.CreateMessageBatchAsync();
        var orders = Enumerable.Range(0, count).Select(_ => GenerateOrder()).ToList();

        foreach (var order in orders)
        {
            var message = new OrderIngestedMessage(
                Guid.NewGuid(), companyId, order.StoreName,
                $"{order.Address}, {order.City}, {order.State} {order.Zip}",
                order.Email, order.Phone, order.District, order.Zone,
                order.OrderDate, DateTime.UtcNow);

            var json = JsonSerializer.Serialize(message);
            batch.TryAddMessage(new ServiceBusMessage(json)
            {
                ContentType = "application/json",
                Subject = nameof(OrderIngestedMessage)
            });
        }

        await sender.SendMessagesAsync(batch);
        AnsiConsole.MarkupLine($"[green]✓[/] Sent batch of [bold]{count}[/] orders to {ServiceBusQueues.OrdersIngest}");

        var table = new Table();
        table.AddColumns("Store", "Address", "Email");
        foreach (var o in orders)
            table.AddRow(o.StoreName, $"{o.City}, {o.State}", o.Email);
        AnsiConsole.Write(table);
    }

    private static OrderInputDto GenerateOrder()
    {
        var rng = new Random();
        var loc = Locations[rng.Next(Locations.Length)];
        var store = StoreNames[rng.Next(StoreNames.Length)];

        return new OrderInputDto
        {
            StoreName = store,
            Address = loc.Address,
            City = loc.City,
            State = loc.State,
            Zip = loc.Zip,
            County = loc.County,
            LicenseNumber = $"LIC-{rng.Next(10000, 99999)}",
            District = $"District {rng.Next(1, 6)}",
            Zone = $"Zone {(char)('A' + rng.Next(0, 5))}",
            OrderDate = DateTime.UtcNow,
            Email = $"orders@{store.Replace(" ", "").ToLower()}.com",
            Phone = $"+1303555{rng.Next(1000, 9999)}",
            Notes = "Test order from TestBed"
        };
    }

    private static void PrintOrder(OrderInputDto order)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumns("Field", "Value");
        table.AddRow("Store", order.StoreName);
        table.AddRow("Address", $"{order.Address}, {order.City}, {order.State} {order.Zip}");
        table.AddRow("Email", order.Email);
        table.AddRow("Phone", order.Phone ?? "(none)");
        table.AddRow("District", order.District);
        table.AddRow("Zone", order.Zone);
        AnsiConsole.Write(table);
    }
}
