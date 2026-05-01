using System.Text;
using Atheres.Atlas.Auth.Functions.Middleware;
using Atheres.Atlas.Auth.Functions.Services;
using Atheres.Atlas.Data;
using Atheres.Atlas.Data.Entities;
using Atheres.Atlas.Data.Services;
using Atheres.Atlas.Domain.Constants;
using Atheres.Atlas.Domain.Entities;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

// Startup trace: writes to stdout/stderr so an `az webapp log tail` (or
// App Insights traces) shows exactly which required app settings are missing
// when the isolated worker fails to index functions. Without this, Azure
// reports only "WarmUp" and there's no clue why.
static void TraceConfigPresence(IConfiguration config)
{
    string[] required =
    {
        "SqlConnectionString", "JwtSecretKey", "JwtIssuer", "JwtAudience",
        "JwtExpiryMinutes", "JwtRefreshExpiryDays",
    };
    foreach (var name in required)
    {
        var v = config[name];
        Console.WriteLine($"[AuthStartup] {name}: {(string.IsNullOrWhiteSpace(v) ? "MISSING" : "present")}");
    }
}

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication(builder =>
    {
        builder.UseMiddleware<JwtAuthMiddleware>();
    })
    .ConfigureServices((ctx, services) =>
    {
        var config = ctx.Configuration;
        TraceConfigPresence(config);

        // ---- Application Insights (register FIRST so DI failures later are
        // captured in App Insights traces, not just stdout). ---------------
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        services.AddHttpContextAccessor();

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

        // ---- ASP.NET Core Identity (core only; no cookie schemes) --------
        // AddIdentity<,> registers cookie-based authentication schemes
        // (Identity.Application etc.) that collide with AddJwtBearer on the
        // isolated worker and can prevent the host from starting — which
        // manifests as "only WarmUp is indexed" on Azure. We use
        // AddIdentityCore + AddSignInManager + AddRoles to get UserManager,
        // SignInManager, role support, and password hashing without the
        // cookie handlers we don't use.
        services.AddIdentityCore<ApplicationUser>(opts =>
        {
            opts.Password.RequiredLength         = 8;
            opts.Password.RequireNonAlphanumeric = true;
            opts.Password.RequireUppercase       = true;
            opts.Password.RequireDigit           = true;

            opts.Lockout.MaxFailedAccessAttempts = 5;
            opts.Lockout.DefaultLockoutTimeSpan  = TimeSpan.FromMinutes(15);

            opts.User.RequireUniqueEmail = true;
        })
        .AddRoles<Microsoft.AspNetCore.Identity.IdentityRole>()
        .AddEntityFrameworkStores<AtlasIdentityDbContext>()
        .AddSignInManager()
        .AddDefaultTokenProviders();

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
        services.AddSingleton<IEmailService, SendGridEmailService>();
    })
    .Build();

// ---- Seed roles + tenant companies + bootstrap users ---------------------
// Run as a background task *after* the host starts. On Azure Linux Consumption
// the platform gives the isolated worker a limited initialization window; a
// cold Azure SQL first-connection plus Identity migrations plus user inserts
// can easily exceed it and the platform kills the worker before function
// indexing finishes -- which shows up as "only WarmUp is registered" in the
// portal. Kicking the seed off in the background lets RunAsync proceed
// immediately and the Functions host indexes in parallel.
var seedLog = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
_ = Task.Run(async () =>
{
    try
    {
        await SeedAsync(host.Services);
        seedLog.LogInformation("Startup seed completed.");
    }
    catch (Exception ex)
    {
        // Non-fatal: the host stays up and endpoints remain reachable.
        // The seed is idempotent so a later restart will retry.
        seedLog.LogError(ex, "Startup seed failed; Function App will continue running. Retry by restarting the app.");
    }
});

await host.RunAsync();

