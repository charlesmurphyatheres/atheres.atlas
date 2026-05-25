using Atheres.Atlas.Domain.Entities;

namespace Atheres.Atlas.Domain.Messages;

/// <summary>
/// One unit of work for <see cref="Atheres.Atlas.Functions.Agents.RouteOptimizationAgent"/>.
/// The shape produced depends on <see cref="Kind"/>:
///
///   Pickup         — warehouse → hub. No deliveries. WarehouseId required.
///   ZonedDelivery  — hub → zone stops → hub. WarehouseId null (pickup is a
///                    separate Pickup message). ZoneId set, all OrderIds share it.
///   DirectDelivery — warehouse → zone stops → hub. WarehouseId required.
///                    Issued only when a warehouse's whole batch is a single
///                    zone (hub bypass per the new flow's exception).
///   Legacy         — old single-route shape from before the pickup/zone split.
///                    The optimizer treats it as backwards-compat: hub →
///                    warehouse → stops → hub when a warehouse is present,
///                    hub → stops → hub otherwise.
/// </summary>
public record RouteOptimizationRequestMessage(
    Guid                 RouteRequestId,
    Guid                 CompanyId,
    Guid?                TruckId,
    Guid                 HubId,
    Guid?                WarehouseId,
    DateTime             DeliveryDate,
    IReadOnlyList<Guid>  OrderIds,
    DateTime             RequestedAt,
    RouteType            Kind   = RouteType.Legacy,
    Guid?                ZoneId = null,
    /// <summary>
    /// Pre-computed earliest dispatch time for this route, set by the
    /// scheduler so paired routes can chain off each other without the
    /// optimizer having to look up siblings at runtime. For Pickup vans
    /// this is the start of the company delivery window; for ZonedDelivery
    /// vans it's the estimated pickup-van return time + hub sort wait,
    /// computed via Haversine on the warehouse→hub leg. The optimizer
    /// stamps it onto <see cref="Atheres.Atlas.Domain.Entities.DeliveryRoute.ScheduledDepartTime"/>
    /// verbatim.
    /// </summary>
    DateTime?            ScheduledDepartTime = null
);
