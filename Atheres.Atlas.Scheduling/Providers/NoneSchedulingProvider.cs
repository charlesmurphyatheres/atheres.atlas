using Atheres.Atlas.Domain.Enums;
using Atheres.Atlas.Scheduling.Abstractions;

namespace Atheres.Atlas.Scheduling.Providers;

/// <summary>
/// Null-object provider for <see cref="SchedulingMethod.None"/>. Reports
/// no availability and lands every scheduled order straight into
/// <see cref="OrderStatus.Confirmed"/> — the "assumed confirmed until
/// manually changed" semantic the admin UI shows for this method.
/// </summary>
public sealed class NoneSchedulingProvider : ISchedulingProvider
{
    public SchedulingMethod Method => SchedulingMethod.None;

    public bool IsConfigured(StoreSchedulingSettings settings) => true;

    public Task<IReadOnlyList<AvailabilitySlot>> GetAvailabilityAsync(
        StoreSchedulingSettings settings,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AvailabilitySlot>>(Array.Empty<AvailabilitySlot>());

    public Task<ScheduleResult> ScheduleAsync(
        StoreSchedulingSettings settings,
        ScheduleRequest request,
        CancellationToken ct = default)
        => Task.FromResult(new ScheduleResult(
            Success: true,
            ResultingOrderStatus: OrderStatus.Confirmed,
            Diagnostic: "No scheduling provider configured; order auto-confirmed."));
}
