using Atheres.Atlas.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Atheres.Atlas.Data.Configurations;

public class UserRouteSettingsConfiguration : IEntityTypeConfiguration<UserRouteSettings>
{
    public void Configure(EntityTypeBuilder<UserRouteSettings> builder)
    {
        builder.ToTable("UserRouteSettings");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.UserId).IsRequired().HasMaxLength(100);
        builder.Property(s => s.StartAddress).IsRequired().HasMaxLength(300);
        builder.Property(s => s.StartCity).HasMaxLength(100);
        builder.Property(s => s.StartState).HasMaxLength(50);
        builder.Property(s => s.StartZip).HasMaxLength(20);
        builder.Property(s => s.EndAddress).IsRequired().HasMaxLength(300);
        builder.Property(s => s.EndCity).HasMaxLength(100);
        builder.Property(s => s.EndState).HasMaxLength(50);
        builder.Property(s => s.EndZip).HasMaxLength(20);

        builder.HasIndex(s => s.CompanyId);
        builder.HasIndex(s => new { s.CompanyId, s.UserId }).IsUnique();
    }
}
