using Atheres.Atlas.Domain.Entities;
using Atheres.Atlas.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Atheres.Atlas.Data.Configurations;

public class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.StoreName).IsRequired().HasMaxLength(200);
        builder.Property(o => o.Address).IsRequired().HasMaxLength(300);
        builder.Property(o => o.City).IsRequired().HasMaxLength(100);
        builder.Property(o => o.State).IsRequired().HasMaxLength(50);
        builder.Property(o => o.Zip).IsRequired().HasMaxLength(20);
        builder.Property(o => o.County).HasMaxLength(100);
        builder.Property(o => o.LicenseNumber).HasMaxLength(100);
        builder.Property(o => o.District).HasMaxLength(100);
        builder.Property(o => o.Zone).HasMaxLength(100);
        builder.Property(o => o.Customer).HasMaxLength(200);
        builder.Property(o => o.SalesOrderNumber).HasMaxLength(100);
        builder.Property(o => o.PurchaseOrderNumber).HasMaxLength(100);
        builder.Property(o => o.Email).IsRequired().HasMaxLength(254);
        builder.Property(o => o.Phone).HasMaxLength(30);
        builder.Property(o => o.FormattedAddress).HasMaxLength(500);
        builder.Property(o => o.ConfirmationToken).HasMaxLength(64);
        builder.Property(o => o.ValidationErrors).HasMaxLength(2000);
        builder.Property(o => o.Notes).HasMaxLength(1000);

        builder.Property(o => o.Status)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.HasIndex(o => o.CompanyId);
        builder.HasIndex(o => new { o.CompanyId, o.Status });
        builder.HasIndex(o => o.Status);
        builder.HasIndex(o => o.OrderDate);
        builder.HasIndex(o => o.Email);
        builder.HasIndex(o => o.ConfirmationToken);
        builder.HasIndex(o => o.ExpectedDeliveryDate);

        builder.Property(o => o.IsAtHub).HasDefaultValue(false);

        builder.HasIndex(o => o.WarehouseId);

        builder.HasOne(o => o.Route)
            .WithMany(r => r.Orders)
            .HasForeignKey(o => o.RouteId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(o => o.Confirmations)
            .WithOne(c => c.Order)
            .HasForeignKey(c => c.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(o => o.AuditLogs)
            .WithOne(a => a.Order)
            .HasForeignKey(a => a.OrderId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
