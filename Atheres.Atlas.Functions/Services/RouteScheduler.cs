using Atheres.Atlas.Data;
using Atheres.Atlas.Domain.Entities;
using Atheres.Atlas.Domain.Messages;
using Atheres.Atlas.Functions.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Functions.Services;

// =============================================================================
// NEW ROUTING CONTRACT (in progress — orchestration rewrite tracked separately)
// =============================================================================
// The pipeline this scheduler is being migrated toward:
//
//   1. A single Pickup route per warehouse per day:
//        warehouse → nearest hub
//      The pickup van is independent of any delivery van. It only carries the
//      day's orders to the hub for sorting.
//
//   2. At the hub, the staff sort the load by Zone. The wait is
//      Hub.SortingWaitMinutes (per-hub, edited in the Hubs admin tab,
//      default 30). Modeled as metadata: each ZonedDelivery route's
//      ScheduledDepartTime = pickup HubArrivalTime + Hub.SortingWaitMinutes.
//      No runtime delay or background timer is required for v1; the
//      scheduler just stamps the departure time when it creates the route.
//
//   3. One ZonedDelivery route per Zone with orders that day:
//        hub → up to MaxStopsPerRoute (default 5) stops in that single
//        Zone → hub. Each route uses a distinct delivery van.
//
//   4. Exception — DirectDelivery: when a single warehouse-pickup batch is
//      entirely one Zone, that van skips the hub:
//        warehouse → stops in that single Zone → hub
//      Same 5-stop cap on the deliveries; the warehouse pickup leg is not
//      counted toward the cap.
//
// Schema for the rewrite already landed in
// 20260524000003_HubSortAndZonedRoutes (Routes.RouteType, Routes.ZoneId,
// Routes.HubArrivalTime, Routes.ScheduledDepartTime, Hubs.SortingWaitMinutes).
// The chunking + multi-route fan-out below is still on the OLD single-route
// model; it will be rewritten in a follow-up commit.
// =============================================================================
public class RouteScheduler : IRouteScheduler
{
    // Google Directions caps optimization waypoints at 25; one slot is
    // reserved for the warehouse pickup stop when present, so each route's
    // delivery count is at most maxStopsPerRoute (or 25 - 1 if a warehouse
    // pickup is included).
    private const int GoogleDirectionsHardCap = 25;

    // Concurrency cap for parallel Google Geocoding calls. 10 keeps us well
    // under the per-second free-tier quota and finishes typical batches
    // (5-50 missing locations) in a few seconds.
    private const int GeocodeConcurrency = 10;

    private readonly AtlasDbContext _db;
    private readonly IServiceBusPublisher _bus;
    private readonly RouteOptimizationAgent _optimizer;
    private readonly IGoogleMapsService _maps;
    private readonly ILogger<RouteScheduler> _logger;

    public RouteScheduler(
        AtlasDbContext db,
        IServiceBusPublisher bus,
        RouteOptimizationAgent optimizer,
        IGoogleMapsService maps,
        ILogger<RouteScheduler> logger)
    {
        _db        = db;
        _bus       = bus;
        _optimizer = optimizer;
        _maps      = maps;
        _logger    = logger;
    }

