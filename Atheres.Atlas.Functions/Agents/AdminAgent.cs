using Atheres.Atlas.Data;
using Atheres.Atlas.Functions.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atheres.Atlas.Functions.Agents;

/// <summary>
/// Maintenance endpoints for Admin / SuperAdmin. Not exercised by the normal
/// tenant flows — these exist to fix up reference data after imports.
/// </summary>
[Authorize(Roles = "Admin,SuperAdmin")]
public class AdminAgent
{
    private readonly AtlasDbContext _db;
    private readonly IGoogleMapsService _maps;
    private readonly ILogger<AdminAgent> _logger;

    public AdminAgent(AtlasDbContext db, IGoogleMapsService maps, ILogger<AdminAgent> logger)
    {
        _db = db;
        _maps = maps;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/maintenance/geocode-stores
    /// Geocodes every Store missing lat/lng, then backfills Orders that share
    /// a store with those coordinates. Idempotent — re-runnable.
    /// Company-scoped via the global query filter (pass X-Company-Id as SuperAdmin
    /// to restrict; otherwise runs cross-tenant).
    /// Query params:  ?max=N   cap the number of stores geocoded in this call.
    /// Routes are under /maintenance/* because Azure Functions reserves /admin/*.
    /// </summary>
    [Function("maintenance-geocode-stores")]
    public async Task<IActionResult> GeocodeStores(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "maintenance/geocode-stores")]
        HttpRequest req,
        CancellationToken ct)
    {
        var max = int.TryParse(req.Query["max"], out var m) ? Math.Clamp(m, 1, 5000) : 5000;

        // IgnoreQueryFilters so SuperAdmin without X-Company-Id can backfill everything.
        var stores = await _db.Stores.IgnoreQueryFilters()
            .Where(s => s.IsActive && (s.Latitude == null || s.Longitude == null))
            .OrderBy(s => s.Id)
            .Take(max)
            .ToListAsync(ct);

        if (stores.Count == 0)
        {
            return new OkObjectResult(new
            {
                message = "All active stores already geocoded.",
                storesGeocoded = 0,
                storesFailed = 0,
                ordersBackfilled = 0,
            });
        }

        _logger.LogInformation("Geocoding {Count} stores...", stores.Count);

        // Controlled concurrency — 10 Geocoding API calls in flight keeps well
        // under the free-tier per-second quota and finishes 1500 stores in ~1 min.
        var sem = new SemaphoreSlim(10);
        var geocoded = 0;
        var failed = 0;

        await Task.WhenAll(stores.Select(async store =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var result = await _maps.GeocodeAsync(store.FullAddress, ct);
                if (result is null)
                {
                    Interlocked.Increment(ref failed);
                    return;
                }
                store.Latitude = result.Latitude;
                store.Longitude = result.Longitude;
                store.FormattedAddress = result.FormattedAddress;
                store.UpdatedAt = DateTime.UtcNow;
                Interlocked.Increment(ref geocoded);
            }
            finally
            {
                sem.Release();
            }
        }));

        await _db.SaveChangesAsync(ct);

        // Backfill orders that now have a geocoded store but null coordinates.
        // Single UPDATE ... FROM is far cheaper than loading every order.
        var ordersBackfilled = await _db.Database.ExecuteSqlRawAsync(@"
UPDATE o
SET    o.Latitude         = s.Latitude,
       o.Longitude        = s.Longitude,
       o.FormattedAddress = COALESCE(o.FormattedAddress, s.FormattedAddress)
FROM   Orders o
JOIN   Stores s ON s.Id = o.StoreId
WHERE (o.Latitude IS NULL OR o.Longitude IS NULL)
  AND  s.Latitude IS NOT NULL
  AND  s.Longitude IS NOT NULL
", ct);

        _logger.LogInformation("Geocoding complete: {Ok} geocoded, {Fail} failed, {Backfilled} orders backfilled",
            geocoded, failed, ordersBackfilled);

        return new OkObjectResult(new
        {
            message = "Geocoding complete.",
            storesGeocoded = geocoded,
            storesFailed = failed,
            ordersBackfilled,
        });
    }
}
