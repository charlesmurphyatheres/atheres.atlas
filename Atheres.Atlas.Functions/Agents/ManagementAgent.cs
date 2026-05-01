using System.Net;
using System.Text.Json;
using Atheres.Atlas.Data;
using Atheres.Atlas.Data.Repositories;
using Atheres.Atlas.Domain.Constants;
using Atheres.Atlas.Domain.Entities;
using Atheres.Atlas.Domain.Enums;
using Atheres.Atlas.Domain.Messages;
using Atheres.Atlas.Functions.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Functions.Agents;

/// <summary>
/// HTTP endpoints for admin and scheduler operations:
/// manual order status updates, manual delivery confirmation, and route stop reordering.
/// Also exposes GET /api/trucks for the scheduler UI.
/// </summary>
[Authorize(Roles = "Admin,SuperAdmin,Logistics")]
public class ManagementAgent
{
    private readonly IOrderRepository _orders;
    private readonly IRouteRepository _routes;
    private readonly IServiceBusPublisher _bus;
    private readonly AtlasDbContext _db;
    private readonly IGoogleMapsService _maps;
    private readonly IRouteScheduler _scheduler;
    private readonly ILogger<ManagementAgent> _logger;

    public ManagementAgent(
        IOrderRepository orders,
        IRouteRepository routes,
        IServiceBusPublisher bus,
        AtlasDbContext db,
        IGoogleMapsService maps,
        IRouteScheduler scheduler,
        ILogger<ManagementAgent> logger)
    {
        _orders    = orders;
        _routes    = routes;
        _bus       = bus;
        _db        = db;
        _maps      = maps;
        _scheduler = scheduler;
        _logger    = logger;
    }

    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    // -----------------------------------------------------------------------
    // GET /api/trucks
    // -----------------------------------------------------------------------
    [Function("mgmt-trucks-list")]
    public async Task<IActionResult> ListTrucks(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "trucks")]
        HttpRequest req,
        CancellationToken ct)
    {
        var trucks = await _db.Trucks
            .Include(t => t.Hub)
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name)
            .Select(t => new
            {
                t.Id,
                t.CompanyId,
                t.Name,
                t.LicensePlate,
                t.HubId,
                hubName = t.Hub != null ? t.Hub.Name : null,
                t.CurrentLocationAddress,
                t.CurrentLocationLatitude,
                t.CurrentLocationLongitude,
                t.CurrentLocationUpdatedAt,
                t.AssignedDriverId,
                status = t.Status.ToString(),
                t.IsActive,
            })
            .ToListAsync(ct);

        return new OkObjectResult(trucks);
    }

    /// <summary>Geocodes an address (if supplied and changed) and stamps the truck's
    /// current-location fields. Empty string clears the location.</summary>
    private async Task ApplyCurrentLocationAsync(
        Domain.Entities.Truck truck, string? address, CancellationToken ct)
    {
        if (address is null) return;

        var trimmed = address.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            truck.CurrentLocationAddress   = null;
            truck.CurrentLocationLatitude  = null;
            truck.CurrentLocationLongitude = null;
            truck.CurrentLocationUpdatedAt = DateTime.UtcNow;
            return;
        }

        // Skip re-geocoding if the address hasn't changed and we already have coords.
        if (string.Equals(truck.CurrentLocationAddress, trimmed, StringComparison.Ordinal)
            && truck.CurrentLocationLatitude.HasValue
            && truck.CurrentLocationLongitude.HasValue)
        {
            return;
        }

        var geo = await _maps.GeocodeAsync(trimmed, ct);
        truck.CurrentLocationAddress   = trimmed;
        truck.CurrentLocationLatitude  = geo?.Latitude;
        truck.CurrentLocationLongitude = geo?.Longitude;
        truck.CurrentLocationUpdatedAt = DateTime.UtcNow;

        if (geo is null)
            _logger.LogWarning("Could not geocode truck {Id} location '{Address}'", truck.Id, trimmed);
    }

    // -----------------------------------------------------------------------
    // POST /api/trucks
    // -----------------------------------------------------------------------
    [Function("mgmt-trucks-create")]
    public async Task<IActionResult> CreateTruck(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "trucks")]
        HttpRequest req, CancellationToken ct)
    {
        JsonDocument doc;
        try { doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct); }
        catch { return new BadRequestObjectResult(new { error = "Invalid JSON." }); }

        var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
            return new BadRequestObjectResult(new { error = "name is required." });

        var licensePlate = doc.RootElement.TryGetProperty("licensePlate", out var lp) ? lp.GetString() : null;
        Guid? hubId = doc.RootElement.TryGetProperty("hubId", out var h) && Guid.TryParse(h.GetString(), out var hid) ? hid : null;
        var currentLocation = doc.RootElement.TryGetProperty("currentLocationAddress", out var loc) ? loc.GetString() : null;

        var companyId = req.HttpContext.User.FindFirst("companyId")?.Value;
        if (!Guid.TryParse(companyId, out var cid))
            return new BadRequestObjectResult(new { error = "Company context required." });

        var truck = new Domain.Entities.Truck
        {
            CompanyId = cid,
            Name = name,
            LicensePlate = licensePlate,
            HubId = hubId,
        };
        await ApplyCurrentLocationAsync(truck, currentLocation, ct);

        _db.Trucks.Add(truck);
        await _db.SaveChangesAsync(ct);

        return new ObjectResult(new { id = truck.Id, name = truck.Name }) { StatusCode = 201 };
    }

    // -----------------------------------------------------------------------
    // PUT /api/trucks/{id}
    // -----------------------------------------------------------------------
    [Function("mgmt-trucks-update")]
    public async Task<IActionResult> UpdateTruck(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "trucks/{id}")]
        HttpRequest req, string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var truckId))
            return new BadRequestObjectResult(new { error = "Invalid id." });

        var truck = await _db.Trucks.FindAsync([truckId], ct);
        if (truck is null)
            return new NotFoundObjectResult(new { error = "Truck not found." });

        JsonDocument doc;
        try { doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct); }
        catch { return new BadRequestObjectResult(new { error = "Invalid JSON." }); }

        if (doc.RootElement.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString()))
            truck.Name = n.GetString()!;
        if (doc.RootElement.TryGetProperty("licensePlate", out var lp))
            truck.LicensePlate = lp.GetString();
        if (doc.RootElement.TryGetProperty("hubId", out var h))
            truck.HubId = Guid.TryParse(h.GetString(), out var hid) ? hid : null;
        if (doc.RootElement.TryGetProperty("assignedDriverId", out var d))
            truck.AssignedDriverId = d.GetString();
        if (doc.RootElement.TryGetProperty("currentLocationAddress", out var loc))
            await ApplyCurrentLocationAsync(truck, loc.GetString(), ct);
        // Accept Status as either the enum name ("Available") or its underlying
        // int (0/1/2) so the admin grid's dropdown and any TestBed scripts can
        // both update it.
        if (doc.RootElement.TryGetProperty("status", out var st))
        {
            if (st.ValueKind == JsonValueKind.String
                && Enum.TryParse<TruckStatus>(st.GetString(), ignoreCase: true, out var statusByName))
            {
                truck.Status = statusByName;
            }
            else if (st.ValueKind == JsonValueKind.Number
                && st.TryGetInt32(out var statusInt)
                && Enum.IsDefined(typeof(TruckStatus), statusInt))
            {
                truck.Status = (TruckStatus)statusInt;
            }
            else
            {
                return new BadRequestObjectResult(new
                {
                    error = "Invalid status. Expected one of: Available, AvailableWithIssues, Unavailable.",
                });
            }
        }

        truck.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new OkObjectResult(new { message = "Truck updated." });
    }

    // -----------------------------------------------------------------------
    // DELETE /api/trucks/{id}  (soft delete)
    // -----------------------------------------------------------------------
    [Function("mgmt-trucks-deactivate")]
    public async Task<IActionResult> DeactivateTruck(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "trucks/{id}")]
        HttpRequest req, string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var truckId))
            return new BadRequestObjectResult(new { error = "Invalid id." });

        var truck = await _db.Trucks.FindAsync([truckId], ct);
        if (truck is null)
            return new NotFoundObjectResult(new { error = "Truck not found." });

        truck.IsActive = false;
        truck.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new OkObjectResult(new { message = "Truck deactivated." });
    }

    // -----------------------------------------------------------------------
    // PATCH /api/orders/{id}/status  (Admin / Logistics / OrderImporter)
    // Body: { "status": "Delivered" }
    // -----------------------------------------------------------------------
    [Function("mgmt-order-set-status")]
    [Authorize(Roles = "Admin,SuperAdmin,Logistics,OrderImporter")]
    public async Task<IActionResult> SetOrderStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "orders/{id}/status")]
        HttpRequest req,
        string id,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var orderId))
            return new BadRequestObjectResult(new { error = "Invalid order id." });

        string? newStatus;
        try
        {
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            newStatus = doc.RootElement.GetProperty("status").GetString();
        }
        catch
        {
            return new BadRequestObjectResult(new { error = "Body must be { \"status\": \"...\" }" });
        }

        if (!Enum.TryParse<OrderStatus>(newStatus, out var status))
            return new BadRequestObjectResult(new { error = $"Unknown status '{newStatus}'." });

        var order = await _orders.GetByIdAsync(orderId, ct);
        if (order is null)
            return new NotFoundObjectResult(new { error = "Order not found." });

        // Stale orders never enter the routing pipeline. If an operator
        // tries to flip one to Scheduled, rewrite the target to RouteOmitted
        // so the row is taken out of contention immediately rather than
        // waiting for a downstream consumer to reject it.
        var effective = status;
        if (status == OrderStatus.Scheduled
            && RoutingPolicy.IsTooOldToRoute(order.OrderDate, DateTime.UtcNow))
        {
            effective = OrderStatus.RouteOmitted;
        }

        var previous = order.Status.ToString();
        order.Status = effective;
        await _orders.UpdateAsync(order, ct);
        await _orders.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Order {OrderId} status manually set to {Status}{RewriteNote}",
            orderId, effective,
            effective != status ? $" (requested {status}; order >24h old)" : string.Empty);

        await _bus.PublishAsync(ServiceBusQueues.Audit, new AuditMessage(
            AuditEventType.OrderStatusChanged, nameof(ManagementAgent),
            order.CompanyId, order.Id, null,
            previous, effective.ToString(),
            effective != status
                ? $"Manual status update — requested {status}, rewritten to {effective} (order >24h old)"
                : "Manual status update via admin panel",
            true, null, DateTime.UtcNow), ct);

        // Newly-Scheduled rows trigger a single optimization run through
        // the shared RouteScheduler. The scheduler does its own
        // geocode-then-backfill pass before publishing route messages, so
        // an old order pointing at an ungeocoded store gets its coords
        // resolved automatically — no separate prep step required here.
        RouteEnqueueResult? schedulerResult = null;
        if (effective == OrderStatus.Scheduled)
        {
            schedulerResult = await _scheduler.EnqueueAsync(new[] { order }, order.CompanyId, ct);
        }

        return new OkObjectResult(new
        {
            orderId,
            status              = effective.ToString(),
            requestedStatus     = status.ToString(),
            rewrittenAsStale    = effective != status,
            routesQueued        = schedulerResult?.RoutesQueued       ?? 0,
            ordersUngeocoded    = schedulerResult?.OrdersUngeocoded   ?? 0,
            noActiveHub         = schedulerResult?.NoActiveHub        ?? false,
            storesGeocoded      = schedulerResult?.StoresGeocoded     ?? 0,
            hubsGeocoded        = schedulerResult?.HubsGeocoded       ?? 0,
            warehousesGeocoded  = schedulerResult?.WarehousesGeocoded ?? 0,
            geocodeFailures     = schedulerResult?.GeocodeFailures    ?? 0,
        });
    }

    // -----------------------------------------------------------------------
    // PATCH /api/orders/status   (Admin / Logistics / OrderImporter)
    // Body: { "orderIds": ["...","..."], "status": "Delivered" }
    //
    // Bulk variant of mgmt-order-set-status — applies the same status to
    // every order id supplied. Each row is updated and audited individually
    // so partial failure is observable: returns per-id results in a list.
    // The whole call uses a single SaveChanges for efficiency.
    // -----------------------------------------------------------------------
    [Function("mgmt-orders-bulk-set-status")]
    [Authorize(Roles = "Admin,SuperAdmin,Logistics,OrderImporter")]
    public async Task<IActionResult> BulkSetOrderStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "orders/status")]
        HttpRequest req,
        CancellationToken ct)
    {
        Guid[] ids;
        OrderStatus status;
        try
        {
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            var statusStr = doc.RootElement.GetProperty("status").GetString();
            if (!Enum.TryParse<OrderStatus>(statusStr, out status))
                return new BadRequestObjectResult(new { error = $"Unknown status '{statusStr}'." });

            var idsEl = doc.RootElement.GetProperty("orderIds");
            ids = idsEl.EnumerateArray()
                .Select(e => Guid.TryParse(e.GetString(), out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .Distinct()
                .ToArray();
        }
        catch
        {
            return new BadRequestObjectResult(new { error = "Body must be { \"orderIds\": [...], \"status\": \"...\" }" });
        }

        if (ids.Length == 0)
            return new BadRequestObjectResult(new { error = "orderIds must not be empty." });

        // Pre-load every requested order in one round-trip. Tenant scoping
        // is enforced by the AtlasDbContext query filter, so an OrderImporter
        // can't flip orders that don't belong to their company even if they
        // forge an id in the request.
        var orders = await _db.Orders
            .Where(o => ids.Contains(o.Id))
            .ToListAsync(ct);

        var nowUtc           = DateTime.UtcNow;
        var results          = new List<object>();
        // Collect every order that ends up in Scheduled state so we can
        // hand the whole set to RouteScheduler at the end — one batched
        // optimization run instead of one per order.
        var scheduledOrders  = new List<Atheres.Atlas.Domain.Entities.Order>();
        foreach (var orderId in ids)
        {
            var order = orders.FirstOrDefault(o => o.Id == orderId);
            if (order is null)
            {
                results.Add(new { orderId, ok = false, error = "Not found." });
                continue;
            }

            // Same staleness guard as the single-update endpoint: an
            // attempt to flip a >24h-old row to Scheduled is rewritten to
            // RouteOmitted so it never enters the optimization pipeline.
            var effective = status;
            if (status == OrderStatus.Scheduled
                && RoutingPolicy.IsTooOldToRoute(order.OrderDate, nowUtc))
            {
                effective = OrderStatus.RouteOmitted;
            }

            var previous = order.Status.ToString();
            order.Status    = effective;
            order.UpdatedAt = nowUtc;

            if (effective == OrderStatus.Scheduled)
                scheduledOrders.Add(order);

            // Fire-and-forget audit message per row so the audit log mirrors
            // single-update behavior. Failure to publish doesn't roll back
            // the status change — we treat the audit pipeline the same way
            // single-update does.
            try
            {
                await _bus.PublishAsync(ServiceBusQueues.Audit, new AuditMessage(
                    AuditEventType.OrderStatusChanged, nameof(ManagementAgent),
                    order.CompanyId, order.Id, null,
                    previous, effective.ToString(),
                    effective != status
                        ? $"Bulk status update — requested {status}, rewritten to {effective} (order >24h old)"
                        : "Bulk status update via admin panel",
                    true, null, nowUtc), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bulk set-status: audit publish failed for {OrderId}", orderId);
            }

            results.Add(new
            {
                orderId,
                ok               = true,
                status           = effective.ToString(),
                rewrittenAsStale = effective != status,
            });
        }

        await _db.SaveChangesAsync(ct);

        var updated      = results.Count(r => (bool)r.GetType().GetProperty("ok")!.GetValue(r)!);
        var routeOmitted = results.Count(r =>
        {
            var prop = r.GetType().GetProperty("rewrittenAsStale");
            return prop is not null && (bool)(prop.GetValue(r) ?? false);
        });

        // One optimization run per company-tenant present in the batch.
        // RouteScheduler internally:
        //   1. Geocodes every store/warehouse/hub it needs that's missing
        //      coordinates and persists the result (so future runs skip
        //      Google entirely).
        //   2. Back-fills coords from store onto orders that lack them.
        //   3. Groups by warehouse, chunks per company.MaxStopsPerRoute,
        //      and publishes one optimization message per chunk.
        // So an operator ticking 50 orders and clicking Apply Scheduled
        // does the full prep + route generation in a single click.
        var routesQueued       = 0;
        var ordersUngeocoded   = 0;
        var noActiveHub        = false;
        var storesGeocoded     = 0;
        var hubsGeocoded       = 0;
        var warehousesGeocoded = 0;
        var geocodeFailures    = 0;
        if (scheduledOrders.Count > 0)
        {
            foreach (var byCompany in scheduledOrders.GroupBy(o => o.CompanyId))
            {
                var outcome = await _scheduler.EnqueueAsync(
                    byCompany.ToList(), byCompany.Key, ct);
                routesQueued       += outcome.RoutesQueued;
                ordersUngeocoded   += outcome.OrdersUngeocoded;
                noActiveHub        |= outcome.NoActiveHub;
                storesGeocoded     += outcome.StoresGeocoded;
                hubsGeocoded       += outcome.HubsGeocoded;
                warehousesGeocoded += outcome.WarehousesGeocoded;
                geocodeFailures    += outcome.GeocodeFailures;
            }
        }

        _logger.LogInformation(
            "Bulk status update: {Updated}/{Total} orders to {Status} ({RouteOmitted} → RouteOmitted, {Routes} run(s) queued, geocoded {Stores}/{Hubs}/{Warehouses} stores/hubs/warehouses, {Ungeocoded} ungeocoded skipped, NoActiveHub={NoHub})",
            updated, ids.Length, status, routeOmitted, routesQueued,
            storesGeocoded, hubsGeocoded, warehousesGeocoded,
            ordersUngeocoded, noActiveHub);

        return new OkObjectResult(new
        {
            requested          = ids.Length,
            updated,
            status             = status.ToString(),
            routeOmitted,
            routesQueued,
            ordersUngeocoded,
            noActiveHub,
            storesGeocoded,
            hubsGeocoded,
            warehousesGeocoded,
            geocodeFailures,
            results,
        });
    }

    // -----------------------------------------------------------------------
    // POST /api/orders/{id}/confirm  (Admin / Logistics)
    // -----------------------------------------------------------------------
    [Function("mgmt-order-confirm")]
    public async Task<IActionResult> ConfirmOrderManually(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders/{id}/confirm")]
        HttpRequest req,
        string id,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var orderId))
            return new BadRequestObjectResult(new { error = "Invalid order id." });

        var order = await _orders.GetByIdAsync(orderId, ct);
        if (order is null)
            return new NotFoundObjectResult(new { error = "Order not found." });

        var previous = order.Status.ToString();
        order.Status = OrderStatus.Confirmed;
        order.ConfirmedAt = DateTime.UtcNow;
        await _orders.UpdateAsync(order, ct);
        await _orders.SaveChangesAsync(ct);

        _logger.LogInformation("Order {OrderId} manually confirmed", orderId);

        await _bus.PublishAsync(ServiceBusQueues.Audit, new AuditMessage(
            AuditEventType.ConfirmationReceived, nameof(ManagementAgent),
            order.CompanyId, order.Id, null,
            previous, OrderStatus.Confirmed.ToString(),
            "Manually confirmed via scheduler panel", true, null, DateTime.UtcNow), ct);

        await _bus.PublishAsync(ServiceBusQueues.Notifications, new NotificationMessage(
            Guid.NewGuid(), order.CompanyId, NotificationType.ConfirmationReceived,
            "Delivery Confirmed",
            $"{order.StoreName} confirmed manually by scheduler.",
            order.Id, null, null, null, DateTime.UtcNow), ct);

        return new OkObjectResult(new { orderId, status = "Confirmed" });
    }

    // -----------------------------------------------------------------------
    // PATCH /api/routes/{routeId}/stops/{orderId}  (Admin / Logistics)
    // Body: { "sequence": 3 }
    // -----------------------------------------------------------------------
    [Function("mgmt-route-reorder-stop")]
    public async Task<IActionResult> ReorderStop(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "routes/{routeId}/stops/{orderId}")]
        HttpRequest req,
        string routeId,
        string orderId,
        CancellationToken ct)
    {
        if (!Guid.TryParse(routeId, out var rId) || !Guid.TryParse(orderId, out var oId))
            return new BadRequestObjectResult(new { error = "Invalid id." });

        int newSequence;
        try
        {
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            newSequence = doc.RootElement.GetProperty("sequence").GetInt32();
        }
        catch
        {
            return new BadRequestObjectResult(new { error = "Body must be { \"sequence\": N }" });
        }

        var route = await _routes.GetByIdAsync(rId, ct);
        if (route is null)
            return new NotFoundObjectResult(new { error = "Route not found." });

        var stop = route.Stops.FirstOrDefault(s => s.OrderId == oId);
        if (stop is null)
            return new NotFoundObjectResult(new { error = "Stop not found on route." });

        // Find the stop currently at newSequence and swap
        var displaced = route.Stops.FirstOrDefault(s => s.Sequence == newSequence && s.OrderId != oId);
        var oldSequence = stop.Sequence;
        stop.Sequence = newSequence;
        if (displaced is not null)
            displaced.Sequence = oldSequence;

        await _routes.UpdateAsync(route, ct);
        await _routes.SaveChangesAsync(ct);

        _logger.LogInformation("Stop {OrderId} on route {RouteId} moved to sequence {Seq}", oId, rId, newSequence);

        return new OkObjectResult(new { routeId, orderId, newSequence });
    }

    // -----------------------------------------------------------------------
    // POST /api/orders/{id}/archive  (Admin / Logistics)
    // Archives an order after successful delivery.
    // -----------------------------------------------------------------------
    [Function("mgmt-order-archive")]
    public async Task<IActionResult> ArchiveOrder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders/{id}/archive")]
        HttpRequest req,
        string id,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var orderId))
            return new BadRequestObjectResult(new { error = "Invalid order id." });

        var order = await _orders.GetByIdAsync(orderId, ct);
        if (order is null)
            return new NotFoundObjectResult(new { error = "Order not found." });

        if (order.Status != OrderStatus.Delivered)
            return new BadRequestObjectResult(new { error = "Only delivered orders can be archived." });

        order.Status = OrderStatus.Archived;
        order.UpdatedAt = DateTime.UtcNow;
        await _orders.UpdateAsync(order, ct);
        await _orders.SaveChangesAsync(ct);

        await _bus.PublishAsync(ServiceBusQueues.Audit, new AuditMessage(
            AuditEventType.OrderArchived, nameof(ManagementAgent),
            order.CompanyId, order.Id, null,
            "Delivered", "Archived",
            "Order archived after delivery", true, null, DateTime.UtcNow), ct);

        _logger.LogInformation("Order {OrderId} archived", orderId);

        return new OkObjectResult(new { orderId, status = "Archived" });
    }

    // -----------------------------------------------------------------------
    // DELETE /api/orders/all  (Demo reset — wipes all orders, batches, routes)
    // -----------------------------------------------------------------------
    [Function("mgmt-orders-reset")]
    public async Task<IActionResult> ResetAllOrders(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "orders/all")]
        HttpRequest req, CancellationToken ct)
    {
        // Delete in dependency order
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM Confirmations", ct);
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM RouteStops", ct);
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM AuditLogs", ct);
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM Orders", ct);
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM Routes", ct);
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM OrderBatches", ct);

        _logger.LogWarning("All orders, routes, and batches wiped (demo reset)");

        return new OkObjectResult(new { message = "All orders, routes, and batches deleted." });
    }
}
