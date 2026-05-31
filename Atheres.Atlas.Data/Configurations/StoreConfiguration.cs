using Atheres.Atlas.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Atheres.Atlas.Data.Configurations;

public class StoreConfiguration : IEntityTypeConfiguration<Store>
{
    public void Configure(EntityTypeBuilder<Store> builder)
    {
        builder.ToTable("Stores");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Customer).IsRequired().HasMaxLength(200);
        builder.Property(s => s.Name).IsRequired().HasMaxLength(200);
        builder.Property(s => s.Address).IsRequired().HasMaxLength(300);
        builder.Property(s => s.City).IsRequired().HasMaxLength(100);
        builder.Property(s => s.State).IsRequired().HasMaxLength(50);
        builder.Property(s => s.Zip).IsRequired().HasMaxLength(20);
        builder.Property(s => s.County).HasMaxLength(100);
        builder.Property(s => s.Region).HasMaxLength(200);
        builder.Property(s => s.LicenseNumber).HasMaxLength(100);
        builder.Property(s => s.Email).HasMaxLength(254);
        builder.Property(s => s.Phone).HasMaxLength(30);
        builder.Property(s => s.FormattedAddress).HasMaxLength(500);

        // Scheduling integration columns. Method is required (defaults to
        // None). Credentials are nullable — only the columns relevant to
        // the chosen Method are populated.
        builder.Property(s => s.SchedulingMethod).HasConversion<int>().IsRequired();
        builder.Property(s => s.BookingClientId).HasMaxLength(200);
        builder.Property(s => s.BookingClientSecret).HasMaxLength(500);
        builder.Property(s => s.BookingCalendarName).HasMaxLength(200);
        builder.Property(s => s.CalendlyAccessToken).HasMaxLength(500);
        builder.Property(s => s.CalendlyCalendarName).HasMaxLength(200);
        builder.Property(s => s.SchedulingEmailRecipients).HasMaxLength(2000);

        builder.HasIndex(s => s.LicenseNumber);
        builder.HasIndex(s => s.Customer);
        builder.HasIndex(s => s.ZoneId);

        builder.HasMany(s => s.Orders)
            .WithOne(o => o.Store)
            .HasForeignKey(o => o.StoreId)
            .OnDelete(DeleteBehavior.NoAction);

        // Many-to-many: a Store can belong to multiple Companies. The join
        // table is the canonical tenant-membership for stores — the global
        // query filter checks it on every Stores query. Columns are named
        // explicitly so the migration + raw SQL elsewhere stays readable.
        // The Companies-side FK is NO ACTION (not Cascade) on purpose. SQL
        // Server forbids multiple cascade paths into the same table, and
        // Companies already cascade-deletes into Hubs/Trucks/Routes, which
        // eventually reach Stores via Order.StoreId. Adding a second cascade
        // path Companies -> StoreCompanies -> (indirectly) Stores trips
        // error 1785. Companies are very rarely hard-deleted anyway —
        // they're soft-deactivated via IsActive — so leaving the cleanup
        // manual on the Companies side is fine. Deleting a Store still
        // cascades the join rows the obvious way.
        builder.HasMany(s => s.Companies)
            .WithMany(c => c.Stores)
            .UsingEntity<Dictionary<string, object>>(
                "StoreCompanies",
                j => j.HasOne<Company>().WithMany().HasForeignKey("CompanyId").OnDelete(DeleteBehavior.NoAction),
                j => j.HasOne<Store>().WithMany().HasForeignKey("StoreId").OnDelete(DeleteBehavior.Cascade),
                j =>
                {
                    j.ToTable("StoreCompanies");
                    j.HasKey("StoreId", "CompanyId");
                    j.HasIndex("CompanyId");
                });
    }
}
