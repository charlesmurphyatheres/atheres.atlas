namespace Atheres.Atlas.Domain.Constants;

/// <summary>
/// Cross-agent rules for when an order is eligible for route optimization.
/// Centralised here so the producers (ReadyToPickup, ScheduleBatch,
/// MarkAllReady, manual status updates) and the
/// <c>RouteOptimizationAgent</c> consumer all use the same cutoff.
/// </summary>
public static class RoutingPolicy
{
    /// <summary>Maximum age (UTC-now minus OrderDate) for an order to still
    /// be eligible for routing. Anything older is flipped to
    /// <see cref="Atheres.Atlas.Domain.Enums.OrderStatus.RouteOmitted"/>
    /// instead.</summary>
    public static readonly TimeSpan MaxOrderAgeForRouting = TimeSpan.FromDays(1);

    /// <summary>True when the given <paramref name="orderDateUtc"/> is older
    /// than <see cref="MaxOrderAgeForRouting"/> relative to <paramref name="utcNow"/>.</summary>
    public static bool IsTooOldToRoute(DateTime orderDateUtc, DateTime utcNow) =>
        orderDateUtc < utcNow - MaxOrderAgeForRouting;
}
