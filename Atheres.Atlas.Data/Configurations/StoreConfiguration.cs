using Atheres.Atlas.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Atheres.Atlas.Data.Configurations;

public class StoreConfiguration : IEntityTypeConfiguration<Store>
{
    public void Configure(EntityTypeBuilder<Store> builder)
    {
        builder.ToTable("Stores");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Customer).IsRequired().HasMaxLength(200);
        builder.Property(s => s.Name).IsRequired().HasMaxLength(200);
        builder.Property(s => s.Address).IsRequired().HasMaxLength(300);
        builder.Property(s => s.City).IsRequired().HasMaxLength(100);
        builder.Property(s => s.State).IsRequired().HasMaxLength(50);
        builder.Property(s => s.Zip).IsRequired().HasMaxLength(20);
        builder.Property(s => s.County).HasMaxLength(100);
        builder.Property(s => s.Region).HasMaxLength(200);
        builder.Property(s => s.LicenseNumber).HasMaxLength(100);
        builder.Property(s => s.Email).HasMaxLength(254);
        builder.Property(s => s.Phone).HasMaxLength(30);
        builder.Property(s => s.FormattedAddress).HasMaxLength(500);

        builder.HasIndex(s => s.CompanyId);
        builder.HasIndex(s => new { s.CompanyId, s.IsActive });
        builder.HasIndex(s => s.LicenseNumber);
        builder.HasIndex(s => s.Customer);

        builder.HasMany(s => s.Orders)
            .WithOne(o => o.Store)
            .HasForeignKey(o => o.StoreId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
