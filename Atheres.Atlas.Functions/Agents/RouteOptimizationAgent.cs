using System.Text.Json;
using Atheres.Atlas.Data;
using Atheres.Atlas.Data.Repositories;
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

        // Load hub (always the route origin and destination)
        var hub = await _db.Hubs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(h => h.Id == message.HubId, ct)
            ?? throw new InvalidOperationException($"Hub {message.HubId} not found.");

        // Load truck settings for delivery window and confirmation deadline.
        // If none are configured (routes now start/end at the hub, so the
        // Settings UI only exposes window + deadline), fall back to the entity
        // defaults — 08:00–17:00 delivery window, 3h confirmation deadline.
        var settingsKey = message.TruckId?.ToString() ?? "default";
        var userSettings = await _settings.GetByUserIdAsync(settingsKey, ct)
                           ?? new UserRouteSettings { UserId = settingsKey, CompanyId = message.CompanyId };

        // Load warehouse if specified (it becomes the first waypoint for pickup)
        Warehouse? warehouse = null;
        if (message.WarehouseId.HasValue)
        {
            warehouse = await _db.Warehouses.IgnoreQueryFilters()
                .FirstOrDefaultAsync(w => w.Id == message.WarehouseId.Value, ct);
        }

        // Load all orders
        var orderList = new List<Order>();
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
            orderList.Add(order);
        }

        if (orderList.Count == 0)
        {
            _logger.LogError("No routable orders found for route request {RouteRequestId}", message.RouteRequestId);
            return;
        }

        // Build waypoints: warehouse pickup (if needed) + store deliveries
        var waypoints = new List<string>();
        if (warehouse is not null)
            waypoints.Add(warehouse.FormattedAddress ?? warehouse.FullAddress);
        waypoints.AddRange(orderList.Select(o => o.FormattedAddress ?? o.FullAddress));

        // Hub is always origin and destination
        var hubAddress = hub.FormattedAddress ?? hub.FullAddress;
        var optimizedRoute = await _maps.GetOptimizedRouteAsync(
            hubAddress,
            hubAddress,
            waypoints,
            ct);

        if (optimizedRoute is null)
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

        // Build delivery times starting from window start
        var deliveryStart = message.DeliveryDate.Date.Add(userSettings.DeliveryWindowStart);
        var runningTime = deliveryStart;

        // Create the route entity (hub is always start and end)
        var route = new DeliveryRoute
        {
            Id = message.RouteRequestId,
            CompanyId = message.CompanyId,
            TruckId = message.TruckId,
            HubId = message.HubId,
            WarehouseId = message.WarehouseId,
            DeliveryDate = message.DeliveryDate,
            StartAddress = hubAddress,
            StartLatitude = hub.Latitude ?? 0,
            StartLongitude = hub.Longitude ?? 0,
            EndAddress = hubAddress,
            EndLatitude = hub.Latitude ?? 0,
            EndLongitude = hub.Longitude ?? 0,
            TotalDistanceMeters = optimizedRoute.TotalDistanceMeters,
            TotalDurationSeconds = optimizedRoute.TotalDurationSeconds,
            TotalStops = orderList.Count,
            IsOptimized = true,
            OverviewPolyline = optimizedRoute.OverviewPolyline,
            OptimizedWaypointOrder = JsonSerializer.Serialize(optimizedRoute.WaypointOrder)
        };

        await _routes.AddAsync(route, ct);
        await _routes.SaveChangesAsync(ct);

        // Assign orders to stops in optimized order.
        // If a warehouse is included, it occupies index 0 in the waypoint list,
        // so we offset order indices by 1 and skip the warehouse waypoint.
        var warehouseOffset = warehouse is not null ? 1 : 0;
        var confirmationMessages = new List<ConfirmationRequestMessage>();
        int stopSequence = 0;

        for (int i = 0; i < optimizedRoute.WaypointOrder.Count; i++)
        {
            var originalIndex = optimizedRoute.WaypointOrder[i];

            // Skip the warehouse waypoint — it's a pickup stop, not a delivery
            if (warehouse is not null && originalIndex == 0)
                continue;

            var orderIndex = originalIndex - warehouseOffset;
            var order = orderList[orderIndex];
            stopSequence++;
            var leg = i < optimizedRoute.Legs.Count ? optimizedRoute.Legs[i] : null;

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

            // Add service time to running total
            runningTime = estimatedArrival.AddMinutes(stop.ServiceTimeMinutes);

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
}
