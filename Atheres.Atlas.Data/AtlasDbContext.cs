using Atheres.Atlas.Data.Services;
using Atheres.Atlas.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Atheres.Atlas.Data;

public class AtlasDbContext : DbContext
{
    private readonly Guid? _companyId;

    public AtlasDbContext(
        DbContextOptions<AtlasDbContext> options,
        ICompanyContext? companyContext = null)
        : base(options)
    {
        _companyId = companyContext?.CompanyId;
    }

    // ---- Entity sets -------------------------------------------
    public DbSet<Company>          Companies       => Set<Company>();
    public DbSet<Truck>            Trucks          => Set<Truck>();
    public DbSet<Order>            Orders          => Set<Order>();
    public DbSet<DeliveryRoute>    Routes          => Set<DeliveryRoute>();
    public DbSet<RouteStop>        RouteStops      => Set<RouteStop>();
    public DbSet<DeliveryConfirmation> Confirmations => Set<DeliveryConfirmation>();
    public DbSet<AuditLog>         AuditLogs       => Set<AuditLog>();
    public DbSet<UserRouteSettings> UserRouteSettings => Set<UserRouteSettings>();
    public DbSet<Store>            Stores            => Set<Store>();
    public DbSet<Hub>              Hubs              => Set<Hub>();
    public DbSet<OrderBatch>       OrderBatches      => Set<OrderBatch>();
    public DbSet<Warehouse>        Warehouses        => Set<Warehouse>();
    public DbSet<District>         Districts         => Set<District>();
    public DbSet<Zone>             Zones             => Set<Zone>();
    public DbSet<OptimizationAudit> OptimizationAudits => Set<OptimizationAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AtlasDbContext).Assembly);

        // ---- Global query filters (tenant isolation) ---------------
        // When _companyId is null (system/timer context), no filter is applied.
        // When _companyId is set, only rows for that company are visible.
        modelBuilder.Entity<Order>()
            .HasQueryFilter(o => _companyId == null || o.CompanyId == _companyId);

        modelBuilder.Entity<DeliveryRoute>()
            .HasQueryFilter(r => _companyId == null || r.CompanyId == _companyId);

        modelBuilder.Entity<AuditLog>()
            .HasQueryFilter(a => _companyId == null || a.CompanyId == _companyId);

        modelBuilder.Entity<UserRouteSettings>()
            .HasQueryFilter(s => _companyId == null || s.CompanyId == _companyId);

        modelBuilder.Entity<Truck>()
            .HasQueryFilter(t => _companyId == null || t.CompanyId == _companyId);

        // Stores and Warehouses use many-to-many tenant membership (Stores.
        // Companies / Warehouses.Companies via StoreCompanies / WarehouseCompanies
        // join tables), so the tenant filter has to test set membership rather
        // than a single FK equality. When _companyId is null (system/timer
        // context), no filter is applied — same as before.
        modelBuilder.Entity<Store>()
            .HasQueryFilter(s => _companyId == null || s.Companies.Any(c => c.Id == _companyId));

        modelBuilder.Entity<Hub>()
            .HasQueryFilter(h => _companyId == null || h.CompanyId == _companyId);

        modelBuilder.Entity<OrderBatch>()
            .HasQueryFilter(b => _companyId == null || b.CompanyId == _companyId);

        modelBuilder.Entity<Warehouse>()
            .HasQueryFilter(w => _companyId == null || w.Companies.Any(c => c.Id == _companyId));

        modelBuilder.Entity<District>()
            .HasQueryFilter(d => _companyId == null || d.CompanyId == _companyId);

        modelBuilder.Entity<Zone>()
            .HasQueryFilter(z => _companyId == null || z.CompanyId == _companyId);

        modelBuilder.Entity<OptimizationAudit>()
            .HasQueryFilter(a => _companyId == null || a.CompanyId == _companyId);

        // RouteStops and Confirmations are accessed through their parent
        // (Route and Order respectively) — no direct filter needed.
    }
}