    public async Task<RouteEnqueueResult> EnqueueAsync(
        IReadOnlyCollection<Order> orders,
        Guid companyId,
        CancellationToken ct = default,
        string? triggeredBy = null,
        string? trigger     = null)
    {
        // Audit accumulator — every decision made below appends a line so
        // the Optimization Audit tab in the admin panel can show the
        // operator the full reasoning behind a given dispatch. Persisted
        // unconditionally at the end of the method (even early-exit paths
        // that didn't produce any routes); failures to write the audit
        // are caught and logged but never bubble up — auditing must not
        // be allowed to break route generation.
        var audit = new OptimizationAuditBuilder(
            companyId,
            triggeredBy,
            trigger ?? "RouteScheduler.EnqueueAsync");
        audit.Raw($"=== Optimization Run · {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC ===");
        audit.Raw($"Trigger:       {audit.Trigger}");
        if (!string.IsNullOrWhiteSpace(audit.TriggeredBy))
            audit.Raw($"Triggered by:  {audit.TriggeredBy}");
        audit.Raw($"Input orders:  {orders.Count}");
        audit.OrderCount = orders.Count;

        if (orders.Count == 0)
        {
            audit.Raw("");
            audit.Raw("No orders to route — nothing to do.");
            await TryPersistAuditAsync(audit, "No orders supplied.", ct);
            return new RouteEnqueueResult(0, 0, 0, false);
        }

        // ---- Step 1: Geocode every reference-data row that's needed for
        // routing this batch and is missing coordinates. The persisted
        // results mean future routing runs read from the DB instead of
        // calling Google again.
        //
        // Stores referenced by these orders -> geocode + persist.
        // Warehouses referenced by these orders -> geocode + persist.
        // The company's active hubs -> geocode + persist (RouteScheduler
        //   picks "the first active hub" below; geocoding all of them is
        //   cheap since a company typically has 1-3 and ensures any
        //   future routing path reads from cache).
        var (storesGeocoded, hubsGeocoded, warehousesGeocoded, geocodeFailures) =
            await GeocodeReferenceDataAsync(orders, companyId, ct);

        audit.Step(1, "Geocode reference data");
        audit.Line($"Stores geocoded:      {storesGeocoded}");
        audit.Line($"Warehouses geocoded:  {warehousesGeocoded}");
        audit.Line($"Hubs geocoded:        {hubsGeocoded}");
        if (geocodeFailures > 0) audit.Line($"Geocode failures:     {geocodeFailures}");

        // ---- Step 2: Now that the underlying Stores have coordinates,
        // backfill Latitude/Longitude/FormattedAddress onto the orders
        // themselves. Order coords are denormalized at creation time, so
        // an old order whose store was just geocoded still has null
        // coords until we copy them across.
        await BackfillOrderCoordinatesFromStoresAsync(orders, ct);

        // ---- Step 3: Drop any order that's still ungeocoded — typically
        // because its linked store has no street address Google could
        // resolve, or it has no StoreId at all. Reporting back the count
        // so the operator can fix the source data.
        var routable = orders
            .Where(o => o.Latitude.HasValue && o.Longitude.HasValue)
            .ToList();

        var ungeocodedCount = orders.Count - routable.Count;
        if (ungeocodedCount > 0)
        {
            _logger.LogWarning(
                "RouteScheduler: dropping {Count} order(s) with no geocode for company {Company}",
                ungeocodedCount, companyId);
        }

        audit.Step(2, "Filter ungeocoded orders");
        audit.Line($"Routable orders:   {routable.Count}");
        audit.Line($"Dropped (no geo):  {ungeocodedCount}");

        if (routable.Count == 0)
        {
            audit.Raw("");
            audit.Raw("No routable orders remain — nothing to dispatch.");
            await TryPersistAuditAsync(audit, "No routable orders.", ct);
            return new RouteEnqueueResult(
                RoutesQueued:       0,
                OrdersRouted:       0,
                OrdersUngeocoded:   ungeocodedCount,
                NoActiveHub:        false,
                StoresGeocoded:     storesGeocoded,
                HubsGeocoded:       hubsGeocoded,
                WarehousesGeocoded: warehousesGeocoded,
                GeocodeFailures:    geocodeFailures);
        }

        // ---- Step 4: Load the company's active hubs. The route always
        // starts and ends at a hub; for each warehouse-group below we
        // pick the hub closest to the warehouse so the driver doesn't
        // backtrack across the metro before pickup.
        var hubs = await _db.Hubs.IgnoreQueryFilters()
            .Where(h => h.CompanyId == companyId && h.IsActive)
            .ToListAsync(ct);
        audit.Step(3, "Resolve active hubs");
        audit.Line($"Active hubs: {hubs.Count}");
        foreach (var h in hubs)
            audit.Line($"  · {h.Name} (sort wait {h.SortingWaitMinutes} min)");

        if (hubs.Count == 0)
        {
            _logger.LogWarning(
                "RouteScheduler: company {Company} has no active hub — skipping enqueue ({Count} orders).",
                companyId, routable.Count);
            audit.Raw("");
            audit.Raw("No active hubs configured — cannot dispatch.");
            await TryPersistAuditAsync(audit, "No active hubs.", ct);
            return new RouteEnqueueResult(
                RoutesQueued:       0,
                OrdersRouted:       0,
                OrdersUngeocoded:   ungeocodedCount,
                NoActiveHub:        true,
                StoresGeocoded:     storesGeocoded,
                HubsGeocoded:       hubsGeocoded,
                WarehousesGeocoded: warehousesGeocoded,
                GeocodeFailures:    geocodeFailures);
        }
        audit.HubCount = hubs.Count;

        var company = await _db.Companies.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == companyId, ct);
        var maxStopsPerRoute = Math.Clamp(company?.MaxStopsPerRoute ?? 12, 1, GoogleDirectionsHardCap);
        // Window-start is the earliest time a van can be on the road. Pickup
        // vans leave the hub at this time; delivery vans (ZonedDelivery)
        // wait for the pickup van to return + the hub sort wait before
        // dispatching. Defaults to 08:00 if no company row is set.
        var deliveryWindowStart = company?.DeliveryWindowStart ?? new TimeSpan(8, 0, 0);

        // Pre-load the warehouses these orders reference so we can pick a
        // hub per warehouse without round-tripping the DB inside the loop.
        var warehouseIds = routable
            .Where(o => o.WarehouseId.HasValue)
            .Select(o => o.WarehouseId!.Value)
            .Distinct()
            .ToList();
        var warehousesById = warehouseIds.Count == 0
            ? new Dictionary<Guid, Warehouse>()
            : await _db.Warehouses.IgnoreQueryFilters()
                .Where(w => warehouseIds.Contains(w.Id))
                .ToDictionaryAsync(w => w.Id, ct);

        // ---- Step 5: Group by source warehouse so each route picks up
        // from the right place. Orders with no warehouse fall into a null
        // group that has no pickup stop reserved.
        //
        // Sub-group by OrderDate so each generated route carries the
        // delivery day the operator scheduled it for, not today's wall
        // clock. A batch dated 2026-05-26 scheduled on 2026-05-24 lands on
        // the calendar at 2026-05-26, which is what the operator expects;
        // it also means the route page filtered by date shows it under
        // the right day instead of today. Stale orders are already filtered
        // out upstream (RoutingPolicy.IsTooOldToRoute via the bulk-status
        // path), so the past-date worry is already handled — anything here
        // is today or future.
        var groups   = routable
            .GroupBy(o => new { o.WarehouseId, OrderDate = o.OrderDate.Date })
            .ToList();
        var messages = new List<RouteOptimizationRequestMessage>();

        audit.Step(4, "Group orders by (Warehouse, OrderDate)");
        audit.Line($"{groups.Count} group(s):");
        foreach (var g in groups)
        {
            var label = g.Key.WarehouseId.HasValue
                ? (warehousesById.TryGetValue(g.Key.WarehouseId.Value, out var wlabel)
                    ? $"{wlabel.BusinessName} ({wlabel.LicenseNumber ?? "no-license"})"
                    : g.Key.WarehouseId.Value.ToString("N").Substring(0, 8))
                : "(no warehouse)";
            audit.Line($"  · {label} → {g.Key.OrderDate:yyyy-MM-dd}: {g.Count()} order(s)");
        }
        audit.WarehouseCount = groups
            .Where(g => g.Key.WarehouseId.HasValue)
            .Select(g => g.Key.WarehouseId)
            .Distinct()
            .Count();

        // The audit lines emitted inside the foreach below describe each
        // warehouse-group's specific shape (Pickup + N ZonedDelivery for
        // multi-zone, or DirectDelivery for single-zone). We avoid
        // pre-committing a "N pickup vans" count up here because the
        // single-zone bypass means each warehouse-group's pickup need is
        // only known after we inspect its zone footprint.
        audit.Raw("");
        audit.Raw("Per-warehouse plan:");

        audit.Step(5, "Pick nearest hub per group + chunk by max stops");
        audit.Line($"MaxStopsPerRoute: {maxStopsPerRoute} (delivery stops only — warehouse pickup is not counted)");

