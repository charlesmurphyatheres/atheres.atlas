using Atheres.Atlas.TestBed.Helpers;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

var serviceBusConnection = config["ServiceBusConnectionString"]
    ?? throw new InvalidOperationException("ServiceBusConnectionString is required in appsettings.json");

// Default to Secure Transport; override via appsettings.json "TestCompanyId"
var testCompanyId = Guid.TryParse(config["TestCompanyId"], out var cid)
    ? cid
    : new Guid("10000000-0000-0000-0000-000000000001");

AnsiConsole.Write(new FigletText("Atheres Atlas").Color(Color.Blue));
AnsiConsole.MarkupLine("[bold]TestBed[/] — Azure Service Bus & Agent Debugger\n");

bool running = true;
while (running)
{
    var choice = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("[yellow]Select an action:[/]")
            .AddChoices(
                "1. Send test orders (single)",
                "2. Send test orders (batch of 5)",
                "3. Trigger route optimization",
                "4. Simulate store confirmation",
                "5. Trigger rescheduling check",
                "6. View queue depths",
                "0. Exit"));

    switch (choice[0])
    {
        case '1':
            await OrderGenerator.SendSingleOrder(serviceBusConnection, testCompanyId);
            break;
        case '2':
            await OrderGenerator.SendBatchOrders(serviceBusConnection, count: 5, testCompanyId);
            break;
        case '3':
            await ServiceBusHelper.TriggerRouteOptimization(serviceBusConnection, testCompanyId);
            break;
        case '4':
            await ServiceBusHelper.SimulateConfirmation(serviceBusConnection);
            break;
        case '5':
            await ServiceBusHelper.TriggerReschedulingCheck(serviceBusConnection, testCompanyId);
            break;
        case '6':
            await ServiceBusHelper.ShowQueueDepths(serviceBusConnection);
            break;
        case '0':
            running = false;
            break;
    }

    if (running)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Press Enter to continue...[/]");
        Console.ReadLine();
    }
}

AnsiConsole.MarkupLine("[green]Goodbye![/]");
