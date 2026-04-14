using Atheres.Atlas.Domain.Entities;

namespace Atheres.Atlas.Data.Repositories;

public interface IUserSettingsRepository
{
    Task<UserRouteSettings?> GetByUserIdAsync(string userId, CancellationToken ct = default);
    Task UpsertAsync(UserRouteSettings settings, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
