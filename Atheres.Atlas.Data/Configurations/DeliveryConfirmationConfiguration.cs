using Atheres.Atlas.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Atheres.Atlas.Data.Configurations;

public class DeliveryConfirmationConfiguration : IEntityTypeConfiguration<DeliveryConfirmation>
{
    public void Configure(EntityTypeBuilder<DeliveryConfirmation> builder)
    {
        builder.ToTable("Confirmations");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Token).IsRequired().HasMaxLength(64);
        builder.Property(c => c.EmailSentTo).HasMaxLength(254);
        builder.Property(c => c.SmsSentTo).HasMaxLength(30);
        builder.Property(c => c.ConfirmedBy).HasMaxLength(20);

        builder.Property(c => c.Status)
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.HasIndex(c => c.Token).IsUnique();
        builder.HasIndex(c => c.OrderId);
        builder.HasIndex(c => c.Status);
        builder.HasIndex(c => c.ExpiresAt);
    }
}
