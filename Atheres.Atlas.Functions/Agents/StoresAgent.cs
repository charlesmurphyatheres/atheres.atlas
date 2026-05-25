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
        // store on the next pass. Stores are many-to-many with Companies,
        // so the dup check looks at the join: "any active store this caller
        // can see with the same license."
        var duplicate = await _db.Stores.IgnoreQueryFilters()
            .AnyAsync(
                s => s.LicenseNumber == dto.LicenseNumber
                  && s.Companies.Any(c => c.Id == companyId.Value),
                ct);
        if (duplicate)
            return new ConflictObjectResult(new { error = $"A store with license number '{dto.LicenseNumber}' already exists." });

        // The caller's company is the initial tenant for this new store.
        // Sharing with additional companies happens via a separate flow.
        var company = await _db.Companies.FindAsync([companyId.Value], ct);
        if (company is null)
            return new BadRequestObjectResult(new { error = "Caller company not found." });

        var store = new Store
        {
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
        store.Companies.Add(company);

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
            store.Name, store.LicenseNumber, companyId.Value);

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

    /// <summary>
    /// PUT /api/stores/{id} — partial update. Each field on the DTO is
    /// applied only when supplied; nulls are "leave alone" rather than
    /// "clear". Address changes drop the cached geocode so the next
    /// routing pass re-resolves and re-persists. License-number changes
    /// run the same in-company uniqueness check the create endpoint uses.
    /// Gated to <c>SuperAdmin</c> only — stores are shared master data
    /// (a single store can belong to multiple companies via the
    /// StoreCompanies join), so per-tenant Admins shouldn't be able to
    /// edit a row whose state other tenants also depend on. Inline
    /// creation in the import flow stays accessible to lower roles via
    /// the unchanged POST endpoint.
    /// </summary>
    [Function("stores-update")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "stores/{id:guid}")]
        HttpRequest req,
        Guid id,
        CancellationToken ct)
    {
        UpdateStoreDto? dto;
        try { dto = await JsonSerializer.DeserializeAsync<UpdateStoreDto>(req.Body, _json, ct); }
        catch { return new BadRequestObjectResult(new { error = "Invalid JSON." }); }
        if (dto is null) return new BadRequestObjectResult(new { error = "Empty body." });

        // Tenant filter on _db.Stores enforces "caller can see this store" —
        // a company Admin can't edit a row that doesn't belong to one of
        // their companies. SuperAdmin sees everything via _company being null.
        var store = await _db.Stores.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (store is null) return new NotFoundObjectResult(new { error = "Store not found." });

        // License change → re-check uniqueness within the caller's tenant
        // so two stores in the same company can't end up sharing one.
        var newLicense = dto.LicenseNumber?.Trim();
        if (!string.IsNullOrWhiteSpace(newLicense)
            && !string.Equals(newLicense, store.LicenseNumber, StringComparison.Ordinal))
        {
            var companyId = _company.CompanyId;
            if (companyId.HasValue)
            {
                var collision = await _db.Stores.IgnoreQueryFilters()
                    .AnyAsync(
                        s => s.Id != store.Id
                          && s.LicenseNumber == newLicense
                          && s.Companies.Any(c => c.Id == companyId.Value),
                        ct);
                if (collision)
                    return new ConflictObjectResult(new { error = $"A store with license number '{newLicense}' already exists." });
            }
        }

        // Track whether the physical location moved so we can invalidate
        // the cached geocode. Mirrors the WarehouseFunctions / HubFunctions
        // pattern: null the coords + FormattedAddress, let the next routing
        // run resolve and persist via the scheduler's geocode pass.
        var addressChanged =
            (!string.IsNullOrWhiteSpace(dto.Address) && !string.Equals(store.Address, dto.Address.Trim(), StringComparison.Ordinal))
            || (dto.City  is not null && !string.Equals(store.City,  dto.City.Trim(),  StringComparison.Ordinal))
            || (dto.State is not null && !string.Equals(store.State, dto.State.Trim(), StringComparison.Ordinal))
            || (dto.Zip   is not null && !string.Equals(store.Zip,   dto.Zip.Trim(),   StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(dto.Name))         store.Name          = dto.Name.Trim();
        if (!string.IsNullOrWhiteSpace(dto.Address))      store.Address       = dto.Address.Trim();
        if (dto.Customer    is not null)                   store.Customer      = dto.Customer.Trim();
        if (dto.City        is not null)                   store.City          = dto.City.Trim();
        if (dto.State       is not null)                   store.State         = dto.State.Trim();
        if (dto.Zip         is not null)                   store.Zip           = dto.Zip.Trim();
        if (dto.County      is not null)                   store.County        = dto.County.Trim();
        if (dto.Email       is not null)                   store.Email         = dto.Email.Trim();
        if (dto.Phone       is not null)                   store.Phone         = dto.Phone.Trim();
        if (!string.IsNullOrWhiteSpace(newLicense))        store.LicenseNumber = newLicense;
        if (dto.IsActive    is not null)                   store.IsActive      = dto.IsActive.Value;

        if (addressChanged)
        {
            store.Latitude         = null;
            store.Longitude        = null;
            store.FormattedAddress = null;
        }

        store.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Store {Id} updated (addressChanged={AddressChanged})", id, addressChanged);
        return new OkObjectResult(new
        {
            id              = store.Id,
            name            = store.Name,
            licenseNumber   = store.LicenseNumber,
            customer        = store.Customer,
            city            = store.City,
            regeocodeQueued = addressChanged,
        });
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

/// <summary>
/// PATCH-style update DTO — every field is optional, and nulls mean
/// "leave alone". Set IsActive = false to soft-deactivate a store (it
/// stops appearing in the Admin Panel's stores list and stops resolving
/// for new orders); set IsActive = true to reactivate.
/// </summary>
public class UpdateStoreDto
{
    public string? Name { get; set; }
    public string? LicenseNumber { get; set; }
    public string? Address { get; set; }
    public string? Customer { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip { get; set; }
    public string? County { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public bool?   IsActive { get; set; }
}
