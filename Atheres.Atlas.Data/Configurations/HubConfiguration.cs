using Atheres.Atlas.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Atheres.Atlas.Data.Configurations;

public class HubConfiguration : IEntityTypeConfiguration<Hub>
{
    public void Configure(EntityTypeBuilder<Hub> builder)
    {
        builder.ToTable("Hubs");
        builder.HasKey(h => h.Id);

        builder.Property(h => h.Name).IsRequired().HasMaxLength(200);
        builder.Property(h => h.Address).IsRequired().HasMaxLength(300);
        builder.Property(h => h.City).IsRequired().HasMaxLength(100);
        builder.Property(h => h.State).IsRequired().HasMaxLength(50);
        builder.Property(h => h.Zip).IsRequired().HasMaxLength(20);
        builder.Property(h => h.FormattedAddress).HasMaxLength(500);

        builder.HasIndex(h => h.CompanyId);
        builder.HasIndex(h => new { h.CompanyId, h.IsActive });

        builder.HasMany(h => h.Routes)
            .WithOne(r => r.Hub)
            .HasForeignKey(r => r.HubId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);
    }
}
