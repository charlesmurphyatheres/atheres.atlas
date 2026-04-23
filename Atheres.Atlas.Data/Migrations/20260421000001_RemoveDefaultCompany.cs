using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

// Secure Transport is the first onboarded tenant. The "Default Company"
// placeholder inserted by AddMultiTenancy is no longer needed — move any rows
// still attached to it onto Secure Transport and remove the row.
//
// Secure Transport is created here in case a fresh DB reaches this migration
// before the Auth Functions startup seed runs (migrations execute first).
[DbContext(typeof(AtlasDbContext))]
[Migration("20260421000001_RemoveDefaultCompany")]
public partial class RemoveDefaultCompany : Migration
{
    private const string DefaultId = "00000000-0000-0000-0000-000000000001";
    private const string SecureId  = "10000000-0000-0000-0000-000000000001";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($@"
IF NOT EXISTS (SELECT 1 FROM Companies WHERE Id = '{SecureId}')
BEGIN
    INSERT INTO Companies (Id, Name, Slug, ContactEmail, Timezone, IsActive, CreatedAt, UpdatedAt)
    VALUES ('{SecureId}', 'Secure Transport', 'secure-transport', 'secure@gmail.com', 'America/Chicago', 1, GETUTCDATE(), GETUTCDATE());
END
");

        // AspNetUsers lives in AtlasIdentityDbContext's schema, which is migrated
        // by the Auth Functions host — it may not exist yet when this migration runs.
        // The startup seed there creates users with the correct CompanyId directly,
        // so we don't need to reassign anything there.
        migrationBuilder.Sql($@"
IF EXISTS (SELECT 1 FROM Companies WHERE Id = '{DefaultId}')
BEGIN
    UPDATE Warehouses        SET CompanyId = '{SecureId}' WHERE CompanyId = '{DefaultId}';
    UPDATE Hubs              SET CompanyId = '{SecureId}' WHERE CompanyId = '{DefaultId}';
    UPDATE Stores            SET CompanyId = '{SecureId}' WHERE CompanyId = '{DefaultId}';
    UPDATE Trucks            SET CompanyId = '{SecureId}' WHERE CompanyId = '{DefaultId}';
    UPDATE Orders            SET CompanyId = '{SecureId}' WHERE CompanyId = '{DefaultId}';
    UPDATE OrderBatches      SET CompanyId = '{SecureId}' WHERE CompanyId = '{DefaultId}';
    UPDATE Routes            SET CompanyId = '{SecureId}' WHERE CompanyId = '{DefaultId}';
    UPDATE UserRouteSettings SET CompanyId = '{SecureId}' WHERE CompanyId = '{DefaultId}';
    UPDATE AuditLogs         SET CompanyId = '{SecureId}' WHERE CompanyId = '{DefaultId}';

    DELETE FROM Companies WHERE Id = '{DefaultId}';
END
");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($@"
IF NOT EXISTS (SELECT 1 FROM Companies WHERE Id = '{DefaultId}')
BEGIN
    INSERT INTO Companies (Id, Name, Slug, ContactEmail, Timezone, IsActive, CreatedAt, UpdatedAt)
    VALUES ('{DefaultId}', 'Default Company', 'default', NULL, 'America/Chicago', 1, GETUTCDATE(), GETUTCDATE());
END
");
    }
}
