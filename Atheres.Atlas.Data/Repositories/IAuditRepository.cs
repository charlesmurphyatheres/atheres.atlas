using Atheres.Atlas.Domain.Entities;
using Atheres.Atlas.Domain.Enums;

namespace Atheres.Atlas.Data.Repositories;

public interface IAuditRepository
{
    Task<IReadOnlyList<AuditLog>> GetByOrderIdAsync(Guid orderId, CancellationToken ct = default);
    Task<IReadOnlyList<AuditLog>> GetByDateRangeAsync(DateTime from, DateTime to, CancellationToken ct = default);
    Task AddAsync(AuditLog log, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
