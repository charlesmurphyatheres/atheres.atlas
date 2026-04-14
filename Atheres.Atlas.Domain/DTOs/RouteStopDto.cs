using Atheres.Atlas.Domain.Enums;

namespace Atheres.Atlas.Domain.DTOs;

public class RouteStopDto
{
    public Guid OrderId { get; set; }
    public int Sequence { get; set; }
    public string StoreName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public DateTime? EstimatedArrival { get; set; }
    public OrderStatus OrderStatus { get; set; }
    public ConfirmationStatus ConfirmationStatus { get; set; }
    public double LegDistanceMiles { get; set; }
    public int LegDurationMinutes { get; set; }
}
