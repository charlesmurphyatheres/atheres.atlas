using Atheres.Atlas.Domain.Entities;

namespace Atheres.Atlas.Functions.Services;

/// <summary>
/// Outcome of a single <see cref="IRouteScheduler.EnqueueAsync"/> call.
/// Returned (instead of a bare count) so callers can tell the operator
/// exactly why their selection didn't produce as many route runs as
/// expected — typically because rows lacked geocodes or the company had
/// no active hub.
/// </summary>
public sealed record RouteEnqueueResult(
    int  RoutesQueued,
    int  OrdersRouted,
    int  OrdersUngeocoded,
    bool NoActiveHub,
    int  StoresGeocoded     = 0,
    int  HubsGeocoded       = 0,
    int  WarehousesGeocoded = 0,
    int  GeocodeFailures    = 0);

/// <summary>
/// Bundles a freshly-scheduled set of orders into the route-optimization
/// pipeline. Centralising this so every "these orders just became
/// Scheduled" entry point (admin bulk status change, CSV import with an
/// initial Scheduled state, single status flip, etc.) routes through the
/// same grouping + chunking logic instead of fanning out per order.
/// </summary>
public interface IRouteScheduler
{
    /// <summary>
    /// Groups <paramref name="orders"/> by warehouse, chunks each group to
    /// the company's MaxStopsPerRoute, and publishes one optimization
    /// request per chunk. Orders without coordinates are skipped and
    /// reported back via <see cref="RouteEnqueueResult.OrdersUngeocoded"/>.
    /// </summary>
    Task<RouteEnqueueResult> EnqueueAsync(
        IReadOnlyCollection<Order> orders,
        Guid companyId,
        CancellationToken ct = default,
        string? triggeredBy = null,
        string? trigger     = null);
}
