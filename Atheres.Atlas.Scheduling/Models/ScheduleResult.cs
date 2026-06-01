using Atheres.Atlas.Domain.Enums;

namespace Atheres.Atlas.Scheduling.Abstractions;

/// <summary>
/// Outcome of <see cref="ISchedulingProvider.ScheduleAsync"/>. The
/// suggested <see cref="ResultingOrderStatus"/> tells the Functions caller
/// what to flip the Order to without it having to know per-provider rules.
/// </summary>
public sealed record ScheduleResult(
    bool Success,
    OrderStatus ResultingOrderStatus,
    string? ProviderEventId = null,
    string? ProviderEventUrl = null,
    string? Diagnostic = null);
