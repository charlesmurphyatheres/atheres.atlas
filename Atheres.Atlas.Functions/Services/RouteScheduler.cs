using Atheres.Atlas.Data;
using Atheres.Atlas.Domain.Entities;
using Atheres.Atlas.Domain.Messages;
using Atheres.Atlas.Functions.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Functions.Services;

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
        CancellationToken ct = default)
    {
        if (orders.Count == 0)
            return new RouteEnqueueResult(0, 0, 0, false);

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

        if (routable.Count == 0)
        {
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
        if (hubs.Count == 0)
        {
            _logger.LogWarning(
                "RouteScheduler: company {Company} has no active hub — skipping enqueue ({Count} orders).",
                companyId, routable.Count);
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

        var company = await _db.Companies.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == companyId, ct);
        var maxStopsPerRoute = Math.Clamp(company?.MaxStopsPerRoute ?? 12, 1, GoogleDirectionsHardCap);

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
        var groups       = routable.GroupBy(o => o.WarehouseId).ToList();
        var groupDate    = DateTime.UtcNow.Date;
        var messages     = new List<RouteOptimizationRequestMessage>();

        foreach (var group in groups)
        {
            var pickupSlot = group.Key.HasValue ? 1 : 0;
            // Subtract the pickup waypoint when present so the actual
            // delivery count never exceeds the configured per-route cap.
            var chunkSize = Math.Max(1, maxStopsPerRoute - pickupSlot);
            var orderedIds = group.Select(o => o.Id).ToList();

            // Pick the hub closest to this group's warehouse via Haversine
            // distance — cheap, no extra Google calls, and produces the
            // intuitive "start at the depot nearest the supplier" loop.
            // Falls back to the first active hub when the warehouse has
            // no coordinates yet (shouldn't happen after the geocode pass
            // above, but be defensive).
            Guid hubIdForGroup;
            if (group.Key.HasValue
                && warehousesById.TryGetValue(group.Key.Value, out var wh)
                && wh.Latitude.HasValue && wh.Longitude.HasValue)
            {
                hubIdForGroup = PickClosestHub(hubs, wh.Latitude.Value, wh.Longitude.Value);
            }
            else
            {
                hubIdForGroup = hubs[0].Id;
            }

            for (var i = 0; i < orderedIds.Count; i += chunkSize)
            {
                var chunk = orderedIds.Skip(i).Take(chunkSize).ToList();
                messages.Add(new RouteOptimizationRequestMessage(
                    Guid.NewGuid(),
                    companyId,
                    null,
                    hubIdForGroup,
                    group.Key,
                    groupDate,
                    chunk,
                    DateTime.UtcNow));
            }
        }

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
