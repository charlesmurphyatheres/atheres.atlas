using Atheres.Atlas.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Atheres.Atlas.Data.Configurations;

public class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Sku).IsRequired().HasMaxLength(100);
        builder.Property(p => p.Name).IsRequired().HasMaxLength(300);
        builder.Property(p => p.Category).HasMaxLength(200);
        builder.Property(p => p.Description).HasMaxLength(2000);

        builder.HasIndex(p => p.Sku).IsUnique();
    }
}
