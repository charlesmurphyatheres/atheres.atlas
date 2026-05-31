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
    private readonly PdfService _pdf;
    private readonly ILogger<QueryAgent> _logger;

    public QueryAgent(
        IOrderRepository orders,
        IRouteRepository routes,
        IUserSettingsRepository settings,
        IServiceBusPublisher bus,
        AtlasDbContext db,
        ICompanyContext companyContext,
        RouteOptimizationAgent optimizer,
        PdfService pdf,
        ILogger<QueryAgent> logger)
    {
        _orders = orders;
        _routes = routes;
        _settings = settings;
        _bus = bus;
        _db = db;
        _companyContext = companyContext;
        _optimizer = optimizer;
        _pdf = pdf;
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
                o.LicenseNumber,
                o.StoreLicenseNumber,
                o.Customer,
                o.SalesOrderNumber,
                o.PurchaseOrderNumber,
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
            // No date filter — take the 10 most recent routes. Within a
            // day, ScheduledDepartTime ascending so Pickup vans land
            // above the paired ZonedDelivery vans (Legacy null-times to
            // the end).
            routes = await _db.Routes
                .Include(r => r.Stops).ThenInclude(s => s.Order)
                .Include(r => r.Warehouse)
                .OrderByDescending(r => r.DeliveryDate)
                .ThenBy(r => r.ScheduledDepartTime ?? DateTime.MaxValue)
                .ThenBy(r => r.RouteType)
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

    // -----------------------------------------------------------------------
    // GET /api/routes/{id}/itinerary.pdf
    //
    // Server-side iText PDF — one-page driver itinerary with the route's
    // start, optional warehouse pickup, every numbered delivery stop
    // (name / full address / leg miles / arrive / depart), and the
    // return-to-hub row. Pulled into the AdminPanel Routes tab + the
    // Logistics screen via a print icon.
    // -----------------------------------------------------------------------
    [Function("query-routes-itinerary-pdf")]
    public async Task<IActionResult> GetRouteItineraryPdf(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "routes/{id:guid}/itinerary.pdf")]
        HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        var route = await _routes.GetByIdAsync(id, ct);
        if (route is null) return new NotFoundObjectResult(new { error = "Route not found." });

        // Per-stop service time: pull the assigned truck's setting if
        // present; fall back to the entity default (currently 15 min).
        // Drives the Depart column on every delivery row.
        var settingsKey = route.TruckId?.ToString() ?? "default";
        var settings = await _settings.GetByUserIdAsync(settingsKey, ct);
        var serviceMinutes = settings?.WaitMinutesPerStop ?? Atheres.Atlas.Domain.Entities.UserRouteSettings.DefaultWaitMinutesPerStop;

        var bytes    = _pdf.BuildRouteItinerary(route, serviceMinutes);
        var fileName = $"route-{route.DeliveryDate:yyyy-MM-dd}-{route.Id.ToString("N").Substring(0, 8)}.pdf";
        return new FileContentResult(bytes, "application/pdf") { FileDownloadName = fileName };
    }

    // -----------------------------------------------------------------------
    // GET /api/optimization-audits/{id}/pdf
    //
    // Server-side iText reproduction of the audit body — replaces the
    // jsPDF download that lived in the AdminPanel.
    // -----------------------------------------------------------------------
    [Function("query-optimization-audits-pdf")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> GetOptimizationAuditPdf(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "optimization-audits/{id:guid}/pdf")]
        HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        var audit = await _db.OptimizationAudits.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (audit is null) return new NotFoundObjectResult(new { error = "Audit not found." });

        var bytes    = _pdf.BuildAuditPdf(audit);
        var stamp    = audit.CreatedAt.ToString("yyyy-MM-dd-HHmmss");
        var fileName = $"optimization-audit-{stamp}.pdf";
        return new FileContentResult(bytes, "application/pdf") { FileDownloadName = fileName };
    }

    private static object MapRoute(Domain.Entities.DeliveryRoute r)
    {
        // Warehouse pickup is the route's first physical stop after the hub
        // (when present). We surface it as a separate field rather than
        // mixing it into `stops` so existing callers that iterate stops
        // for delivery confirmations / per-stop status don't have to skip
        // it. The "estimated arrival at the warehouse" is recoverable from
        // the first delivery's ETA minus that stop's leg duration: the
        // optimizer's leg-0 is `Warehouse → first delivery`, so the time
        // *before* leg-0 starts is the warehouse pickup ETA.
        object? warehousePickup = null;
        if (r.Warehouse is not null)
        {
            DateTime? warehouseEta = null;
            var firstStop = r.Stops.OrderBy(s => s.Sequence).FirstOrDefault();
            if (firstStop?.EstimatedArrival is DateTime eta)
                warehouseEta = eta.AddSeconds(-firstStop.LegDurationSeconds);

            warehousePickup = new
            {
                id        = r.Warehouse.Id,
                name      = string.IsNullOrWhiteSpace(r.Warehouse.AlternateName)
                                ? r.Warehouse.BusinessName
                                : r.Warehouse.AlternateName,
                address   = string.IsNullOrWhiteSpace(r.Warehouse.FormattedAddress)
                                ? r.Warehouse.FullAddress
                                : r.Warehouse.FormattedAddress,
                latitude  = r.Warehouse.Latitude,
                longitude = r.Warehouse.Longitude,
                estimatedArrival = warehouseEta,
            };
        }

        return new
        {
            r.Id,
            r.CompanyId,
            r.TruckId,
            r.HubId,
            r.WarehouseId,
            // New-flow shape fields. The Routes tab can use these to label
            // Pickup vans, ZonedDelivery vans, and DirectDelivery vans
            // distinctly. ScheduledDepartTime / HubArrivalTime are non-null
            // only for the routes that have a meaningful timing offset
            // (e.g. ZonedDelivery vans waiting for the hub sort to finish).
            routeType           = r.RouteType.ToString(),
            r.ZoneId,
            r.ScheduledDepartTime,
            r.HubArrivalTime,
            warehousePickup,
            r.DeliveryDate,
            r.StartAddress,
            r.StartLatitude,
            r.StartLongitude,
            r.EndAddress,
            r.EndLatitude,
            r.EndLongitude,
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
    }

    // -----------------------------------------------------------------------
    // GET /api/optimization-audits     (Admin / SuperAdmin)
    // GET /api/optimization-audits/{id} (Admin / SuperAdmin)
    //
    // The list returns headers only — id, timestamps, counts, summary — so
    // the AdminPanel can render a sortable table without pulling every
    // multi-kilobyte LogText body. The detail endpoint streams the full
    // log so the PDF generator (and the in-page viewer) has the complete
    // trail.
    // -----------------------------------------------------------------------
    [Function("query-optimization-audits-list")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> ListOptimizationAudits(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "optimization-audits")]
        HttpRequest req, CancellationToken ct)
    {
        var take = int.TryParse(req.Query["take"], out var t) ? Math.Clamp(t, 1, 200) : 50;
        var audits = await _db.OptimizationAudits
            .OrderByDescending(a => a.CreatedAt)
            .Take(take)
            .Select(a => new
            {
                a.Id,
                a.CompanyId,
                a.CreatedAt,
                a.TriggeredBy,
                a.Trigger,
                a.Summary,
                a.OrderCount,
                a.RouteCount,
                a.WarehouseCount,
                a.HubCount,
                a.ZoneCount,
                a.DirectDeliveryCount,
            })
            .ToListAsync(ct);
        return new OkObjectResult(audits);
    }

    [Function("query-optimization-audits-get")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> GetOptimizationAudit(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "optimization-audits/{id:guid}")]
        HttpRequest req, Guid id, CancellationToken ct)
    {
        var audit = await _db.OptimizationAudits
            .FirstOrDefaultAsync(a => a.Id == id, ct);
        if (audit is null)
            return new NotFoundObjectResult(new { error = "Audit not found." });
        return new OkObjectResult(new
        {
            audit.Id,
            audit.CompanyId,
            audit.CreatedAt,
            audit.TriggeredBy,
            audit.Trigger,
            audit.Summary,
            audit.OrderCount,
            audit.RouteCount,
            audit.WarehouseCount,
            audit.HubCount,
            audit.ZoneCount,
            audit.DirectDeliveryCount,
            audit.LogText,
        });
    }

    // -----------------------------------------------------------------------
    // GET /api/stores
    // -----------------------------------------------------------------------
    [Function("query-stores-list")]
    public async Task<IActionResult> ListStores(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "stores")]
        HttpRequest req, CancellationToken ct)
    {
        // Defensive filter: a handful of legacy rows from earlier imports had
        // empty Name and/or LicenseNumber. They render as blank rows in the
        // dashboard, so skip them server-side rather than asking every caller
        // to filter. import-data.ps1 also deactivates these on next run.
        var stores = await _db.Stores
            .Where(s => s.IsActive
                     && s.Name          != null && s.Name          != ""
                     && s.LicenseNumber != null && s.LicenseNumber != "")
            .OrderBy(s => s.Name)
            .Select(s => new
            {
                s.Id,
                companies = s.Companies.Select(c => new { id = c.Id, name = c.Name }),
                s.Name,
                s.LicenseNumber,
                s.Customer,
                // Full address + contact fields so the Admin Panel can
                // pre-fill the edit form without a separate GET. Cheap to
                // project — no extra joins beyond what we already do for
                // Zone / District.
                s.Address,
                s.City,
                s.State,
                s.Zip,
                s.County,
                s.Email,
                s.Phone,
                s.IsActive,
                // Scheduling settings (per-store). Method always projected;
                // credentials are projected as-is so the admin panel can
                // pre-fill the sub-interface form. Secrets are surfaced
                // unredacted — same trust boundary as the rest of the
                // SuperAdmin Stores tab today (we already return the
                // store's contact email + phone).
                SchedulingMethod          = s.SchedulingMethod.ToString(),
                s.BookingClientId,
                s.BookingClientSecret,
                s.BookingCalendarName,
                s.CalendlyAccessToken,
                s.CalendlyCalendarName,
                s.SchedulingEmailRecipients,
                Zone     = s.Zone == null ? null : s.Zone.Code,
                District = s.Zone == null ? null : s.Zone.District.Name,
            })
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
    // Marks every Ordered row for the company (optionally a single delivery
    // date) as ready-to-pickup, which is also the trigger that kicks off
    // routing — publishes optimization messages inline so there's no separate
    // "run optimization" step.
    //
    // Body: { "companySlug": "<slug-or-guid>", "deliveryDate"?: "yyyy-MM-dd" }
    // -----------------------------------------------------------------------
    [Function("orders-mark-all-ready")]
    [Authorize(Roles = "Admin,SuperAdmin,Logistics")]
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

        // Pull every Ordered row first (with optional delivery-date filter),
        // then split into routable vs. stale. Stale rows (more than a day
        // old) are flipped to RouteOmitted in place rather than dispatched —
        // the warehouse has likely already moved on from them, so we record
        // the omission and don't generate a route request.
        var allQuery = _db.Orders.IgnoreQueryFilters()
            .Where(o => o.CompanyId == company.Id
                     && o.Status == OrderStatus.Ordered);
        if (deliveryDate is not null)
            allQuery = allQuery.Where(o => o.OrderDate.Date == deliveryDate.Value);

        var allOrdered = await allQuery.ToListAsync(ct);
        var nowUtc     = DateTime.UtcNow;
        var stale      = allOrdered.Where(o => Atheres.Atlas.Domain.Constants.RoutingPolicy.IsTooOldToRoute(o.OrderDate, nowUtc)).ToList();
        // Routable rows still have to satisfy geocoding — without coords
        // there's nothing to optimize.
        var orders     = allOrdered
            .Except(stale)
            .Where(o => o.Latitude != null && o.Longitude != null)
            .ToList();

        if (stale.Count > 0)
        {
            foreach (var s in stale)
            {
                s.Status    = OrderStatus.RouteOmitted;
                s.UpdatedAt = nowUtc;
            }
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "MarkAllReady: flipped {Count} stale order(s) to RouteOmitted for company {Company}",
                stale.Count, company.Id);
        }

        if (orders.Count == 0)
            return new OkObjectResult(new
            {
                message      = "No routable orders ready to pickup.",
                routesQueued = 0,
                totalOrders  = 0,
                routeOmitted = stale.Count,
            });

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
        // further chunked into sub-groups that fit inside the limit. The cap
        // is set on the Company row — this is a company-wide policy.
        const int GoogleDirectionsHardCap = 25;
        var maxStopsPerRoute = Math.Clamp(company.MaxStopsPerRoute, 1, GoogleDirectionsHardCap);
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
            // MaxStopsPerRoute counts delivery stops only; the warehouse
            // pickup leg is not counted against the cap. Mirrors the
            // chunking in RouteScheduler — both must agree or operators
            // get a different stop count between the bulk-Scheduled flow
            // and the on-demand Optimize Now path.
            var chunkSize  = Math.Max(1, maxStopsPerRoute);
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
            message      = "Orders marked ready to pickup. Routing in progress.",
            routesQueued = messages.Count,
            totalOrders  = orders.Count,
            routeOmitted = stale.Count,
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
        // Company-wide policy (delivery window, max stops) lives on Company;
        // per-truck/user prefs (confirmation deadline, wait minutes) live on
        // UserRouteSettings. The Settings page edits both via this endpoint.
        var companyId = _companyContext.CompanyId;
        var company = companyId.HasValue
            ? await _db.Companies.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.Id == companyId.Value, ct)
            : null;

        var s = await _settings.GetByUserIdAsync(userId, ct);
        if (s is null && company is null)
        {
            return new OkObjectResult(new
            {
                userId,
                deliveryWindowStart = "08:00:00",
                deliveryWindowEnd   = "17:00:00",
                confirmationDeadlineHours = 3,
                maxStopsPerRoute    = Domain.Entities.UserRouteSettings.DefaultMaxStops,
                waitMinutesPerStop  = 0,
            });
        }

        return new OkObjectResult(new
        {
            userId = s?.UserId ?? userId,
            deliveryWindowStart = (company?.DeliveryWindowStart ?? new TimeSpan(8, 0, 0))
                .ToString(@"hh\:mm\:ss"),
            deliveryWindowEnd   = (company?.DeliveryWindowEnd   ?? new TimeSpan(17, 0, 0))
                .ToString(@"hh\:mm\:ss"),
            confirmationDeadlineHours = s?.ConfirmationDeadlineHours ?? 3,
            maxStopsPerRoute    = company?.MaxStopsPerRoute
                                  ?? Domain.Entities.UserRouteSettings.DefaultMaxStops,
            waitMinutesPerStop  = s?.WaitMinutesPerStop ?? 0,
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

        // Company-wide policy fields are persisted to the Companies row, so
        // they're shared by every truck in the company. Filtered by company
        // context already because companyId came from the request scope.
        var company = await _db.Companies.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == companyId.Value, ct);
        if (company is null)
            return new BadRequestObjectResult(new { error = "Company not found." });

        string? Str(string key) =>
            doc.RootElement.TryGetProperty(key, out var v) ? v.GetString() : null;
        int? Int(string key) =>
            doc.RootElement.TryGetProperty(key, out var v) && v.TryGetInt32(out var i) ? i : null;

        // ---- Company-scoped fields ----
        if (Str("deliveryWindowStart") is { } ws && TimeSpan.TryParse(ws, out var wsts))
            company.DeliveryWindowStart = wsts;
        if (Str("deliveryWindowEnd") is { } we && TimeSpan.TryParse(we, out var wets))
            company.DeliveryWindowEnd = wets;
        // Clamp at the hard cap server-side: the UI also enforces it, but a
        // raw API caller could try to bypass the input validation.
        if (Int("maxStopsPerRoute") is { } maxStops)
            company.MaxStopsPerRoute = Math.Clamp(maxStops, 1,
                Domain.Entities.UserRouteSettings.MaxStopsHardCap);

        // ---- Per-truck / per-user fields ----
        if (Int("confirmationDeadlineHours") is { } h)
            existing.ConfirmationDeadlineHours = h;
        if (Int("waitMinutesPerStop") is { } wait)
            existing.WaitMinutesPerStop = Math.Max(0, wait);

        existing.UpdatedAt = DateTime.UtcNow;
        company.UpdatedAt  = DateTime.UtcNow;

        await _settings.UpsertAsync(existing, ct);
        await _settings.SaveChangesAsync(ct);

        return new OkObjectResult(new { message = "Settings saved." });
    }
}
