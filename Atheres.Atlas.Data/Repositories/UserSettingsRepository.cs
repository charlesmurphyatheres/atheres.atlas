using Atheres.Atlas.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Atheres.Atlas.Data.Repositories;

public class UserSettingsRepository : IUserSettingsRepository
{
    private readonly AtlasDbContext _db;

    public UserSettingsRepository(AtlasDbContext db) => _db = db;

    public async Task<UserRouteSettings?> GetByUserIdAsync(string userId, CancellationToken ct = default) =>
        await _db.UserRouteSettings.FirstOrDefaultAsync(s => s.UserId == userId, ct);

    public async Task UpsertAsync(UserRouteSettings settings, CancellationToken ct = default)
    {
        var existing = await GetByUserIdAsync(settings.UserId, ct);
        if (existing is null)
        {
            await _db.UserRouteSettings.AddAsync(settings, ct);
        }
        else
        {
            existing.ConfirmationDeadlineHours = settings.ConfirmationDeadlineHours;
            existing.WaitMinutesPerStop        = settings.WaitMinutesPerStop;
            existing.UpdatedAt                 = DateTime.UtcNow;
            _db.UserRouteSettings.Update(existing);
        }
    }

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
