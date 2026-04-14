using Atheres.Atlas.Domain.Entities;
using Atheres.Atlas.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Atheres.Atlas.Data.Repositories;

public class ConfirmationRepository : IConfirmationRepository
{
    private readonly AtlasDbContext _db;

    public ConfirmationRepository(AtlasDbContext db) => _db = db;

    public async Task<DeliveryConfirmation?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Confirmations.FindAsync(new object[] { id }, ct);

    public async Task<DeliveryConfirmation?> GetByTokenAsync(string token, CancellationToken ct = default) =>
        await _db.Confirmations
            .Include(c => c.Order)
            .FirstOrDefaultAsync(c => c.Token == token, ct);

    public async Task<IReadOnlyList<DeliveryConfirmation>> GetPendingExpiredAsync(CancellationToken ct = default) =>
        await _db.Confirmations
            .Where(c => (c.Status == ConfirmationStatus.Pending ||
                         c.Status == ConfirmationStatus.SentEmail ||
                         c.Status == ConfirmationStatus.SentSms) &&
                        c.ExpiresAt <= DateTime.UtcNow)
            .Include(c => c.Order)
            .ToListAsync(ct);

    public async Task AddAsync(DeliveryConfirmation confirmation, CancellationToken ct = default) =>
        await _db.Confirmations.AddAsync(confirmation, ct);

    public Task UpdateAsync(DeliveryConfirmation confirmation, CancellationToken ct = default)
    {
        _db.Confirmations.Update(confirmation);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
