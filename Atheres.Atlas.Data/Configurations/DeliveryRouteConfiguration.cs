using Atheres.Atlas.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Atheres.Atlas.Data.Configurations;

public class DeliveryRouteConfiguration : IEntityTypeConfiguration<DeliveryRoute>
{
    public void Configure(EntityTypeBuilder<DeliveryRoute> builder)
    {
        builder.ToTable("Routes");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.StartAddress).IsRequired().HasMaxLength(500);
        builder.Property(r => r.EndAddress).IsRequired().HasMaxLength(500);
        builder.Property(r => r.OptimizedWaypointOrder).HasMaxLength(2000);
        builder.Property(r => r.OverviewPolyline).HasMaxLength(4000);

        builder.HasIndex(r => r.CompanyId);
        builder.HasIndex(r => r.TruckId);
        builder.HasIndex(r => r.WarehouseId);
        builder.HasIndex(r => new { r.CompanyId, r.DeliveryDate });
        builder.HasIndex(r => r.DeliveryDate);

        builder.HasMany(r => r.Stops)
            .WithOne(s => s.Route)
            .HasForeignKey(s => s.RouteId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
