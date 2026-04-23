using System.Net;
using System.Text.Json;
using Atheres.Atlas.Auth.Functions.Services;
using Atheres.Atlas.Data;
using Atheres.Atlas.Data.Entities;
using Atheres.Atlas.Domain.Constants;
using Atheres.Atlas.Domain.DTOs.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Auth.Functions.Functions;

public class AuthFunctions
{
    private readonly UserManager<ApplicationUser>  _users;
    private readonly SignInManager<ApplicationUser> _signIn;
    private readonly ITokenService                 _tokens;
    private readonly AtlasDbContext                _db;
    private readonly ILogger<AuthFunctions>        _log;

    private static Guid? GetCallerCompanyId(HttpRequest req)
    {
        var claim = req.HttpContext.User.FindFirst("companyId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public AuthFunctions(
        UserManager<ApplicationUser>  users,
        SignInManager<ApplicationUser> signIn,
        ITokenService                 tokens,
        AtlasDbContext                db,
        ILogger<AuthFunctions>        log)
    {
        _users  = users;
        _signIn = signIn;
        _tokens = tokens;
        _db     = db;
        _log    = log;
    }

    // -----------------------------------------------------------------------
    // POST /api/auth/register   (caller must be SuperAdmin or Admin)
    // -----------------------------------------------------------------------
    [Function("auth-register")]
    public async Task<IActionResult> Register(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/register")]
        HttpRequest req,
        CancellationToken ct)
    {
        RegisterDto? dto;
        try
        {
            dto = await JsonSerializer.DeserializeAsync<RegisterDto>(
                req.Body, _json, ct);
        }
        catch
        {
            return new BadRequestObjectResult(new { error = "Invalid JSON body." });
        }

        if (dto is null ||
            string.IsNullOrWhiteSpace(dto.Email) ||
            string.IsNullOrWhiteSpace(dto.Password))
        {
            return new BadRequestObjectResult(new { error = "Email and Password are required." });
        }

        var isSuperAdmin = req.HttpContext.User.IsInRole(Roles.SuperAdmin);
        var isAdmin      = req.HttpContext.User.IsInRole(Roles.Admin);

        if (!isSuperAdmin && !isAdmin)
            return Unauthorized("Only SuperAdmin or Admin may register new users.");

        Guid? targetCompanyId = dto.CompanyId;
        var callerCompanyId = GetCallerCompanyId(req);

        if (isAdmin && !isSuperAdmin && callerCompanyId.HasValue)
        {
            // Company Admin can only add users to their own company
            targetCompanyId = callerCompanyId;
        }

        // Role enforcement:
        // - SuperAdmin can assign any role
        // - Company Admin can only assign Logistics or Driver
        var role = dto.Role ?? Roles.Driver;
        if (!Roles.All.Contains(role, StringComparer.OrdinalIgnoreCase))
            role = Roles.Driver;

        if (isAdmin && !isSuperAdmin &&
            !Roles.AdminAssignableRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
        {
            return new BadRequestObjectResult(new { error = "Company administrators can only assign Logistics or Driver roles." });
        }

        var user = new ApplicationUser
        {
            UserName  = dto.Email,
            Email     = dto.Email,
            FirstName = dto.FirstName,
            LastName  = dto.LastName,
            CompanyId = targetCompanyId,
        };

        var result = await _users.CreateAsync(user, dto.Password);
        if (!result.Succeeded)
        {
            return new BadRequestObjectResult(new
            {
                error  = "Registration failed.",
                errors = result.Errors.Select(e => e.Description),
            });
        }

        await _users.AddToRoleAsync(user, role);

        _log.LogInformation("New user registered: {Email} as {Role} in company {Company}",
            dto.Email, role, targetCompanyId);

        return new OkObjectResult(new { message = "Registration successful.", userId = user.Id });
    }

    // -----------------------------------------------------------------------
    // POST /api/auth/login
    // -----------------------------------------------------------------------
    [Function("auth-login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/login")]
        HttpRequest req,
        CancellationToken ct)
    {
        LoginDto? dto;
        try
        {
            dto = await JsonSerializer.DeserializeAsync<LoginDto>(req.Body, _json, ct);
        }
        catch
        {
            return new BadRequestObjectResult(new { error = "Invalid JSON body." });
        }

        if (dto is null)
            return new BadRequestObjectResult(new { error = "Email and Password are required." });

        var user = await _users.FindByEmailAsync(dto.Email);
        if (user is null || !user.IsActive)
            return Unauthorized("Invalid credentials.");

        var check = await _signIn.CheckPasswordSignInAsync(user, dto.Password, lockoutOnFailure: true);
        if (!check.Succeeded)
        {
            var msg = check.IsLockedOut ? "Account locked." : "Invalid credentials.";
            return Unauthorized(msg);
        }

        var roles        = (await _users.GetRolesAsync(user)).ToList();
        var accessToken  = _tokens.GenerateAccessToken(user, roles);
        var refreshToken = await _tokens.CreateRefreshTokenAsync(user.Id, ct);

        user.LastLoginAt = DateTime.UtcNow;
        await _users.UpdateAsync(user);

        // Load company details for response
        string? companyName = null;
        string? companySlug = null;
        if (user.CompanyId.HasValue)
        {
            var co = await _db.Companies.FindAsync([user.CompanyId.Value], cancellationToken: ct);
            companyName = co?.Name;
            companySlug = co?.Slug;
        }

        return new OkObjectResult(new TokenResponseDto
        {
            AccessToken  = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt    = DateTime.UtcNow.AddMinutes(15),
            UserId       = user.Id,
            Email        = user.Email ?? string.Empty,
            FullName     = user.FullName,
            Roles        = roles,
            CompanyId    = user.CompanyId,
            CompanyName  = companyName,
            CompanySlug  = companySlug,
        });
    }

    // -----------------------------------------------------------------------
    // POST /api/auth/refresh
    // -----------------------------------------------------------------------
    [Function("auth-refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/refresh")]
        HttpRequest req,
        CancellationToken ct)
    {
        RefreshTokenDto? dto;
        try
        {
            dto = await JsonSerializer.DeserializeAsync<RefreshTokenDto>(req.Body, _json, ct);
        }
        catch
        {
            return new BadRequestObjectResult(new { error = "Invalid JSON body." });
        }

        if (dto is null || string.IsNullOrWhiteSpace(dto.RefreshToken))
            return new BadRequestObjectResult(new { error = "RefreshToken is required." });

        var response = await _tokens.RefreshAsync(dto.RefreshToken, ct);
        if (response is null)
            return Unauthorized("Invalid or expired refresh token.");

        return new OkObjectResult(response);
    }

