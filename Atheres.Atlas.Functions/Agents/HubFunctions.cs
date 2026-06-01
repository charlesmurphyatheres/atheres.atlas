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
/// Hub management — company home base / operations center.
/// Routes start and end at a hub. A company can have multiple hubs.
///
/// Tenant scoping is handled by the AtlasDbContext global query filter
/// (driven by ICompanyContext): a company Admin sees / touches only their own
/// hubs, a SuperAdmin (null company context) any company's. A SuperAdmin can
/// scope a listing to a single company via ?companyId={guid} (or the
/// X-Company-Id header honored by HttpCompanyContext).
/// </summary>
public class HubFunctions
{
    private readonly AtlasDbContext _db;
    private readonly ICompanyContext _company;
    private readonly ILogger<HubFunctions> _log;

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public HubFunctions(AtlasDbContext db, ICompanyContext company, ILogger<HubFunctions> log)
    {
        _db      = db;
        _company = company;
        _log     = log;
    }

    [Function("hubs-list")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "hubs")]
        HttpRequest req, CancellationToken ct)
    {
        // The global query filter already scopes a company Admin to their own
        // hubs and lets a SuperAdmin (null company context) see all. SuperAdmin
        // may further scope to one company via ?companyId={guid}; a blank or
        // empty-Guid value means "all companies" — keep the full list.
        var query = _db.Hubs.Where(h => h.IsActive);
        if (req.HttpContext.User.IsInRole(Roles.SuperAdmin)
            && Guid.TryParse(req.Query["companyId"], out var filterCompanyId)
            && filterCompanyId != Guid.Empty)
            query = query.Where(h => h.CompanyId == filterCompanyId);

        var hubs = await query
            .OrderBy(h => h.Name)
            .Select(h => new
            {
                h.Id, h.CompanyId, h.Name,
                h.Address, h.City, h.State, h.Zip,
                h.SortingWaitMinutes,
                h.IsTransferSite,
                h.IsChicagoLand,
                h.IsActive,
            })
            .ToListAsync(ct);

        return new OkObjectResult(hubs);
    }

    [Function("hubs-create")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "hubs")]
        HttpRequest req, CancellationToken ct)
    {
        CreateHubDto? dto;
        try { dto = await JsonSerializer.DeserializeAsync<CreateHubDto>(req.Body, _json, ct); }
        catch { return new BadRequestObjectResult(new { error = "Invalid JSON." }); }

        if (dto is null || string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Address))
            return new BadRequestObjectResult(new { error = "Name and Address are required." });

        // Create can't be enforced by the read-side query filter, so the target
        // company is resolved + authorized explicitly: it comes from the
        // caller's context, and a SuperAdmin (null context) must name it via the
        // DTO. A company Admin may only create hubs for their own company.
        var isSuperAdmin = req.HttpContext.User.IsInRole(Roles.SuperAdmin);
        var callerCompanyId = _company.CompanyId;

        var targetCompanyId = dto.CompanyId ?? callerCompanyId;
        if (targetCompanyId is null)
            return new BadRequestObjectResult(new { error = "Company context required." });

        if (!isSuperAdmin && targetCompanyId != callerCompanyId)
            return Forbid();

        var hub = new Hub
        {
            CompanyId = targetCompanyId.Value,
            Name      = dto.Name,
            Address   = dto.Address,
            City      = dto.City ?? string.Empty,
            State     = dto.State ?? string.Empty,
            Zip       = dto.Zip ?? string.Empty,
            // SortingWaitMinutes defaults to 30 on the entity; allow create
            // to override it when the operator already knows the hub's pace.
            SortingWaitMinutes = ClampSortingWait(dto.SortingWaitMinutes ?? 30),
            IsTransferSite    = dto.IsTransferSite ?? false,
            IsChicagoLand     = dto.IsChicagoLand  ?? false,
        };

        _db.Hubs.Add(hub);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Hub created: {Name} ({Id}) for company {Company}",
            hub.Name, hub.Id, hub.CompanyId);

        return new ObjectResult(new { id = hub.Id, name = hub.Name }) { StatusCode = 201 };
    }

    [Function("hubs-update")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "hubs/{id:guid}")]
        HttpRequest req, Guid id, CancellationToken ct)
    {
        // Global query filter scopes this lookup: a company Admin can't load a
        // hub outside their tenant (returns null -> 404); SuperAdmin sees all.
        var hub = await _db.Hubs.FirstOrDefaultAsync(h => h.Id == id, ct);
        if (hub is null) return new NotFoundObjectResult(new { error = "Hub not found." });

        UpdateHubDto? dto;
        try { dto = await JsonSerializer.DeserializeAsync<UpdateHubDto>(req.Body, _json, ct); }
        catch { return new BadRequestObjectResult(new { error = "Invalid JSON." }); }

        // Track address changes so we can invalidate the cached geocode
        // when the physical location moved. The main Functions'
        // RouteOptimizationAgent re-geocodes lazily on the next routing run
        // and persists the fresh result.
        var addressChanged =
            (!string.IsNullOrWhiteSpace(dto?.Address) && !string.Equals(hub.Address, dto.Address, StringComparison.Ordinal))
            || (dto?.City  is not null && !string.Equals(hub.City,  dto.City,  StringComparison.Ordinal))
            || (dto?.State is not null && !string.Equals(hub.State, dto.State, StringComparison.Ordinal))
            || (dto?.Zip   is not null && !string.Equals(hub.Zip,   dto.Zip,   StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(dto?.Name))    hub.Name    = dto.Name;
        if (!string.IsNullOrWhiteSpace(dto?.Address)) hub.Address = dto.Address;
        if (dto?.City is not null)                     hub.City    = dto.City;
        if (dto?.State is not null)                    hub.State   = dto.State;
        if (dto?.Zip is not null)                      hub.Zip     = dto.Zip;
        if (dto?.IsActive is not null)                 hub.IsActive = dto.IsActive.Value;
        if (dto?.SortingWaitMinutes is not null)       hub.SortingWaitMinutes = ClampSortingWait(dto.SortingWaitMinutes.Value);
        if (dto?.IsTransferSite is not null)           hub.IsTransferSite     = dto.IsTransferSite.Value;
        if (dto?.IsChicagoLand  is not null)           hub.IsChicagoLand      = dto.IsChicagoLand.Value;

        if (addressChanged)
        {
            hub.Latitude         = null;
            hub.Longitude        = null;
            hub.FormattedAddress = null;
        }

        hub.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new OkObjectResult(new { message = "Hub updated.", regeocodeQueued = addressChanged });
    }

    [Function("hubs-deactivate")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> Deactivate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "hubs/{id:guid}")]
        HttpRequest req, Guid id, CancellationToken ct)
    {
        var hub = await _db.Hubs.FirstOrDefaultAsync(h => h.Id == id, ct);
        if (hub is null) return new NotFoundObjectResult(new { error = "Hub not found." });

        hub.IsActive  = false;
        hub.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Hub deactivated: {Id}", id);
        return new OkObjectResult(new { message = "Hub deactivated." });
    }

    private static IActionResult Forbid() =>
        new ObjectResult(new { error = "Access denied." }) { StatusCode = 403 };

    /// <summary>Constrains the sort-wait to a sensible range. 0 lets ops disable
    /// the sort step entirely; 240 (4 hours) is an arbitrary upper bound that
    /// keeps a fat-fingered value from making delivery routes vanish off the
    /// schedule.</summary>
    private static int ClampSortingWait(int minutes) => Math.Clamp(minutes, 0, 240);
}

public class CreateHubDto
{
    public Guid? CompanyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip { get; set; }
    public int? SortingWaitMinutes { get; set; }
    public bool? IsTransferSite { get; set; }
    public bool? IsChicagoLand { get; set; }
}

public class UpdateHubDto
{
    public string? Name { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip { get; set; }
    public bool? IsActive { get; set; }
    public int? SortingWaitMinutes { get; set; }
    public bool? IsTransferSite { get; set; }
    public bool? IsChicagoLand { get; set; }
}
