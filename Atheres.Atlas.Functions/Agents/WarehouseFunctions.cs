using System.Text.Json;
using Atheres.Atlas.Data;
using Atheres.Atlas.Data.Services;
using Atheres.Atlas.Domain.Constants;
using Atheres.Atlas.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Functions.Agents;

/// <summary>
/// Warehouse management — external pickup locations. A warehouse can be shared
/// by several companies (many-to-many).
///
/// Tenant scoping is handled by the AtlasDbContext global query filter
/// (driven by ICompanyContext): a company Admin sees / touches only warehouses
/// linked to their company, a SuperAdmin (null company context) any. A
/// SuperAdmin can scope a listing to one company via ?companyId={guid} (or the
/// X-Company-Id header honored by HttpCompanyContext).
/// </summary>
public class WarehouseFunctions
{
    private readonly AtlasDbContext _db;
    private readonly ICompanyContext _company;
    private readonly ILogger<WarehouseFunctions> _log;

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public WarehouseFunctions(AtlasDbContext db, ICompanyContext company, ILogger<WarehouseFunctions> log)
    {
        _db      = db;
        _company = company;
        _log     = log;
    }

    // -----------------------------------------------------------------------
    // GET /api/warehouses  (Admin, SuperAdmin)
    // -----------------------------------------------------------------------
    [Function("warehouses-list")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "warehouses")]
        HttpRequest req, CancellationToken ct)
    {
        // The global query filter already scopes a company Admin to warehouses
        // linked to their company and lets a SuperAdmin (null company context)
        // see all. SuperAdmin may further scope to one company via
        // ?companyId={guid}; a blank or empty-Guid value means "all companies".
        var query = _db.Warehouses.Where(w => w.IsActive);
        if (req.HttpContext.User.IsInRole(Roles.SuperAdmin)
            && Guid.TryParse(req.Query["companyId"], out var filterCompanyId)
            && filterCompanyId != Guid.Empty)
            query = query.Where(w => w.Companies.Any(c => c.Id == filterCompanyId));

        var warehouses = await query
            .OrderBy(w => w.BusinessName)
            .Select(w => new
            {
                w.Id,
                companies = w.Companies.Select(c => new { id = c.Id, name = c.Name }),
                w.BusinessName, w.AlternateName,
                w.Address, w.City, w.State, w.Zip,
                w.LicenseNumber, w.LegacyLicenseNumber,
                w.LoadingWaitMinutes,
                w.IsChicagoLand,
                w.IsActive,
                mondayPickupTime    = w.MondayPickupTime.HasValue    ? w.MondayPickupTime.Value.ToString(@"hh\:mm")    : null,
                tuesdayPickupTime   = w.TuesdayPickupTime.HasValue   ? w.TuesdayPickupTime.Value.ToString(@"hh\:mm")   : null,
                wednesdayPickupTime = w.WednesdayPickupTime.HasValue ? w.WednesdayPickupTime.Value.ToString(@"hh\:mm") : null,
                thursdayPickupTime  = w.ThursdayPickupTime.HasValue  ? w.ThursdayPickupTime.Value.ToString(@"hh\:mm")  : null,
                fridayPickupTime    = w.FridayPickupTime.HasValue    ? w.FridayPickupTime.Value.ToString(@"hh\:mm")    : null,
                saturdayPickupTime  = w.SaturdayPickupTime.HasValue  ? w.SaturdayPickupTime.Value.ToString(@"hh\:mm")  : null,
                sundayPickupTime    = w.SundayPickupTime.HasValue    ? w.SundayPickupTime.Value.ToString(@"hh\:mm")    : null,
            })
            .ToListAsync(ct);

        return new OkObjectResult(warehouses);
    }

    // -----------------------------------------------------------------------
    // POST /api/warehouses  (Admin, SuperAdmin)
    // -----------------------------------------------------------------------
    [Function("warehouses-create")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "warehouses")]
        HttpRequest req, CancellationToken ct)
    {
        CreateWarehouseDto? dto;
        try { dto = await JsonSerializer.DeserializeAsync<CreateWarehouseDto>(req.Body, _json, ct); }
        catch { return new BadRequestObjectResult(new { error = "Invalid JSON." }); }

        if (dto is null || string.IsNullOrWhiteSpace(dto.BusinessName) || string.IsNullOrWhiteSpace(dto.Address))
            return new BadRequestObjectResult(new { error = "BusinessName and Address are required." });

        // Create can't be enforced by the read-side query filter, so the target
        // company is resolved + authorized explicitly: it comes from the
        // caller's context, and a SuperAdmin (null context) must name it via the
        // DTO. A company Admin may only create warehouses for their own company.
        var isSuperAdmin = req.HttpContext.User.IsInRole(Roles.SuperAdmin);
        var callerCompanyId = _company.CompanyId;

        var targetCompanyId = dto.CompanyId ?? callerCompanyId;
        if (targetCompanyId is null)
            return new BadRequestObjectResult(new { error = "Company context required." });

        if (!isSuperAdmin && targetCompanyId != callerCompanyId)
            return Forbid();

        // Multi-tenant: a new warehouse starts linked to the target company.
        // Additional companies can be attached later through a future
        // sharing endpoint; for now Create only adds the single membership.
        // IgnoreQueryFilters so a SuperAdmin (whose context is null) and a
        // company Admin alike can resolve the target Company row.
        var targetCompany = await _db.Companies.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == targetCompanyId.Value, ct);
        if (targetCompany is null)
            return new BadRequestObjectResult(new { error = "Target company not found." });

        var warehouse = new Warehouse
        {
            BusinessName         = dto.BusinessName,
            AlternateName        = dto.AlternateName,
            Address              = dto.Address,
            City                 = dto.City ?? string.Empty,
            State                = dto.State ?? string.Empty,
            Zip                  = dto.Zip ?? string.Empty,
            LicenseNumber        = dto.LicenseNumber,
            LegacyLicenseNumber  = dto.LegacyLicenseNumber,
            // Loading wait — clamped to the same 0–240 range used by
            // Hub.SortingWaitMinutes so a fat-finger value can't push
            // delivery routes hours out.
            LoadingWaitMinutes   = ClampLoadingWait(dto.LoadingWaitMinutes ?? 15),
            IsChicagoLand        = dto.IsChicagoLand ?? false,
        };
        warehouse.Companies.Add(targetCompany);

        _db.Warehouses.Add(warehouse);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Warehouse created: {Name} ({Id}) for company {Company}",
            warehouse.BusinessName, warehouse.Id, targetCompanyId);

        return new ObjectResult(new { id = warehouse.Id, businessName = warehouse.BusinessName }) { StatusCode = 201 };
    }

    // -----------------------------------------------------------------------
    // PUT /api/warehouses/{id}  (Admin, SuperAdmin)
    // -----------------------------------------------------------------------
    [Function("warehouses-update")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "warehouses/{id:guid}")]
        HttpRequest req, Guid id, CancellationToken ct)
    {
        // Global query filter scopes this lookup: a company Admin can't load a
        // warehouse outside their tenant (returns null -> 404); SuperAdmin all.
        var warehouse = await _db.Warehouses.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (warehouse is null) return new NotFoundObjectResult(new { error = "Warehouse not found." });

        UpdateWarehouseDto? dto;
        try { dto = await JsonSerializer.DeserializeAsync<UpdateWarehouseDto>(req.Body, _json, ct); }
        catch { return new BadRequestObjectResult(new { error = "Invalid JSON." }); }

        // Same idea as HubFunctions: when address changes, drop the cached
        // geocode so RouteOptimizationAgent re-fetches and re-persists on
        // the next routing run.
        var addressChanged =
            (!string.IsNullOrWhiteSpace(dto?.Address) && !string.Equals(warehouse.Address, dto.Address, StringComparison.Ordinal))
            || (dto?.City  is not null && !string.Equals(warehouse.City,  dto.City,  StringComparison.Ordinal))
            || (dto?.State is not null && !string.Equals(warehouse.State, dto.State, StringComparison.Ordinal))
            || (dto?.Zip   is not null && !string.Equals(warehouse.Zip,   dto.Zip,   StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(dto?.BusinessName))  warehouse.BusinessName  = dto.BusinessName;
        if (dto?.AlternateName is not null)                 warehouse.AlternateName = dto.AlternateName;
        if (!string.IsNullOrWhiteSpace(dto?.Address))      warehouse.Address       = dto.Address;
        if (dto?.City is not null)                          warehouse.City          = dto.City;
        if (dto?.State is not null)                         warehouse.State         = dto.State;
        if (dto?.Zip is not null)                           warehouse.Zip           = dto.Zip;
        if (dto?.LicenseNumber is not null)                 warehouse.LicenseNumber = dto.LicenseNumber;
        if (dto?.LegacyLicenseNumber is not null)           warehouse.LegacyLicenseNumber = dto.LegacyLicenseNumber;
        if (dto?.IsActive is not null)                      warehouse.IsActive      = dto.IsActive.Value;
        if (dto?.LoadingWaitMinutes is not null)            warehouse.LoadingWaitMinutes = ClampLoadingWait(dto.LoadingWaitMinutes.Value);
        if (dto?.IsChicagoLand is not null)                 warehouse.IsChicagoLand = dto.IsChicagoLand.Value;

        if (addressChanged)
        {
            warehouse.Latitude         = null;
            warehouse.Longitude        = null;
            warehouse.FormattedAddress = null;
        }

        // Weekly pickup schedule
        if (dto?.MondayPickupTime is not null)    warehouse.MondayPickupTime    = ParseTime(dto.MondayPickupTime);
        if (dto?.TuesdayPickupTime is not null)   warehouse.TuesdayPickupTime   = ParseTime(dto.TuesdayPickupTime);
        if (dto?.WednesdayPickupTime is not null) warehouse.WednesdayPickupTime = ParseTime(dto.WednesdayPickupTime);
        if (dto?.ThursdayPickupTime is not null)  warehouse.ThursdayPickupTime  = ParseTime(dto.ThursdayPickupTime);
        if (dto?.FridayPickupTime is not null)    warehouse.FridayPickupTime    = ParseTime(dto.FridayPickupTime);
        if (dto?.SaturdayPickupTime is not null)  warehouse.SaturdayPickupTime  = ParseTime(dto.SaturdayPickupTime);
        if (dto?.SundayPickupTime is not null)    warehouse.SundayPickupTime    = ParseTime(dto.SundayPickupTime);

        warehouse.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new OkObjectResult(new { message = "Warehouse updated.", regeocodeQueued = addressChanged });
    }

    // -----------------------------------------------------------------------
    // DELETE /api/warehouses/{id}  (Admin, SuperAdmin — soft delete)
    // -----------------------------------------------------------------------
    [Function("warehouses-deactivate")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> Deactivate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "warehouses/{id:guid}")]
        HttpRequest req, Guid id, CancellationToken ct)
    {
        var warehouse = await _db.Warehouses.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (warehouse is null) return new NotFoundObjectResult(new { error = "Warehouse not found." });

        warehouse.IsActive  = false;
        warehouse.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Warehouse deactivated: {Id}", id);
        return new OkObjectResult(new { message = "Warehouse deactivated." });
    }

    private static IActionResult Forbid() =>
        new ObjectResult(new { error = "Access denied." }) { StatusCode = 403 };

    /// <summary>Parses "HH:mm" or "" (empty = clear the schedule for that day).</summary>
    private static TimeSpan? ParseTime(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null :
        TimeSpan.TryParse(value, out var t) ? t : null;

    /// <summary>Constrains the loading wait to a sensible range — 0 lets
    /// ops disable the wait entirely; 240 (4h) is an arbitrary upper bound
    /// that prevents a typo from making delivery routes invisible on the
    /// schedule. Mirrors the clamp on Hub.SortingWaitMinutes.</summary>
    private static int ClampLoadingWait(int minutes) => Math.Clamp(minutes, 0, 240);
}

