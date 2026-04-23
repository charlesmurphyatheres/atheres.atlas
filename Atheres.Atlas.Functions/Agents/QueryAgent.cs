using Atheres.Atlas.Data;
using Atheres.Atlas.Data.Repositories;
using Atheres.Atlas.Data.Services;
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
/// Read-only HTTP endpoints for orders, routes, and settings.
/// All endpoints are company-scoped via ICompanyContext / AtlasDbContext global filters.
/// </summary>
[Authorize]
public class QueryAgent
{
    private readonly IOrderRepository _orders;
    private readonly IRouteRepository _routes;
    private readonly IUserSettingsRepository _settings;
    private readonly IServiceBusPublisher _bus;
    private readonly AtlasDbContext _db;
    private readonly ICompanyContext _companyContext;
    private readonly RouteOptimizationAgent _optimizer;
    private readonly ILogger<QueryAgent> _logger;

    public QueryAgent(
        IOrderRepository orders,
        IRouteRepository routes,
        IUserSettingsRepository settings,
        IServiceBusPublisher bus,
        AtlasDbContext db,
        ICompanyContext companyContext,
        RouteOptimizationAgent optimizer,
        ILogger<QueryAgent> logger)
    {
        _orders = orders;
        _routes = routes;
        _settings = settings;
        _bus = bus;
        _db = db;
        _companyContext = companyContext;
        _optimizer = optimizer;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // GET /api/orders?status=&date=&page=&pageSize=
    // -----------------------------------------------------------------------
    [Function("query-orders-list")]
    public async Task<IActionResult> ListOrders(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "orders")]
        HttpRequest req,
        CancellationToken ct)
    {
        var query = _db.Orders.AsQueryable();

        if (req.Query.TryGetValue("status", out var statusStr) && !string.IsNullOrWhiteSpace(statusStr))
        {
            if (Enum.TryParse<OrderStatus>(statusStr, out var status))
                query = query.Where(o => o.Status == status);
        }

        if (req.Query.TryGetValue("date", out var dateStr) && DateTime.TryParse(dateStr, out var date))
            query = query.Where(o => o.OrderDate.Date == date.Date);

        var total = await query.CountAsync(ct);

        var page = int.TryParse(req.Query["page"], out var p) ? Math.Max(1, p) : 1;
        var pageSize = int.TryParse(req.Query["pageSize"], out var ps) ? Math.Clamp(ps, 1, 200) : 25;

        var items = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new
            {
                o.Id,
                o.CompanyId,
                o.StoreName,
                o.Address,
                o.City,
                o.State,
                o.Zip,
                o.County,
                o.District,
                o.Zone,
                o.OrderDate,
                o.Email,
                o.Phone,
                o.Latitude,
                o.Longitude,
                status = o.Status.ToString(),
                o.ExpectedDeliveryDate,
                o.ConfirmationDeadline,
                o.RescheduleCount,
                o.ConfirmedAt,
                o.StopSequence,
                o.RouteId,
                o.Notes,
                o.CreatedAt,
                o.UpdatedAt,
            })
            .ToListAsync(ct);

        return new OkObjectResult(new { items, total, page, pageSize });
    }

    // -----------------------------------------------------------------------
    // GET /api/orders/{id}
    // -----------------------------------------------------------------------
    [Function("query-orders-get")]
    public async Task<IActionResult> GetOrder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "orders/{id}")]
        HttpRequest req,
        string id,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var orderId))
            return new BadRequestObjectResult(new { error = "Invalid id." });

        var order = await _orders.GetByIdAsync(orderId, ct);
        if (order is null) return new NotFoundObjectResult(new { error = "Order not found." });

        return new OkObjectResult(new
        {
            order.Id,
            order.CompanyId,
            order.StoreName,
            order.Address,
            order.City,
            order.State,
            order.Zip,
            order.County,
            order.District,
            order.Zone,
            order.OrderDate,
            order.Email,
            order.Phone,
            order.Latitude,
            order.Longitude,
            order.FormattedAddress,
            status = order.Status.ToString(),
            order.ExpectedDeliveryDate,
            order.ConfirmationDeadline,
            order.RescheduleCount,
            order.ConfirmedAt,
            order.StopSequence,
            order.RouteId,
            order.Notes,
            order.ValidationErrors,
            order.CreatedAt,
            order.UpdatedAt,
        });
    }

    // -----------------------------------------------------------------------
    // GET /api/routes?date=yyyy-MM-dd
    // -----------------------------------------------------------------------
    [Function("query-routes-list")]
    public async Task<IActionResult> ListRoutes(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "routes")]
        HttpRequest req,
        CancellationToken ct)
    {
        List<Domain.Entities.DeliveryRoute> routes;
        if (req.Query.TryGetValue("date", out var dateStr) && DateTime.TryParse(dateStr, out var d))
        {
            routes = (await _routes.GetByDateAsync(d.Date, ct)).ToList();
        }
        else
        {
            routes = await _db.Routes
                .Include(r => r.Stops).ThenInclude(s => s.Order)
                .OrderByDescending(r => r.DeliveryDate)
                .Take(10)
                .ToListAsync(ct);
        }

        return new OkObjectResult(routes.Select(r => MapRoute(r)));
    }

    // -----------------------------------------------------------------------
    // GET /api/routes/{id}
    // -----------------------------------------------------------------------
    [Function("query-routes-get")]
    public async Task<IActionResult> GetRoute(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "routes/{id}")]
        HttpRequest req,
        string id,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var routeId))
            return new BadRequestObjectResult(new { error = "Invalid id." });

        var route = await _routes.GetByIdAsync(routeId, ct);
        if (route is null) return new NotFoundObjectResult(new { error = "Route not found." });

        return new OkObjectResult(MapRoute(route));
    }

    private static object MapRoute(Domain.Entities.DeliveryRoute r) => new
    {
        r.Id,
        r.CompanyId,
        r.TruckId,
        r.DeliveryDate,
        r.StartAddress,
        r.EndAddress,
        r.TotalStops,
        r.TotalDistanceMiles,
        totalDuration = r.TotalDuration.ToString(@"h\:mm"),
        r.IsOptimized,
        r.OverviewPolyline,
        stops = r.Stops.OrderBy(s => s.Sequence).Select(s =>
        {
            // Derive a best-effort confirmation status from the order status
            var orderStatus = s.Order?.Status;
            var confirmationStatus = orderStatus switch
            {
                OrderStatus.Confirmed => "Confirmed",
                OrderStatus.Rejected => "Rejected",
                OrderStatus.ConfirmationPending => "SentEmail",
                OrderStatus.OutForDelivery or OrderStatus.Delivered or OrderStatus.Archived => "Confirmed",
                _ => "Pending",
            };

            return new
            {
                s.OrderId,
                s.Sequence,
                s.StoreName,
                s.Address,
                s.Latitude,
                s.Longitude,
                s.EstimatedArrival,
                orderStatus = orderStatus?.ToString() ?? "Unknown",
                confirmationStatus,
                s.LegDistanceMeters,
                legDistanceMiles = s.LegDistanceMeters * 0.000621371,
                legDurationMinutes = s.LegDurationSeconds / 60.0,
            };
        }),
    };

    // -----------------------------------------------------------------------
    // GET /api/stores
    // -----------------------------------------------------------------------
    [Function("query-stores-list")]
    [AllowAnonymous]
    public async Task<IActionResult> ListStores(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "stores")]
        HttpRequest req, CancellationToken ct)
    {
        var stores = await _db.Stores
            .Where(s => s.IsActive)
            .OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, s.LicenseNumber, s.Customer, s.City })
            .ToListAsync(ct);

        return new OkObjectResult(stores);
    }

    // -----------------------------------------------------------------------
    // GET /api/orders/communications — email tracking dashboard data
    // -----------------------------------------------------------------------
    [Function("query-orders-communications")]
    public async Task<IActionResult> ListCommunications(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "orders/communications")]
        HttpRequest req, CancellationToken ct)
    {
        var page = int.TryParse(req.Query["page"], out var p) ? Math.Max(1, p) : 1;
        var pageSize = int.TryParse(req.Query["pageSize"], out var ps) ? Math.Clamp(ps, 1, 100) : 25;

        var query = _db.Orders
            .Include(o => o.Confirmations)
            .Where(o => o.Status == OrderStatus.ConfirmationPending
                      || o.Status == OrderStatus.Confirmed
                      || o.Status == OrderStatus.Rejected
                      || o.Status == OrderStatus.RouteOptimized);

        if (req.Query.TryGetValue("status", out var statusStr) && Enum.TryParse<OrderStatus>(statusStr, out var status))
            query = query.Where(o => o.Status == status);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(o => o.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new
            {
                o.Id,
                o.StoreName,
                o.Address,
                o.City,
                status = o.Status.ToString(),
                o.Email,
                o.ExpectedDeliveryDate,
                o.ConfirmationDeadline,
                o.ConfirmedAt,
                confirmation = o.Confirmations
                    .OrderByDescending(c => c.CreatedAt)
                    .Select(c => new
                    {
                        c.Id,
                        status = c.Status.ToString(),
                        c.EmailSentTo,
                        c.EmailSentAt,
                        c.ConfirmedAt,
                        c.ConfirmedBy,
                        c.ExpiresAt,
                    })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return new OkObjectResult(new { items, total, page, pageSize });
    }

    // -----------------------------------------------------------------------
    // POST /api/orders/mark-all-ready
    // Called by the order simulator. Marking orders as ready-to-pickup is also
    // the trigger that kicks off routing — publishes optimization messages
    // inline so there's no separate "run optimization" step.
    //
    // Body: { "companySlug": "<slug-or-guid>", "deliveryDate"?: "yyyy-MM-dd" }
    // Anonymous on purpose: the demo app runs without auth.
    // -----------------------------------------------------------------------
    [Function("orders-mark-all-ready")]
    [AllowAnonymous]
    public async Task<IActionResult> MarkAllReady(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders/mark-all-ready")]
        HttpRequest req,
        CancellationToken ct)
    {
        System.Text.Json.JsonDocument doc;
        try { doc = await System.Text.Json.JsonDocument.ParseAsync(req.Body, cancellationToken: ct); }
        catch { return new BadRequestObjectResult(new { error = "Invalid JSON." }); }

        var companyKey = doc.RootElement.TryGetProperty("companySlug", out var csEl) ? csEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(companyKey))
            return new BadRequestObjectResult(new { error = "companySlug is required." });

        // Accept either slug or company GUID
        var company = await _db.Companies.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => (c.Slug == companyKey || c.Id.ToString() == companyKey) && c.IsActive, ct);
        if (company is null)
            return new NotFoundObjectResult(new { error = $"Company not found: {companyKey}" });

        DateTime? deliveryDate = null;
        if (doc.RootElement.TryGetProperty("deliveryDate", out var dateEl) &&
            DateTime.TryParse(dateEl.GetString(), out var parsedDate))
            deliveryDate = parsedDate.Date;

        var query = _db.Orders.IgnoreQueryFilters()
            .Where(o => o.CompanyId == company.Id
                     && o.Status == OrderStatus.Ordered
                     && o.Latitude != null
                     && o.Longitude != null);
        if (deliveryDate is not null)
            query = query.Where(o => o.OrderDate.Date == deliveryDate.Value);

        var orders = await query.ToListAsync(ct);
        if (orders.Count == 0)
            return new OkObjectResult(new { message = "No routable orders ready to pickup.", routesQueued = 0, totalOrders = 0 });

        var firstHubId = await _db.Hubs.IgnoreQueryFilters()
            .Where(h => h.CompanyId == company.Id && h.IsActive)
            .Select(h => (Guid?)h.Id)
            .FirstOrDefaultAsync(ct);
        if (firstHubId is null)
            return new BadRequestObjectResult(new { error = "No active hub configured for this company." });

        // Group by warehouse so each route picks up from the right place. Orders
        // without a warehouse land in a null group that has no pickup stop.
        // Google Directions caps waypoints at 25 per request (one reserved for
        // the warehouse pickup stop when present), so each warehouse group is
        // further chunked into sub-groups that fit inside the limit.
        const int MaxStopsPerRoute = 25;
        var groups = orders.GroupBy(o => o.WarehouseId).ToList();

        foreach (var order in orders)
            order.Status = OrderStatus.Scheduled;
        await _db.SaveChangesAsync(ct);

        // In direct-optimize mode the optimizer will re-load and mutate these
        // same orders. Release them from the ChangeTracker now so the optimizer
        // starts with a clean slate and doesn't collide with stale tracking.
        _db.ChangeTracker.Clear();

        var groupDate = deliveryDate ?? DateTime.UtcNow.Date;
        var directOptimize = string.Equals(
            Environment.GetEnvironmentVariable("DirectOptimizer"), "true",
            StringComparison.OrdinalIgnoreCase);

        var messages = new List<RouteOptimizationRequestMessage>();
        foreach (var group in groups)
        {
            var pickupSlot = group.Key.HasValue ? 1 : 0;
            var chunkSize = MaxStopsPerRoute - pickupSlot;
            var orderedIds = group.Select(o => o.Id).ToList();
            for (int i = 0; i < orderedIds.Count; i += chunkSize)
            {
                var chunk = orderedIds.Skip(i).Take(chunkSize).ToList();
                messages.Add(new RouteOptimizationRequestMessage(
                    Guid.NewGuid(),
                    company.Id,
                    null,
                    firstHubId.Value,
                    group.Key,
                    groupDate,
                    chunk,
                    DateTime.UtcNow));
            }
        }

        try
        {
            if (directOptimize)
            {
                // Dev fallback: optimizer runs in-process so a broken Service Bus
                // (port 5672 colliding with native RabbitMQ, etc.) doesn't prevent
                // routes from being written. Prod should leave this unset so the
                // real queue does the work asynchronously.
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
            _logger.LogError(ex, "mark-all-ready: optimizer invocation failed");
            return new ObjectResult(new { error = $"Failed to queue optimization: {ex.Message}" }) { StatusCode = 500 };
        }

        _logger.LogInformation("mark-all-ready: {Company} queued {Routes} route(s) for {Orders} orders",
            company.Slug, groups.Count, orders.Count);

        return new OkObjectResult(new
        {
            message = "Orders marked ready to pickup. Routing in progress.",
            routesQueued = messages.Count,
            totalOrders = orders.Count,
        });
    }

    // -----------------------------------------------------------------------
    // GET /api/settings/{userId}
    // -----------------------------------------------------------------------
    [Function("query-settings-get")]
    public async Task<IActionResult> GetSettings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/{userId}")]
        HttpRequest req,
        string userId,
        CancellationToken ct)
    {
        var s = await _settings.GetByUserIdAsync(userId, ct);
        if (s is null)
        {
            return new OkObjectResult(new
            {
                userId,
                deliveryWindowStart = "08:00:00",
                deliveryWindowEnd   = "17:00:00",
                confirmationDeadlineHours = 3,
            });
        }

        return new OkObjectResult(new
        {
            userId = s.UserId,
            deliveryWindowStart = s.DeliveryWindowStart.ToString(@"hh\:mm\:ss"),
            deliveryWindowEnd   = s.DeliveryWindowEnd.ToString(@"hh\:mm\:ss"),
            s.ConfirmationDeadlineHours,
        });
    }

    // -----------------------------------------------------------------------
    // PUT /api/settings/{userId}
    // -----------------------------------------------------------------------
    [Function("query-settings-put")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> SaveSettings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "settings/{userId}")]
        HttpRequest req,
        string userId,
        CancellationToken ct)
    {
        System.Text.Json.JsonDocument doc;
        try { doc = await System.Text.Json.JsonDocument.ParseAsync(req.Body, cancellationToken: ct); }
        catch { return new BadRequestObjectResult(new { error = "Invalid JSON." }); }

        var companyId = _companyContext.CompanyId;
        if (!companyId.HasValue)
            return new BadRequestObjectResult(new { error = "Select a company before saving settings." });

        var existing = await _settings.GetByUserIdAsync(userId, ct)
            ?? new Domain.Entities.UserRouteSettings { UserId = userId, CompanyId = companyId.Value };

        string? Str(string key) =>
            doc.RootElement.TryGetProperty(key, out var v) ? v.GetString() : null;
        int? Int(string key) =>
            doc.RootElement.TryGetProperty(key, out var v) && v.TryGetInt32(out var i) ? i : null;

        if (Str("deliveryWindowStart") is { } ws && TimeSpan.TryParse(ws, out var wsts))
            existing.DeliveryWindowStart = wsts;
        if (Str("deliveryWindowEnd") is { } we && TimeSpan.TryParse(we, out var wets))
            existing.DeliveryWindowEnd = wets;
        if (Int("confirmationDeadlineHours") is { } h)
            existing.ConfirmationDeadlineHours = h;

        existing.UpdatedAt = DateTime.UtcNow;

        await _settings.UpsertAsync(existing, ct);
        await _settings.SaveChangesAsync(ct);

        return new OkObjectResult(new { message = "Settings saved." });
    }
}
