using Atheres.Atlas.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Atheres.Atlas.Data.Configurations;

public class DistrictConfiguration : IEntityTypeConfiguration<District>
{
    public void Configure(EntityTypeBuilder<District> builder)
    {
        builder.ToTable("Districts");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Name).IsRequired().HasMaxLength(200);

        builder.HasIndex(d => d.CompanyId);
        builder.HasIndex(d => new { d.CompanyId, d.Number }).IsUnique();

        builder.HasMany(d => d.Zones)
            .WithOne(z => z.District)
            .HasForeignKey(z => z.DistrictId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
