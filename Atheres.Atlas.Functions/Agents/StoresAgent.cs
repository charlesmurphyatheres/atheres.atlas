using System.Net;
using System.Text.Json;
using Atheres.Atlas.Data;
using Atheres.Atlas.Data.Services;
using Atheres.Atlas.Domain.Entities;
using Atheres.Atlas.Functions.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Functions.Agents;

/// <summary>
/// Mutation endpoints for Store master data. Read-only listing lives in
/// QueryAgent; this agent owns inline creation from flows like the CSV
/// import UI (where a row's license number doesn't match an existing store
/// and the operator wants to add it without leaving the import grid).
/// </summary>
[Authorize(Roles = "Admin,SuperAdmin,OrderImporter,Logistics")]
public class StoresAgent
{
    private readonly AtlasDbContext _db;
    private readonly ICompanyContext _company;
    private readonly IGoogleMapsService _maps;
    private readonly ILogger<StoresAgent> _log;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public StoresAgent(
        AtlasDbContext db,
        ICompanyContext company,
        IGoogleMapsService maps,
        ILogger<StoresAgent> log)
    {
        _db      = db;
        _company = company;
        _maps    = maps;
        _log     = log;
    }

    /// <summary>
    /// POST /api/stores — create a new Store under the caller's company.
    /// Address is geocoded on save so subsequent route optimization has
    /// real coordinates without a separate backfill pass.
    /// </summary>
    [Function("stores-create")]
    public async Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "stores")]
        HttpRequest req, CancellationToken ct)
    {
        CreateStoreDto? dto;
        try { dto = await JsonSerializer.DeserializeAsync<CreateStoreDto>(req.Body, _json, ct); }
        catch { return new BadRequestObjectResult(new { error = "Invalid JSON." }); }

        if (dto is null
            || string.IsNullOrWhiteSpace(dto.Name)
            || string.IsNullOrWhiteSpace(dto.LicenseNumber)
            || string.IsNullOrWhiteSpace(dto.Address))
        {
            return new BadRequestObjectResult(new { error = "Name, LicenseNumber, and Address are required." });
        }

        var companyId = _company.CompanyId;
        if (!companyId.HasValue)
            return new BadRequestObjectResult(new { error = "Company context required." });

        // License number is the natural key the import flow uses for
        // matching, so a duplicate inside the same company is almost
        // always an operator mistake. Reject with a clear message rather
        // than silently letting the import row reattach to the existing
        // store on the next pass.
        var duplicate = await _db.Stores.IgnoreQueryFilters()
            .AnyAsync(
                s => s.CompanyId == companyId.Value
                  && s.LicenseNumber == dto.LicenseNumber,
                ct);
        if (duplicate)
            return new ConflictObjectResult(new { error = $"A store with license number '{dto.LicenseNumber}' already exists." });

        var store = new Store
        {
            CompanyId     = companyId.Value,
            Name          = dto.Name.Trim(),
            Customer      = dto.Customer?.Trim()      ?? string.Empty,
            Address       = dto.Address.Trim(),
            City          = dto.City?.Trim()          ?? string.Empty,
            State         = string.IsNullOrWhiteSpace(dto.State) ? "IL" : dto.State.Trim(),
            Zip           = dto.Zip?.Trim()           ?? string.Empty,
            County        = dto.County?.Trim(),
            LicenseNumber = dto.LicenseNumber.Trim(),
            Email         = dto.Email?.Trim(),
            Phone         = dto.Phone?.Trim(),
        };

        // Best-effort geocode. A failure here is non-fatal — the row is
        // still routable later via the existing geocode-stores maintenance
        // endpoint. We just won't have coords on day one.
        try
        {
            var geo = await _maps.GeocodeAsync(store.FullAddress, ct);
            if (geo is not null)
            {
                store.Latitude         = geo.Latitude;
                store.Longitude        = geo.Longitude;
                store.FormattedAddress = geo.FormattedAddress;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Geocode failed for new store {License}; continuing without coords.", store.LicenseNumber);
        }

        _db.Stores.Add(store);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Store created via inline import flow: {Name} ({License}) for company {Company}",
            store.Name, store.LicenseNumber, store.CompanyId);

        return new ObjectResult(new
        {
            id            = store.Id,
            name          = store.Name,
            licenseNumber = store.LicenseNumber,
            customer      = store.Customer,
            city          = store.City,
        })
        { StatusCode = (int)HttpStatusCode.Created };
    }
}

public class CreateStoreDto
{
    public string Name { get; set; } = string.Empty;
    public string LicenseNumber { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? Customer { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip { get; set; }
    public string? County { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
}
