using System;
using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

// Adds the Districts + Zones lookup tables and the optional Stores.ZoneId FK.
// Data is loaded from data/zones.csv by scripts/import-data.ps1; the schema is
// company-scoped so each tenant gets its own district/zone vocabulary even if
// later tenants ship different territory definitions.
[DbContext(typeof(AtlasDbContext))]
[Migration("20260524000001_AddDistrictsAndZones")]
public partial class AddDistrictsAndZones : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Districts",
            columns: table => new
            {
                Id        = table.Column<Guid>(nullable: false),
                CompanyId = table.Column<Guid>(nullable: false),
                Number    = table.Column<int>(nullable: false),
                Name      = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                IsActive  = table.Column<bool>(nullable: false, defaultValue: true),
                CreatedAt = table.Column<DateTime>(nullable: false),
                UpdatedAt = table.Column<DateTime>(nullable: false),
            },
            constraints: table => { table.PrimaryKey("PK_Districts", x => x.Id); });

        migrationBuilder.CreateIndex("IX_Districts_CompanyId", "Districts", "CompanyId");
        migrationBuilder.CreateIndex(
            name:   "IX_Districts_CompanyId_Number",
            table:  "Districts",
            columns: new[] { "CompanyId", "Number" },
            unique: true);

        migrationBuilder.CreateTable(
            name: "Zones",
            columns: table => new
            {
                Id         = table.Column<Guid>(nullable: false),
                CompanyId  = table.Column<Guid>(nullable: false),
                DistrictId = table.Column<Guid>(nullable: false),
                Code       = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                IsActive   = table.Column<bool>(nullable: false, defaultValue: true),
                CreatedAt  = table.Column<DateTime>(nullable: false),
                UpdatedAt  = table.Column<DateTime>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Zones", x => x.Id);
                table.ForeignKey(
                    name:           "FK_Zones_Districts_DistrictId",
                    column:         x => x.DistrictId,
                    principalTable: "Districts",
                    principalColumn: "Id",
                    onDelete:        ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("IX_Zones_CompanyId",  "Zones", "CompanyId");
        migrationBuilder.CreateIndex("IX_Zones_DistrictId", "Zones", "DistrictId");
        migrationBuilder.CreateIndex(
            name:    "IX_Zones_CompanyId_DistrictId_Code",
            table:   "Zones",
            columns: new[] { "CompanyId", "DistrictId", "Code" },
            unique:  true);

        migrationBuilder.AddColumn<Guid>(
            name:     "ZoneId",
            table:    "Stores",
            nullable: true);

        migrationBuilder.CreateIndex("IX_Stores_ZoneId", "Stores", "ZoneId");

        migrationBuilder.AddForeignKey(
            name:            "FK_Stores_Zones_ZoneId",
            table:           "Stores",
            column:          "ZoneId",
            principalTable:  "Zones",
            principalColumn: "Id",
            onDelete:        ReferentialAction.SetNull);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey("FK_Stores_Zones_ZoneId", "Stores");
        migrationBuilder.DropIndex("IX_Stores_ZoneId",            "Stores");
        migrationBuilder.DropColumn("ZoneId",                     "Stores");

        migrationBuilder.DropTable("Zones");
        migrationBuilder.DropTable("Districts");
    }
}
