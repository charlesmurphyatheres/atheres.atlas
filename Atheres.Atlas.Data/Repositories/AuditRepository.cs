using Atheres.Atlas.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Atheres.Atlas.Data.Repositories;

public class AuditRepository : IAuditRepository
{
    private readonly AtlasDbContext _db;

    public AuditRepository(AtlasDbContext db) => _db = db;

    public async Task<IReadOnlyList<AuditLog>> GetByOrderIdAsync(Guid orderId, CancellationToken ct = default) =>
        await _db.AuditLogs
            .Where(a => a.OrderId == orderId)
            .OrderByDescending(a => a.OccurredAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<AuditLog>> GetByDateRangeAsync(DateTime from, DateTime to, CancellationToken ct = default) =>
        await _db.AuditLogs
            .Where(a => a.OccurredAt >= from && a.OccurredAt <= to)
            .OrderByDescending(a => a.OccurredAt)
            .ToListAsync(ct);

    public async Task AddAsync(AuditLog log, CancellationToken ct = default) =>
        await _db.AuditLogs.AddAsync(log, ct);

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
