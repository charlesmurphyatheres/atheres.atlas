using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Atheres.Atlas.Data;
using Atheres.Atlas.Data.Entities;
using Atheres.Atlas.Domain.DTOs.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Atheres.Atlas.Auth.Functions.Services;

public sealed class TokenService : ITokenService
{
    private readonly AtlasIdentityDbContext _identityDb;
    private readonly AtlasDbContext _businessDb;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _config;

    public TokenService(
        AtlasIdentityDbContext identityDb,
        AtlasDbContext businessDb,
        UserManager<ApplicationUser> userManager,
        IConfiguration config)
    {
        _identityDb  = identityDb;
        _businessDb  = businessDb;
        _userManager = userManager;
        _config      = config;
    }

    // -----------------------------------------------------------------------
    public string GenerateAccessToken(ApplicationUser user, IList<string> roles)
    {
        var key     = GetSigningKey();
        var expiry  = DateTime.UtcNow.AddMinutes(
            _config.GetValue<int>("JwtExpiryMinutes", 15));

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub,   user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti,   Guid.NewGuid().ToString()),
            new("firstName", user.FirstName),
            new("lastName",  user.LastName),
        };

        // Embed company context so downstream services can scope queries
        if (user.CompanyId.HasValue)
            claims.Add(new Claim("companyId", user.CompanyId.Value.ToString()));

        if (user.AssignedTruckId.HasValue)
            claims.Add(new Claim("truckId", user.AssignedTruckId.Value.ToString()));

        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        var token = new JwtSecurityToken(
            issuer:   _config["JwtIssuer"],
            audience: _config["JwtAudience"],
            claims:   claims,
            expires:  expiry,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // -----------------------------------------------------------------------
    public async Task<string> CreateRefreshTokenAsync(
        string userId, CancellationToken ct = default)
    {
        var token = GenerateSecureToken();
        var expiryDays = _config.GetValue<int>("JwtRefreshExpiryDays", 7);

        _identityDb.RefreshTokens.Add(new RefreshToken
        {
            UserId    = userId,
            Token     = token,
            ExpiresAt = DateTime.UtcNow.AddDays(expiryDays),
        });

        await _identityDb.SaveChangesAsync(ct);
        return token;
    }

    // -----------------------------------------------------------------------
    public async Task<TokenResponseDto?> RefreshAsync(
        string refreshToken, CancellationToken ct = default)
    {
        var stored = await _identityDb.RefreshTokens
            .Include(r => r.User)
            .FirstOrDefaultAsync(r => r.Token == refreshToken, ct);

        if (stored is null || !stored.IsActive)
            return null;

        var user  = stored.User;
        var roles = (await _userManager.GetRolesAsync(user)).ToList();

        // Rotate — revoke old, issue new
        stored.IsRevoked      = true;
        stored.RevokedReason  = "rotated";
        var newRefreshToken   = GenerateSecureToken();
        var expiryDays        = _config.GetValue<int>("JwtRefreshExpiryDays", 7);
        var newToken          = new RefreshToken
        {
            UserId    = user.Id,
            Token     = newRefreshToken,
            ExpiresAt = DateTime.UtcNow.AddDays(expiryDays),
        };
        stored.ReplacedByToken = newRefreshToken;
        _identityDb.RefreshTokens.Add(newToken);
        await _identityDb.SaveChangesAsync(ct);

        var accessToken = GenerateAccessToken(user, roles);
        var expiry      = DateTime.UtcNow.AddMinutes(
            _config.GetValue<int>("JwtExpiryMinutes", 15));

        // Load company name for the response payload
        string? companyName = null;
        string? companySlug = null;
        if (user.CompanyId.HasValue)
        {
            var co = await _businessDb.Companies.FindAsync([user.CompanyId.Value], ct);
            companyName = co?.Name;
            companySlug = co?.Slug;
        }

        return new TokenResponseDto
        {
            AccessToken  = accessToken,
            RefreshToken = newRefreshToken,
            ExpiresAt    = expiry,
            UserId       = user.Id,
            Email        = user.Email ?? string.Empty,
            FullName     = user.FullName,
            Roles        = roles,
            CompanyId    = user.CompanyId,
            CompanyName  = companyName,
            CompanySlug  = companySlug,
        };
    }

    // -----------------------------------------------------------------------
    public async Task RevokeAllAsync(
        string userId, string reason = "logout", CancellationToken ct = default)
    {
        var tokens = await _identityDb.RefreshTokens
            .Where(r => r.UserId == userId && !r.IsRevoked)
            .ToListAsync(ct);

        foreach (var t in tokens)
        {
            t.IsRevoked     = true;
            t.RevokedReason = reason;
        }

        await _identityDb.SaveChangesAsync(ct);
    }

    // -----------------------------------------------------------------------
    private SymmetricSecurityKey GetSigningKey()
    {
        var secret = _config["JwtSecretKey"]
            ?? throw new InvalidOperationException(
                "JwtSecretKey is not configured.");

        var keyBytes = System.Text.Encoding.UTF8.GetBytes(secret);
        if (keyBytes.Length < 32)
            throw new InvalidOperationException(
                "JwtSecretKey must be at least 32 characters.");

        return new SymmetricSecurityKey(keyBytes);
    }

    private static string GenerateSecureToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Convert.ToBase64String(bytes);
    }
}
