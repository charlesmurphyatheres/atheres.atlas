using System;
using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

// UserRouteSettings no longer stores start/end addresses — every route begins
// and ends at the assigned truck's home hub. This migration drops the now-dead
// columns so the table matches the trimmed entity.
[DbContext(typeof(AtlasDbContext))]
[Migration("20260422000002_DropUserRouteSettingsEndpoints")]
public partial class DropUserRouteSettingsEndpoints : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var col in new[]
        {
            "StartAddress", "StartCity", "StartState", "StartZip", "StartLatitude", "StartLongitude",
            "EndAddress",   "EndCity",   "EndState",   "EndZip",   "EndLatitude",   "EndLongitude",
        })
        {
            migrationBuilder.DropColumn(col, "UserRouteSettings");
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("StartAddress", "UserRouteSettings", type: "nvarchar(300)", maxLength: 300, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>("StartCity",    "UserRouteSettings", type: "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>("StartState",   "UserRouteSettings", type: "nvarchar(50)",  maxLength: 50,  nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>("StartZip",     "UserRouteSettings", type: "nvarchar(20)",  maxLength: 20,  nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<double>("StartLatitude",  "UserRouteSettings", type: "float", nullable: true);
        migrationBuilder.AddColumn<double>("StartLongitude", "UserRouteSettings", type: "float", nullable: true);

        migrationBuilder.AddColumn<string>("EndAddress", "UserRouteSettings", type: "nvarchar(300)", maxLength: 300, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>("EndCity",    "UserRouteSettings", type: "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>("EndState",   "UserRouteSettings", type: "nvarchar(50)",  maxLength: 50,  nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>("EndZip",     "UserRouteSettings", type: "nvarchar(20)",  maxLength: 20,  nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<double>("EndLatitude",  "UserRouteSettings", type: "float", nullable: true);
        migrationBuilder.AddColumn<double>("EndLongitude", "UserRouteSettings", type: "float", nullable: true);
    }
}
