using System.Text;
using Atheres.Atlas.Auth.Functions.Services;
using Atheres.Atlas.Data;
using Atheres.Atlas.Data.Entities;
using Atheres.Atlas.Data.Services;
using Atheres.Atlas.Domain.Constants;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((ctx, services) =>
    {
        var config = ctx.Configuration;

        var connectionString = config["SqlConnectionString"]
            ?? throw new InvalidOperationException("SqlConnectionString is required.");

        // ---- Identity DbContext ------------------------------------------
        services.AddDbContext<AtlasIdentityDbContext>(opts =>
            opts.UseSqlServer(connectionString, sql =>
            {
                sql.EnableRetryOnFailure(3);
                sql.MigrationsHistoryTable("__IdentityMigrationsHistory");
            }));

        // ---- Business DbContext (for Company/Truck management) -----------
        // Auth functions operate cross-tenant (SuperAdmin), so no CompanyContext filter.
        services.AddDbContext<AtlasDbContext>((sp, opts) =>
            opts.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(3)));
        services.AddScoped<ICompanyContext>(_ => ExplicitCompanyContext.System);

        // ---- ASP.NET Core Identity ---------------------------------------
        services.AddIdentity<ApplicationUser, Microsoft.AspNetCore.Identity.IdentityRole>(opts =>
        {
            opts.Password.RequiredLength         = 8;
            opts.Password.RequireNonAlphanumeric = true;
            opts.Password.RequireUppercase       = true;
            opts.Password.RequireDigit           = true;

            opts.Lockout.MaxFailedAccessAttempts = 5;
            opts.Lockout.DefaultLockoutTimeSpan  = TimeSpan.FromMinutes(15);

            opts.User.RequireUniqueEmail = true;
        })
        .AddEntityFrameworkStores<AtlasIdentityDbContext>();

        // ---- JWT Bearer --------------------------------------------------
        var jwtKey = config["JwtSecretKey"]
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
                ValidIssuer              = config["JwtIssuer"],
                ValidAudience            = config["JwtAudience"],
                IssuerSigningKey         = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(jwtKey)),
                ClockSkew                = TimeSpan.FromSeconds(30),
            };
        });

        services.AddAuthorization(opts =>
        {
            opts.AddPolicy("SuperAdminOnly",  p => p.RequireRole(Roles.SuperAdmin));
            opts.AddPolicy("AdminOnly",       p => p.RequireRole(Roles.SuperAdmin, Roles.Admin));
            opts.AddPolicy("LogisticsUp",     p => p.RequireRole(Roles.SuperAdmin, Roles.Admin, Roles.Logistics));
            opts.AddPolicy("AuthenticatedUser", p => p.RequireAuthenticatedUser());
        });

        // ---- Application services ----------------------------------------
        services.AddScoped<ITokenService, TokenService>();

        // ---- Application Insights ----------------------------------------
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
    })
    .Build();

// ---- Seed roles and default admin on first start -------------------------
await SeedAsync(host.Services);

await host.RunAsync();

// ---------------------------------------------------------------------------
static async Task SeedAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var sp = scope.ServiceProvider;

    var db = sp.GetRequiredService<AtlasIdentityDbContext>();
    await db.Database.MigrateAsync();

    var roleManager = sp.GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<
        Microsoft.AspNetCore.Identity.IdentityRole>>();

    foreach (var role in Roles.All)
    {
        if (!await roleManager.RoleExistsAsync(role))
            await roleManager.CreateAsync(new Microsoft.AspNetCore.Identity.IdentityRole(role));
    }

    // Seed an initial admin from environment (optional — skip if already exists)
    var config = sp.GetRequiredService<IConfiguration>();
    var adminEmail    = config["SeedAdminEmail"];
    var adminPassword = config["SeedAdminPassword"];

    if (!string.IsNullOrWhiteSpace(adminEmail) && !string.IsNullOrWhiteSpace(adminPassword))
    {
        var userManager = sp.GetRequiredService<
            Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();

        if (await userManager.FindByEmailAsync(adminEmail) is null)
        {
            var admin = new ApplicationUser
            {
                UserName  = adminEmail,
                Email     = adminEmail,
                FirstName = "Global",
                LastName  = "Administrator",
                // CompanyId intentionally null — SuperAdmin is cross-tenant
            };

            var result = await userManager.CreateAsync(admin, adminPassword);
            if (result.Succeeded)
                await userManager.AddToRoleAsync(admin, Roles.SuperAdmin);
        }
    }
}