// ---------------------------------------------------------------------------
// Bootstrap seed — runs on Auth Functions startup. Creates every tenant company
// and user in a single pass using UserManager directly (no HTTP, no ordering).
// Idempotent: existing rows are left untouched.
// ---------------------------------------------------------------------------
static async Task SeedAsync(IServiceProvider services)
{
    var secureTransportId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    var demoCompanyId     = Guid.Parse("20000000-0000-0000-0000-000000000001");

    using var scope = services.CreateScope();
    var sp = scope.ServiceProvider;

    var identityDb = sp.GetRequiredService<AtlasIdentityDbContext>();
    await identityDb.Database.MigrateAsync();

    var businessDb = sp.GetRequiredService<AtlasDbContext>();
    var roleManager = sp.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("AuthSeed");

    // ---- 1. Roles ----------------------------------------------------------
    foreach (var role in Roles.All)
    {
        if (!await roleManager.RoleExistsAsync(role))
            await roleManager.CreateAsync(new IdentityRole(role));
    }

    // ---- 2. Companies (business DB) ----------------------------------------
    await UpsertCompanyAsync(businessDb, secureTransportId, "Secure Transport", "secure-transport", "secure@gmail.com");
    await UpsertCompanyAsync(businessDb, demoCompanyId,     "Demo Company",     "demo-company",     "demo@atheres.com");
    await businessDb.SaveChangesAsync();

    // ---- 3. Users (Identity DB) --------------------------------------------
    var config = sp.GetRequiredService<IConfiguration>();
    var bootstrapEmail    = config["SeedAdminEmail"];
    var bootstrapPassword = config["SeedAdminPassword"];

    var users = new List<SeedUser>();

    if (!string.IsNullOrWhiteSpace(bootstrapEmail) && !string.IsNullOrWhiteSpace(bootstrapPassword))
    {
        users.Add(new SeedUser(bootstrapEmail, bootstrapPassword, "Global", "Administrator", null, Roles.SuperAdmin));
    }

    users.AddRange(new[]
    {
        new SeedUser("ken@atheres.com",          "Phone@3313059708",  "Ken",    "Administrator", null,              Roles.SuperAdmin),
        new SeedUser("secure@gmail.com",         "Secure@1234567890", "Secure", "Admin",         secureTransportId, Roles.Admin),
        new SeedUser("secureuser@gmail.com",     "Secure@1234567890", "Secure", "User",          secureTransportId, Roles.Driver),
        new SeedUser("demo@atheres.com",         "Phone@3464978286",  "Demo",   "Admin",         demoCompanyId,     Roles.Admin),
        new SeedUser("demouser@atheres.com",     "Phone@3464978286",  "Demo",   "User",          demoCompanyId,     Roles.Driver),
    });

    foreach (var u in users)
    {
        if (await userManager.FindByEmailAsync(u.Email) is not null) continue;

        var appUser = new ApplicationUser
        {
            UserName  = u.Email,
            Email     = u.Email,
            FirstName = u.FirstName,
            LastName  = u.LastName,
            CompanyId = u.CompanyId,
        };

        var result = await userManager.CreateAsync(appUser, u.Password);
        if (!result.Succeeded)
        {
            logger.LogWarning("Seed: failed to create {Email}: {Errors}",
                u.Email, string.Join("; ", result.Errors.Select(e => e.Description)));
            continue;
        }

        await userManager.AddToRoleAsync(appUser, u.Role);
        logger.LogInformation("Seed: created {Email} as {Role}", u.Email, u.Role);
    }

    // ---- 4. Cleanup: drop the legacy single-account data users that were
    //         seeded before importers became warehouse-pinned. Hard delete
    //         is safe here — they're seed data, never real users, and any
    //         FKs that pointed at them have been removed in prior migrations.
    foreach (var legacyEmail in new[] { "securedatauser@gmail.com", "demodatauser@atheres.com" })
    {
        var legacy = await userManager.FindByEmailAsync(legacyEmail);
        if (legacy is null) continue;
        var dropResult = await userManager.DeleteAsync(legacy);
        if (dropResult.Succeeded)
            logger.LogInformation("Seed: removed legacy data user {Email}", legacyEmail);
        else
            logger.LogWarning("Seed: failed to remove legacy data user {Email}: {Errors}",
                legacyEmail, string.Join("; ", dropResult.Errors.Select(e => e.Description)));
    }

    // ---- 5. Per-warehouse OrderImporter accounts for Secure Transport -------
    await SeedWarehouseImportersAsync(
        businessDb, userManager,
        secureTransportId,
        emailDomain:    "securetransport.com",
        password:       "Secure@1234567890",
        maxToSeed:      5,
        logger:         logger);
}

