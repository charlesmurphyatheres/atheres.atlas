using Atheres.Atlas.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Atheres.Atlas.Data.Configurations;

public class ZoneConfiguration : IEntityTypeConfiguration<Zone>
{
    public void Configure(EntityTypeBuilder<Zone> builder)
    {
        builder.ToTable("Zones");
        builder.HasKey(z => z.Id);

        builder.Property(z => z.Code).IsRequired().HasMaxLength(20);

        builder.HasIndex(z => z.CompanyId);
        // A zone code can repeat across districts (e.g. "1.0" exists under both
        // Bloomington and West Central in zones.csv); uniqueness is scoped to
        // the parent district within a tenant.
        builder.HasIndex(z => new { z.CompanyId, z.DistrictId, z.Code }).IsUnique();

        builder.HasMany(z => z.Stores)
            .WithOne(s => s.Zone)
            .HasForeignKey(s => s.ZoneId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
