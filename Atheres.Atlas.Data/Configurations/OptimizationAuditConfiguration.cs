using Atheres.Atlas.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Atheres.Atlas.Data.Configurations;

public class OptimizationAuditConfiguration : IEntityTypeConfiguration<OptimizationAudit>
{
    public void Configure(EntityTypeBuilder<OptimizationAudit> builder)
    {
        builder.ToTable("OptimizationAudits");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.TriggeredBy).HasMaxLength(254);
        builder.Property(a => a.Trigger).HasMaxLength(120).IsRequired();
        builder.Property(a => a.Summary).HasMaxLength(500).IsRequired();
        // LogText is unbounded — nvarchar(max) — because the audit body
        // grows linearly with the number of orders and routes in a run.
        builder.Property(a => a.LogText).HasColumnType("nvarchar(max)").IsRequired();

        builder.HasIndex(a => a.CompanyId);
        builder.HasIndex(a => new { a.CompanyId, a.CreatedAt });

        builder.HasOne(a => a.Company)
            .WithMany()
            .HasForeignKey(a => a.CompanyId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
