using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

// Adds the IsChicagoLand flag to Hubs, Warehouses, and Districts. Drives the
// non-ChicagoLand bypass on the routing side: a pickup at a warehouse where
// IsChicagoLand=0 whose orders all resolve (via Store → Zone → District) to
// IsChicagoLand=0 may skip the HUB_ROM transfer site entirely.
//
// Backfills are seed-data driven:
//   * Districts.IsChicagoLand   — true for District 5 (Chicago-Naperville-Elgin).
//   * Hubs.IsChicagoLand        — true for the Romeoville and Rolling Meadows
//                                 hubs (both Cook/Will county sites in the MSA).
//                                 Pekin and Springfield stay false.
//   * Warehouses.IsChicagoLand  — true for warehouses whose Zip falls inside the
//                                 Chicago-Naperville-Elgin MSA (Chicago city
//                                 ZIPs plus the suburban ZIPs that appear in
//                                 data/warehouses.csv). The full ZIP set lives
//                                 in the script too so it can be re-applied
//                                 on subsequent imports.
[DbContext(typeof(AtlasDbContext))]
[Migration("20260525000002_AddIsChicagoLand")]
public partial class AddIsChicagoLand : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name:         "IsChicagoLand",
            table:        "Hubs",
            nullable:     false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name:         "IsChicagoLand",
            table:        "Warehouses",
            nullable:     false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name:         "IsChicagoLand",
            table:        "Districts",
            nullable:     false,
            defaultValue: false);

        migrationBuilder.Sql(@"
UPDATE Districts
SET    IsChicagoLand = 1,
       UpdatedAt     = SYSUTCDATETIME()
WHERE  Number = 5;");

        migrationBuilder.Sql(@"
UPDATE Hubs
SET    IsChicagoLand = 1,
       UpdatedAt     = SYSUTCDATETIME()
WHERE  Name IN (N'Romeoville Transfer Site', N'Rolling Meadows');");

        // Chicago-Naperville-Elgin MSA ZIPs: Chicago city proper plus the
        // suburban ZIPs that currently appear in data/warehouses.csv. Kept
        // explicit (not derived from a prefix) so future warehouses outside
        // this set must be flagged deliberately by an administrator.
        migrationBuilder.Sql(@"
UPDATE Warehouses
SET    IsChicagoLand = 1,
       UpdatedAt     = SYSUTCDATETIME()
WHERE  Zip IN (
    -- Chicago city
    N'60601', N'60602', N'60603', N'60604', N'60605', N'60606', N'60607', N'60608', N'60609', N'60610',
    N'60611', N'60612', N'60613', N'60614', N'60615', N'60616', N'60617', N'60618', N'60619', N'60620',
    N'60621', N'60622', N'60623', N'60624', N'60625', N'60626', N'60628', N'60629', N'60630', N'60631',
    N'60632', N'60633', N'60634', N'60636', N'60637', N'60638', N'60639', N'60640', N'60641', N'60642',
    N'60643', N'60644', N'60645', N'60646', N'60647', N'60649', N'60651', N'60652', N'60653', N'60654',
    N'60655', N'60656', N'60657', N'60659', N'60660', N'60661', N'60664', N'60666', N'60668', N'60669',
    N'60670', N'60673', N'60674', N'60675', N'60677', N'60678', N'60680', N'60681', N'60682', N'60684',
    N'60685', N'60686', N'60687', N'60688', N'60689', N'60690', N'60691', N'60693', N'60694', N'60695',
    N'60696', N'60697', N'60699', N'60701', N'60706', N'60707', N'60803', N'60804', N'60805', N'60827',
    -- MSA suburbs present in warehouses.csv
    N'60007', N'60008', N'60018', N'60077', N'60098', N'60123', N'60131',
    N'60152', N'60155', N'60171', N'60443', N'60449', N'60450', N'60468', N'60471'
);");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("IsChicagoLand", "Districts");
        migrationBuilder.DropColumn("IsChicagoLand", "Warehouses");
        migrationBuilder.DropColumn("IsChicagoLand", "Hubs");
    }
}
