using Atheres.Atlas.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Atheres.Atlas.Data.Configurations;

public class OrderBatchConfiguration : IEntityTypeConfiguration<OrderBatch>
{
    public void Configure(EntityTypeBuilder<OrderBatch> builder)
    {
        builder.ToTable("OrderBatches");
        builder.HasKey(b => b.Id);

        builder.Property(b => b.Name).IsRequired().HasMaxLength(200);
        builder.Property(b => b.Status).HasConversion<string>().HasMaxLength(50);

        builder.HasIndex(b => b.CompanyId);
        builder.HasIndex(b => b.HubId);
        builder.HasIndex(b => b.WarehouseId);
        builder.HasIndex(b => b.Status);
        builder.HasIndex(b => new { b.CompanyId, b.Status });

        builder.HasOne(b => b.Hub)
            .WithMany()
            .HasForeignKey(b => b.HubId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(b => b.Warehouse)
            .WithMany()
            .HasForeignKey(b => b.WarehouseId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(b => b.Truck)
            .WithMany()
            .HasForeignKey(b => b.TruckId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(b => b.Route)
            .WithMany()
            .HasForeignKey(b => b.RouteId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasMany(b => b.Orders)
            .WithOne(o => o.Batch)
            .HasForeignKey(o => o.BatchId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
