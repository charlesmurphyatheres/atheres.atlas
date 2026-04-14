using System.ComponentModel.DataAnnotations;

namespace Atheres.Atlas.Domain.DTOs.Company;

public class CreateCompanyDto
{
    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>URL-safe slug — auto-generated from Name if omitted.</summary>
    [MaxLength(100)]
    public string? Slug { get; set; }

    [EmailAddress, MaxLength(254)]
    public string? ContactEmail { get; set; }

    [MaxLength(30)]
    public string? ContactPhone { get; set; }

    /// <summary>IANA timezone, e.g. "America/Chicago". Defaults to "America/Chicago".</summary>
    [MaxLength(60)]
    public string Timezone { get; set; } = "America/Chicago";

    // Optional: seed an initial Admin user for this company
    [EmailAddress, MaxLength(254)]
    public string? AdminEmail    { get; set; }
    [MinLength(8), MaxLength(128)]
    public string? AdminPassword { get; set; }
    [MaxLength(100)]
    public string? AdminFirstName { get; set; }
    [MaxLength(100)]
    public string? AdminLastName  { get; set; }
}
