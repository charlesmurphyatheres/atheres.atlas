using Atheres.Atlas.Domain.Enums;

namespace Atheres.Atlas.Domain.Messages;

public record AuditMessage(
    AuditEventType EventType,
    string         AgentName,
    Guid           CompanyId,
    Guid?          OrderId,
    Guid?          RouteId,
    string?        PreviousStatus,
    string?        NewStatus,
    string?        Details,
    bool           Success,
    string?        ErrorMessage,
    DateTime       OccurredAt
);
