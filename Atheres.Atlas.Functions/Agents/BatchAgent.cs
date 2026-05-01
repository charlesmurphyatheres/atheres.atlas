using System.Text.Json;
using Atheres.Atlas.Data;
using Atheres.Atlas.Data.Repositories;
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
/// Manages order batches: create, list, add/remove orders, and schedule pickup.
/// When a pickup date is assigned, route optimization runs automatically.
/// </summary>
[Authorize(Roles = "Admin,SuperAdmin,Logistics")]
public class BatchAgent
{
    private readonly AtlasDbContext _db;
    private readonly IServiceBusPublisher _bus;
    private readonly IGoogleMapsService _maps;
    private readonly ILogger<BatchAgent> _logger;

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public BatchAgent(AtlasDbContext db, IServiceBusPublisher bus, IGoogleMapsService maps, ILogger<BatchAgent> logger)
    {
        _db = db;
        _bus = bus;
        _maps = maps;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // GET /api/batches?status=Open
    // -----------------------------------------------------------------------
    [Function("batches-list")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "batches")]
        HttpRequest req, CancellationToken ct)
    {
        var statusFilter = req.Query["status"].FirstOrDefault();

        var query = _db.OrderBatches
            .Include(b => b.Hub)
            .Include(b => b.Warehouse)
            .Include(b => b.Truck)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(statusFilter) &&
            Enum.TryParse<BatchStatus>(statusFilter, true, out var status))
            query = query.Where(b => b.Status == status);

        var batches = await query
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => new
            {
                b.Id, b.CompanyId, b.Name, b.Status,
                b.PickupDate, b.RouteId,
                hubName = b.Hub.Name,
                warehouseName = b.Warehouse.BusinessName,
                truckName = b.Truck != null ? b.Truck.Name : null,
                orderCount = b.Orders.Count,
                b.CreatedAt,
            })
            .ToListAsync(ct);

        return new OkObjectResult(batches);
    }

    // -----------------------------------------------------------------------
    // GET /api/batches/{id}
    // -----------------------------------------------------------------------
    [Function("batches-get")]
    public async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "batches/{id:guid}")]
        HttpRequest req, Guid id, CancellationToken ct)
    {
        var batch = await _db.OrderBatches
            .Include(b => b.Hub)
            .Include(b => b.Warehouse)
            .Include(b => b.Orders)
            .FirstOrDefaultAsync(b => b.Id == id, ct);

        if (batch is null)
            return new NotFoundObjectResult(new { error = "Batch not found." });

        return new OkObjectResult(new
        {
            batch.Id, batch.CompanyId, batch.Name, batch.Status,
            batch.HubId, batch.WarehouseId, batch.TruckId,
            batch.PickupDate, batch.RouteId,
            hubName = batch.Hub.Name,
            warehouseName = batch.Warehouse.BusinessName,
            orders = batch.Orders.Select(o => new
            {
                o.Id, o.StoreName, o.Address, o.City, o.State, o.Zip, o.Status,
            }),
            batch.CreatedAt,
        });
    }

    // -----------------------------------------------------------------------
    // POST /api/batches
    // Body: { name, hubId, warehouseId, truckId? }
    // -----------------------------------------------------------------------
    [Function("batches-create")]
    public async Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "batches")]
        HttpRequest req, CancellationToken ct)
    {
        CreateBatchDto? dto;
        try { dto = await JsonSerializer.DeserializeAsync<CreateBatchDto>(req.Body, _json, ct); }
        catch { return new BadRequestObjectResult(new { error = "Invalid JSON." }); }

        if (dto is null || string.IsNullOrWhiteSpace(dto.Name))
            return new BadRequestObjectResult(new { error = "Name is required." });

        var companyId = GetCompanyId(req);
        if (!companyId.HasValue)
            return new BadRequestObjectResult(new { error = "Company context required." });

        var batch = new OrderBatch
        {
            CompanyId   = companyId.Value,
            Name        = dto.Name,
            HubId       = dto.HubId,
            WarehouseId = dto.WarehouseId,
            TruckId     = dto.TruckId,
        };

        _db.OrderBatches.Add(batch);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Batch created: {Name} ({Id})", batch.Name, batch.Id);
        return new ObjectResult(new { id = batch.Id, name = batch.Name }) { StatusCode = 201 };
    }

    // -----------------------------------------------------------------------
    // POST /api/batches/{id}/orders
    // Body: { orderIds: [...] }
    // -----------------------------------------------------------------------
    [Function("batches-add-orders")]
    public async Task<IActionResult> AddOrders(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "batches/{id:guid}/orders")]
        HttpRequest req, Guid id, CancellationToken ct)
    {
        var batch = await _db.OrderBatches.FindAsync([id], ct);
        if (batch is null)
            return new NotFoundObjectResult(new { error = "Batch not found." });

        if (batch.Status != BatchStatus.Open)
            return new BadRequestObjectResult(new { error = "Can only add orders to an Open batch." });

        List<Guid> orderIds;
        try
        {
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            orderIds = doc.RootElement.GetProperty("orderIds").EnumerateArray()
                .Select(e => Guid.Parse(e.GetString()!))
                .ToList();
        }
        catch { return new BadRequestObjectResult(new { error = "Body must be { \"orderIds\": [...] }" }); }

        var orders = await _db.Orders
            .Where(o => orderIds.Contains(o.Id) && o.BatchId == null)
            .ToListAsync(ct);

        foreach (var order in orders)
        {
            order.BatchId = batch.Id;
            order.WarehouseId = batch.WarehouseId;
            order.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);

        return new OkObjectResult(new { added = orders.Count, batchId = batch.Id });
    }

    // -----------------------------------------------------------------------
    // DELETE /api/batches/{id}/orders/{orderId}
    // -----------------------------------------------------------------------
    [Function("batches-remove-order")]
    public async Task<IActionResult> RemoveOrder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "batches/{id:guid}/orders/{orderId:guid}")]
        HttpRequest req, Guid id, Guid orderId, CancellationToken ct)
    {
        var batch = await _db.OrderBatches.FindAsync([id], ct);
        if (batch is null)
            return new NotFoundObjectResult(new { error = "Batch not found." });

        if (batch.Status != BatchStatus.Open)
            return new BadRequestObjectResult(new { error = "Can only modify an Open batch." });

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId && o.BatchId == id, ct);
        if (order is null)
            return new NotFoundObjectResult(new { error = "Order not in this batch." });

        order.BatchId = null;
        order.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new OkObjectResult(new { removed = orderId });
    }

    // -----------------------------------------------------------------------
    // POST /api/batches/{id}/schedule
    // Body: { pickupDate: "2026-04-10T08:00:00" }
    // Sets the pickup date and triggers route optimization.
    // -----------------------------------------------------------------------
    [Function("batches-schedule")]
    public async Task<IActionResult> Schedule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "batches/{id:guid}/schedule")]
        HttpRequest req, Guid id, CancellationToken ct)
    {
        var batch = await _db.OrderBatches
            .Include(b => b.Orders)
            .FirstOrDefaultAsync(b => b.Id == id, ct);

        if (batch is null)
            return new NotFoundObjectResult(new { error = "Batch not found." });

        if (batch.Status != BatchStatus.Open)
            return new BadRequestObjectResult(new { error = "Batch is already scheduled." });

        if (batch.Orders.Count == 0)
            return new BadRequestObjectResult(new { error = "Batch has no orders." });

        DateTime pickupDate;
        try
        {
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            pickupDate = doc.RootElement.GetProperty("pickupDate").GetDateTime();
        }
        catch { return new BadRequestObjectResult(new { error = "Body must be { \"pickupDate\": \"...\" }" }); }

        batch.PickupDate = pickupDate;
        batch.Status = BatchStatus.Scheduled;
        batch.UpdatedAt = DateTime.UtcNow;

        // Mark all orders as pending optimization
        var validOrderIds = new List<Guid>();
        foreach (var order in batch.Orders)
        {
            if (order.Latitude.HasValue && order.Longitude.HasValue)
            {
                order.Status = OrderStatus.Scheduled;
                order.UpdatedAt = DateTime.UtcNow;
                validOrderIds.Add(order.Id);
            }
        }

        await _db.SaveChangesAsync(ct);

        if (validOrderIds.Count == 0)
        {
            return new BadRequestObjectResult(new { error = "No geocoded orders in batch. Ensure orders are validated first." });
        }

        // Trigger route optimization
        var routeRequestId = Guid.NewGuid();
        await _bus.PublishAsync(ServiceBusQueues.RoutesOptimize,
            new RouteOptimizationRequestMessage(
                routeRequestId,
                batch.CompanyId,
                batch.TruckId,
                batch.HubId,
                batch.WarehouseId,
                pickupDate,
                validOrderIds,
                DateTime.UtcNow), ct);

        _logger.LogInformation(
            "Batch {BatchId} scheduled for {PickupDate} — optimization queued for {Count} orders",
            batch.Id, pickupDate, validOrderIds.Count);

        return new OkObjectResult(new
        {
            batchId = batch.Id,
            pickupDate,
            ordersQueued = validOrderIds.Count,
            routeRequestId,
        });
    }

    // -----------------------------------------------------------------------
    // POST /api/ready-to-pickup  (Admin / SuperAdmin / Logistics)
    // Body: { warehouseLicenseNumber, pickupDateTime }
    // Auto-creates a batch with all Ordered orders for that warehouse,
    // sets them to Scheduled, and triggers route optimization. Inherits
    // the class-level role gate; no [AllowAnonymous] override.
    // -----------------------------------------------------------------------
    [Function("batches-ready-to-pickup")]
    public async Task<IActionResult> ReadyToPickup(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "ready-to-pickup")]
        HttpRequest req, CancellationToken ct)
    {
        Domain.DTOs.ReadyToPickupDto? dto;
        try { dto = await System.Text.Json.JsonSerializer.DeserializeAsync<Domain.DTOs.ReadyToPickupDto>(req.Body, _json, ct); }
        catch { return new BadRequestObjectResult(new { error = "Invalid JSON." }); }

        if (dto is null || string.IsNullOrWhiteSpace(dto.WarehouseLicenseNumber) || string.IsNullOrWhiteSpace(dto.CompanySlug))
            return new BadRequestObjectResult(new { error = "companySlug and warehouseLicenseNumber are required." });

        // Resolve company by slug or ID
        var company = await _db.Companies.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => (c.Slug == dto.CompanySlug || c.Id.ToString() == dto.CompanySlug) && c.IsActive, ct);

        if (company is null)
            return new NotFoundObjectResult(new { error = $"Company not found: {dto.CompanySlug}" });

        // Look up warehouse by license number scoped to company
        var warehouse = await _db.Warehouses.IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.LicenseNumber == dto.WarehouseLicenseNumber && w.CompanyId == company.Id && w.IsActive, ct);

        if (warehouse is null)
            return new NotFoundObjectResult(new { error = $"Warehouse not found: {dto.WarehouseLicenseNumber} for company {dto.CompanySlug}" });

        // Pull every Ordered order at this warehouse — orders are sales-order
        // level only (no per-item readiness), so the warehouse signaling
        // "ready to pickup" implicitly marks every pending order ready.
        var orders = await _db.Orders.IgnoreQueryFilters()
            .Where(o => o.WarehouseId == warehouse.Id && o.CompanyId == company.Id && o.Status == OrderStatus.Ordered)
            .ToListAsync(ct);

        if (orders.Count == 0)
            return new OkObjectResult(new { message = "No pending orders for this warehouse.", orderCount = 0 });

        // Get all active hubs with available trucks
        var hubs = await _db.Hubs.IgnoreQueryFilters()
            .Where(h => h.CompanyId == warehouse.CompanyId && h.IsActive)
            .ToListAsync(ct);

        if (hubs.Count == 0)
            return new BadRequestObjectResult(new { error = "No active hubs configured for this company." });

        // Filter to geocoded orders only
        var routableOrders = orders.Where(o => o.Latitude.HasValue && o.Longitude.HasValue).ToList();
        if (routableOrders.Count == 0)
            return new BadRequestObjectResult(new { error = "No geocoded orders available for routing." });

        // ---- Assign orders to best hub using Google Maps Distance Matrix ----
        Dictionary<Guid, List<Order>> hubAssignments;

        if (hubs.Count == 1)
        {
            // Single hub — no need for Distance Matrix
            hubAssignments = new() { [hubs[0].Id] = routableOrders };
        }
        else
        {
            // Use Distance Matrix: measure distance from each hub to each store
            var hubAddresses = hubs.Select(h => h.FormattedAddress ?? h.FullAddress).ToList();
            var storeAddresses = routableOrders.Select(o => o.FormattedAddress ?? o.FullAddress).ToList();

            var matrix = await _maps.GetDistanceMatrixAsync(hubAddresses, storeAddresses, ct);

            hubAssignments = hubs.ToDictionary(h => h.Id, _ => new List<Order>());

            if (matrix is not null && matrix.Count > 0)
            {
                // For each order, find the hub with shortest travel time
                for (int orderIdx = 0; orderIdx < routableOrders.Count; orderIdx++)
                {
                    var order = routableOrders[orderIdx];
                    var bestHubId = hubs[0].Id;
                    var bestDuration = int.MaxValue;

                    for (int hubIdx = 0; hubIdx < hubs.Count; hubIdx++)
                    {
                        // Matrix entries: hubIdx * destCount + orderIdx
                        var entryIdx = hubIdx * routableOrders.Count + orderIdx;
                        if (entryIdx < matrix.Count && matrix[entryIdx].DurationSeconds < bestDuration)
                        {
                            bestDuration = matrix[entryIdx].DurationSeconds;
                            bestHubId = hubs[hubIdx].Id;
                        }
                    }

                    hubAssignments[bestHubId].Add(order);
                }
            }
            else
            {
                // Fallback: assign all to first hub if Distance Matrix fails
                _logger.LogWarning("Distance Matrix failed, falling back to first hub");
                hubAssignments[hubs[0].Id] = routableOrders;
            }
        }

        // ---- Create one batch + route per hub that has orders ----
        var batchResults = new List<object>();
        int totalScheduled = 0;

        foreach (var (hubId, hubOrders) in hubAssignments)
        {
            if (hubOrders.Count == 0) continue;

            var hub = hubs.First(h => h.Id == hubId);

            // Pick an available truck from this hub
            var truck = await _db.Trucks.IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.HubId == hubId && t.IsActive, ct);

            var batch = new OrderBatch
            {
                CompanyId   = warehouse.CompanyId,
                Name        = $"{warehouse.BusinessName} → {hub.Name} - {dto.PickupDateTime:MMM dd HH:mm}",
                HubId       = hubId,
                WarehouseId = warehouse.Id,
                TruckId     = truck?.Id,
                PickupDate  = dto.PickupDateTime,
                Status      = BatchStatus.Scheduled,
            };
            _db.OrderBatches.Add(batch);

            var orderIds = new List<Guid>();
            foreach (var order in hubOrders)
            {
                order.BatchId = batch.Id;
                order.Status = OrderStatus.Scheduled;
                order.UpdatedAt = DateTime.UtcNow;
                orderIds.Add(order.Id);
            }

            await _db.SaveChangesAsync(ct);

            // Trigger route optimization for this hub's batch
            var routeRequestId = Guid.NewGuid();
            await _bus.PublishAsync(ServiceBusQueues.RoutesOptimize,
                new RouteOptimizationRequestMessage(
                    routeRequestId, warehouse.CompanyId, truck?.Id, hubId, warehouse.Id,
                    dto.PickupDateTime, orderIds, DateTime.UtcNow), ct);

            totalScheduled += orderIds.Count;

            batchResults.Add(new
            {
                batchId = batch.Id,
                hubName = hub.Name,
                truckName = truck?.Name,
                orderCount = orderIds.Count,
                routeRequestId,
            });

            _logger.LogInformation(
                "ReadyToPickup: batch {BatchId} for hub {Hub} with {Count} orders, truck {Truck}",
                batch.Id, hub.Name, orderIds.Count, truck?.Name ?? "unassigned");
        }

        // Mark non-routable orders as scheduled too (they're in the batch but won't route)
        foreach (var order in orders.Where(o => !o.Latitude.HasValue || !o.Longitude.HasValue))
        {
            order.Status = OrderStatus.Scheduled;
            order.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);

        await _bus.PublishAsync(ServiceBusQueues.Audit, new AuditMessage(
            AuditEventType.BatchCreatedViaPickup, nameof(BatchAgent),
            warehouse.CompanyId, null, null, null, null,
            $"ReadyToPickup: {orders.Count} orders for {warehouse.BusinessName} split across {batchResults.Count} hub(s)",
            true, null, DateTime.UtcNow), ct);

        return new OkObjectResult(new
        {
            warehouseName = warehouse.BusinessName,
            pickupDate = dto.PickupDateTime,
            totalOrders = orders.Count,
            totalScheduled,
            batches = batchResults,
        });
    }

    // -----------------------------------------------------------------------
    private static Guid? GetCompanyId(HttpRequest req)
    {
        var claim = req.HttpContext.User.FindFirst("companyId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}

public class CreateBatchDto
{
    public string Name { get; set; } = string.Empty;
    public Guid HubId { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid? TruckId { get; set; }
}