        // Pre-load StoreId → ZoneId so the audit can tag each chunk with
        // its zones (and flag single-zone chunks as DirectDelivery candidates).
        // One query for the whole batch; cheap since we already know every
        // store id the orders reference.
        var storeIdsInBatch = routable
            .Where(o => o.StoreId.HasValue)
            .Select(o => o.StoreId!.Value)
            .Distinct()
            .ToList();
        var storeZoneById = storeIdsInBatch.Count == 0
            ? new Dictionary<Guid, Guid?>()
            : await _db.Stores.IgnoreQueryFilters()
                .Where(s => storeIdsInBatch.Contains(s.Id))
                .Select(s => new { s.Id, s.ZoneId })
                .ToDictionaryAsync(s => s.Id, s => s.ZoneId, ct);

        // Resolve Zone → (Code, District#, DistrictName) for every zone
        // referenced by this batch. Used by the audit to print
        // "Zone 5.72 · District 5 — Bloomington" against each store so the
        // administrator can verify the orders landed in the right zones.
        var zoneIdsInBatch = storeZoneById.Values
            .Where(z => z.HasValue)
            .Select(z => z!.Value)
            .Distinct()
            .ToList();
        var zoneDetailById = zoneIdsInBatch.Count == 0
            ? new Dictionary<Guid, ZoneDetail>()
            : await _db.Zones.IgnoreQueryFilters()
                .Where(z => zoneIdsInBatch.Contains(z.Id))
                .Select(z => new { z.Id, z.Code, DistrictNumber = z.District.Number, DistrictName = z.District.Name })
                .ToDictionaryAsync(
                    x => x.Id,
                    x => new ZoneDetail(x.Code, x.DistrictNumber, x.DistrictName),
                    ct);

        var totalZones = new HashSet<Guid>();

        // Per-hub consolidation pool for multi-zone ZonedDelivery. A
        // ZonedDelivery van that serves zone X out of hub H takes EVERY
        // zone-X order that arrived at hub H today — regardless of which
        // warehouse pickup brought it. Its earliest dispatch time is
        // MAX(HubArrivalTime over every contributing pickup) + the hub's
        // sort wait. Keyed by (hub, zone, orderDate) so cross-warehouse
        // contributors converge into a single delivery batch.
        var consolidationPools = new Dictionary<
            (Guid HubId, Guid? ZoneId, DateTime OrderDate),
            ConsolidationPool>();

        // Pickup ↔ ZonedDelivery correlation table. Populated after
        // ZonedDelivery messages are emitted from the pools; each entry
        // links a set of pickup RouteIds (the contributors) to the set
        // of ZonedDelivery RouteIds whose ScheduledDepartTime must be
        // refreshed once the pickups complete.
        var pickupCorrelations = new List<PickupCorrelation>();

        // MaxStopsPerRoute counts delivery stops only; the warehouse
        // pickup leg (if present) is logically a separate activity
        // (see UserRouteSettings.DefaultMaxStops doc-comment), so we
        // do NOT subtract a "pickup slot" from the cap. Google's
        // 25-waypoint hard cap is enforced separately at the optimizer
        // layer. Computed once for the whole batch — both the per-group
        // DirectDelivery emit and the consolidation-pool ZonedDelivery
        // emit (post-loop) use the same value.
        var chunkSize = Math.Max(1, maxStopsPerRoute);

