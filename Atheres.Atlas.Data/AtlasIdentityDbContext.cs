using Atheres.Atlas.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Atheres.Atlas.Data;

/// <summary>
/// Separate Identity DbContext so ASP.NET Core Identity tables and their
/// migrations are kept entirely apart from the main AtlasDbContext.
///
/// Migration history: __IdentityMigrationsHistory  (not __EFMigrationsHistory)
/// Design-time CLI:
///   dotnet ef migrations add InitialIdentity --context AtlasIdentityDbContext \
///     --project Atheres.Atlas.Data \
///     --startup-project Atheres.Atlas.Auth.Functions \
///     --output-dir Migrations/Identity
/// </summary>
public class AtlasIdentityDbContext
    : IdentityDbContext<ApplicationUser, IdentityRole, string>
{
    public AtlasIdentityDbContext(DbContextOptions<AtlasIdentityDbContext> options)
        : base(options) { }

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // ---- RefreshTokens -----------------------------------------------
        builder.Entity<RefreshToken>(e =>
        {
            e.ToTable("RefreshTokens");
            e.HasKey(r => r.Id);
            e.Property(r => r.Token).HasMaxLength(512).IsRequired();
            e.HasIndex(r => r.Token).IsUnique();
            e.Property(r => r.UserId).IsRequired();

            e.HasOne(r => r.User)
             .WithMany(u => u.RefreshTokens)
             .HasForeignKey(r => r.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ---- ApplicationUser extras --------------------------------------
        builder.Entity<ApplicationUser>(e =>
        {
            e.Property(u => u.FirstName).HasMaxLength(100).IsRequired();
            e.Property(u => u.LastName).HasMaxLength(100).IsRequired();
        });
    }

}
