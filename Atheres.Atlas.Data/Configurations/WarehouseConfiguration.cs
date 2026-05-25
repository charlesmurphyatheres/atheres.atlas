using Atheres.Atlas.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Atheres.Atlas.Data.Configurations;

public class WarehouseConfiguration : IEntityTypeConfiguration<Warehouse>
{
    public void Configure(EntityTypeBuilder<Warehouse> builder)
    {
        builder.ToTable("Warehouses");
        builder.HasKey(w => w.Id);

        builder.Property(w => w.BusinessName).IsRequired().HasMaxLength(200);
        builder.Property(w => w.AlternateName).HasMaxLength(200);
        builder.Property(w => w.Address).IsRequired().HasMaxLength(300);
        builder.Property(w => w.City).IsRequired().HasMaxLength(100);
        builder.Property(w => w.State).IsRequired().HasMaxLength(50);
        builder.Property(w => w.Zip).IsRequired().HasMaxLength(20);
        builder.Property(w => w.LicenseNumber).HasMaxLength(100);
        builder.Property(w => w.LegacyLicenseNumber).HasMaxLength(100);
        builder.Property(w => w.FormattedAddress).HasMaxLength(500);

        builder.HasIndex(w => w.LicenseNumber);

        builder.HasMany(w => w.Orders)
            .WithOne(o => o.Warehouse)
            .HasForeignKey(o => o.WarehouseId)
            .OnDelete(DeleteBehavior.NoAction);

        // Many-to-many: a Warehouse can belong to multiple Companies. The
        // join table is the canonical tenant-membership for warehouses.
        // See StoreConfiguration for the rationale — the Companies-side FK
        // must be NO ACTION because Companies already cascade-delete into
        // Hubs/Trucks/Routes and SQL Server forbids multiple cascade paths
        // into a table reachable from Warehouse (via Order.WarehouseId).
        // Companies are soft-deactivated, not hard-deleted, so manual
        // cleanup on the Companies side is acceptable.
        builder.HasMany(w => w.Companies)
            .WithMany(c => c.Warehouses)
            .UsingEntity<Dictionary<string, object>>(
                "WarehouseCompanies",
                j => j.HasOne<Company>().WithMany().HasForeignKey("CompanyId").OnDelete(DeleteBehavior.NoAction),
                j => j.HasOne<Warehouse>().WithMany().HasForeignKey("WarehouseId").OnDelete(DeleteBehavior.Cascade),
                j =>
                {
                    j.ToTable("WarehouseCompanies");
                    j.HasKey("WarehouseId", "CompanyId");
                    j.HasIndex("CompanyId");
                });
    }
}