        foreach (var group in groups)
        {

            // Pick the hub closest to this group's warehouse via Haversine
            // distance — cheap, no extra Google calls, and produces the
            // intuitive "start at the depot nearest the supplier" loop.
            // Falls back to the first active hub when the warehouse has
            // no coordinates yet (shouldn't happen after the geocode pass
            // above, but be defensive).
            Guid hubIdForGroup;
            if (group.Key.WarehouseId.HasValue
                && warehousesById.TryGetValue(group.Key.WarehouseId.Value, out var wh)
                && wh.Latitude.HasValue && wh.Longitude.HasValue)
            {
                hubIdForGroup = PickClosestHub(hubs, wh.Latitude.Value, wh.Longitude.Value);
            }
            else
            {
                hubIdForGroup = hubs[0].Id;
            }

            var hubForGroup     = hubs.First(h => h.Id == hubIdForGroup);
            var warehouseLabel  = group.Key.WarehouseId.HasValue
                && warehousesById.TryGetValue(group.Key.WarehouseId.Value, out var whName)
                    ? whName.BusinessName
                    : "(no warehouse)";

            audit.Raw("");
            audit.Line($"Group: {warehouseLabel} → hub \"{hubForGroup.Name}\" (sort wait {hubForGroup.SortingWaitMinutes} min)");

            // Inspect the whole warehouse-group's zone footprint. The
            // dispatch shape depends on whether this group is single-zone
            // (hub bypass — DirectDelivery) or multi-zone (warehouse →
            // hub Pickup, then per-zone delivery vans dispatch from the hub
            // after the sort wait).
            var ordersByZone = new Dictionary<Guid, List<Order>>();
            var unzonedOrders = new List<Order>();
            foreach (var o in group)
            {
                Guid? zoneId = o.StoreId.HasValue
                                && storeZoneById.TryGetValue(o.StoreId.Value, out var z)
                                    ? z
                                    : null;
                if (zoneId.HasValue)
                {
                    if (!ordersByZone.TryGetValue(zoneId.Value, out var list))
                        ordersByZone[zoneId.Value] = list = new List<Order>();
                    list.Add(o);
                    totalZones.Add(zoneId.Value);
                }
                else
                {
                    unzonedOrders.Add(o);
                }
            }

            // Timing baseline for this delivery date. The window-start is
            // company-level (see Company.DeliveryWindowStart); all vans
            // dispatched for this group are anchored to that wall-clock
            // moment. DirectDelivery vans + Pickup vans leave AT this
            // time. ZonedDelivery vans wait for the pickup van to return
            // + the hub's sort wait, computed below from a Haversine
            // estimate of the hub→warehouse round trip.
            var deliveryStart = group.Key.OrderDate.Date.Add(deliveryWindowStart);

            // Single-zone (or all-unzoned) → DirectDelivery: the van picks
            // up at the warehouse and goes straight to the stops, skipping
            // the hub sort entirely. Same 5-stop cap on deliveries; the
            // warehouse pickup leg is not counted toward the cap (see
            // UserRouteSettings.DefaultMaxStops).
            var isSingleZone = ordersByZone.Count <= 1 && unzonedOrders.Count == 0;
            if (isSingleZone)
            {
                var zoneId = ordersByZone.Keys.FirstOrDefault();
                var ordersForZone = ordersByZone.Values.FirstOrDefault() ?? new List<Order>();
                if (ordersForZone.Count == 0)
                {
                    // No zones AND no orders shouldn't happen by construction,
                    // but be defensive — skip this group.
                    audit.Line("  · empty group (no zones, no unzoned) — skipped");
                    continue;
                }

                audit.Line($"  Single-zone group → DirectDelivery (van skips the hub).");
                EmitChunks(
                    ordersForZone,
                    RouteType.DirectDelivery,
                    zoneId == Guid.Empty ? null : (Guid?)zoneId,
                    chunkSize,
                    group.Key.WarehouseId,
                    hubIdForGroup,
                    group.Key.OrderDate,
                    deliveryStart,
                    storeZoneById,
                    zoneDetailById,
                    messages,
                    audit,
                    "DirectDelivery van");
                audit.DirectDeliveryCount += Math.Max(1, (ordersForZone.Count + chunkSize - 1) / chunkSize);
                continue;
            }

            // Multi-zone → 1 Pickup (warehouse → hub) + N ZonedDelivery
            // routes, one per zone (each chunked by the cap). The pickup
            // van's job is sorting-bound, so each zone's delivery van
            // dispatches after the hub's SortingWaitMinutes has elapsed.
            audit.Line($"  Multi-zone group ({ordersByZone.Count} zone(s), {unzonedOrders.Count} unzoned) → 1 Pickup van + per-zone delivery vans.");

            // Estimate the pickup van's round-trip duration via Haversine
            // so the scheduler can stamp ZonedDelivery ScheduledDepartTime
            // *before* Google Directions runs. Urban average 50 km/h is a
            // reasonable proxy; Google's actual leg duration will refine
            // HubArrivalTime on the Pickup route, but for v1 the delivery
            // van depart-time uses this estimate so dispatch decisions are
            // deterministic and don't depend on optimizer ordering.
            int travelSeconds = 30 * 60; // 30-min default when coords missing
            // Warehouse loading wait — minutes the van spends at the
            // warehouse for paperwork / loading dock — pulled from the
            // warehouse row (default 15). Folded into the pickup return
            // estimate so paired ZonedDelivery depart times reflect the
            // real elapsed time.
            var warehouseWaitMinutes = group.Key.WarehouseId.HasValue
                && warehousesById.TryGetValue(group.Key.WarehouseId.Value, out var whForWait)
                    ? whForWait.LoadingWaitMinutes
                    : 0;

            if (group.Key.WarehouseId.HasValue
                && warehousesById.TryGetValue(group.Key.WarehouseId.Value, out var whForEstimate)
                && whForEstimate.Latitude.HasValue && whForEstimate.Longitude.HasValue
                && hubForGroup.Latitude.HasValue && hubForGroup.Longitude.HasValue)
            {
                var meters = GeoMath.HaversineMeters(
                    hubForGroup.Latitude.Value, hubForGroup.Longitude.Value,
                    whForEstimate.Latitude.Value, whForEstimate.Longitude.Value);
                const double urbanMetersPerSecond = 50.0 * 1000.0 / 3600.0; // 50 km/h
                // ×2 because pickup is hub → warehouse → hub round trip.
                travelSeconds = (int)Math.Round(meters * 2 / urbanMetersPerSecond);
            }

            var roundTripSeconds     = travelSeconds + (warehouseWaitMinutes * 60);
            var pickupReturnEstimate = deliveryStart.AddSeconds(roundTripSeconds);
            var zonedDepartEstimate  = pickupReturnEstimate.AddMinutes(hubForGroup.SortingWaitMinutes);
            audit.Line($"  Pickup round-trip est: {(travelSeconds / 60.0):F1} min travel + {warehouseWaitMinutes} min loading = {(roundTripSeconds / 60.0):F1} min — pickup van back at hub ≈ {pickupReturnEstimate:HH:mm}");
            audit.Line($"  Sort wait: {hubForGroup.SortingWaitMinutes} min — ZonedDelivery vans dispatch ≈ {zonedDepartEstimate:HH:mm}");

            // Pickup: hub → warehouse → hub round-trip. The driver starts
            // the day at the hub (their home base), drives to the warehouse
            // for pickup, then returns to the hub for the sort. Carries the
            // whole group's order ids as the truck's cargo manifest — no
            // delivery stops materialise from this message.
            var pickupAllOrderIds = group.Select(o => o.Id).ToList();
            var pickupRouteId     = Guid.NewGuid();
            messages.Add(new RouteOptimizationRequestMessage(
                pickupRouteId,
                companyId,
                null,
                hubIdForGroup,
                group.Key.WarehouseId,
                group.Key.OrderDate,
                pickupAllOrderIds,
                DateTime.UtcNow,
                RouteType.Pickup,
                null,
                deliveryStart));
            audit.Line($"  · Pickup van: hub → warehouse → hub ({pickupAllOrderIds.Count} order(s) on the truck) — leaves hub at {deliveryStart:HH:mm}");
            // Cargo manifest — every order on this Pickup van. Lets the
            // administrator confirm the truck is carrying exactly the
            // orders it should before reading the per-zone delivery vans
            // below. Sorted by zone first so the manifest groups
            // intuitively when the operator skims it.
            var cargoOrders = group
                .OrderBy(o => o.StoreId.HasValue && storeZoneById.TryGetValue(o.StoreId.Value, out var zz) && zz.HasValue
                              ? (zoneDetailById.TryGetValue(zz.Value, out var zd) ? zd.Code : "")
                              : "~")
                .ThenBy(o => o.StoreName)
                .ToList();
            for (int idx = 0; idx < cargoOrders.Count; idx++)
                WriteStopDetail(audit, cargoOrders[idx], $"[cargo {idx + 1}]", storeZoneById, zoneDetailById);

            // Don't emit per-zone ZonedDelivery messages here. Instead
            // contribute every (hub, zone, date) cell into the
            // consolidation pool so cross-warehouse contributors converge
            // into ONE delivery van per zone. The actual ZonedDelivery
            // EmitChunks happens after the warehouse-group loop closes,
            // once we know every pickup feeding each pool.
            foreach (var (zoneId, ordersForZone) in ordersByZone)
            {
                var key = (HubId: hubIdForGroup, ZoneId: (Guid?)zoneId, OrderDate: group.Key.OrderDate);
                if (!consolidationPools.TryGetValue(key, out var pool))
                {
                    pool = new ConsolidationPool(hubForGroup);
                    consolidationPools[key] = pool;
                }
                pool.Orders.AddRange(ordersForZone);
                pool.Contributors.Add((pickupRouteId, pickupReturnEstimate));
            }
            if (unzonedOrders.Count > 0)
            {
                // Stragglers without a zone get their own pool keyed on
                // ZoneId = null at this hub. Still consolidates across
                // warehouses (a 'wait for everyone' bucket), but the
                // audit flags it so the operator can fix the source data
                // by populating store.ZoneId.
                audit.Line($"  · {unzonedOrders.Count} unzoned order(s) added to the hub's unzoned bucket — fix store.ZoneId to clean this up.");
                var key = (HubId: hubIdForGroup, ZoneId: (Guid?)null, OrderDate: group.Key.OrderDate);
                if (!consolidationPools.TryGetValue(key, out var pool))
                {
                    pool = new ConsolidationPool(hubForGroup);
                    consolidationPools[key] = pool;
                }
                pool.Orders.AddRange(unzonedOrders);
                pool.Contributors.Add((pickupRouteId, pickupReturnEstimate));
            }
        }

