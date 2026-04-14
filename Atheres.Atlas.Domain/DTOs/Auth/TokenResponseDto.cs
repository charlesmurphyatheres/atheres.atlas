namespace Atheres.Atlas.Domain.DTOs.Auth;

public class TokenResponseDto
{
    public string AccessToken  { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime ExpiresAt  { get; set; }
    public string UserId       { get; set; } = string.Empty;
    public string Email        { get; set; } = string.Empty;
    public string FullName     { get; set; } = string.Empty;
    public IList<string> Roles { get; set; } = [];

    // Company context — null for SuperAdmin
    public Guid?   CompanyId   { get; set; }
    public string? CompanyName { get; set; }
    public string? CompanySlug { get; set; }
}
