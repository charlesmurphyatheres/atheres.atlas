using System.Text.Json;
using Atheres.Atlas.Data;
using Atheres.Atlas.Data.Repositories;
using Atheres.Atlas.Domain.Constants;
using Atheres.Atlas.Domain.Entities;
using Atheres.Atlas.Domain.Enums;
using Atheres.Atlas.Domain.Messages;
using Atheres.Atlas.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Functions.Agents;

/// <summary>
/// Receives a route optimization request, calls the Google Maps Directions API
/// with waypoint optimization, persists the optimized route, and queues confirmation requests.
/// Queue: atlas-routes-optimize
/// </summary>
public class RouteOptimizationAgent
{
    private readonly IOrderRepository _orders;
    private readonly IRouteRepository _routes;
    private readonly IUserSettingsRepository _settings;
    private readonly IGoogleMapsService _maps;
    private readonly IServiceBusPublisher _bus;
    private readonly AtlasDbContext _db;
    private readonly ILogger<RouteOptimizationAgent> _logger;

    public RouteOptimizationAgent(
        IOrderRepository orders,
        IRouteRepository routes,
        IUserSettingsRepository settings,
        IGoogleMapsService maps,
        IServiceBusPublisher bus,
        AtlasDbContext db,
        ILogger<RouteOptimizationAgent> logger)
    {
        _orders = orders;
        _routes = routes;
        _settings = settings;
        _maps = maps;
        _bus = bus;
        _db = db;
        _logger = logger;
    }

    [Function(nameof(OptimizeRoute))]
    public async Task OptimizeRoute(
        [ServiceBusTrigger(ServiceBusQueues.RoutesOptimize, Connection = "ServiceBusConnection")]
        RouteOptimizationRequestMessage message,
        CancellationToken ct)
    {
        _logger.LogInformation("Optimizing route for {Count} orders on {Date:yyyy-MM-dd}",
            message.OrderIds.Count, message.DeliveryDate);

        await SafePublish(() => _bus.PublishAsync(ServiceBusQueues.Audit, new AuditMessage(
            AuditEventType.RouteOptimizationStarted, nameof(RouteOptimizationAgent),
            message.CompanyId, null, message.RouteRequestId,
            null, null,
            $"Optimizing {message.OrderIds.Count} stops for {message.DeliveryDate:yyyy-MM-dd}",
            true, null, DateTime.UtcNow), ct),
            "start audit event");

        // Resolve the route's origin and final destination. The route always
        // begins and ends at the home hub of the assigned van — that is the
        // physical depot the truck is based out of. The message's HubId is
        // only used as a fallback for unassigned (TruckId == null) routes or
        // for trucks whose HubId hasn't been set yet.
        Guid effectiveHubId = message.HubId;
        if (message.TruckId.HasValue)
        {
            var truck = await _db.Trucks.IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Id == message.TruckId.Value, ct);
            if (truck?.HubId is Guid truckHubId)
            {
                if (truckHubId != message.HubId)
                {
                    _logger.LogInformation(
                        "Route {RouteRequestId}: using truck home hub {TruckHubId} as origin/destination instead of message hub {RequestedHubId}.",
                        message.RouteRequestId, truckHubId, message.HubId);
                }
                effectiveHubId = truckHubId;
            }
        }

        var hub = await _db.Hubs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(h => h.Id == effectiveHubId, ct)
            ?? throw new InvalidOperationException($"Hub {effectiveHubId} not found.");

        // First-use geocode self-heal for the hub. Hubs are created via
        // Auth.Functions HubFunctions.Create which doesn't (and can't
        // easily) call Google Maps; we cache the result here on the way
        // into the routing pipeline so subsequent runs skip the lookup.
        await EnsureHubGeocodedAsync(hub, ct);