        // ---- Emit consolidated ZonedDelivery batches per (hub, zone, date) ----
        // Each pool may have been fed by multiple warehouse pickups. The
        // ZonedDelivery van waits for the LAST of those pickups to return,
        // then sort runs, then it dispatches. EstimatedDepart =
        // MAX(estimated pickup return across contributors) + hub sort wait.
        if (consolidationPools.Count > 0)
        {
            audit.Raw("");
            audit.Raw($"Per-hub consolidation: {consolidationPools.Count} (hub × zone × date) pool(s) — each van waits for every contributing pickup before dispatching.");

            foreach (var (poolKey, pool) in consolidationPools.OrderBy(kv => kv.Key.HubId).ThenBy(kv => kv.Key.ZoneId))
            {
                var contributors          = pool.Contributors;
                var maxEstimateReturn     = contributors.Max(c => c.EstimateReturn);
                var poolDepartEstimate    = maxEstimateReturn.AddMinutes(pool.Hub.SortingWaitMinutes);

                var zoneLabelForVan = poolKey.ZoneId.HasValue
                    ? (zoneDetailById.TryGetValue(poolKey.ZoneId.Value, out var zlab)
                        ? $"zone {zlab.Code}"
                        : $"zone {poolKey.ZoneId.Value.ToString("N").Substring(0, 6)}")
                    : "unzoned bucket";

                audit.Line($"  · Hub {pool.Hub.Name} · {zoneLabelForVan}: {contributors.Count} contributing pickup(s), max return {maxEstimateReturn:HH:mm} → depart {poolDepartEstimate:HH:mm}");

                var zonedStartIdx = messages.Count;
                EmitChunks(
                    pool.Orders,
                    RouteType.ZonedDelivery,
                    poolKey.ZoneId,
                    chunkSize,
                    // ZonedDelivery messages carry NO warehouseId — the
                    // pickup leg(s) own that. WarehouseId = null also
                    // flips the optimizer into the hub → stops → hub
                    // branch (no warehouse waypoint).
                    null,
                    poolKey.HubId,
                    poolKey.OrderDate,
                    poolDepartEstimate,
                    storeZoneById,
                    zoneDetailById,
                    messages,
                    audit,
                    $"ZonedDelivery van — {zoneLabelForVan}");

                var zonedRouteIds = messages
                    .Skip(zonedStartIdx)
                    .Select(m => m.RouteRequestId)
                    .ToList();
                pickupCorrelations.Add(new PickupCorrelation(
                    contributors.Select(c => c.PickupId).Distinct().ToList(),
                    zonedRouteIds,
                    pool.Hub,
                    poolDepartEstimate));
            }
        }
        audit.ZoneCount  = totalZones.Count;
        audit.RouteCount = messages.Count;

        // ---- Step 6: Direct-optimize is the dev fallback used when
        // Service Bus isn't reachable (port collision with a native
        // RabbitMQ on 5672, etc.). Production deploys leave this unset
        // so the queue does the work asynchronously off the request
        // thread.
        var directOptimize = string.Equals(
            Environment.GetEnvironmentVariable("DirectOptimizer"), "true",
            StringComparison.OrdinalIgnoreCase);

        try
        {
            if (directOptimize)
            {
                foreach (var message in messages)
                    await _optimizer.OptimizeRoute(message, ct);
            }
            else
            {
                foreach (var message in messages)
                    await _bus.PublishAsync(ServiceBusQueues.RoutesOptimize, message, ct);
            }
        }
        catch (Exception ex)
        {
            // Any single message failing shouldn't take down the caller —
            // the orders are already saved with Status = Scheduled, so a
            // future retrigger (or operator action) can re-queue them.
            _logger.LogError(ex,
                "RouteScheduler: failed to enqueue {Count} optimization message(s) for company {Company}.",
                messages.Count, companyId);
            throw;
        }

        _logger.LogInformation(
            "RouteScheduler: queued {Routes} route(s) for {Orders} order(s) in company {Company} (geocoded: {Stores} stores, {Hubs} hubs, {Warehouses} warehouses; {Ungeocoded} ungeocoded skipped, {Failures} geocode failures)",
            messages.Count, routable.Count, companyId,
            storesGeocoded, hubsGeocoded, warehousesGeocoded,
            ungeocodedCount, geocodeFailures);

        audit.Step(6, "Dispatch");
        audit.Line($"Route messages emitted: {messages.Count} (via {(directOptimize ? "DirectOptimizer" : "Service Bus")})");
        if (audit.DirectDeliveryCount > 0)
            audit.Line($"DirectDelivery candidates (hub bypass): {audit.DirectDeliveryCount}");

