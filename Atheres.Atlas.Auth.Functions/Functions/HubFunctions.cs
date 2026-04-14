using System.Text.Json;
using Atheres.Atlas.Data;
using Atheres.Atlas.Domain.Constants;
using Atheres.Atlas.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Auth.Functions.Functions;

/// <summary>
/// Hub management — company home base / operations center.
/// Routes start and end at a hub. A company can have multiple hubs.
/// </summary>
public class HubFunctions
{
    private readonly AtlasDbContext _db;
    private readonly ILogger<HubFunctions> _log;

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public HubFunctions(AtlasDbContext db, ILogger<HubFunctions> log)
    {
        _db  = db;
        _log = log;
    }

    [Function("hubs-list")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "hubs")]
        HttpRequest req, CancellationToken ct)
    {
        var companyId = GetCallerCompanyId(req);

        var query = _db.Hubs.Where(h => h.IsActive);
        if (!req.HttpContext.User.IsInRole(Roles.SuperAdmin) && companyId.HasValue)
            query = query.Where(h => h.CompanyId == companyId.Value);

        var hubs = await query
            .OrderBy(h => h.Name)
            .Select(h => new
            {
                h.Id, h.CompanyId, h.Name,
                h.Address, h.City, h.State, h.Zip,
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

        var companyId = GetCallerCompanyId(req);
        if (!companyId.HasValue && !req.HttpContext.User.IsInRole(Roles.SuperAdmin))
            return new BadRequestObjectResult(new { error = "Company context required." });

        var targetCompanyId = dto.CompanyId ?? companyId!.Value;

        if (!req.HttpContext.User.IsInRole(Roles.SuperAdmin) && targetCompanyId != companyId)
            return Forbid();

        var hub = new Hub
        {
            CompanyId = targetCompanyId,
            Name      = dto.Name,
            Address   = dto.Address,
            City      = dto.City ?? string.Empty,
            State     = dto.State ?? string.Empty,
            Zip       = dto.Zip ?? string.Empty,
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
        var hub = await _db.Hubs.FindAsync([id], ct);
        if (hub is null) return new NotFoundObjectResult(new { error = "Hub not found." });

        if (!CanAccess(req, hub.CompanyId))
            return Forbid();

        UpdateHubDto? dto;
        try { dto = await JsonSerializer.DeserializeAsync<UpdateHubDto>(req.Body, _json, ct); }
        catch { return new BadRequestObjectResult(new { error = "Invalid JSON." }); }

        if (!string.IsNullOrWhiteSpace(dto?.Name))    hub.Name    = dto.Name;
        if (!string.IsNullOrWhiteSpace(dto?.Address)) hub.Address = dto.Address;
        if (dto?.City is not null)                     hub.City    = dto.City;
        if (dto?.State is not null)                    hub.State   = dto.State;
        if (dto?.Zip is not null)                      hub.Zip     = dto.Zip;
        if (dto?.IsActive is not null)                 hub.IsActive = dto.IsActive.Value;

        hub.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new OkObjectResult(new { message = "Hub updated." });
    }

    [Function("hubs-deactivate")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> Deactivate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "hubs/{id:guid}")]
        HttpRequest req, Guid id, CancellationToken ct)
    {
        var hub = await _db.Hubs.FindAsync([id], ct);
        if (hub is null) return new NotFoundObjectResult(new { error = "Hub not found." });

        if (!CanAccess(req, hub.CompanyId))
            return Forbid();

        hub.IsActive  = false;
        hub.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Hub deactivated: {Id}", id);
        return new OkObjectResult(new { message = "Hub deactivated." });
    }

    private bool CanAccess(HttpRequest req, Guid companyId)
    {
        if (req.HttpContext.User.IsInRole(Roles.SuperAdmin)) return true;
        var claim = req.HttpContext.User.FindFirst("companyId")?.Value;
        return Guid.TryParse(claim, out var id) && id == companyId;
    }

    private static Guid? GetCallerCompanyId(HttpRequest req)
    {
        var claim = req.HttpContext.User.FindFirst("companyId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private static IActionResult Forbid() =>
        new ObjectResult(new { error = "Access denied." }) { StatusCode = 403 };
}

public class CreateHubDto
{
    public Guid? CompanyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip { get; set; }
}

public class UpdateHubDto
{
    public string? Name { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip { get; set; }
    public bool? IsActive { get; set; }
}
