using System.Text.Json;
using Atheres.Atlas.Data;
using Atheres.Atlas.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Auth.Functions.Functions;

/// <summary>
/// District management. Districts are seeded from data/store_zone.csv and
/// don't get created or destroyed through the UI — only their flags
/// (IsChicagoLand, IsActive) and display name are mutable from the admin panel.
/// </summary>
public class DistrictFunctions
{
    private readonly AtlasDbContext _db;
    private readonly ILogger<DistrictFunctions> _log;

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public DistrictFunctions(AtlasDbContext db, ILogger<DistrictFunctions> log)
    {
        _db  = db;
        _log = log;
    }

    [Function("districts-list")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "districts")]
        HttpRequest req, CancellationToken ct)
    {
        var companyId = GetCallerCompanyId(req);

        var query = _db.Districts.AsQueryable();
        if (!req.HttpContext.User.IsInRole(Roles.SuperAdmin) && companyId.HasValue)
            query = query.Where(d => d.CompanyId == companyId.Value);

        var districts = await query
            .OrderBy(d => d.Number)
            .Select(d => new
            {
                d.Id, d.CompanyId,
                d.Number, d.Name,
                d.IsChicagoLand,
                d.IsActive,
                zoneCount = d.Zones.Count(z => z.IsActive),
            })
            .ToListAsync(ct);

        return new OkObjectResult(districts);
    }

    [Function("districts-update")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "districts/{id:guid}")]
        HttpRequest req, Guid id, CancellationToken ct)
    {
        var district = await _db.Districts.FindAsync([id], ct);
        if (district is null) return new NotFoundObjectResult(new { error = "District not found." });

        if (!CanAccess(req, district.CompanyId))
            return Forbid();

        UpdateDistrictDto? dto;
        try { dto = await JsonSerializer.DeserializeAsync<UpdateDistrictDto>(req.Body, _json, ct); }
        catch { return new BadRequestObjectResult(new { error = "Invalid JSON." }); }

        if (!string.IsNullOrWhiteSpace(dto?.Name))  district.Name          = dto.Name;
        if (dto?.IsChicagoLand is not null)         district.IsChicagoLand = dto.IsChicagoLand.Value;
        if (dto?.IsActive      is not null)         district.IsActive      = dto.IsActive.Value;

        district.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("District updated: {Id} (IsChicagoLand={Flag})", id, district.IsChicagoLand);
        return new OkObjectResult(new { message = "District updated." });
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

public class UpdateDistrictDto
{
    public string? Name { get; set; }
    public bool? IsChicagoLand { get; set; }
    public bool? IsActive { get; set; }
}
