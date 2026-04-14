using Atheres.Atlas.Domain.Entities;
using Atheres.Atlas.Domain.Enums;

namespace Atheres.Atlas.Data.Repositories;

public interface IOrderRepository
{
    Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Order>> GetByStatusAsync(OrderStatus status, CancellationToken ct = default);
    Task<IReadOnlyList<Order>> GetByDeliveryDateAsync(DateTime date, CancellationToken ct = default);
    Task<IReadOnlyList<Order>> GetUnconfirmedPastDeadlineAsync(CancellationToken ct = default);
    Task<Order?> GetByConfirmationTokenAsync(string token, CancellationToken ct = default);
    Task AddAsync(Order order, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<Order> orders, CancellationToken ct = default);
    Task UpdateAsync(Order order, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
