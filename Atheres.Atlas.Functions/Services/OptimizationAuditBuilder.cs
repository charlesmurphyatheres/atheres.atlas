using System.Text;
using Atheres.Atlas.Domain.Entities;

namespace Atheres.Atlas.Functions.Services;

/// <summary>
/// Accumulator used by <see cref="RouteScheduler"/> to record every decision
/// it makes during a single optimization run. The scheduler appends a line
/// at each branching point (group orders, pick hub, chunk, detect single-zone
/// bypass, dispatch) and finally calls <see cref="ToAudit"/> which produces
/// a persistable <see cref="OptimizationAudit"/> entity.
///
/// All state is in-memory until the run commits. If anything in the
/// scheduler throws mid-run, the partial audit is still written so the
/// administrator can see how far the planner got before failing.
/// </summary>
public sealed class OptimizationAuditBuilder
{
    private readonly StringBuilder _log = new();
    private readonly DateTime _startedAt;

    public OptimizationAuditBuilder(Guid companyId, string? triggeredBy, string trigger)
    {
        CompanyId    = companyId;
        TriggeredBy  = triggeredBy;
        Trigger      = string.IsNullOrWhiteSpace(trigger) ? "(unknown)" : trigger;
        _startedAt   = DateTime.UtcNow;
    }

    public Guid    CompanyId   { get; }
    public string? TriggeredBy { get; }
    public string  Trigger     { get; }

    public int OrderCount          { get; set; }
    public int RouteCount          { get; set; }
    public int WarehouseCount      { get; set; }
    public int HubCount            { get; set; }
    public int ZoneCount           { get; set; }
    public int DirectDeliveryCount { get; set; }

    /// <summary>Append a section header — "STEP N: …" — followed by a blank
    /// line for readability. Returns this for fluent chaining.</summary>
    public OptimizationAuditBuilder Step(int n, string title)
    {
        if (_log.Length > 0) _log.AppendLine();
        _log.Append("STEP ").Append(n).Append(": ").AppendLine(title);
        _log.AppendLine(new string('-', title.Length + 8));
        return this;
    }

    /// <summary>Append a free-form line. Indented with two spaces so it
    /// reads as a child of the most recent Step header.</summary>
    public OptimizationAuditBuilder Line(string text)
    {
        _log.Append("  ").AppendLine(text);
        return this;
    }

    /// <summary>Append a free-form line without the step-child indent — use
    /// for top-level prose like the opening banner or closing summary.</summary>
    public OptimizationAuditBuilder Raw(string text)
    {
        _log.AppendLine(text);
        return this;
    }

    public OptimizationAudit ToAudit(string summary)
    {
        return new OptimizationAudit
        {
            CompanyId           = CompanyId,
            CreatedAt           = _startedAt,
            TriggeredBy         = TriggeredBy,
            Trigger             = Trigger,
            Summary             = summary,
            OrderCount          = OrderCount,
            RouteCount          = RouteCount,
            WarehouseCount      = WarehouseCount,
            HubCount            = HubCount,
            ZoneCount           = ZoneCount,
            DirectDeliveryCount = DirectDeliveryCount,
            LogText             = _log.ToString(),
        };
    }
}
