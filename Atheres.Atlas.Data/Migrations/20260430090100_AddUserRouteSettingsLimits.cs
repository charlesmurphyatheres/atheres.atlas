using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

// Adds two operational knobs to per-truck route settings:
//   * MaxStopsPerRoute  - hard cap on deliveries per route (NOT NULL, default 12,
//                         server-enforced ceiling of 20)
//   * WaitMinutesPerStop - extra idle minutes the van waits at each delivery
//                          stop, applied only to in-route stops (NOT NULL, default 0)
// Both columns use SQL defaults so existing rows transition cleanly without a
// separate UPDATE pass.
[DbContext(typeof(AtlasDbContext))]
[Migration("20260430090100_AddUserRouteSettingsLimits")]
public partial class AddUserRouteSettingsLimits : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "MaxStopsPerRoute",
            table: "UserRouteSettings",
            type: "int",
            nullable: false,
            defaultValue: 12);

        migrationBuilder.AddColumn<int>(
            name: "WaitMinutesPerStop",
            table: "UserRouteSettings",
            type: "int",
            nullable: false,
            defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("MaxStopsPerRoute",   "UserRouteSettings");
        migrationBuilder.DropColumn("WaitMinutesPerStop", "UserRouteSettings");
    }
}
