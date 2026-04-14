using Atheres.Atlas.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Atheres.Atlas.Data.Configurations;

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.EventName).IsRequired().HasMaxLength(100);
        builder.Property(a => a.Details).HasMaxLength(4000);
        builder.Property(a => a.PreviousStatus).HasMaxLength(50);
        builder.Property(a => a.NewStatus).HasMaxLength(50);
        builder.Property(a => a.ErrorMessage).HasMaxLength(2000);
        builder.Property(a => a.AgentName).HasMaxLength(100);

        builder.Property(a => a.EventType)
            .HasConversion<string>()
            .HasMaxLength(100);

        builder.HasIndex(a => a.OrderId);
        builder.HasIndex(a => a.OccurredAt);
        builder.HasIndex(a => a.EventType);
    }
}
