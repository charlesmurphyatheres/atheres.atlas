using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Atheres.Atlas.Data;
using Atheres.Atlas.Data.Entities;
using Atheres.Atlas.Domain.Constants;
using Atheres.Atlas.Domain.DTOs.Company;
using Atheres.Atlas.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Auth.Functions.Functions;

/// <summary>
/// Company (tenant) management.
///   SuperAdmin  — create / list all / deactivate
///   Admin       — view and update their own company
/// </summary>
public class CompanyFunctions
{
    private readonly AtlasDbContext              _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ILogger<CompanyFunctions>    _log;

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public CompanyFunctions(
        AtlasDbContext               db,
        UserManager<ApplicationUser> users,
        ILogger<CompanyFunctions>    log)
    {
        _db    = db;
        _users = users;
        _log   = log;
    }

    // -----------------------------------------------------------------------
    // GET /api/companies  (SuperAdmin)
    // -----------------------------------------------------------------------
    [Function("companies-list")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "companies")]
        HttpRequest req, CancellationToken ct)
    {
        var companies = await _db.Companies
            .OrderBy(c => c.Name)
            .Select(c => new CompanyDto
            {
                Id           = c.Id,
                Name         = c.Name,
                Slug         = c.Slug,
                ContactEmail = c.ContactEmail,
                ContactPhone = c.ContactPhone,
                Timezone     = c.Timezone,
                IsActive     = c.IsActive,
                CreatedAt    = c.CreatedAt,
                TruckCount   = c.Trucks.Count(t => t.IsActive),
            })
            .ToListAsync(ct);

        return new OkObjectResult(companies);
    }

    // -----------------------------------------------------------------------
    // GET /api/companies/{id}  (SuperAdmin or company Admin)
    // -----------------------------------------------------------------------
    [Function("companies-get")]
    [Authorize]
    public async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "companies/{id:guid}")]
        HttpRequest req, Guid id, CancellationToken ct)
    {
        if (!CanAccessCompany(req, id))
            return Forbid();

        var c = await _db.Companies
            .Include(x => x.Trucks.Where(t => t.IsActive))
            .FirstOrDefaultAsync(x => x.Id == id, ct);

        if (c is null) return new NotFoundObjectResult(new { error = "Company not found." });

        return new OkObjectResult(new
        {
            id           = c.Id,
            name         = c.Name,
            slug         = c.Slug,
            contactEmail = c.ContactEmail,
            contactPhone = c.ContactPhone,
            timezone     = c.Timezone,
            isActive     = c.IsActive,
            createdAt    = c.CreatedAt,
            trucks       = c.Trucks.Select(t => new { t.Id, t.Name, t.AssignedDriverId, t.IsActive }),
        });
    }

    // -----------------------------------------------------------------------
    // POST /api/companies  (SuperAdmin)
    // -----------------------------------------------------------------------
    [Function("companies-create")]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "companies")]
        HttpRequest req, CancellationToken ct)
    {
        CreateCompanyDto? dto;
        try   { dto = await JsonSerializer.DeserializeAsync<CreateCompanyDto>(req.Body, _json, ct); }
        catch { return new BadRequestObjectResult(new { error = "Invalid JSON." }); }

        if (dto is null || string.IsNullOrWhiteSpace(dto.Name))
            return new BadRequestObjectResult(new { error = "Name is required." });

        var slug = string.IsNullOrWhiteSpace(dto.Slug)
            ? Slugify(dto.Name)
            : dto.Slug.ToLowerInvariant();

        if (await _db.Companies.AnyAsync(c => c.Slug == slug, ct))
            return new ConflictObjectResult(new { error = $"Slug '{slug}' is already taken." });

        var company = new Company
        {
            Name         = dto.Name,
            Slug         = slug,
            ContactEmail = dto.ContactEmail,
            ContactPhone = dto.ContactPhone,
            Timezone     = dto.Timezone,
        };
        _db.Companies.Add(company);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Company created: {Name} ({Id})", company.Name, company.Id);

        // Optionally seed an Admin user for this company
        if (!string.IsNullOrWhiteSpace(dto.AdminEmail) && !string.IsNullOrWhiteSpace(dto.AdminPassword))
        {
            var admin = new ApplicationUser
            {
                UserName  = dto.AdminEmail,
                Email     = dto.AdminEmail,
                FirstName = dto.AdminFirstName ?? "Admin",
                LastName  = dto.AdminLastName  ?? company.Name,
                CompanyId = company.Id,
            };
            var result = await _users.CreateAsync(admin, dto.AdminPassword);
            if (result.Succeeded)
            {
                await _users.AddToRoleAsync(admin, Roles.Admin);
                _log.LogInformation("Seeded Admin {Email} for company {Id}", dto.AdminEmail, company.Id);
            }
        }

        return new ObjectResult(new { id = company.Id, slug = company.Slug }) { StatusCode = 201 };
    }

    // -----------------------------------------------------------------------
    // PUT /api/companies/{id}  (SuperAdmin or company Admin)
    // -----------------------------------------------------------------------
    [Function("companies-update")]
    [Authorize]
    public async Task<IActionResult> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "companies/{id:guid}")]
        HttpRequest req, Guid id, CancellationToken ct)
    {
        if (!CanAccessCompany(req, id))
            return Forbid();

        UpdateCompanyDto? dto;
        try   { dto = await JsonSerializer.DeserializeAsync<UpdateCompanyDto>(req.Body, _json, ct); }
        catch { return new BadRequestObjectResult(new { error = "Invalid JSON." }); }

        var company = await _db.Companies.FindAsync([id], ct);
        if (company is null) return new NotFoundObjectResult(new { error = "Company not found." });

        if (!string.IsNullOrWhiteSpace(dto?.Name))      company.Name         = dto.Name;
        if (dto?.ContactEmail is not null)               company.ContactEmail = dto.ContactEmail;
        if (dto?.ContactPhone is not null)               company.ContactPhone = dto.ContactPhone;
        if (!string.IsNullOrWhiteSpace(dto?.Timezone))  company.Timezone     = dto.Timezone;
        if (dto?.IsActive is not null && IsSuperAdmin(req)) company.IsActive  = dto.IsActive.Value;

        company.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new OkObjectResult(new { message = "Company updated." });
    }

    // -----------------------------------------------------------------------
    // DELETE /api/companies/{id}  (SuperAdmin — soft delete)
    // -----------------------------------------------------------------------
    [Function("companies-deactivate")]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<IActionResult> Deactivate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "companies/{id:guid}")]
        HttpRequest req, Guid id, CancellationToken ct)
    {
        var company = await _db.Companies.FindAsync([id], ct);
        if (company is null) return new NotFoundObjectResult(new { error = "Company not found." });

        company.IsActive  = false;
        company.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _log.LogWarning("Company deactivated: {Id} ({Name})", id, company.Name);
        return new OkObjectResult(new { message = "Company deactivated." });
    }

    // -----------------------------------------------------------------------
    // POST /api/companies/{id}/trucks  (Admin or SuperAdmin)
    // -----------------------------------------------------------------------
    [Function("companies-add-truck")]
    [Authorize]
    public async Task<IActionResult> AddTruck(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "companies/{id:guid}/trucks")]
        HttpRequest req, Guid id, CancellationToken ct)
    {
        if (!CanAccessCompany(req, id))
            return Forbid();

        string? name;
        try
        {
            using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            name = doc.RootElement.GetProperty("name").GetString();
        }
        catch { return new BadRequestObjectResult(new { error = "Body must be { \"name\": \"...\" }" }); }

        if (string.IsNullOrWhiteSpace(name))
            return new BadRequestObjectResult(new { error = "Truck name is required." });

        var truck = new Truck { CompanyId = id, Name = name };
        _db.Trucks.Add(truck);
        await _db.SaveChangesAsync(ct);

        return new ObjectResult(new { id = truck.Id, name = truck.Name }) { StatusCode = 201 };
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    private static bool CanAccessCompany(HttpRequest req, Guid companyId)
    {
        if (req.HttpContext.User.IsInRole(Roles.SuperAdmin)) return true;

        var claim = req.HttpContext.User.FindFirst("companyId")?.Value;
        return Guid.TryParse(claim, out var id) && id == companyId;
    }

    private static bool IsSuperAdmin(HttpRequest req) =>
        req.HttpContext.User.IsInRole(Roles.SuperAdmin);

    private static IActionResult Forbid() =>
        new ObjectResult(new { error = "Access denied." }) { StatusCode = 403 };

    private static string Slugify(string name) =>
        Regex.Replace(name.ToLowerInvariant().Trim(), @"[^a-z0-9]+", "-").Trim('-');
}