    // -----------------------------------------------------------------------
    // POST /api/auth/logout
    // -----------------------------------------------------------------------
    [Function("auth-logout")]
    [Authorize]
    public async Task<IActionResult> Logout(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/logout")]
        HttpRequest req,
        CancellationToken ct)
    {
        var userId = req.HttpContext.User.FindFirst(
            System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized("Not authenticated.");

        await _tokens.RevokeAllAsync(userId, ct: ct);
        return new OkObjectResult(new { message = "Logged out." });
    }

    // -----------------------------------------------------------------------
    // GET /api/auth/me
    // -----------------------------------------------------------------------
    [Function("auth-me")]
    [Authorize]
    public async Task<IActionResult> Me(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "auth/me")]
        HttpRequest req,
        CancellationToken ct)
    {
        var userId = req.HttpContext.User.FindFirst(
            System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized("Not authenticated.");

        var user = await _users.FindByIdAsync(userId);
        if (user is null || !user.IsActive)
            return new NotFoundObjectResult(new { error = "User not found." });

        var roles = (await _users.GetRolesAsync(user)).ToList();

        return new OkObjectResult(new
        {
            userId   = user.Id,
            email    = user.Email,
            fullName = user.FullName,
            roles,
            lastLoginAt = user.LastLoginAt,
        });
    }

    // -----------------------------------------------------------------------
    private static IActionResult Unauthorized(string message)
        => new ObjectResult(new { error = message })
        {
            StatusCode = (int)HttpStatusCode.Unauthorized,
        };
}
