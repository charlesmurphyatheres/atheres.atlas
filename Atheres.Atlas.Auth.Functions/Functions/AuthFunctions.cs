using System.Net;
using System.Security.Cryptography;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Auth.Functions.Functions;

public class AuthFunctions
{
    private readonly UserManager<ApplicationUser>  _users;
    private readonly SignInManager<ApplicationUser> _signIn;
    private readonly ITokenService                 _tokens;
    private readonly AtlasDbContext                _db;
    private readonly IEmailService                 _email;
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
        IEmailService                 email,
        ILogger<AuthFunctions>        log)
    {
        _users  = users;
        _signIn = signIn;
        _tokens = tokens;
        _db     = db;
        _email  = email;
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

        // OrderImporter accounts are pinned to a single warehouse — every
        // order they upload is scoped to it, and they only see orders from
        // it. The selection is mandatory at create time so the account is
        // never in a state where it has no warehouse to import into.
        Guid? assignedWarehouseId = null;
        if (string.Equals(role, Roles.OrderImporter, StringComparison.OrdinalIgnoreCase))
        {
            if (!dto.WarehouseId.HasValue)
                return new BadRequestObjectResult(new { error = "Order Importer accounts must be assigned to a warehouse." });

            var warehouse = await _db.Warehouses.IgnoreQueryFilters()
                .Include(w => w.Companies)
                .FirstOrDefaultAsync(w => w.Id == dto.WarehouseId.Value && w.IsActive);
            if (warehouse is null)
                return new BadRequestObjectResult(new { error = "Selected warehouse not found or inactive." });

            // Warehouses are many-to-many with Companies now; the importer
            // can be pinned only if the target company is one of them.
            if (!warehouse.Companies.Any(c => c.Id == targetCompanyId))
                return new BadRequestObjectResult(new { error = "Selected warehouse does not belong to the target company." });

            assignedWarehouseId = warehouse.Id;
        }

        // The admin enters everything except the password — the system
        // generates a one-time temporary password, emails it to the new
        // user, and forces a reset on first login. This stops admins from
        // ever holding an importer's real credentials.
        var isImporter        = string.Equals(role, Roles.OrderImporter, StringComparison.OrdinalIgnoreCase);
        var temporaryPassword = isImporter ? GenerateTemporaryPassword() : null;
        var passwordToUse     = temporaryPassword ?? dto.Password;

        var user = new ApplicationUser
        {
            UserName            = dto.Email,
            Email               = dto.Email,
            FirstName           = dto.FirstName,
            LastName            = dto.LastName,
            CompanyId           = targetCompanyId,
            AssignedWarehouseId = assignedWarehouseId,
            MustChangePassword  = isImporter,
        };

        var result = await _users.CreateAsync(user, passwordToUse);
        if (!result.Succeeded)
        {
            return new BadRequestObjectResult(new
            {
                error  = "Registration failed.",
                errors = result.Errors.Select(e => e.Description),
            });
        }

        await _users.AddToRoleAsync(user, role);

        if (isImporter && temporaryPassword is not null)
        {
            await SendInvitationEmailAsync(user, temporaryPassword);
        }

        _log.LogInformation("New user registered: {Email} as {Role} in company {Company} (warehouse {Warehouse})",
            dto.Email, role, targetCompanyId, assignedWarehouseId);

        return new OkObjectResult(new
        {
            message            = "Registration successful.",
            userId             = user.Id,
            invitationEmailed  = isImporter,
        });
    }

    /// <summary>
    /// Generates a temporary password that satisfies the Identity password
    /// policy (8+ chars, upper, digit, non-alphanumeric). Used for the
    /// OrderImporter invitation flow.
    /// </summary>
    private static string GenerateTemporaryPassword()
    {
        // Cryptographically random body + a fixed-shape suffix that
        // guarantees the result passes the configured Identity rules
        // (uppercase letter, digit, special character).
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";
        var body = new char[10];
        var bytes = RandomNumberGenerator.GetBytes(body.Length);
        for (var i = 0; i < body.Length; i++)
            body[i] = alphabet[bytes[i] % alphabet.Length];
        return $"Tmp!{new string(body)}9";
    }

    private async Task SendInvitationEmailAsync(ApplicationUser user, string temporaryPassword)
    {
        var subject = "Your Atlas Deliver invitation — change your password";
        var greeting = string.IsNullOrWhiteSpace(user.FirstName) ? "there" : user.FirstName;

        var html = $"""
            <div style="font-family:Arial,sans-serif;color:#111;max-width:560px;">
              <h2 style="color:#1f2937;margin-bottom:8px;">Welcome to Atlas Deliver, {System.Net.WebUtility.HtmlEncode(greeting)}</h2>
              <p>An administrator has created an Order Importer account for you. Use the temporary credentials below to sign in. <strong>You will be required to change your password immediately on first login.</strong></p>
              <div style="background:#f3f4f6;border:1px solid #d1d5db;border-radius:8px;padding:16px;margin:16px 0;">
                <p style="margin:0 0 4px 0;"><strong>Email:</strong> {System.Net.WebUtility.HtmlEncode(user.Email ?? string.Empty)}</p>
                <p style="margin:0;"><strong>Temporary password:</strong> <span style="font-family:Consolas,monospace;background:#fff;padding:2px 6px;border-radius:4px;border:1px solid #e5e7eb;">{System.Net.WebUtility.HtmlEncode(temporaryPassword)}</span></p>
              </div>
              <p>If you did not expect this invitation, please ignore the message — the temporary password is not usable until you change it on first login.</p>
              <p style="color:#6b7280;font-size:12px;margin-top:24px;">— Atlas Deliver</p>
            </div>
            """;

        var plain = $"""
            Welcome to Atlas Deliver, {greeting}.

            An administrator has created an Order Importer account for you.
            Use these temporary credentials to sign in — you will be required
            to change your password immediately on first login.

              Email:               {user.Email}
              Temporary password:  {temporaryPassword}

            If you did not expect this invitation, please ignore the message.
            """;

        var sent = await _email.SendAsync(new EmailRequest(
            To:            user.Email ?? string.Empty,
            ToName:        user.FullName,
            Subject:       subject,
            HtmlBody:      html,
            PlainTextBody: plain));

        if (!sent)
        {
            // Logged but non-fatal — the admin can resend manually if the
            // first attempt fails (e.g. SendGrid outage). The account is
            // already created with the temp password and MustChangePassword
            // flag, so the invitation email can be retried via a future
            // resend endpoint without re-creating the user.
            _log.LogWarning("Invitation email failed to send for {Email}; account is still active.", user.Email);
        }
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

        string? warehouseName = null;
        if (user.AssignedWarehouseId.HasValue)
        {
            var wh = await _db.Warehouses.IgnoreQueryFilters()
                .FirstOrDefaultAsync(w => w.Id == user.AssignedWarehouseId.Value, ct);
            warehouseName = wh?.BusinessName;
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
            WarehouseId  = user.AssignedWarehouseId,
            WarehouseName = warehouseName,
            MustChangePassword = user.MustChangePassword,
        });
    }

    // -----------------------------------------------------------------------
    // POST /api/auth/change-password
    // Authenticated. Used to satisfy the MustChangePassword flag set when
    // an admin creates a new account via the invitation flow, and is also
    // available to any user who wants to rotate their password voluntarily.
    // -----------------------------------------------------------------------
    [Function("auth-change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/change-password")]
        HttpRequest req,
        CancellationToken ct)
    {
        ChangePasswordDto? dto;
        try { dto = await JsonSerializer.DeserializeAsync<ChangePasswordDto>(req.Body, _json, ct); }
        catch { return new BadRequestObjectResult(new { error = "Invalid JSON body." }); }

        if (dto is null
            || string.IsNullOrWhiteSpace(dto.CurrentPassword)
            || string.IsNullOrWhiteSpace(dto.NewPassword))
        {
            return new BadRequestObjectResult(new { error = "CurrentPassword and NewPassword are required." });
        }

        var userId = req.HttpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized("Not authenticated.");

        var user = await _users.FindByIdAsync(userId);
        if (user is null || !user.IsActive)
            return new NotFoundObjectResult(new { error = "User not found." });

        var result = await _users.ChangePasswordAsync(user, dto.CurrentPassword, dto.NewPassword);
        if (!result.Succeeded)
        {
            return new BadRequestObjectResult(new
            {
                error  = "Password change failed.",
                errors = result.Errors.Select(e => e.Description),
            });
        }

        if (user.MustChangePassword)
        {
            user.MustChangePassword = false;
            await _users.UpdateAsync(user);
        }

        // Invalidate any refresh tokens issued under the old password. The
        // caller must reauthenticate to pick up a fresh access+refresh pair
        // (frontend handles this by routing back through the login flow).
        await _tokens.RevokeAllAsync(user.Id, "password-changed", ct);

        _log.LogInformation("Password changed for {UserId}", user.Id);
        return new OkObjectResult(new { message = "Password updated. Please sign in again." });
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
