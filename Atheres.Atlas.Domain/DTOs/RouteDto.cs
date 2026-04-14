namespace Atheres.Atlas.Domain.DTOs;

public class RouteDto
{
    public Guid Id { get; set; }
    public DateTime DeliveryDate { get; set; }
    public string StartAddress { get; set; } = string.Empty;
    public string EndAddress { get; set; } = string.Empty;
    public int TotalStops { get; set; }
    public double TotalDistanceMiles { get; set; }
    public string TotalDuration { get; set; } = string.Empty;
    public bool IsOptimized { get; set; }
    public string? OverviewPolyline { get; set; }
    public List<RouteStopDto> Stops { get; set; } = new();
}
