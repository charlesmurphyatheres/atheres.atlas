using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

// Adds an operational-readiness column to Trucks so admins can flip a van
// between Available (0), AvailableWithIssues (1) and Unavailable (2) inline
// from the Vans grid. Default 0 ensures every existing row is Available with
// no separate backfill needed.
[DbContext(typeof(AtlasDbContext))]
[Migration("20260430090000_AddTruckStatus")]
public partial class AddTruckStatus : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "Status",
            table: "Trucks",
            type: "int",
            nullable: false,
            defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("Status", "Trucks");
    }
}
