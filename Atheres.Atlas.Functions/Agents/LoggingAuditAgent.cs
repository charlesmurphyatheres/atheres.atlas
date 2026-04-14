using Atheres.Atlas.Data.Repositories;
using Atheres.Atlas.Domain.Entities;
using Atheres.Atlas.Domain.Messages;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Functions.Agents;

/// <summary>
/// Consumes all audit messages from every other agent and persists them to SQL Azure.
/// Provides a complete, immutable audit trail of every system decision and state change.
/// Queue: atlas-audit
/// </summary>
public class LoggingAuditAgent
{
    private readonly IAuditRepository _audit;
    private readonly ILogger<LoggingAuditAgent> _logger;

    public LoggingAuditAgent(IAuditRepository audit, ILogger<LoggingAuditAgent> logger)
    {
        _audit = audit;
        _logger = logger;
    }

    [Function(nameof(PersistAuditEvent))]
    public async Task PersistAuditEvent(
        [ServiceBusTrigger(ServiceBusQueues.Audit, Connection = "ServiceBusConnection")]
        AuditMessage message,
        CancellationToken ct)
    {
        var log = new AuditLog
        {
            CompanyId = message.CompanyId,
            EventType = message.EventType,
            EventName = message.EventType.ToString(),
            AgentName = message.AgentName,
            OrderId = message.OrderId,
            RouteId = message.RouteId,
            PreviousStatus = message.PreviousStatus,
            NewStatus = message.NewStatus,
            Details = message.Details,
            Success = message.Success,
            ErrorMessage = message.ErrorMessage,
            OccurredAt = message.OccurredAt
        };

        await _audit.AddAsync(log, ct);
        await _audit.SaveChangesAsync(ct);

        if (message.Success)
            _logger.LogInformation("[AUDIT] {Agent} → {Event} | Order: {OrderId} | {Details}",
                message.AgentName, message.EventType, message.OrderId, message.Details);
        else
            _logger.LogWarning("[AUDIT-FAIL] {Agent} → {Event} | Order: {OrderId} | Error: {Error}",
                message.AgentName, message.EventType, message.OrderId, message.ErrorMessage);
    }
}
