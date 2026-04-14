using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Atheres.Atlas.Data;

/// <summary>
/// Allows EF Core CLI to instantiate AtlasIdentityDbContext at design time.
///
/// Usage (from solution root or Data project):
///   dotnet ef migrations add InitialIdentity \
///     --context AtlasIdentityDbContext \
///     --project Atheres.Atlas.Data \
///     --startup-project Atheres.Atlas.Auth.Functions \
///     --output-dir Migrations/Identity
///
///   dotnet ef database update \
///     --context AtlasIdentityDbContext \
///     --project Atheres.Atlas.Data \
///     --startup-project Atheres.Atlas.Auth.Functions
/// </summary>
public class DesignTimeIdentityDbContextFactory
    : IDesignTimeDbContextFactory<AtlasIdentityDbContext>
{
    public AtlasIdentityDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("SqlConnectionString");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var authFunctionsPath = Path.Combine(
                Directory.GetCurrentDirectory(), "..", "Atheres.Atlas.Auth.Functions");

            if (Directory.Exists(authFunctionsPath))
            {
                var config = new ConfigurationBuilder()
                    .SetBasePath(authFunctionsPath)
                    .AddJsonFile("local.settings.json", optional: true)
                    .Build();

                connectionString = config["Values:SqlConnectionString"];
            }
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true)
                .AddEnvironmentVariables()
                .Build();

            connectionString = config["SqlConnectionString"];
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString =
                "Server=(localdb)\\mssqllocaldb;Database=AtheresAtlasIdentity;Trusted_Connection=True;";

            Console.WriteLine(
                "[DesignTimeIdentityDbContextFactory] WARNING: SqlConnectionString not found. " +
                "Falling back to LocalDB.");
        }

        var optionsBuilder = new DbContextOptionsBuilder<AtlasIdentityDbContext>();
        optionsBuilder.UseSqlServer(connectionString, sql =>
        {
            sql.EnableRetryOnFailure(maxRetryCount: 3);
            sql.MigrationsHistoryTable("__IdentityMigrationsHistory");
            sql.MigrationsAssembly(typeof(AtlasIdentityDbContext).Assembly.GetName().Name);
        });

        return new AtlasIdentityDbContext(optionsBuilder.Options);
    }
}
