using Atheres.Atlas.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Atheres.Atlas.Data.Configurations;

public class TruckConfiguration : IEntityTypeConfiguration<Truck>
{
    public void Configure(EntityTypeBuilder<Truck> builder)
    {
        builder.ToTable("Trucks");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name).IsRequired().HasMaxLength(100);
        builder.Property(t => t.LicensePlate).HasMaxLength(50);
        builder.Property(t => t.AssignedDriverId).HasMaxLength(450);

        builder.HasIndex(t => t.CompanyId);
        builder.HasIndex(t => t.HubId);
        builder.HasIndex(t => t.AssignedDriverId);
        builder.HasIndex(t => t.LicensePlate);

        // Hub where this truck is based
        builder.HasOne(t => t.Hub)
            .WithMany()
            .HasForeignKey(t => t.HubId)
            .OnDelete(DeleteBehavior.NoAction);

        // RouteSettings linked to this truck (optional)
        builder.HasOne(t => t.RouteSettings)
            .WithMany()
            .HasForeignKey(t => t.RouteSettingsId)
            .OnDelete(DeleteBehavior.SetNull);

        // Routes belonging to this truck
        builder.HasMany(t => t.Routes)
            .WithOne(r => r.Truck)
            .HasForeignKey(r => r.TruckId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
