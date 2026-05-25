using System.Text;
using Atheres.Atlas.Data;
using Atheres.Atlas.Data.Repositories;
using Atheres.Atlas.Data.Services;
using Atheres.Atlas.Functions.Middleware;
using Atheres.Atlas.Functions.Services;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication(builder =>
    {
        builder.UseMiddleware<JwtAuthMiddleware>();
    })
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;

        // Application Insights
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // ---- Company context (reads companyId JWT claim per HTTP request) --
        services.AddHttpContextAccessor();
        services.AddScoped<ICompanyContext, HttpCompanyContext>();

        // ---- SQL Azure / EF Core (tenant-scoped via ICompanyContext) -------
        var connectionString = Environment.GetEnvironmentVariable("SqlConnectionString")
            ?? throw new InvalidOperationException("SqlConnectionString is required.");

        services.AddDbContext<AtlasDbContext>((sp, opts) =>
        {
            opts.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(3));
        });

        // ---- JWT Bearer validation ----------------------------------------
        var jwtKey = Environment.GetEnvironmentVariable("JwtSecretKey")
            ?? throw new InvalidOperationException("JwtSecretKey is required.");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(opts =>
            {
                opts.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer           = true,
                    ValidateAudience         = true,
                    ValidateLifetime         = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer              = Environment.GetEnvironmentVariable("JwtIssuer") ?? "http://localhost",
                    ValidAudience            = Environment.GetEnvironmentVariable("JwtAudience") ?? "http://localhost",
                    IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
                    ClockSkew                = TimeSpan.FromSeconds(30),
                };
            });

        services.AddAuthorization();

        // ---- Repositories --------------------------------------------------
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IRouteRepository, RouteRepository>();
        services.AddScoped<IConfirmationRepository, ConfirmationRepository>();
        services.AddScoped<IAuditRepository, AuditRepository>();
        services.AddScoped<IUserSettingsRepository, UserSettingsRepository>();

        // Registered so QueryAgent can invoke OptimizeRoute in-process when
        // DirectOptimizer=true (dev fallback for when Service Bus is unreachable).
        services.AddScoped<Atheres.Atlas.Functions.Agents.RouteOptimizationAgent>();

        // Centralised route-enqueue helper used by every "these orders just
        // became Scheduled" entry point (ManagementAgent bulk + single
        // status flip, OrderImportAgent's initial-Scheduled imports, etc.)
        // so they share one batching/chunking implementation instead of
        // each fanning out per-order optimization requests.
        services.AddScoped<IRouteScheduler, RouteScheduler>();

        // Server-side PDF generation (iText). Stateless — singleton is
        // fine and avoids per-request allocation of the document builder.
        services.AddSingleton<PdfService>();

        // ---- Azure Service Bus ---------------------------------------------
        var serviceBusConnection = Environment.GetEnvironmentVariable("ServiceBusConnection")
            ?? throw new InvalidOperationException("ServiceBusConnection is required.");
        services.AddSingleton(_ => new ServiceBusClient(serviceBusConnection));
        services.AddSingleton<IServiceBusPublisher, ServiceBusPublisher>();

        // ---- Google Maps ---------------------------------------------------
        services.AddHttpClient<IGoogleMapsService, GoogleMapsService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        // ---- Communication -------------------------------------------------
        services.AddSingleton<IEmailService, SendGridEmailService>();
    })
    .ConfigureLogging(logging =>
    {
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Information);
        logging.AddFilter("Azure.Messaging.ServiceBus", LogLevel.Warning);
        logging.AddFilter("Azure.Core", LogLevel.Warning);
    })
    .Build();

host.Run();
