using Atheres.Atlas.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Atheres.Atlas.Data.Configurations;

public class RouteStopConfiguration : IEntityTypeConfiguration<RouteStop>
{
    public void Configure(EntityTypeBuilder<RouteStop> builder)
    {
        builder.ToTable("RouteStops");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Address).IsRequired().HasMaxLength(500);
        builder.Property(s => s.StoreName).IsRequired().HasMaxLength(200);

        builder.HasIndex(s => new { s.RouteId, s.Sequence });
        builder.HasIndex(s => s.OrderId);
    }
}
