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
            existing.StartAddress = settings.StartAddress;
            existing.StartCity = settings.StartCity;
            existing.StartState = settings.StartState;
            existing.StartZip = settings.StartZip;
            existing.StartLatitude = settings.StartLatitude;
            existing.StartLongitude = settings.StartLongitude;
            existing.EndAddress = settings.EndAddress;
            existing.EndCity = settings.EndCity;
            existing.EndState = settings.EndState;
            existing.EndZip = settings.EndZip;
            existing.EndLatitude = settings.EndLatitude;
            existing.EndLongitude = settings.EndLongitude;
            existing.DeliveryWindowStart = settings.DeliveryWindowStart;
            existing.DeliveryWindowEnd = settings.DeliveryWindowEnd;
            existing.ConfirmationDeadlineHours = settings.ConfirmationDeadlineHours;
            existing.UpdatedAt = DateTime.UtcNow;
            _db.UserRouteSettings.Update(existing);
        }
    }

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