/// <summary>
/// Creates OrderImporter accounts pinned to the first <paramref name="maxToSeed"/>
/// warehouses (alphabetical by BusinessName) of the given company. Each
/// account's username is "data_{slug-of-business-name}@{emailDomain}".
/// Idempotent: skips warehouses whose corresponding email already exists.
/// No-op when the company has no warehouses yet (warehouses are loaded
/// out-of-band by scripts/import-data.ps1, not in this seed).
/// </summary>
static async Task SeedWarehouseImportersAsync(
    AtlasDbContext db,
    UserManager<ApplicationUser> userManager,
    Guid companyId,
    string emailDomain,
    string password,
    int maxToSeed,
    ILogger logger)
{
    var warehouses = await db.Warehouses
        .IgnoreQueryFilters()
        .Where(w => w.CompanyId == companyId && w.IsActive)
        .OrderBy(w => w.BusinessName)
        .Take(maxToSeed)
        .ToListAsync();

    if (warehouses.Count == 0)
    {
        logger.LogInformation(
            "Seed: no warehouses for company {Company} — skipping OrderImporter seed. "
            + "Run scripts/import-data.ps1 to load warehouses, then restart the Auth Functions to seed importers.",
            companyId);
        return;
    }

    foreach (var wh in warehouses)
    {
        var slug = SlugifyForEmail(wh.BusinessName);
        if (string.IsNullOrEmpty(slug))
        {
            logger.LogWarning("Seed: warehouse {Id} has unusable BusinessName '{Name}' — skipping.", wh.Id, wh.BusinessName);
            continue;
        }

        var email = $"data_{slug}@{emailDomain}";
        if (await userManager.FindByEmailAsync(email) is not null) continue;

        var user = new ApplicationUser
        {
            UserName            = email,
            Email               = email,
            FirstName           = "Data",
            LastName            = wh.BusinessName,
            CompanyId           = companyId,
            AssignedWarehouseId = wh.Id,
            // Seed accounts skip the invitation flow — the password is
            // already a known dev/QA credential, so there's no first-login
            // forced reset.
            MustChangePassword  = false,
        };

        var result = await userManager.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            logger.LogWarning("Seed: failed to create importer {Email} for warehouse {Warehouse}: {Errors}",
                email, wh.BusinessName, string.Join("; ", result.Errors.Select(e => e.Description)));
            continue;
        }

        await userManager.AddToRoleAsync(user, Roles.OrderImporter);
        logger.LogInformation(
            "Seed: created OrderImporter {Email} pinned to warehouse {Warehouse}",
            email, wh.BusinessName);
    }
}

/// <summary>Reduces an arbitrary BusinessName to lowercase a-z/0-9 only so it
/// safely embeds in an email local-part (e.g. "Ascend Illinois - Barry" →
/// "ascendillinoisbarry"). Returns empty string when nothing survives.</summary>
static string SlugifyForEmail(string? name)
{
    if (string.IsNullOrWhiteSpace(name)) return string.Empty;
    var lowered = name.ToLowerInvariant();
    return new string(lowered.Where(char.IsLetterOrDigit).ToArray());
}

static async Task UpsertCompanyAsync(AtlasDbContext db, Guid id, string name, string slug, string contactEmail)
{
    if (await db.Companies.AnyAsync(c => c.Id == id)) return;
    db.Companies.Add(new Company
    {
        Id           = id,
        Name         = name,
        Slug         = slug,
        ContactEmail = contactEmail,
        Timezone     = "America/Chicago",
        IsActive     = true,
    });
}

record SeedUser(string Email, string Password, string FirstName, string LastName, Guid? CompanyId, string Role);
