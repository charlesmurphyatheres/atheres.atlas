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

        builder.HasIndex(s => s.CompanyId);
        builder.HasIndex(s => new { s.CompanyId, s.UserId }).IsUnique();
    }
}
