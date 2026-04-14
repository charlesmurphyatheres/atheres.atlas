using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Atheres.Atlas.Data;

/// <summary>
/// Allows the EF Core CLI tools (dotnet ef migrations add / dotnet ef database update)
/// to instantiate AtlasDbContext at design time without a running host.
///
/// Usage:
///   cd Atheres.Atlas.Data
///   dotnet ef migrations add &lt;MigrationName&gt; --startup-project ../Atheres.Atlas.Functions
///   dotnet ef database update            --startup-project ../Atheres.Atlas.Functions
///
/// The connection string is read from (in order of priority):
///   1. Environment variable  : SqlConnectionString
///   2. local.settings.json   : Values.SqlConnectionString  (Functions project)
///   3. appsettings.json      : SqlConnectionString          (fallback)
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AtlasDbContext>
{
    public AtlasDbContext CreateDbContext(string[] args)
    {
        // 1. Try environment variable directly
        var connectionString = Environment.GetEnvironmentVariable("SqlConnectionString");

        // 2. Try Functions project's local.settings.json
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var functionsSettingsPath = Path.Combine(
                Directory.GetCurrentDirectory(), "..", "Atheres.Atlas.Functions");

            if (Directory.Exists(functionsSettingsPath))
            {
                var config = new ConfigurationBuilder()
                    .SetBasePath(functionsSettingsPath)
                    .AddJsonFile("local.settings.json", optional: true)
                    .Build();

                connectionString = config["Values:SqlConnectionString"];
            }
        }

        // 3. Fallback — standard appsettings.json
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
            // Last resort: use LocalDB for local development
            connectionString =
                "Server=(localdb)\\mssqllocaldb;Database=AtheresAtlas;Trusted_Connection=True;";

            Console.WriteLine(
                "[DesignTimeDbContextFactory] WARNING: SqlConnectionString not found. " +
                "Falling back to LocalDB. Set SqlConnectionString to target a specific database.");
        }

        var optionsBuilder = new DbContextOptionsBuilder<AtlasDbContext>();
        optionsBuilder.UseSqlServer(connectionString, sql =>
        {
            sql.EnableRetryOnFailure(maxRetryCount: 3);
            sql.MigrationsAssembly(typeof(AtlasDbContext).Assembly.GetName().Name);
        });

        return new AtlasDbContext(optionsBuilder.Options);
    }
}
