namespace Atheres.Atlas.Data.Entities;

public class RefreshToken
{
    public Guid   Id        { get; set; } = Guid.NewGuid();
    public string UserId    { get; set; } = string.Empty;
    public string Token     { get; set; } = string.Empty;
    public DateTime ExpiresAt   { get; set; }
    public bool  IsRevoked  { get; set; }
    public string? RevokedReason    { get; set; }
    public string? ReplacedByToken  { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ApplicationUser User { get; set; } = null!;

    public bool IsActive => !IsRevoked && DateTime.UtcNow < ExpiresAt;
}
