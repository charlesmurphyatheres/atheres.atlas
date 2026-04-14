using System;
using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

[DbContext(typeof(AtlasDbContext))]
[Migration("20260413000003_AddWarehousePickupSchedule")]
public partial class AddWarehousePickupSchedule : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<TimeSpan>("MondayPickupTime",    "Warehouses", type: "time", nullable: true);
        migrationBuilder.AddColumn<TimeSpan>("TuesdayPickupTime",   "Warehouses", type: "time", nullable: true);
        migrationBuilder.AddColumn<TimeSpan>("WednesdayPickupTime", "Warehouses", type: "time", nullable: true);
        migrationBuilder.AddColumn<TimeSpan>("ThursdayPickupTime",  "Warehouses", type: "time", nullable: true);
        migrationBuilder.AddColumn<TimeSpan>("FridayPickupTime",    "Warehouses", type: "time", nullable: true);
        migrationBuilder.AddColumn<TimeSpan>("SaturdayPickupTime",  "Warehouses", type: "time", nullable: true);
        migrationBuilder.AddColumn<TimeSpan>("SundayPickupTime",    "Warehouses", type: "time", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("SundayPickupTime",    "Warehouses");
        migrationBuilder.DropColumn("SaturdayPickupTime",  "Warehouses");
        migrationBuilder.DropColumn("FridayPickupTime",    "Warehouses");
        migrationBuilder.DropColumn("ThursdayPickupTime",  "Warehouses");
        migrationBuilder.DropColumn("WednesdayPickupTime", "Warehouses");
        migrationBuilder.DropColumn("TuesdayPickupTime",   "Warehouses");
        migrationBuilder.DropColumn("MondayPickupTime",    "Warehouses");
    }
}
