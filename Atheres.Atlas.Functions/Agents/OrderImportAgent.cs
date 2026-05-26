using System.Globalization;
using System.Text.Json;
using Atheres.Atlas.Data;
using Atheres.Atlas.Domain.Constants;
using Atheres.Atlas.Domain.Entities;
using Atheres.Atlas.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Functions.Agents;

/// <summary>
/// CSV-driven order import for the admin "Import" UI. Accepts a list of rows
/// already mapped to canonical fields by the frontend mapping grid and creates
/// Order rows directly — no upstream warehouse system, no product detail.
/// Each order is created in <see cref="OrderStatus.Ordered"/> with the
/// Customer/SalesOrderNumber/PurchaseOrderNumber metadata captured so product
/// detail can be filled in later.
/// </summary>
[Authorize(Roles = "Admin,SuperAdmin,OrderImporter")]
public class OrderImportAgent
{
    private readonly AtlasDbContext _db;
    private readonly Atheres.Atlas.Functions.Services.IRouteScheduler _scheduler;
    private readonly ILogger<OrderImportAgent> _log;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public OrderImportAgent(
        AtlasDbContext db,
        Atheres.Atlas.Functions.Services.IRouteScheduler scheduler,
        ILogger<OrderImportAgent> log)
    {
        _db        = db;
        _scheduler = scheduler;
        _log       = log;
    }

    /// <summary>
    /// POST /api/orders/import
    /// Body: { rows: ImportedOrderRow[] }
    /// Returns: { created, orderIds, errors }
    /// </summary>
    [Function("orders-import")]
    public async Task<IActionResult> Import(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders/import")]
        HttpRequest req, CancellationToken ct)
    {
        ImportOrdersDto? dto;
        try { dto = await JsonSerializer.DeserializeAsync<ImportOrdersDto>(req.Body, _json, ct); }
        catch { return new BadRequestObjectResult(new { error = "Invalid JSON." }); }

        if (dto?.Rows is null || dto.Rows.Count == 0)
            return new BadRequestObjectResult(new { error = "No rows provided." });

        // Initial status defaults to Ordered. Operators can also pick
        // Scheduled, in which case routable rows are routed immediately
        // through RouteScheduler and stale rows fall through to
        // RouteOmitted. Anything outside that allowlist is rejected so
        // typos don't silently produce broken orders.
        var requestedInitial = OrderStatus.Ordered;
        if (!string.IsNullOrWhiteSpace(dto.InitialStatus))
        {
            if (!Enum.TryParse<OrderStatus>(dto.InitialStatus, ignoreCase: true, out requestedInitial)
                || (requestedInitial != OrderStatus.Ordered && requestedInitial != OrderStatus.Scheduled))
            {
                return new BadRequestObjectResult(new
                {
                    error = $"Unsupported initialStatus '{dto.InitialStatus}'. Allowed: Ordered, Scheduled.",
                });
            }
        }

        var companyId = GetCallerCompanyId(req);
        if (!companyId.HasValue)
            return new BadRequestObjectResult(new { error = "Company context required. Select a company first." });

        // Confirm the resolved company exists. SuperAdmin sets X-Company-Id;
        // Admin's company is on the JWT.
        var company = await _db.Companies.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == companyId.Value && c.IsActive, ct);
        if (company is null)
            return new BadRequestObjectResult(new { error = "Company not found." });

        // OrderImporter accounts are pinned to a single warehouse via the
        // "warehouseId" JWT claim. We require that claim and validate it
        // against the caller's company so a tampered token can't drop
        // orders into a warehouse that doesn't belong to them.
        var isOrderImporter = req.HttpContext.User.IsInRole(Roles.OrderImporter);
        Guid? defaultWarehouseId = null;
        if (isOrderImporter)
        {
            var claim = req.HttpContext.User.FindFirst("warehouseId")?.Value;
            if (!Guid.TryParse(claim, out var warehouseId))
                return new BadRequestObjectResult(new { error = "Order Importer account is missing a warehouse assignment." });

            var warehouseExists = await _db.Warehouses.IgnoreQueryFilters()
                .AnyAsync(
                    w => w.Id == warehouseId
                      && w.IsActive
                      && w.Companies.Any(c => c.Id == company.Id),
                    ct);
            if (!warehouseExists)
                return new BadRequestObjectResult(new { error = "Assigned warehouse not found or inactive." });

            defaultWarehouseId = warehouseId;
        }

