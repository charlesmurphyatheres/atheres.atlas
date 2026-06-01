namespace Atheres.Atlas.Domain.Entities;

/// <summary>
/// One row per <see cref="Atheres.Atlas.Functions.Services.IRouteScheduler"/>
/// invocation. Captures the operator-readable decision trail the route
/// scheduler followed — warehouses involved, hubs chosen, chunks formed,
/// hub-bypass exceptions, and so on — so administrators can audit how a
/// given dispatch was planned and why.
///
/// The body lives in <see cref="LogText"/> as multi-line plain text, ready
/// to render in the AdminPanel and to dump to PDF without a structured
/// rehydration step.
/// </summary>
public class OptimizationAudit
{
    public Guid     Id          { get; set; } = Guid.NewGuid();
    public Guid     CompanyId   { get; set; }
    public DateTime CreatedAt   { get; set; } = DateTime.UtcNow;

    /// <summary>Email or display name of the user who triggered the run.
    /// Null when triggered by a system path (e.g. timer-based reschedule)
    /// rather than an interactive request.</summary>
    public string?  TriggeredBy { get; set; }

    /// <summary>Short label describing what kicked off the run, e.g.
    /// "Bulk status → Scheduled" or "ReadyToPickup". Surfaced in the audit
    /// list view so the operator can pick the right row at a glance.</summary>
    public string   Trigger     { get; set; } = string.Empty;

    /// <summary>One-line summary for the list view — counts of orders,
    /// routes, warehouses, and so on. The detailed reasoning lives in
    /// <see cref="LogText"/>.</summary>
    public string   Summary     { get; set; } = string.Empty;

    public int      OrderCount       { get; set; }
    public int      RouteCount       { get; set; }
    public int      WarehouseCount   { get; set; }
    public int      HubCount         { get; set; }
    public int      ZoneCount        { get; set; }
    /// <summary>How many of the generated routes qualified as DirectDelivery
    /// (single-zone batch where the van skips the hub). Useful for the
    /// admin to see at a glance how often the bypass was applied.</summary>
    public int      DirectDeliveryCount { get; set; }

    /// <summary>Full multi-line audit body. Plain text — line breaks are
    /// preserved as-is and the AdminPanel renders this in a monospace block.
    /// Stored as nvarchar(max) so there's no practical length limit.</summary>
    public string   LogText     { get; set; } = string.Empty;

    public Company? Company { get; set; }
}
