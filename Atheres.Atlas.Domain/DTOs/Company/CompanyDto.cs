namespace Atheres.Atlas.Domain.DTOs.Company;

public class CompanyDto
{
    public Guid   Id           { get; set; }
    public string Name         { get; set; } = string.Empty;
    public string Slug         { get; set; } = string.Empty;
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string Timezone     { get; set; } = string.Empty;
    public bool   IsActive     { get; set; }
    public DateTime CreatedAt  { get; set; }
    public int TruckCount      { get; set; }
}
