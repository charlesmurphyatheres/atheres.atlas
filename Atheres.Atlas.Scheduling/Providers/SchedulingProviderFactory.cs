using Atheres.Atlas.Domain.Enums;
using Atheres.Atlas.Scheduling.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Atheres.Atlas.Scheduling.Providers;

/// <summary>
/// Resolves providers lazily through the DI container on every call so
/// scoped registrations (the Calendly HTTP-typed client, anything wired
/// to a per-request EF context later) get a fresh instance each time
/// rather than the snapshot captured at factory construction.
/// </summary>
internal sealed class SchedulingProviderFactory : ISchedulingProviderFactory
{
    private readonly IServiceProvider _sp;

    public SchedulingProviderFactory(IServiceProvider sp)
    {
        _sp = sp;
    }

    public ISchedulingProvider Get(SchedulingMethod method)
    {
        var providers = _sp.GetServices<ISchedulingProvider>();
        var match = providers.LastOrDefault(p => p.Method == method);
        if (match is null)
            throw new InvalidOperationException(
                $"No ISchedulingProvider is registered for SchedulingMethod.{method}.");
        return match;
    }
}