        // Pre-load every store the import could possibly reference so we
        // don't issue one query per row. The frontend sends StoreId for
        // each row (it has already resolved them via the preview grid),
        // but we re-look-up server-side as a defense-in-depth check —
        // the operator could have edited the request, the store could be
        // deactivated, or a SuperAdmin could be importing into a tenant
        // they don't actually administrate.
        var storeIds = dto.Rows
            .Where(r => r.StoreId.HasValue)
            .Select(r => r.StoreId!.Value)
            .Distinct()
            .ToList();

        var storesById = await _db.Stores.IgnoreQueryFilters()
            .Where(s => storeIds.Contains(s.Id) && s.Companies.Any(c => c.Id == company.Id))
            .ToDictionaryAsync(s => s.Id, ct);

        // Pre-load every warehouse the import could possibly reference,
        // restricted to the caller's company via the many-to-many join.
        // Each row's WarehouseId overrides defaultWarehouseId (if any);
        // requests from non-importer callers must specify it per row.
        var warehouseIds = dto.Rows
            .Where(r => r.WarehouseId.HasValue)
            .Select(r => r.WarehouseId!.Value)
            .Distinct()
            .ToList();

        var warehousesById = warehouseIds.Count == 0
            ? new Dictionary<Guid, Warehouse>()
            : await _db.Warehouses.IgnoreQueryFilters()
                .Where(w => warehouseIds.Contains(w.Id)
                         && w.IsActive
                         && w.Companies.Any(c => c.Id == company.Id))
                .ToDictionaryAsync(w => w.Id, ct);