        // Post-dispatch refinement — only meaningful in DirectOptimizer mode
        // where all routes are already persisted by the time we get here.
        // For Service Bus mode the optimizer runs async and the routes
        // don't yet exist; skip the refresh and rely on the Haversine
        // estimate. (A queue-driven follow-up would publish a "pickup
        // completed" signal and re-stamp ZonedDelivery rows then.)
        if (directOptimize && pickupCorrelations.Count > 0)
        {
            audit.Step(7, "Refine ZonedDelivery depart times from real pickup durations");
            // Load every contributing pickup across every correlation in
            // one round trip.
            var allPickupIds = pickupCorrelations
                .SelectMany(c => c.ContributingPickupRouteIds)
                .Distinct()
                .ToList();
            var pickupsById = await _db.Routes.IgnoreQueryFilters()
                .Where(r => allPickupIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, ct);

            var refreshedZoned = new Dictionary<Guid, DateTime>();
            foreach (var corr in pickupCorrelations)
            {
                // Each ZonedDelivery batch waits for the SLOWEST of its
                // contributing pickups — that's the moment all cargo for
                // this zone has landed at the hub. Skip refresh entirely
                // if any contributor lacks a HubArrivalTime (its leg
                // hasn't finalised yet); the Haversine estimate stays.
                var contributors = corr.ContributingPickupRouteIds
                    .Select(id => pickupsById.TryGetValue(id, out var r) ? r : null)
                    .ToList();
                if (contributors.Any(r => r is null || r.HubArrivalTime is null))
                {
                    audit.Line($"  · Hub {corr.Hub.Name}: {corr.ContributingPickupRouteIds.Count} contributor(s) — one or more pickups not yet finalised. Skipping refresh.");
                    continue;
                }

                var arrivals = contributors.Select(r => r!.HubArrivalTime!.Value).ToList();
                var maxActualReturn = arrivals.Max();
                var newDepart       = maxActualReturn.AddMinutes(corr.Hub.SortingWaitMinutes);
                var estimateReturn  = corr.EstimatedDepart.AddMinutes(-corr.Hub.SortingWaitMinutes);

                audit.Line($"  · Hub {corr.Hub.Name}:");
                audit.Line($"      Contributing pickups: {corr.ContributingPickupRouteIds.Count} (returns: {string.Join(", ", arrivals.Select(a => a.ToString("HH:mm")))})");
                audit.Line($"      Pickup return (max) — estimate {estimateReturn:HH:mm} (Haversine) → actual {maxActualReturn:HH:mm} (Google)");
                audit.Line($"      ZonedDelivery depart — estimate {corr.EstimatedDepart:HH:mm} → refreshed {newDepart:HH:mm} (+{corr.Hub.SortingWaitMinutes} min sort)");
                audit.Line($"      {corr.ZonedRouteIds.Count} ZonedDelivery route(s) restamped.");

                foreach (var zoneRouteId in corr.ZonedRouteIds)
                    refreshedZoned[zoneRouteId] = newDepart;
            }

            if (refreshedZoned.Count > 0)
            {
                var ids = refreshedZoned.Keys.ToList();
                var rows = await _db.Routes.IgnoreQueryFilters()
                    .Where(r => ids.Contains(r.Id))
                    .ToListAsync(ct);
                var nowUtc = DateTime.UtcNow;
                foreach (var r in rows)
                {
                    r.ScheduledDepartTime = refreshedZoned[r.Id];
                    r.UpdatedAt = nowUtc;
                }
                await _db.SaveChangesAsync(ct);
            }
        }
        else if (!directOptimize && pickupCorrelations.Count > 0)
        {
            audit.Step(7, "Refine ZonedDelivery depart times — DEFERRED");
            audit.Line("Service Bus mode: routes haven't materialised yet at scheduler return.");
            audit.Line("ZonedDelivery ScheduledDepartTime stays at the Haversine estimate until a");
            audit.Line("follow-up consumer wires the 'pickup completed' refresh.");
        }

        var summary =
            $"{routable.Count} order(s) → {messages.Count} route(s) across "
            + $"{audit.WarehouseCount} warehouse(s), {audit.ZoneCount} zone(s)"
            + (audit.DirectDeliveryCount > 0 ? $", {audit.DirectDeliveryCount} hub-bypass" : "");
        await TryPersistAuditAsync(audit, summary, ct);

