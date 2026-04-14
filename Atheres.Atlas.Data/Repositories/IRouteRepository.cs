using Atheres.Atlas.Domain.Entities;

namespace Atheres.Atlas.Data.Repositories;

public interface IRouteRepository
{
    Task<DeliveryRoute?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<DeliveryRoute>> GetByDateAsync(DateTime date, CancellationToken ct = default);
    Task AddAsync(DeliveryRoute route, CancellationToken ct = default);
    Task UpdateAsync(DeliveryRoute route, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