        // Pre-fetch every order in this company whose SalesOrderNumber OR
        // PurchaseOrderNumber matches one of the incoming rows. We overwrite
        // those rows in-place instead of inserting a duplicate — same
        // SO#/PO# = same physical order, and the import semantics are
        // "the new row IS the source of truth." Comparison is case-
        // insensitive so "SO-123" matches "so-123" the way operators
        // expect when they paste from a spreadsheet.
        var batchSalesOrderNumbers = dto.Rows
            .Select(r => r.SalesOrderNumber?.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();
        var batchPurchaseOrderNumbers = dto.Rows
            .Select(r => r.PurchaseOrderNumber?.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();

        var existingMatches = (batchSalesOrderNumbers.Count == 0 && batchPurchaseOrderNumbers.Count == 0)
            ? new List<Order>()
            : await _db.Orders.IgnoreQueryFilters()
                .Where(o => o.CompanyId == company.Id
                    && ((o.SalesOrderNumber != null && batchSalesOrderNumbers.Contains(o.SalesOrderNumber))
                     || (o.PurchaseOrderNumber != null && batchPurchaseOrderNumbers.Contains(o.PurchaseOrderNumber))))
                .ToListAsync(ct);

        // Two lookup maps so a row can match by either SO# or PO# in O(1).
        // The same Order may appear in both when it carries both numbers.
        var existingBySalesOrder    = new Dictionary<string, Order>(StringComparer.OrdinalIgnoreCase);
        var existingByPurchaseOrder = new Dictionary<string, Order>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in existingMatches)
        {
            if (!string.IsNullOrWhiteSpace(existing.SalesOrderNumber))
                existingBySalesOrder[existing.SalesOrderNumber] = existing;
            if (!string.IsNullOrWhiteSpace(existing.PurchaseOrderNumber))
                existingByPurchaseOrder[existing.PurchaseOrderNumber] = existing;
        }

        // Track SO#/PO# we've staged in this single import so a batch
        // carrying an internal duplicate still errors — silently merging
        // two rows that disagree would lose data.
        var batchSeenSalesOrders    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batchSeenPurchaseOrders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Track which existing orders we've already targeted for overwrite
        // in this batch. Catches the case where row 1 matches order X by
        // SO# and row 2 matches the same X by PO# — both would silently
        // clobber each other otherwise.
        var batchOverwrittenIds     = new HashSet<Guid>();

        var errors          = new List<ImportRowError>();
        var insertedOrders  = new List<Order>();
        var overwrittenOrders = new List<Order>();

        for (var i = 0; i < dto.Rows.Count; i++)
        {
            var row = dto.Rows[i];

            if (!TryParseDate(row.OrderDate, out var orderDate))
            {
                errors.Add(new ImportRowError(i, $"Invalid Order Date: '{row.OrderDate}'."));
                continue;
            }

            // Each importable row must reference a Store within the caller's
            // company. The preview grid forces this on the frontend (rows
            // are either ignored, matched by license, or have a new store
            // created inline) — we still verify here so a tampered request
            // can't bypass the check.
            if (!row.StoreId.HasValue
                || !storesById.TryGetValue(row.StoreId.Value, out var matchedStore))
            {
                errors.Add(new ImportRowError(i, "Row has no matching store. Match by license number or create a new store."));
                continue;
            }

            // Resolve the pickup warehouse for this row: row.WarehouseId wins
            // if present and valid; otherwise fall back to the importer's
            // pinned warehouse. Without a warehouse the order has no pickup
            // leg, which is precisely the bug that motivated this column.
            Guid? rowWarehouseId;
            if (row.WarehouseId.HasValue)
            {
                if (!warehousesById.ContainsKey(row.WarehouseId.Value))
                {
                    errors.Add(new ImportRowError(i, "Warehouse not found or not accessible by this company."));
                    continue;
                }
                // Defense in depth: an OrderImporter shouldn't be able to
                // drop orders into a warehouse other than their pinned one
                // by forging the request. defaultWarehouseId is the pinned
                // warehouse extracted from the JWT claim above.
                if (isOrderImporter && row.WarehouseId.Value != defaultWarehouseId)
                {
                    errors.Add(new ImportRowError(i, "Order Importer can only import orders pinned to their assigned warehouse."));
                    continue;
                }
                rowWarehouseId = row.WarehouseId;
            }
            else if (defaultWarehouseId.HasValue)
            {
                rowWarehouseId = defaultWarehouseId;
            }
            else
            {
                errors.Add(new ImportRowError(i, "Row has no Warehouse License #. Map a Warehouse License # column or sign in as an Order Importer for a specific warehouse."));
                continue;
            }

            var salesOrder    = NullIfBlank(row.SalesOrderNumber);
            var purchaseOrder = NullIfBlank(row.PurchaseOrderNumber);

            // Within-batch dedup — two rows in the same upload sharing an
            // SO#/PO# is almost always a CSV mistake; merging silently
            // would lose whichever variant comes later in the file.
            if (salesOrder is not null && !batchSeenSalesOrders.Add(salesOrder))
            {
                errors.Add(new ImportRowError(i, $"Sales order '{salesOrder}' is duplicated within this import."));
                continue;
            }
            if (purchaseOrder is not null && !batchSeenPurchaseOrders.Add(purchaseOrder))
            {
                errors.Add(new ImportRowError(i, $"Purchase order '{purchaseOrder}' is duplicated within this import."));
                continue;
            }

            // Match this row against any existing order in the company.
            // Either SO# or PO# is sufficient; if both match but point to
            // different orders the row is ambiguous and we error rather
            // than silently overwrite the wrong one.
            Order? matchBySo = (salesOrder    is not null && existingBySalesOrder.TryGetValue(salesOrder, out var so)) ? so : null;
            Order? matchByPo = (purchaseOrder is not null && existingByPurchaseOrder.TryGetValue(purchaseOrder, out var po)) ? po : null;
            if (matchBySo is not null && matchByPo is not null && matchBySo.Id != matchByPo.Id)
            {
                errors.Add(new ImportRowError(i,
                    $"Row matches two different existing orders: SO '{salesOrder}' → {matchBySo.Id}, PO '{purchaseOrder}' → {matchByPo.Id}. Resolve the conflict before re-importing."));
                continue;
            }
            var existing = matchBySo ?? matchByPo;
            if (existing is not null && !batchOverwrittenIds.Add(existing.Id))
            {
                errors.Add(new ImportRowError(i,
                    $"Multiple rows in this import target the same existing order ({existing.Id}). Merge them before re-importing."));
                continue;
            }

            // Initial status reset on every imported row (insert OR
            // overwrite): when the operator picked Scheduled, route
            // immediately — unless the order is more than a day old, in
            // which case it falls through to RouteOmitted (consistent
            // with every other "schedule this" path in the system).
            var resolvedStatus = requestedInitial == OrderStatus.Scheduled
                ? (RoutingPolicy.IsTooOldToRoute(orderDate, DateTime.UtcNow)
                    ? OrderStatus.RouteOmitted
                    : OrderStatus.Scheduled)
                : OrderStatus.Ordered;

            // Order address comes from the matched Store, not the CSV. The
            // store has already been geocoded so route optimization gets
            // real coordinates, and "address on the order" stays consistent
            // with the store master data even when the CSV has a slightly
            // different formatting.
            if (existing is not null)
            {
                // Overwrite the existing order in place. Id / CompanyId /
                // CreatedAt stay so FK relationships and the audit trail
                // survive; every domain field gets the new value. Any
                // transient routing state (batch, route, sequence,
                // confirmation) is wiped because the row's content has
                // changed — the prior optimization is stale.
                existing.WarehouseId            = rowWarehouseId;
                existing.StoreId                = matchedStore.Id;
                existing.StoreLicenseNumber     = matchedStore.LicenseNumber;
                existing.StoreName              = matchedStore.Name;
                existing.Address                = matchedStore.Address;
                existing.City                   = matchedStore.City;
                existing.State                  = matchedStore.State;
                existing.Zip                    = matchedStore.Zip;
                existing.County                 = matchedStore.County ?? string.Empty;
                existing.LicenseNumber          = matchedStore.LicenseNumber ?? string.Empty;
                existing.Customer               = NullIfBlank(row.Customer);
                existing.SalesOrderNumber       = salesOrder;
                existing.PurchaseOrderNumber    = purchaseOrder;
                existing.Email                  = matchedStore.Email ?? string.Empty;
                existing.Phone                  = matchedStore.Phone;
                existing.Latitude               = matchedStore.Latitude;
                existing.Longitude              = matchedStore.Longitude;
                existing.FormattedAddress       = matchedStore.FormattedAddress;
                existing.OrderDate              = orderDate;
                existing.Status                 = resolvedStatus;
                existing.Notes                  = "Re-imported via CSV";
                existing.UpdatedAt              = DateTime.UtcNow;

                // Reset transient routing/confirmation state — the prior
                // route was optimized for the prior data and is no longer
                // valid. A Scheduled overwrite will fan out through
                // RouteScheduler below and re-optimize.
                existing.BatchId                = null;
                existing.RouteId                = null;
                existing.StopSequence           = null;
                existing.ExpectedDeliveryDate   = null;
                existing.ConfirmationDeadline   = null;
                existing.ConfirmationToken      = null;
                existing.ConfirmedAt            = null;
                existing.RescheduleCount        = 0;
                existing.DeferredAt             = null;
                existing.OriginalRouteId        = null;
                existing.IsAtHub                = false;
                existing.ValidationErrors       = null;

                overwrittenOrders.Add(existing);
            }
            else
            {
                var order = new Order
                {
                    CompanyId           = company.Id,
                    WarehouseId         = rowWarehouseId,
                    StoreId             = matchedStore.Id,
                    StoreLicenseNumber  = matchedStore.LicenseNumber,
                    StoreName           = matchedStore.Name,
                    Address             = matchedStore.Address,
                    City                = matchedStore.City,
                    State               = matchedStore.State,
                    Zip                 = matchedStore.Zip,
                    County              = matchedStore.County ?? string.Empty,
                    LicenseNumber       = matchedStore.LicenseNumber ?? string.Empty,
                    Customer            = NullIfBlank(row.Customer),
                    SalesOrderNumber    = salesOrder,
                    PurchaseOrderNumber = purchaseOrder,
                    Email               = matchedStore.Email ?? string.Empty,
                    Phone               = matchedStore.Phone,
                    Latitude            = matchedStore.Latitude,
                    Longitude           = matchedStore.Longitude,
                    FormattedAddress    = matchedStore.FormattedAddress,
                    OrderDate           = orderDate,
                    Status              = resolvedStatus,
                    Notes               = "Imported via CSV",
                };

                _db.Orders.Add(order);
                insertedOrders.Add(order);
            }
        }

        // The combined list — order matters only for the response payload
        // (insertedOrders are reported first, then overwrites). Save once;
        // EF tracks the inserts (Added by AddRange below) and the
        // overwrites (Modified — already tracked via the existing-entity
        // mutations above) in a single round trip.
        var allTouched = insertedOrders.Concat(overwrittenOrders).ToList();
        if (allTouched.Count == 0)
            return new BadRequestObjectResult(new { error = "No valid rows to import.", errors });

        await _db.SaveChangesAsync(ct);

        // Single batched optimization run for everything that came in as
        // Scheduled — both fresh inserts AND overwrites of pre-existing
        // orders. RouteScheduler groups by warehouse and chunks per
        // company.MaxStopsPerRoute, so even a 200-row Scheduled import
        // produces a small, bounded number of optimization messages —
        // never one per order.
        var routesQueued       = 0;
        var ordersUngeocoded   = 0;
        var noActiveHub        = false;
        var storesGeocoded     = 0;
        var hubsGeocoded       = 0;
        var warehousesGeocoded = 0;
        var geocodeFailures    = 0;
        var routeOmittedCount  = allTouched.Count(o => o.Status == OrderStatus.RouteOmitted);
        var freshlyScheduled   = allTouched.Where(o => o.Status == OrderStatus.Scheduled).ToList();
        if (freshlyScheduled.Count > 0)
        {
            try
            {
                var triggeredBy = req.HttpContext.User?.Identity?.Name
                                  ?? req.HttpContext.User?.FindFirst("email")?.Value;
                var outcome = await _scheduler.EnqueueAsync(
                    freshlyScheduled, company.Id, ct,
                    triggeredBy: triggeredBy,
                    trigger:     "CSV import (initialStatus=Scheduled)");
                routesQueued       = outcome.RoutesQueued;
                ordersUngeocoded   = outcome.OrdersUngeocoded;
                noActiveHub        = outcome.NoActiveHub;
                storesGeocoded     = outcome.StoresGeocoded;
                hubsGeocoded       = outcome.HubsGeocoded;
                warehousesGeocoded = outcome.WarehousesGeocoded;
                geocodeFailures    = outcome.GeocodeFailures;
            }
            catch (Exception ex)
            {
                // Orders are persisted in Scheduled state; failing the
                // whole call would lose visibility of them. Surface the
                // error in the response so the operator can retry routing.
                _log.LogError(ex, "CSV import: scheduler enqueue failed; orders left as Scheduled.");
                return new ObjectResult(new
                {
                    created      = insertedOrders.Count,
                    overwritten  = overwrittenOrders.Count,
                    orderIds     = allTouched.Select(o => o.Id),
                    routesQueued = 0,
                    routeOmitted = routeOmittedCount,
                    errors,
                    schedulerError = ex.Message,
                })
                { StatusCode = 207 }; // multi-status — partial success
            }
        }

        _log.LogInformation(
            "CSV import: {Created} created, {Overwritten} overwritten for company {Company} ({Errors} skipped, {Routes} run(s) queued, geocoded {Stores}/{Hubs}/{Warehouses} stores/hubs/warehouses, {Ungeocoded} ungeocoded skipped, NoActiveHub={NoHub})",
            insertedOrders.Count, overwrittenOrders.Count, company.Id, errors.Count, routesQueued,
            storesGeocoded, hubsGeocoded, warehousesGeocoded,
            ordersUngeocoded, noActiveHub);

        return new OkObjectResult(new
        {
            created            = insertedOrders.Count,
            overwritten        = overwrittenOrders.Count,
            orderIds           = allTouched.Select(o => o.Id),
            routesQueued,
            ordersUngeocoded,
            noActiveHub,
            storesGeocoded,
            hubsGeocoded,
            warehousesGeocoded,
            geocodeFailures,
            routeOmitted       = routeOmittedCount,
            initialStatus      = requestedInitial.ToString(),
            errors,
        });
    }

