using Atheres.Atlas.Domain.Enums;

namespace Atheres.Atlas.Domain.Entities;

public class AuditLog
{
    public Guid  Id        { get; set; } = Guid.NewGuid();
    public Guid  CompanyId { get; set; }
    public Guid? OrderId   { get; set; }
    public Guid? RouteId   { get; set; }

    public AuditEventType EventType { get; set; }
    public string EventName { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string? PreviousStatus { get; set; }
    public string? NewStatus { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
    public string? AgentName { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    // Navigation property
    public Order? Order { get; set; }
}
