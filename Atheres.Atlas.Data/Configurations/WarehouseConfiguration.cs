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

        builder.HasIndex(w => w.CompanyId);
        builder.HasIndex(w => new { w.CompanyId, w.IsActive });
        builder.HasIndex(w => w.LicenseNumber);

        builder.HasMany(w => w.Orders)
            .WithOne(o => o.Warehouse)
            .HasForeignKey(o => o.WarehouseId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
