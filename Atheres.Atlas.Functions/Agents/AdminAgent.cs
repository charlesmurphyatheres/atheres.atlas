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

    /// <summary>
    /// POST /api/maintenance/geocode-hubs
    /// Geocodes every Hub missing lat/lng and persists the result. Same
    /// shape as <see cref="GeocodeStores"/> minus the per-order backfill
    /// (Orders don't reference Hubs directly).
    /// </summary>
    [Function("maintenance-geocode-hubs")]
    public async Task<IActionResult> GeocodeHubs(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "maintenance/geocode-hubs")]
        HttpRequest req,
        CancellationToken ct)
    {
        var max = int.TryParse(req.Query["max"], out var m) ? Math.Clamp(m, 1, 5000) : 5000;

        var hubs = await _db.Hubs.IgnoreQueryFilters()
            .Where(h => h.IsActive && (h.Latitude == null || h.Longitude == null))
            .OrderBy(h => h.Id)
            .Take(max)
            .ToListAsync(ct);

        if (hubs.Count == 0)
        {
            return new OkObjectResult(new
            {
                message      = "All active hubs already geocoded.",
                hubsGeocoded = 0,
                hubsFailed   = 0,
            });
        }

        _logger.LogInformation("Geocoding {Count} hubs...", hubs.Count);

        var sem      = new SemaphoreSlim(10);
        var geocoded = 0;
        var failed   = 0;

        await Task.WhenAll(hubs.Select(async hub =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var result = await _maps.GeocodeAsync(hub.FullAddress, ct);
                if (result is null) { Interlocked.Increment(ref failed); return; }
                hub.Latitude         = result.Latitude;
                hub.Longitude        = result.Longitude;
                hub.FormattedAddress = result.FormattedAddress;
                hub.UpdatedAt        = DateTime.UtcNow;
                Interlocked.Increment(ref geocoded);
            }
            finally { sem.Release(); }
        }));

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Hub geocoding complete: {Ok} geocoded, {Fail} failed", geocoded, failed);
        return new OkObjectResult(new
        {
            message      = "Hub geocoding complete.",
            hubsGeocoded = geocoded,
            hubsFailed   = failed,
        });
    }

    /// <summary>
    /// POST /api/maintenance/geocode-warehouses
    /// Geocodes every Warehouse missing lat/lng and persists the result.
    /// Mirror of <see cref="GeocodeHubs"/>.
    /// </summary>
    [Function("maintenance-geocode-warehouses")]
    public async Task<IActionResult> GeocodeWarehouses(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "maintenance/geocode-warehouses")]
        HttpRequest req,
        CancellationToken ct)
    {
        var max = int.TryParse(req.Query["max"], out var m) ? Math.Clamp(m, 1, 5000) : 5000;

        var warehouses = await _db.Warehouses.IgnoreQueryFilters()
            .Where(w => w.IsActive && (w.Latitude == null || w.Longitude == null))
            .OrderBy(w => w.Id)
            .Take(max)
            .ToListAsync(ct);

        if (warehouses.Count == 0)
        {
            return new OkObjectResult(new
            {
                message            = "All active warehouses already geocoded.",
                warehousesGeocoded = 0,
                warehousesFailed   = 0,
            });
        }

        _logger.LogInformation("Geocoding {Count} warehouses...", warehouses.Count);

        var sem      = new SemaphoreSlim(10);
        var geocoded = 0;
        var failed   = 0;

        await Task.WhenAll(warehouses.Select(async wh =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var result = await _maps.GeocodeAsync(wh.FullAddress, ct);
                if (result is null) { Interlocked.Increment(ref failed); return; }
                wh.Latitude         = result.Latitude;
                wh.Longitude        = result.Longitude;
                wh.FormattedAddress = result.FormattedAddress;
                wh.UpdatedAt        = DateTime.UtcNow;
                Interlocked.Increment(ref geocoded);
            }
            finally { sem.Release(); }
        }));

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Warehouse geocoding complete: {Ok} geocoded, {Fail} failed", geocoded, failed);
        return new OkObjectResult(new
        {
            message            = "Warehouse geocoding complete.",
            warehousesGeocoded = geocoded,
            warehousesFailed   = failed,
        });
    }
}
