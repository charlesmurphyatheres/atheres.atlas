namespace Atheres.Atlas.Domain.Messages;

/// <summary>
/// Central registry of all Azure Service Bus queue names.
/// </summary>
public static class ServiceBusQueues
{
    public const string OrdersIngest = "atlas-orders-ingest";
    public const string RoutesOptimize = "atlas-routes-optimize";
    public const string ConfirmationsSend = "atlas-confirmations-send";
    public const string ConfirmationsReceived = "atlas-confirmations-received";
    public const string Reschedule = "atlas-reschedule";
    public const string Notifications = "atlas-notifications";
    public const string Audit = "atlas-audit";
}
