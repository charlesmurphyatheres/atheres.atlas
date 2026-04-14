using Atheres.Atlas.Domain.Entities;
using Atheres.Atlas.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Atheres.Atlas.Data.Repositories;

public class OrderRepository : IOrderRepository
{
    private readonly AtlasDbContext _db;

    public OrderRepository(AtlasDbContext db) => _db = db;

    public async Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Orders
            .Include(o => o.Confirmations)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task<IReadOnlyList<Order>> GetByStatusAsync(OrderStatus status, CancellationToken ct = default) =>
        await _db.Orders
            .Where(o => o.Status == status)
            .OrderBy(o => o.OrderDate)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Order>> GetByDeliveryDateAsync(DateTime date, CancellationToken ct = default) =>
        await _db.Orders
            .Where(o => o.ExpectedDeliveryDate.HasValue &&
                        o.ExpectedDeliveryDate.Value.Date == date.Date)
            .Include(o => o.Route)
            .OrderBy(o => o.StopSequence)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Order>> GetUnconfirmedPastDeadlineAsync(CancellationToken ct = default) =>
        await _db.Orders
            .Where(o => o.Status == OrderStatus.ConfirmationPending &&
                        o.ConfirmationDeadline.HasValue &&
                        o.ConfirmationDeadline.Value <= DateTime.UtcNow)
            .ToListAsync(ct);

    public async Task<Order?> GetByConfirmationTokenAsync(string token, CancellationToken ct = default) =>
        await _db.Orders
            .Include(o => o.Confirmations)
            .FirstOrDefaultAsync(o => o.ConfirmationToken == token, ct);

    public async Task AddAsync(Order order, CancellationToken ct = default) =>
        await _db.Orders.AddAsync(order, ct);

    public async Task AddRangeAsync(IEnumerable<Order> orders, CancellationToken ct = default) =>
        await _db.Orders.AddRangeAsync(orders, ct);

    public Task UpdateAsync(Order order, CancellationToken ct = default)
    {
        order.UpdatedAt = DateTime.UtcNow;
        _db.Orders.Update(order);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
