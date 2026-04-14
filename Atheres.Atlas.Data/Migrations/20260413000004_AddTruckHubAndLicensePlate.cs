using System;
using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

[DbContext(typeof(AtlasDbContext))]
[Migration("20260413000004_AddTruckHubAndLicensePlate")]
public partial class AddTruckHubAndLicensePlate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("LicensePlate", "Trucks",
            type: "nvarchar(50)", maxLength: 50, nullable: true);

        migrationBuilder.AddColumn<Guid>("HubId", "Trucks",
            type: "uniqueidentifier", nullable: true);

        migrationBuilder.CreateIndex("IX_Trucks_HubId", "Trucks", "HubId");
        migrationBuilder.CreateIndex("IX_Trucks_LicensePlate", "Trucks", "LicensePlate");

        migrationBuilder.AddForeignKey(
            name: "FK_Trucks_Hubs_HubId",
            table: "Trucks",
            column: "HubId",
            principalTable: "Hubs",
            principalColumn: "Id",
            onDelete: ReferentialAction.NoAction);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey("FK_Trucks_Hubs_HubId", "Trucks");
        migrationBuilder.DropIndex("IX_Trucks_LicensePlate", "Trucks");
        migrationBuilder.DropIndex("IX_Trucks_HubId", "Trucks");
        migrationBuilder.DropColumn("HubId", "Trucks");
        migrationBuilder.DropColumn("LicensePlate", "Trucks");
    }
}