        return new RouteEnqueueResult(
            RoutesQueued:       messages.Count,
            OrdersRouted:       routable.Count,
            OrdersUngeocoded:   ungeocodedCount,
            NoActiveHub:        false,
            StoresGeocoded:     storesGeocoded,
            HubsGeocoded:       hubsGeocoded,
            WarehousesGeocoded: warehousesGeocoded,
            GeocodeFailures:    geocodeFailures);
    }

    /// <summary>
    /// Persists the optimization audit row. Failures are swallowed-with-log:
    /// auditing must never block dispatch, and a partial audit (e.g. one
    /// captured before a mid-run failure) is more useful than nothing.
    /// </summary>
    private async Task TryPersistAuditAsync(
        OptimizationAuditBuilder audit, string summary, CancellationToken ct)
    {
        try
        {
            _db.OptimizationAudits.Add(audit.ToAudit(summary));
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RouteScheduler: failed to persist optimization audit for company {Company}.",
                audit.CompanyId);
        }
    }

    // -----------------------------------------------------------------------
    // Reference-data geocoding
    // -----------------------------------------------------------------------

    private async Task<(int Stores, int Hubs, int Warehouses, int Failures)>
        GeocodeReferenceDataAsync(
            IReadOnlyCollection<Order> orders,
            Guid companyId,
            CancellationToken ct)
    {
        var failures = 0;

        // Stores referenced by the orders that don't yet have coordinates.
        var storeIds = orders
            .Where(o => o.StoreId.HasValue)
            .Select(o => o.StoreId!.Value)
            .Distinct()
            .ToList();
        var storesGeocoded = 0;
        if (storeIds.Count > 0)
        {
            var stores = await _db.Stores.IgnoreQueryFilters()
                .Where(s => storeIds.Contains(s.Id) && (s.Latitude == null || s.Longitude == null))
                .ToListAsync(ct);

            (storesGeocoded, var sFailures) = await BatchGeocodeAsync(
                stores,
                getAddress: s => s.FullAddress,
                applyResult: (s, geo) =>
                {
                    s.Latitude         = geo.Latitude;
                    s.Longitude        = geo.Longitude;
                    s.FormattedAddress = geo.FormattedAddress;
                    s.UpdatedAt        = DateTime.UtcNow;
                },
                describe: s => $"store {s.Id} ({s.Name})",
                ct);
            failures += sFailures;
        }

        // Warehouses referenced by the orders that don't yet have coordinates.
        var warehouseIds = orders
            .Where(o => o.WarehouseId.HasValue)
            .Select(o => o.WarehouseId!.Value)
            .Distinct()
            .ToList();
        var warehousesGeocoded = 0;
        if (warehouseIds.Count > 0)
        {
            var warehouses = await _db.Warehouses.IgnoreQueryFilters()
                .Where(w => warehouseIds.Contains(w.Id) && (w.Latitude == null || w.Longitude == null))
                .ToListAsync(ct);

            (warehousesGeocoded, var wFailures) = await BatchGeocodeAsync(
                warehouses,
                getAddress: w => w.FullAddress,
                applyResult: (w, geo) =>
                {
                    w.Latitude         = geo.Latitude;
                    w.Longitude        = geo.Longitude;
                    w.FormattedAddress = geo.FormattedAddress;
                    w.UpdatedAt        = DateTime.UtcNow;
                },
                describe: w => $"warehouse {w.Id} ({w.BusinessName})",
                ct);
            failures += wFailures;
        }

        // All active hubs for the company. Cheap (typically 1-3) and means
        // any future routing path through any hub reads from cache.
        var hubsToGeocode = await _db.Hubs.IgnoreQueryFilters()
            .Where(h => h.CompanyId == companyId
                     && h.IsActive
                     && (h.Latitude == null || h.Longitude == null))
            .ToListAsync(ct);

        var hubsGeocoded = 0;
        if (hubsToGeocode.Count > 0)
        {
            (hubsGeocoded, var hFailures) = await BatchGeocodeAsync(
                hubsToGeocode,
                getAddress: h => h.FullAddress,
                applyResult: (h, geo) =>
                {
                    h.Latitude         = geo.Latitude;
                    h.Longitude        = geo.Longitude;
                    h.FormattedAddress = geo.FormattedAddress;
                    h.UpdatedAt        = DateTime.UtcNow;
                },
                describe: h => $"hub {h.Id} ({h.Name})",
                ct);
            failures += hFailures;
        }

        // Single SaveChanges for everything we just geocoded. EF tracks
        // the entities since they were loaded from this same context.
        if (storesGeocoded > 0 || warehousesGeocoded > 0 || hubsGeocoded > 0)
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "RouteScheduler geocode pass: {Stores} stores, {Hubs} hubs, {Warehouses} warehouses geocoded for company {Company}",
                storesGeocoded, hubsGeocoded, warehousesGeocoded, companyId);
        }

        return (storesGeocoded, hubsGeocoded, warehousesGeocoded, failures);
    }

    /// <summary>
    /// Runs Google Geocoding for every entity in <paramref name="entities"/>
    /// in parallel (capped at <see cref="GeocodeConcurrency"/>) and applies
    /// the result via <paramref name="applyResult"/>. Returns successes
    /// and failures separately so the caller can surface partial outcomes.
    /// Does NOT call SaveChanges — the caller batches that across all
    /// reference types in one round-trip.
    /// </summary>
    private async Task<(int Succeeded, int Failed)> BatchGeocodeAsync<T>(
        IReadOnlyCollection<T> entities,
        Func<T, string> getAddress,
        Action<T, GeocodedAddress> applyResult,
        Func<T, string> describe,
        CancellationToken ct)
    {
        if (entities.Count == 0) return (0, 0);

        var sem      = new SemaphoreSlim(GeocodeConcurrency);
        var ok       = 0;
        var failed   = 0;

        await Task.WhenAll(entities.Select(async entity =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var addr = getAddress(entity);
                if (string.IsNullOrWhiteSpace(addr))
                {
                    Interlocked.Increment(ref failed);
                    _logger.LogWarning("Geocode skipped — empty address for {Desc}", describe(entity));
                    return;
                }

                var geo = await _maps.GeocodeAsync(addr, ct);
                if (geo is null)
                {
                    Interlocked.Increment(ref failed);
                    _logger.LogWarning("Geocode failed for {Desc}", describe(entity));
                    return;
                }

                applyResult(entity, geo);
                Interlocked.Increment(ref ok);
            }
            finally
            {
                sem.Release();
            }
        }));

        return (ok, failed);
    }

    /// <summary>Zone code + district info for the audit-detail block.</summary>
    private sealed record ZoneDetail(string Code, int DistrictNumber, string DistrictName);

    /// <summary>
    /// Pairs one or more Pickup routes with the ZonedDelivery routes that
    /// depend on them. A ZonedDelivery van that consolidates cargo from
    /// multiple warehouse pickups can only dispatch once the LAST of those
    /// pickups has returned to the hub — so the post-dispatch refresh
    /// reads <c>MAX(pickup.HubArrivalTime)</c> across
    /// <see cref="ContributingPickupRouteIds"/> and re-stamps every
    /// ZonedDelivery route's <c>ScheduledDepartTime</c> to
    /// <c>that-max + Hub.SortingWaitMinutes</c>.
    /// </summary>
    private sealed record PickupCorrelation(
        IReadOnlyList<Guid> ContributingPickupRouteIds,
        IReadOnlyList<Guid> ZonedRouteIds,
        Hub              Hub,
        DateTime         EstimatedDepart);

    /// <summary>
    /// One (hub × zone × orderDate) bucket of orders waiting to be put on
    /// a consolidated ZonedDelivery van. The same bucket can collect
    /// orders that arrived via different warehouse pickups — the van
    /// dispatches once every contributing pickup has returned. The
    /// scheduler emits the bucket as one or more ZonedDelivery messages
    /// (chunked by MaxStopsPerRoute) at the end of the warehouse-group
    /// loop.
    /// </summary>
    private sealed class ConsolidationPool
    {
        public ConsolidationPool(Hub hub) { Hub = hub; }
        public Hub Hub { get; }
        public List<Order> Orders { get; } = new();
        public List<(Guid PickupId, DateTime EstimateReturn)> Contributors { get; } = new();
    }

    /// <summary>
    /// Writes a multi-line audit block for a single stop — name, license,
    /// address, zone assignment, customer, sales order — so an administrator
    /// can scan the audit and verify each order landed at the right store
    /// and in the right zone. <paramref name="stopLabel"/> is the prefix
    /// (e.g. "[1]" for the first delivery, "·" for a cargo manifest line).
    /// </summary>
    private static void WriteStopDetail(
        OptimizationAuditBuilder audit,
        Order order,
        string stopLabel,
        IReadOnlyDictionary<Guid, Guid?> storeZoneById,
        IReadOnlyDictionary<Guid, ZoneDetail> zoneDetailById)
    {
        ZoneDetail? zone = null;
        if (order.StoreId.HasValue
            && storeZoneById.TryGetValue(order.StoreId.Value, out var zid)
            && zid.HasValue
            && zoneDetailById.TryGetValue(zid.Value, out var detail))
        {
            zone = detail;
        }
        var zoneLine = zone is null
            ? "Zone:     — (store has no zone assignment)"
            : $"Zone:     {zone.Code}  ·  District {zone.DistrictNumber} — {zone.DistrictName}";

        audit.Line($"    {stopLabel} {order.StoreName}");
        audit.Line($"        License:  {order.LicenseNumber ?? "—"}");
        audit.Line($"        Address:  {order.Address}, {order.City}, {order.State} {order.Zip}");
        audit.Line($"        {zoneLine}");
        if (!string.IsNullOrWhiteSpace(order.Customer))
            audit.Line($"        Customer: {order.Customer}");
        if (!string.IsNullOrWhiteSpace(order.SalesOrderNumber))
            audit.Line($"        SO #:     {order.SalesOrderNumber}");
    }

    /// <summary>
    /// Splits <paramref name="orders"/> into chunks of at most
    /// <paramref name="chunkSize"/> and emits one
    /// <see cref="RouteOptimizationRequestMessage"/> per chunk into
    /// <paramref name="messages"/>. Each chunk gets a fresh
    /// <c>RouteRequestId</c> and a per-stop audit block so the operator
    /// can verify the orders landed in the right zone.
    /// </summary>
    private static void EmitChunks(
        IReadOnlyList<Order> orders,
        RouteType kind,
        Guid?     zoneId,
        int       chunkSize,
        Guid?     warehouseId,
        Guid      hubId,
        DateTime  deliveryDate,
        DateTime? scheduledDepartTime,
        IReadOnlyDictionary<Guid, Guid?> storeZoneById,
        IReadOnlyDictionary<Guid, ZoneDetail> zoneDetailById,
        List<RouteOptimizationRequestMessage> messages,
        OptimizationAuditBuilder audit,
        string label)
    {
        for (var i = 0; i < orders.Count; i += chunkSize)
        {
            var chunk = orders.Skip(i).Take(chunkSize).ToList();
            messages.Add(new RouteOptimizationRequestMessage(
                Guid.NewGuid(),
                orders[0].CompanyId,
                null,
                hubId,
                warehouseId,
                deliveryDate,
                chunk.Select(o => o.Id).ToList(),
                DateTime.UtcNow,
                kind,
                zoneId,
                scheduledDepartTime));
            var departLabel = scheduledDepartTime is DateTime d
                ? $" — depart {d:HH:mm}"
                : "";
            audit.Line($"  · {label}: {chunk.Count} stop(s)" + departLabel);
            for (int idx = 0; idx < chunk.Count; idx++)
                WriteStopDetail(audit, chunk[idx], $"[{idx + 1}]", storeZoneById, zoneDetailById);
        }
    }

    /// <summary>
    /// Returns the Id of the hub geographically closest to the given
    /// warehouse coordinates (Haversine). Hubs without coordinates are
    /// treated as "infinitely far" — they only get picked when nothing
    /// else has coords either, and only as a last-ditch fallback.
    /// </summary>
    private static Guid PickClosestHub(IReadOnlyList<Hub> hubs, double warehouseLat, double warehouseLng)
    {
        var bestId   = hubs[0].Id;
        var bestDist = double.MaxValue;
        foreach (var h in hubs)
        {
            if (!h.Latitude.HasValue || !h.Longitude.HasValue) continue;
            var d = GeoMath.HaversineMeters(h.Latitude.Value, h.Longitude.Value, warehouseLat, warehouseLng);
            if (d < bestDist)
            {
                bestDist = d;
                bestId   = h.Id;
            }
        }
        return bestId;
    }

    /// <summary>
    /// Copies Latitude/Longitude/FormattedAddress from each order's linked
    /// Store onto the order itself when the order is missing them. Run
    /// after the store-geocode pass so the order picks up coordinates that
    /// were just resolved this same call.
    /// </summary>
    private async Task BackfillOrderCoordinatesFromStoresAsync(
        IReadOnlyCollection<Order> orders, CancellationToken ct)
    {
        var ungeocoded = orders
            .Where(o => !o.Latitude.HasValue || !o.Longitude.HasValue)
            .ToList();
        if (ungeocoded.Count == 0) return;

        var storeIds = ungeocoded
            .Where(o => o.StoreId.HasValue)
            .Select(o => o.StoreId!.Value)
            .Distinct()
            .ToList();
        if (storeIds.Count == 0) return;

        var stores = await _db.Stores.IgnoreQueryFilters()
            .Where(s => storeIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);

        var nowUtc     = DateTime.UtcNow;
        var backfilled = 0;
        foreach (var order in ungeocoded)
        {
            if (!order.StoreId.HasValue) continue;
            if (!stores.TryGetValue(order.StoreId.Value, out var store)) continue;
            if (!store.Latitude.HasValue || !store.Longitude.HasValue) continue;

            order.Latitude         = store.Latitude;
            order.Longitude        = store.Longitude;
            order.FormattedAddress = store.FormattedAddress ?? order.FormattedAddress;
            order.UpdatedAt        = nowUtc;
            backfilled++;
        }

        if (backfilled > 0)
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "RouteScheduler: back-filled coordinates from store onto {Count} order(s).",
                backfilled);
        }
    }
}
