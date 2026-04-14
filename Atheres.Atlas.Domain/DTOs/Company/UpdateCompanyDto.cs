using System.ComponentModel.DataAnnotations;

namespace Atheres.Atlas.Domain.DTOs.Company;

public class UpdateCompanyDto
{
    [MaxLength(200)]
    public string? Name { get; set; }

    [EmailAddress, MaxLength(254)]
    public string? ContactEmail { get; set; }

    [MaxLength(30)]
    public string? ContactPhone { get; set; }

    [MaxLength(60)]
    public string? Timezone { get; set; }

    public bool? IsActive { get; set; }
}
