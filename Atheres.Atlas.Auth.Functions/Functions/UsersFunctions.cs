using System.Net;
using Atheres.Atlas.Data.Entities;
using Atheres.Atlas.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Auth.Functions.Functions;

/// <summary>
/// Admin-only endpoints for user management.
/// All routes require the Admin role (enforced by [Authorize(Roles = "Admin,SuperAdmin")]).
/// </summary>
public class UsersFunctions
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly ILogger<UsersFunctions> _log;

    public UsersFunctions(
        UserManager<ApplicationUser> users,
        ILogger<UsersFunctions> log)
    {
        _users = users;
        _log   = log;
    }

    private static Guid? GetCallerCompanyId(HttpRequest req)
    {
        var claim = req.HttpContext.User.FindFirst("companyId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    // -----------------------------------------------------------------------
    // GET /api/users  (Admin)
    // -----------------------------------------------------------------------
    [Function("users-list")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "users")]
        HttpRequest req,
        CancellationToken ct)
    {
        // Company Admin only sees users in their own company
        var callerCompanyId = GetCallerCompanyId(req);
        var isSuperAdmin = req.HttpContext.User.IsInRole("SuperAdmin");

        var query = _users.Users.Where(u => u.IsActive);
        if (!isSuperAdmin && callerCompanyId.HasValue)
            query = query.Where(u => u.CompanyId == callerCompanyId.Value);

        var userList = query.OrderBy(u => u.LastName).ToList();

        var result = new List<object>();
        foreach (var u in userList)
        {
            var roles = await _users.GetRolesAsync(u);
            result.Add(new
            {
                id = u.Id,
                email = u.Email,
                fullName = $"{u.FirstName} {u.LastName}".Trim(),
                roles,
                companyId = u.CompanyId,
                assignedTruckId = u.AssignedTruckId,
                assignedWarehouseId = u.AssignedWarehouseId,
                mustChangePassword = u.MustChangePassword,
                isActive = u.IsActive,
                lastLoginAt = u.LastLoginAt,
            });
        }

        return new OkObjectResult(result);
    }

    // -----------------------------------------------------------------------
    // GET /api/users/{id}  (Admin)
    // -----------------------------------------------------------------------
    [Function("users-get")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "users/{id}")]
        HttpRequest req,
        string id,
        CancellationToken ct)
    {
        var user = await _users.FindByIdAsync(id);
        if (user is null)
            return new NotFoundObjectResult(new { error = "User not found." });

        var roles = (await _users.GetRolesAsync(user)).ToList();

        return new OkObjectResult(new
        {
            user.Id,
            user.Email,
            user.FirstName,
            user.LastName,
            user.IsActive,
            user.CreatedAt,
            user.LastLoginAt,
            roles,
        });
    }

    // -----------------------------------------------------------------------
    // PUT /api/users/{id}/roles  (Admin) — replace user's role assignment
    // Body: { "role": "Dispatcher" }
    // -----------------------------------------------------------------------
    [Function("users-set-role")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> SetRole(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "users/{id}/roles")]
        HttpRequest req,
        string id,
        CancellationToken ct)
    {
        var user = await _users.FindByIdAsync(id);
        if (user is null)
            return new NotFoundObjectResult(new { error = "User not found." });

        string? role;
        try
        {
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            role = doc.RootElement.GetProperty("role").GetString();
        }
        catch
        {
            return new BadRequestObjectResult(new { error = "Body must be { \"role\": \"...\" }" });
        }

        if (string.IsNullOrWhiteSpace(role) || !Roles.All.Contains(role, StringComparer.OrdinalIgnoreCase))
        {
            return new BadRequestObjectResult(new
            {
                error = $"Invalid role. Valid values: {string.Join(", ", Roles.All)}",
            });
        }

        // Company Admin can only assign Logistics or Driver
        var isSuperAdmin = req.HttpContext.User.IsInRole(Roles.SuperAdmin);
        if (!isSuperAdmin && !Roles.AdminAssignableRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
        {
            return new BadRequestObjectResult(new
            {
                error = "Company administrators can only assign Logistics or Driver roles.",
            });
        }

        var current = await _users.GetRolesAsync(user);
        await _users.RemoveFromRolesAsync(user, current);
        await _users.AddToRoleAsync(user, role);

        _log.LogInformation("User {Id} role set to {Role}", id, role);
        return new OkObjectResult(new { message = $"Role set to {role}." });
    }

    // -----------------------------------------------------------------------
    // DELETE /api/users/{id}  (Admin) — soft-delete
    // -----------------------------------------------------------------------
    [Function("users-deactivate")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<IActionResult> Deactivate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "users/{id}")]
        HttpRequest req,
        string id,
        CancellationToken ct)
    {
        var user = await _users.FindByIdAsync(id);
        if (user is null)
            return new NotFoundObjectResult(new { error = "User not found." });

        user.IsActive = false;
        await _users.UpdateAsync(user);

        _log.LogInformation("User {Id} deactivated.", id);
        return new OkObjectResult(new { message = "User deactivated." });
    }
}
