using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

// Flags the single hub per company that may receive warehouse pickups for
// sorting before per-zone delivery dispatch. Only transfer-site hubs can host
// the warehouse → hub-sort leg; every other hub is delivery-only.
//
// Backfill flips IsTransferSite to true for the Romeoville hub (HUB_ROM in the
// seed CSV, "Romeoville Transfer Site" in the DB) across every company that
// has one — that is the only transfer site in the current fleet.
[DbContext(typeof(AtlasDbContext))]
[Migration("20260525000001_AddHubIsTransferSite")]
public partial class AddHubIsTransferSite : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name:         "IsTransferSite",
            table:        "Hubs",
            nullable:     false,
            defaultValue: false);

        migrationBuilder.Sql(@"
UPDATE Hubs
SET    IsTransferSite = 1,
       UpdatedAt      = SYSUTCDATETIME()
WHERE  Name = N'Romeoville Transfer Site';");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("IsTransferSite", "Hubs");
    }
}
