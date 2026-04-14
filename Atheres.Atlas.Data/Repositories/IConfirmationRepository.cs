using Atheres.Atlas.Domain.Entities;
using Atheres.Atlas.Domain.Enums;

namespace Atheres.Atlas.Data.Repositories;

public interface IConfirmationRepository
{
    Task<DeliveryConfirmation?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<DeliveryConfirmation?> GetByTokenAsync(string token, CancellationToken ct = default);
    Task<IReadOnlyList<DeliveryConfirmation>> GetPendingExpiredAsync(CancellationToken ct = default);
    Task AddAsync(DeliveryConfirmation confirmation, CancellationToken ct = default);
    Task UpdateAsync(DeliveryConfirmation confirmation, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
