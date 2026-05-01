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
    private readonly ILogger<OrderImportAgent> _log;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public OrderImportAgent(AtlasDbContext db, ILogger<OrderImportAgent> log)
    {
        _db  = db;
        _log = log;
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
                .AnyAsync(w => w.Id == warehouseId && w.CompanyId == company.Id && w.IsActive, ct);
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
            .Where(s => s.CompanyId == company.Id && storeIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);

        var errors = new List<ImportRowError>();
        var orders = new List<Order>();

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

            // Order address comes from the matched Store, not the CSV. The
            // store has already been geocoded so route optimization gets
            // real coordinates, and "address on the order" stays consistent
            // with the store master data even when the CSV has a slightly
            // different formatting.
            var order = new Order
            {
                CompanyId           = company.Id,
                WarehouseId         = defaultWarehouseId,
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
                SalesOrderNumber    = NullIfBlank(row.SalesOrderNumber),
                PurchaseOrderNumber = NullIfBlank(row.PurchaseOrderNumber),
                Email               = matchedStore.Email ?? string.Empty,
                Phone               = matchedStore.Phone,
                Latitude            = matchedStore.Latitude,
                Longitude           = matchedStore.Longitude,
                FormattedAddress    = matchedStore.FormattedAddress,
                OrderDate           = orderDate,
                Status              = OrderStatus.Ordered,
                Notes               = "Imported via CSV",
            };

            orders.Add(order);
        }

        if (orders.Count == 0)
            return new BadRequestObjectResult(new { error = "No valid rows to import.", errors });

        _db.Orders.AddRange(orders);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "CSV import: {Created} orders created for company {Company} ({Errors} skipped)",
            orders.Count, company.Id, errors.Count);

        return new OkObjectResult(new
        {
            created  = orders.Count,
            orderIds = orders.Select(o => o.Id),
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
}

public class ImportedOrderRow
{
    /// <summary>The store the order is delivered to. Resolved by the
    /// frontend's preview grid (license-number match or inline create);
    /// re-validated server-side against the caller's company.</summary>
    public Guid? StoreId { get; set; }
    public string? OrderDate { get; set; }
    public string? Customer { get; set; }
    public string? SalesOrderNumber { get; set; }
    public string? PurchaseOrderNumber { get; set; }
}

public record ImportRowError(int Row, string Message);
