using Microsoft.AspNetCore.Identity;

namespace Atheres.Atlas.Data.Entities;

public class ApplicationUser : IdentityUser
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName  { get; set; } = string.Empty;

    /// <summary>
    /// The company this user belongs to.
    /// Null only for SuperAdmin accounts that are not tied to any single company.
    /// </summary>
    public Guid? CompanyId { get; set; }

    /// <summary>FK to Trucks — set for Driver accounts so they see only their route.</summary>
    public Guid? AssignedTruckId { get; set; }

    public bool IsActive    { get; set; } = true;
    public DateTime CreatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }

    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];

    public string FullName => $"{FirstName} {LastName}".Trim();
}