        // Load the company so we can read company-wide delivery window. The
        // window is a company-level policy (not per-truck), so a missing
        // company would mean a malformed request — fall back to defaults.
        var company = await _db.Companies.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == message.CompanyId, ct);

        // Load truck settings for confirmation deadline + per-stop wait. If
        // none are configured for this truck, fall back to the entity
        // defaults (3h confirmation deadline, 0min wait).
        var settingsKey = message.TruckId?.ToString() ?? "default";
        var userSettings = await _settings.GetByUserIdAsync(settingsKey, ct)
                           ?? new UserRouteSettings { UserId = settingsKey, CompanyId = message.CompanyId };

        var deliveryWindowStart = company?.DeliveryWindowStart ?? new TimeSpan(8, 0, 0);

        // Load warehouse if specified (it becomes the first waypoint for pickup)
        Warehouse? warehouse = null;
        if (message.WarehouseId.HasValue)
        {
            warehouse = await _db.Warehouses.IgnoreQueryFilters()
                .FirstOrDefaultAsync(w => w.Id == message.WarehouseId.Value, ct);
            if (warehouse is not null)
            {
                // Same self-heal as the hub: cache the warehouse's coords
                // on first use so future routing runs hit the DB instead
                // of Google's geocoder.
                await EnsureWarehouseGeocodedAsync(warehouse, ct);
            }
        }

        // ---- Pickup short-circuit -------------------------------------------
        // A Pickup message represents the pickup van's warehouse → hub trip.
        // It has order IDs attached (so we know what's on the truck) but no
        // delivery stops materialise here — the actual deliveries are handled
        // by the paired ZonedDelivery routes. Stamp HubArrivalTime so those
        // paired routes can compute their ScheduledDepartTime later.
        if (message.Kind == RouteType.Pickup)
        {
            await BuildPickupRouteAsync(message, hub, warehouse, deliveryWindowStart, ct);
            return;
        }

        // Load all orders. Stale rows (more than a day old) get flipped to
        // RouteOmitted right here as a backstop — every trigger endpoint
        // already filters them out, but messages can sit on the queue long
        // enough to age in, and a future caller may forget to enforce the
        // rule. Marking them RouteOmitted (rather than silently skipping)
        // makes the omission visible to operators and prevents the next
        // optimization run from rediscovering them.
        var nowUtc       = DateTime.UtcNow;
        var orderList    = new List<Order>();
        var staleOrders  = new List<Order>();
        foreach (var orderId in message.OrderIds)
        {
            var order = await _orders.GetByIdAsync(orderId, ct);
            if (order is null)
            {
                _logger.LogWarning("Order {OrderId} not found, skipping", orderId);
                continue;
            }
            if (order.Latitude is null || order.Longitude is null)
            {
                _logger.LogWarning("Order {OrderId} has no geocode, skipping", orderId);
                continue;
            }
            if (RoutingPolicy.IsTooOldToRoute(order.OrderDate, nowUtc))
            {
                staleOrders.Add(order);
                _logger.LogWarning(
                    "Order {OrderId} is more than a day old ({OrderDate:o}); flipping to RouteOmitted instead of routing.",
                    orderId, order.OrderDate);
                continue;
            }
            orderList.Add(order);
        }

        if (staleOrders.Count > 0)
        {
            foreach (var s in staleOrders)
            {
                s.Status    = OrderStatus.RouteOmitted;
                s.UpdatedAt = nowUtc;
                await _orders.UpdateAsync(s, ct);
            }
            await _orders.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Route {RouteRequestId}: flipped {Stale} stale order(s) to RouteOmitted.",
                message.RouteRequestId, staleOrders.Count);
        }

        if (orderList.Count == 0)
        {
            _logger.LogError("No routable orders found for route request {RouteRequestId}", message.RouteRequestId);
            return;
        }

        // Build the route. The driver's physical day is:
        //   1. Hub → Warehouse (pickup of products) — pinned first
        //   2. Warehouse → optimized(deliveries) → Hub
        // We can't pin the warehouse inside Google Directions' optimize:true
        // (it reorders ALL waypoints), so we issue two separate Directions
        // calls and stitch the results. Without a warehouse, we fall back
        // to the original single-call optimize-from-hub behavior.
        //
        // Coordinate strings ("lat,lng") are preferred over free-form
        // addresses so Google doesn't re-geocode on every routing run —
        // the cached geocodes from the scheduler's earlier pass are used
        // directly.
        var hubPoint = BuildRoutePoint(hub.Latitude, hub.Longitude, hub.FormattedAddress ?? hub.FullAddress);
        var deliveryWaypoints = orderList.Select(o =>
            BuildRoutePoint(o.Latitude, o.Longitude, o.FormattedAddress ?? o.FullAddress)).ToList();

        OptimizedRoute? pickupLeg     = null;   // Hub → Warehouse, when warehouse is present
        OptimizedRoute  deliveryLoop;            // Warehouse|Hub → optimized(deliveries) → Hub

        if (warehouse is not null)
        {
            var warehousePoint = BuildRoutePoint(
                warehouse.Latitude, warehouse.Longitude,
                warehouse.FormattedAddress ?? warehouse.FullAddress);

            // DirectDelivery skips the Hub→Warehouse pickup leg — the van
            // starts at the warehouse (already loaded by the morning
            // pickup) and goes straight to its zoned stops. Legacy keeps
            // the old "depot to supplier then deliveries" shape.
            if (message.Kind != RouteType.DirectDelivery)
            {
                pickupLeg = await _maps.GetOptimizedRouteAsync(hubPoint, warehousePoint, Array.Empty<string>(), ct);
                if (pickupLeg is null)
                {
                    _logger.LogError("Hub→Warehouse leg failed for {RouteRequestId}", message.RouteRequestId);
                    await SafePublish(() => _bus.PublishAsync(ServiceBusQueues.Audit, new AuditMessage(
                        AuditEventType.RouteOptimizationFailed, nameof(RouteOptimizationAgent),
                        message.CompanyId, null, message.RouteRequestId,
                        null, null, "Google Maps API returned no hub→warehouse route", false,
                        "No pickup leg", DateTime.UtcNow), ct),
                        "failure audit event");
                    return;
                }
            }

            // Delivery loop: warehouse is the origin, hub is the destination,
            // and all the deliveries get optimized in between. This guarantees
            // the warehouse is the first physical stop after the hub.
            var loop = await _maps.GetOptimizedRouteAsync(warehousePoint, hubPoint, deliveryWaypoints, ct);
            if (loop is null)
            {
                _logger.LogError("Warehouse→deliveries→Hub leg failed for {RouteRequestId}", message.RouteRequestId);
                await SafePublish(() => _bus.PublishAsync(ServiceBusQueues.Audit, new AuditMessage(
                    AuditEventType.RouteOptimizationFailed, nameof(RouteOptimizationAgent),
                    message.CompanyId, null, message.RouteRequestId,
                    null, null, "Google Maps API returned no delivery loop", false,
                    "No delivery loop", DateTime.UtcNow), ct),
                    "failure audit event");
                return;
            }
            deliveryLoop = loop;
        }
        else
        {
            // No warehouse pickup — original single-call behavior:
            // Hub → optimized(deliveries) → Hub.
            var loop = await _maps.GetOptimizedRouteAsync(hubPoint, hubPoint, deliveryWaypoints, ct);
            if (loop is null)
            {
                _logger.LogError("Route optimization failed for {RouteRequestId}", message.RouteRequestId);
                await SafePublish(() => _bus.PublishAsync(ServiceBusQueues.Audit, new AuditMessage(
                    AuditEventType.RouteOptimizationFailed, nameof(RouteOptimizationAgent),
                    message.CompanyId, null, message.RouteRequestId,
                    null, null, "Google Maps API returned no route", false,
                    "No route returned", DateTime.UtcNow), ct),
                    "failure audit event");
                return;
            }
            deliveryLoop = loop;
        }

        // Aggregate distance/duration across both legs so the route summary
        // reflects the full driving day (hub→warehouse→deliveries→hub),
        // not just the optimized loop.
        var pickupDistance = pickupLeg?.TotalDistanceMeters  ?? 0;
        var pickupDuration = pickupLeg?.TotalDurationSeconds ?? 0;

        // Warehouse loading wait — applies whenever the route visits a
        // warehouse, i.e. Legacy with-warehouse (hub→warehouse→stops) AND
        // DirectDelivery (warehouse→stops). For ZonedDelivery the
        // warehouse is null and the wait is 0. Folded into totalDuration
        // so the route's "driver day" matches reality, and into
        // runningTime so per-stop ETAs reflect the loading dock time.
        var warehouseWaitSeconds = (warehouse?.LoadingWaitMinutes ?? 0) * 60;

        var totalDistance  = pickupDistance + deliveryLoop.TotalDistanceMeters;
        var totalDuration  = pickupDuration + warehouseWaitSeconds + deliveryLoop.TotalDurationSeconds;

        // Resolve when this route actually leaves first — every per-stop
        // ETA below builds on top of it. For ZonedDelivery vans the
        // scheduler stamps ScheduledDepartTime = pickup-return + sort
        // wait on the message; the van can't begin delivering until then
        // because it doesn't physically have the cargo. For
        // DirectDelivery / Legacy / Pickup the message either carries a
        // pre-computed value or we fall back to the window start.
        var deliveryStart = message.DeliveryDate.Date.Add(deliveryWindowStart);
        DateTime? scheduledDepart = message.ScheduledDepartTime;
        if (scheduledDepart is null)
        {
            if (message.Kind == RouteType.ZonedDelivery)
                scheduledDepart = deliveryStart.AddMinutes(hub.SortingWaitMinutes);
            else if (message.Kind == RouteType.DirectDelivery)
                scheduledDepart = deliveryStart;
        }

        // Anchor the running clock to scheduledDepart (when set) instead
        // of the bare delivery-window start — otherwise a ZonedDelivery
        // van's stop ETAs would read as if it left at 08:00 sharp, even
        // though the van actually waits at HUB_ROM for the pickup-return
        // plus sort wait before it can dispatch. The pickup leg time +
        // warehouse loading wait still stack on top for the route types
        // that physically visit a warehouse on the way out (Legacy with
        // warehouse, DirectDelivery); for ZonedDelivery both are 0 so
        // the running clock starts exactly at scheduledDepart.
        var effectiveDepart = scheduledDepart ?? deliveryStart;
        var runningTime     = effectiveDepart.AddSeconds(pickupDuration + warehouseWaitSeconds);

        var route = new DeliveryRoute
        {
            Id = message.RouteRequestId,
            CompanyId = message.CompanyId,
            TruckId = message.TruckId,
            HubId = effectiveHubId,
            WarehouseId = message.WarehouseId,
            RouteType = message.Kind,
            ZoneId = message.ZoneId,
            ScheduledDepartTime = scheduledDepart,
            DeliveryDate = message.DeliveryDate,
            // Start / End are the driver's home base — always the hub,
            // regardless of RouteType. DirectDelivery's optimizer geometry
            // begins at the warehouse (we skip the Hub→Warehouse Google
            // query to save an API call), but conceptually the driver
            // starts and ends their day at the hub. The warehouse is a
            // pickup stop surfaced separately on the route via
            // WarehouseId / the WarehousePickup projection — not the
            // route's depot. Labelling the warehouse as "Start · Depot"
            // both confused operators (the warehouse address rendered
            // next to the green S chip alongside the amber W "Pickup"
            // row) and conflicted with the home-hub semantics used
            // everywhere else (Truck.HubId, returns-to-hub, etc.).
            StartAddress  = hub.FormattedAddress ?? hub.FullAddress,
            StartLatitude  = hub.Latitude  ?? 0,
            StartLongitude = hub.Longitude ?? 0,
            EndAddress = hub.FormattedAddress ?? hub.FullAddress,
            EndLatitude = hub.Latitude ?? 0,
            EndLongitude = hub.Longitude ?? 0,
            TotalDistanceMeters = totalDistance,
            TotalDurationSeconds = totalDuration,
            TotalStops = orderList.Count,
            IsOptimized = true,
            // Stitch the pickup leg + delivery loop into a single
            // continuous polyline so the map draws the full
            // Hub → Warehouse → optimized stops → Hub loop. Google's
            // encoded-polyline format is delta-based, so naive string
            // concatenation breaks; PolylineCodec.Concat decodes both,
            // dedupes the joint point, and re-encodes.
            OverviewPolyline = pickupLeg is not null
                ? PolylineCodec.Concat(pickupLeg.OverviewPolyline, deliveryLoop.OverviewPolyline)
                : deliveryLoop.OverviewPolyline,
            OptimizedWaypointOrder = JsonSerializer.Serialize(deliveryLoop.WaypointOrder)
        };

        await _routes.AddAsync(route, ct);
        await _routes.SaveChangesAsync(ct);

        // Walk the optimized delivery loop, building one RouteStop per
        // delivery in order. Warehouse is no longer a waypoint here (it's
        // the loop's origin), so we don't have to skip-or-offset anything
        // — every entry in the optimized order is a real delivery.
        var confirmationMessages = new List<ConfirmationRequestMessage>();
        int stopSequence = 0;

        for (int i = 0; i < deliveryLoop.WaypointOrder.Count; i++)
        {
            var orderIndex = deliveryLoop.WaypointOrder[i];
            var order = orderList[orderIndex];
            stopSequence++;
            var leg = i < deliveryLoop.Legs.Count ? deliveryLoop.Legs[i] : null;

            // Advance running time by leg duration + service time
            if (leg is not null)
                runningTime = runningTime.AddSeconds(leg.DurationSeconds);

            var estimatedArrival = runningTime;
            var confirmationDeadline = estimatedArrival.AddHours(-userSettings.ConfirmationDeadlineHours);

            // Create route stop
            var stop = new RouteStop
            {
                RouteId = route.Id,
                OrderId = order.Id,
                Sequence = stopSequence,
                Address = order.FormattedAddress ?? order.FullAddress,
                Latitude = order.Latitude ?? 0,
                Longitude = order.Longitude ?? 0,
                StoreName = order.StoreName,
                EstimatedArrival = estimatedArrival,
                LegDistanceMeters = leg?.DistanceMeters ?? 0,
                LegDurationSeconds = leg?.DurationSeconds ?? 0
            };
            route.Stops.Add(stop);
            // Force Added state — after the route itself is saved and becomes
            // Unchanged, EF's collection-fixup can otherwise read the
            // already-populated Guid on a freshly-constructed RouteStop as
            // "this row exists" and emit a phantom UPDATE instead of INSERT.
            _db.Entry(stop).State = EntityState.Added;

            // Update order
            order.RouteId = route.Id;
            order.StopSequence = stopSequence;
            order.ExpectedDeliveryDate = estimatedArrival;
            order.ConfirmationDeadline = confirmationDeadline;
            order.Status = OrderStatus.RouteOptimized;

            var confirmationToken = Guid.NewGuid().ToString("N");
            order.ConfirmationToken = confirmationToken;

            await _orders.UpdateAsync(order, ct);

            // Add service time + per-stop wait to running total. The wait
            // applies only to delivery stops in this loop — the route's
            // initial start and final return to the home hub are stored on
            // DeliveryRoute itself (not as RouteStop rows) so they never
            // pass through here, which matches the requirement that wait
            // does not apply to depot bookends.
            runningTime = estimatedArrival
                .AddMinutes(stop.ServiceTimeMinutes)
                .AddMinutes(userSettings.WaitMinutesPerStop);

            // Queue confirmation request
            var confirmation = new DeliveryConfirmation
            {
                OrderId = order.Id,
                Token = confirmationToken,
                EmailSentTo = order.Email,
                SmsSentTo = order.Phone,
                ExpiresAt = confirmationDeadline,
                Status = ConfirmationStatus.Pending
            };

            confirmationMessages.Add(new ConfirmationRequestMessage(
                confirmation.Id,
                order.Id,
                message.CompanyId,
                order.StoreName,
                order.Email,
                order.Phone,
                order.FormattedAddress ?? order.FullAddress,
                estimatedArrival,
                confirmationDeadline,
                confirmationToken,
                1,
                DateTime.UtcNow));
        }

        // The route was just inserted and is still tracked; the stops we added
        // to route.Stops and the order mutations are flushed by the shared
        // DbContext on SaveChanges. An explicit Update() here would mark the
        // whole Route as Modified and issue a redundant UPDATE — which EF then
        // blows up on as a phantom concurrency conflict when scoping rules
        // tweak the WHERE clause.
        await _db.SaveChangesAsync(ct);

        // In-process direct-invoke mode (dev fallback) calls this method once
        // per warehouse group within the same scope. Release tracked entities
        // so the next invocation starts with a clean ChangeTracker.
        _db.ChangeTracker.Clear();

        // Downstream queue publishes (confirmation emails, audit, notification)
        // are all best-effort: the route is persisted, so a Service Bus outage
        // shouldn't roll the route write back. Each publish is wrapped so one
        // broken queue doesn't prevent the others from being attempted.
        await SafePublish(() => _bus.PublishBatchAsync(ServiceBusQueues.ConfirmationsSend, confirmationMessages, ct),
            "confirmation requests");

        await SafePublish(() => _bus.PublishAsync(ServiceBusQueues.Audit, new AuditMessage(
            AuditEventType.RouteOptimizationCompleted, nameof(RouteOptimizationAgent),
            message.CompanyId, null, route.Id, null, null,
            $"Route with {route.TotalStops} stops, {route.TotalDistanceMiles:F1} miles", true, null, DateTime.UtcNow), ct),
            "audit event");

        await SafePublish(() => _bus.PublishAsync(ServiceBusQueues.Notifications, new NotificationMessage(
            Guid.NewGuid(), message.CompanyId, NotificationType.RouteReady,
            "Route Optimized",
            $"Delivery route for {message.DeliveryDate:MMM dd} with {route.TotalStops} stops is ready.",
            null, route.Id, null, null, DateTime.UtcNow), ct),
            "route-ready notification");

        _logger.LogInformation("Route {RouteId} optimized: {Stops} stops, {Miles:F1} miles",
            route.Id, route.TotalStops, route.TotalDistanceMiles);
    }

    private async Task SafePublish(Func<Task> publish, string label)
    {
        try { await publish(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Downstream {Label} publish failed (route still persisted).", label);
        }
    }

    /// <summary>
    /// Returns "lat,lng" when coordinates are present; falls back to the
    /// free-form address otherwise. Google Directions accepts both, but
    /// passing coordinates avoids a server-side geocode lookup on every
    /// routing call — the whole point of caching them in our DB.
    /// </summary>
    private static string BuildRoutePoint(double? lat, double? lng, string fallbackAddress) =>
        lat.HasValue && lng.HasValue
            ? FormattableString.Invariant($"{lat.Value:F6},{lng.Value:F6}")
            : fallbackAddress;

    /// <summary>
    /// Builds + persists a <see cref="RouteType.Pickup"/> route as a
    /// Hub → Warehouse → Hub round trip. The driver starts the day at
    /// the hub (their home base), drives to the warehouse to load, then
    /// returns to the hub for the sort. HubArrivalTime is stamped from
    /// the start time + total Google leg duration so paired ZonedDelivery
    /// routes know when their sort wait actually begins from Google's
    /// real numbers (the scheduler's Haversine estimate is a fallback).
    /// The order IDs on the message are the truck's cargo manifest — no
    /// RouteStops materialise here and no order Status changes; that's
    /// owned by the paired ZonedDelivery routes.
    /// </summary>
    private async Task BuildPickupRouteAsync(
        RouteOptimizationRequestMessage message,
        Hub hub,
        Warehouse? warehouse,
        TimeSpan deliveryWindowStart,
        CancellationToken ct)
    {
        if (warehouse is null)
        {
            _logger.LogError(
                "Pickup route {RouteRequestId} has no WarehouseId — cannot compute warehouse→hub leg.",
                message.RouteRequestId);
            return;
        }

        var warehousePoint = BuildRoutePoint(
            warehouse.Latitude, warehouse.Longitude,
            warehouse.FormattedAddress ?? warehouse.FullAddress);
        var hubPoint = BuildRoutePoint(hub.Latitude, hub.Longitude, hub.FormattedAddress ?? hub.FullAddress);

        // Two-leg round trip. Issuing both legs through Directions gives us
        // accurate per-leg durations; concatenating the polylines via
        // PolylineCodec lets the map draw the full out-and-back trip in
        // one continuous line.
        var outbound = await _maps.GetOptimizedRouteAsync(hubPoint, warehousePoint, Array.Empty<string>(), ct);
        if (outbound is null)
        {
            _logger.LogError("Pickup outbound leg failed for {RouteRequestId} (hub {HubId} → warehouse {WarehouseId})",
                message.RouteRequestId, hub.Id, warehouse.Id);
            return;
        }
        var inbound = await _maps.GetOptimizedRouteAsync(warehousePoint, hubPoint, Array.Empty<string>(), ct);
        if (inbound is null)
        {
            _logger.LogError("Pickup inbound leg failed for {RouteRequestId} (warehouse {WarehouseId} → hub {HubId})",
                message.RouteRequestId, warehouse.Id, hub.Id);
            return;
        }

        // Loading wait — minutes the van sits at the warehouse loading
        // before turning around. Persisted on the route's TotalDuration
        // so the driver's day reflects the real elapsed time, not just
        // driving. Pulled from the warehouse row (default 15).
        var warehouseWaitSeconds = warehouse.LoadingWaitMinutes * 60;
        var totalDistance  = outbound.TotalDistanceMeters  + inbound.TotalDistanceMeters;
        var totalDuration  = outbound.TotalDurationSeconds + warehouseWaitSeconds + inbound.TotalDurationSeconds;
        var deliveryStart  = message.ScheduledDepartTime
                             ?? message.DeliveryDate.Date.Add(deliveryWindowStart);
        var hubArrival     = deliveryStart.AddSeconds(totalDuration);

        var route = new DeliveryRoute
        {
            Id           = message.RouteRequestId,
            CompanyId    = message.CompanyId,
            TruckId      = message.TruckId,
            HubId        = hub.Id,
            WarehouseId  = warehouse.Id,
            RouteType    = RouteType.Pickup,
            ScheduledDepartTime = deliveryStart,
            DeliveryDate = message.DeliveryDate,
            // Pickup starts AND ends at the hub.
            StartAddress    = hub.FormattedAddress ?? hub.FullAddress,
            StartLatitude   = hub.Latitude  ?? 0,
            StartLongitude  = hub.Longitude ?? 0,
            EndAddress      = hub.FormattedAddress ?? hub.FullAddress,
            EndLatitude     = hub.Latitude  ?? 0,
            EndLongitude    = hub.Longitude ?? 0,
            TotalDistanceMeters  = totalDistance,
            TotalDurationSeconds = totalDuration,
            TotalStops           = 0,
            HubArrivalTime       = hubArrival,
            IsOptimized          = true,
            // Stitch the outbound + inbound polylines into one continuous
            // path. PolylineCodec.Concat dedupes the joint waypoint (the
            // warehouse) so the line doesn't double up on the loading dock.
            OverviewPolyline     = PolylineCodec.Concat(outbound.OverviewPolyline, inbound.OverviewPolyline),
        };

        await _routes.AddAsync(route, ct);
        await _routes.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Pickup route {RouteRequestId}: {HubName} → {WarehouseName} → {HubName}, return ~{Arrival:HH:mm}",
            message.RouteRequestId, hub.Name, warehouse.BusinessName, hub.Name, hubArrival);
    }

    /// <summary>
    /// Geocodes a Hub through Google Maps if it has no coordinates and
    /// persists the result back to the row, so subsequent routing runs
    /// read from cache instead of calling Google again. Failure to geocode
    /// is non-fatal — the route still goes out using the address string.
    /// </summary>
    private async Task EnsureHubGeocodedAsync(Hub hub, CancellationToken ct)
    {
        if (hub.Latitude.HasValue && hub.Longitude.HasValue) return;
        if (string.IsNullOrWhiteSpace(hub.Address)) return;

        var geo = await _maps.GeocodeAsync(hub.FullAddress, ct);
        if (geo is null)
        {
            _logger.LogWarning("Could not geocode hub {HubId} ({Name}); routing will fall back to address string.",
                hub.Id, hub.Name);
            return;
        }

        hub.Latitude         = geo.Latitude;
        hub.Longitude        = geo.Longitude;
        hub.FormattedAddress = geo.FormattedAddress;
        hub.UpdatedAt        = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Cached geocode for hub {HubId} ({Name}).", hub.Id, hub.Name);
    }

    /// <summary>
    /// Same as <see cref="EnsureHubGeocodedAsync"/> but for warehouses.
    /// Persists the result so we never re-geocode the same warehouse on a
    /// future routing run.
    /// </summary>
    private async Task EnsureWarehouseGeocodedAsync(Warehouse wh, CancellationToken ct)
    {
        if (wh.Latitude.HasValue && wh.Longitude.HasValue) return;
        if (string.IsNullOrWhiteSpace(wh.Address)) return;

        var geo = await _maps.GeocodeAsync(wh.FullAddress, ct);
        if (geo is null)
        {
            _logger.LogWarning("Could not geocode warehouse {WarehouseId} ({Name}); routing will fall back to address string.",
                wh.Id, wh.BusinessName);
            return;
        }

        wh.Latitude         = geo.Latitude;
        wh.Longitude        = geo.Longitude;
        wh.FormattedAddress = geo.FormattedAddress;
        wh.UpdatedAt        = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Cached geocode for warehouse {WarehouseId} ({Name}).", wh.Id, wh.BusinessName);
    }
}
