using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

// Strip Demo Company out of the Stores ↔ Companies join. Every store in
// the database belongs to Secure Transport only; rogue rows in
// StoreCompanies (most likely from an `import-data.ps1 -CompanyId
// 20000000-...` run, or hand-rolled SQL during early seeding) caused Demo
// Company admins to see Secure Transport's store list.
//
// The schema itself stays many-to-many — a store may legitimately serve
// multiple carriers — so this is a pure data fix, not a relationship
// change. The Demo Company row itself is left alone (other seed data
// references it).
//
// Down is a no-op on purpose: we never want to re-introduce join rows
// that shouldn't have existed.
[DbContext(typeof(AtlasDbContext))]
[Migration("20260531000002_RemoveDemoCompanyStoreJoins")]
public partial class RemoveDemoCompanyStoreJoins : Migration
{
    // GUID matches the seed in Atheres.Atlas.Auth.Functions/Program.cs.
    private const string DemoCompanyId = "20000000-0000-0000-0000-000000000001";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($@"
DELETE FROM StoreCompanies
WHERE  CompanyId = '{DemoCompanyId}';");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Intentionally empty — recreating the deleted rows would re-introduce
        // the bug. A real demo-store backfill should be a separate forward
        // migration that joins specific stores by license number.
    }
}
