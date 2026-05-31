using Atheres.Atlas.Scheduling.Abstractions;
using Atheres.Atlas.Scheduling.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Atheres.Atlas.Scheduling.DependencyInjection;

public static class SchedulingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the four <see cref="ISchedulingProvider"/> implementations
    /// and the factory. The caller is responsible for registering an
    /// <see cref="ISchedulingEmailSender"/> (the Functions side wires it
    /// onto its existing SendGrid IEmailService).
    /// </summary>
    public static IServiceCollection AddAtlasScheduling(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // BookingsTenantId is read from either the "Scheduling:Bookings:TenantId"
        // section (modern config layout) or the flat "BookingsTenantId" app
        // setting (Functions host convention).
        services.Configure<BookingsProviderOptions>(opts =>
        {
            opts.TenantId = configuration["Scheduling:Bookings:TenantId"]
                         ?? configuration["BookingsTenantId"];
        });

        // All providers are Scoped; the factory resolves them per-call via
        // IServiceProvider so the typed HttpClient (Calendly) and any
        // future scoped dependencies (logger captures, EF context, etc.)
        // get a fresh instance every time.
        services.AddScoped<ISchedulingProvider, NoneSchedulingProvider>();
        services.AddScoped<ISchedulingProvider, EmailSchedulingProvider>();
        services.AddScoped<ISchedulingProvider, BookingsSchedulingProvider>();

        services.AddHttpClient<CalendlySchedulingProvider>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddScoped<ISchedulingProvider>(sp => sp.GetRequiredService<CalendlySchedulingProvider>());

        services.AddScoped<ISchedulingProviderFactory, SchedulingProviderFactory>();

        return services;
    }
}
