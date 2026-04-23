using System;
using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

// A truck's "home hub" is where it lives; a truck's current location is where
// it actually is right now (may differ — on the road, parked elsewhere). Admins
// edit this from the Vans tab; the optional address is auto-geocoded server-side.
[DbContext(typeof(AtlasDbContext))]
[Migration("20260422000001_AddTruckCurrentLocation")]
public partial class AddTruckCurrentLocation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "CurrentLocationAddress",
            table: "Trucks",
            type: "nvarchar(300)",
            maxLength: 300,
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "CurrentLocationLatitude",
            table: "Trucks",
            type: "float",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "CurrentLocationLongitude",
            table: "Trucks",
            type: "float",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "CurrentLocationUpdatedAt",
            table: "Trucks",
            type: "datetime2",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("CurrentLocationAddress",   "Trucks");
        migrationBuilder.DropColumn("CurrentLocationLatitude",  "Trucks");
        migrationBuilder.DropColumn("CurrentLocationLongitude", "Trucks");
        migrationBuilder.DropColumn("CurrentLocationUpdatedAt", "Trucks");
    }
}
