using Atheres.Atlas.Domain.Enums;

namespace Atheres.Atlas.Scheduling.Abstractions;

/// <summary>
/// Resolves an <see cref="ISchedulingProvider"/> for a store, dispatching
/// on its <see cref="SchedulingMethod"/>. Always returns a non-null
/// provider (the None method has a no-op provider).
/// </summary>
public interface ISchedulingProviderFactory
{
    ISchedulingProvider Get(SchedulingMethod method);
    ISchedulingProvider Get(StoreSchedulingSettings settings) => Get(settings.Method);
}