    // -----------------------------------------------------------------------
    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool TryParseDate(string? value, out DateTime parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        // Accept ISO-8601, US-style (M/d/yyyy), and any locale-default
        // representation. Spreadsheets almost always emit one of those three.
        if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsed))
        {
            return true;
        }

        return DateTime.TryParse(value, out parsed);
    }

    private static Guid? GetCallerCompanyId(HttpRequest req)
    {
        // SuperAdmin operates cross-tenant: pick the company off the
        // X-Company-Id header that the SPA already sets for other admin calls.
        if (req.HttpContext.User.IsInRole(Roles.SuperAdmin))
        {
            var header = req.Headers["X-Company-Id"].ToString();
            if (Guid.TryParse(header, out var headerId)) return headerId;
        }

        var claim = req.HttpContext.User.FindFirst("companyId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }
}

// ---- DTOs ------------------------------------------------------------------

public class ImportOrdersDto
{
    public List<ImportedOrderRow> Rows { get; set; } = [];

    /// <summary>
    /// Optional initial status for every imported row. Defaults to "Ordered".
    /// "Scheduled" routes the import immediately through RouteScheduler —
    /// stale rows still fall through to RouteOmitted as everywhere else.
    /// Other values are rejected with 400.
    /// </summary>
    public string? InitialStatus { get; set; }
}

public class ImportedOrderRow
{
    /// <summary>The store the order is delivered to. Resolved by the
    /// frontend's preview grid (license-number match or inline create);
    /// re-validated server-side against the caller's company.</summary>
    public Guid? StoreId { get; set; }
    /// <summary>Pickup warehouse for this order, resolved by the frontend
    /// from the row's Warehouse License # column. Optional in the payload
    /// because OrderImporter accounts fall back to their JWT-pinned
    /// warehouse server-side; required (effectively) for Admin/SuperAdmin
    /// imports since they have no default. Re-validated against the
    /// caller's company.</summary>
    public Guid? WarehouseId { get; set; }
    public string? OrderDate { get; set; }
    public string? Customer { get; set; }
    public string? SalesOrderNumber { get; set; }
    public string? PurchaseOrderNumber { get; set; }
}

public record ImportRowError(int Row, string Message);
