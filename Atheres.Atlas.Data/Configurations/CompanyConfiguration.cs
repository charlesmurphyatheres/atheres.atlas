using Atheres.Atlas.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Atheres.Atlas.Data.Configurations;

public class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> builder)
    {
        builder.ToTable("Companies");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name).IsRequired().HasMaxLength(200);
        builder.Property(c => c.Slug).IsRequired().HasMaxLength(100);
        builder.Property(c => c.ContactEmail).HasMaxLength(254);
        builder.Property(c => c.ContactPhone).HasMaxLength(30);
        builder.Property(c => c.Timezone).IsRequired().HasMaxLength(60).HasDefaultValue("America/Chicago");

        builder.HasIndex(c => c.Slug).IsUnique();
        builder.HasIndex(c => c.IsActive);

        // Trucks owned by this company
        builder.HasMany(c => c.Trucks)
            .WithOne(t => t.Company)
            .HasForeignKey(t => t.CompanyId)
            .OnDelete(DeleteBehavior.Cascade);

        // Hubs
        builder.HasMany(c => c.Hubs)
            .WithOne(h => h.Company)
            .HasForeignKey(h => h.CompanyId)
            .OnDelete(DeleteBehavior.Cascade);

        // Stores and Warehouses are many-to-many — the relationship is
        // configured from the dependent side in StoreConfiguration and
        // WarehouseConfiguration (UsingEntity<Dictionary<...>>(...)), so we
        // don't restate it here. Listing the inverse `c.Stores` and
        // `c.Warehouses` collections on Company is enough.

        // Batches
        builder.HasMany(c => c.Batches)
            .WithOne(b => b.Company)
            .HasForeignKey(b => b.CompanyId)
            .OnDelete(DeleteBehavior.Cascade);

        // Orders — set null on company delete to preserve history
        builder.HasMany(c => c.Orders)
            .WithOne(o => o.Company)
            .HasForeignKey(o => o.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        // Routes
        builder.HasMany(c => c.Routes)
            .WithOne(r => r.Company)
            .HasForeignKey(r => r.CompanyId)
            .OnDelete(DeleteBehavior.Restrict);

        // Route settings
        builder.HasMany(c => c.RouteSettings)
            .WithOne(s => s.Company)
            .HasForeignKey(s => s.CompanyId)
            .OnDelete(DeleteBehavior.Cascade);

        // Audit logs
        builder.HasMany(c => c.AuditLogs)
            .WithOne()
            .HasForeignKey(a => a.CompanyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
