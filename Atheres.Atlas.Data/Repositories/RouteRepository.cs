using Atheres.Atlas.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Atheres.Atlas.Data.Repositories;

public class RouteRepository : IRouteRepository
{
    private readonly AtlasDbContext _db;

    public RouteRepository(AtlasDbContext db) => _db = db;

    public async Task<DeliveryRoute?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Routes
            .Include(r => r.Stops).ThenInclude(s => s.Order)
            .Include(r => r.Orders)
            .Include(r => r.Warehouse)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<DeliveryRoute>> GetByDateAsync(DateTime date, CancellationToken ct = default) =>
        await _db.Routes
            .Include(r => r.Stops).ThenInclude(s => s.Order)
            .Include(r => r.Warehouse)
            .Where(r => r.DeliveryDate.Date == date.Date)
            .ToListAsync(ct);

    public async Task AddAsync(DeliveryRoute route, CancellationToken ct = default) =>
        await _db.Routes.AddAsync(route, ct);

    public Task UpdateAsync(DeliveryRoute route, CancellationToken ct = default)
    {
        route.UpdatedAt = DateTime.UtcNow;
        _db.Routes.Update(route);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
