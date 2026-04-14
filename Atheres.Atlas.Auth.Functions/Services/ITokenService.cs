using Atheres.Atlas.Data.Entities;
using Atheres.Atlas.Domain.DTOs.Auth;

namespace Atheres.Atlas.Auth.Functions.Services;

public interface ITokenService
{
    /// <summary>Generates a signed JWT access token for the given user + roles.</summary>
    string GenerateAccessToken(ApplicationUser user, IList<string> roles);

    /// <summary>Creates a new persisted refresh token for the user.</summary>
    Task<string> CreateRefreshTokenAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Validates a refresh token and, if valid, rotates it (revokes old, issues new).
    /// Returns null if the token is invalid or expired.
    /// </summary>
    Task<TokenResponseDto?> RefreshAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>Revokes all refresh tokens belonging to the user (logout).</summary>
    Task RevokeAllAsync(string userId, string reason = "logout", CancellationToken ct = default);
}