// ---- DTOs ----

public class CreateWarehouseDto
{
    public Guid? CompanyId { get; set; }
    public string BusinessName { get; set; } = string.Empty;
    public string? AlternateName { get; set; }
    public string Address { get; set; } = string.Empty;
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip { get; set; }
    public string? LicenseNumber { get; set; }
    public string? LegacyLicenseNumber { get; set; }
    public int? LoadingWaitMinutes { get; set; }
    public bool? IsChicagoLand { get; set; }
}

public class UpdateWarehouseDto
{
    public string? BusinessName { get; set; }
    public string? AlternateName { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip { get; set; }
    public string? LicenseNumber { get; set; }
    public string? LegacyLicenseNumber { get; set; }
    public bool? IsActive { get; set; }
    public int? LoadingWaitMinutes { get; set; }
    public bool? IsChicagoLand { get; set; }
    // Weekly pickup schedule ("HH:mm" or "" to clear)
    public string? MondayPickupTime { get; set; }
    public string? TuesdayPickupTime { get; set; }
    public string? WednesdayPickupTime { get; set; }
    public string? ThursdayPickupTime { get; set; }
    public string? FridayPickupTime { get; set; }
    public string? SaturdayPickupTime { get; set; }
    public string? SundayPickupTime { get; set; }
}
