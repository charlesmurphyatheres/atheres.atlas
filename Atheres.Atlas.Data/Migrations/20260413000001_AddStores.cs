using System;
using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

[DbContext(typeof(AtlasDbContext))]
[Migration("20260413000001_AddStores")]
public partial class AddStores : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ----------------------------------------------------------------
        // 1. Stores table
        // ----------------------------------------------------------------
        migrationBuilder.CreateTable(
            name: "Stores",
            columns: table => new
            {
                Id               = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CompanyId        = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Customer         = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Name             = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Address          = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                City             = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                State            = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                Zip              = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                County           = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                Region           = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                LicenseNumber    = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                Latitude         = table.Column<double>(type: "float", nullable: true),
                Longitude        = table.Column<double>(type: "float", nullable: true),
                FormattedAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                IsActive         = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                CreatedAt        = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt        = table.Column<DateTime>(type: "datetime2", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Stores", x => x.Id);
                table.ForeignKey(
                    name: "FK_Stores_Companies_CompanyId",
                    column: x => x.CompanyId,
                    principalTable: "Companies",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_Stores_CompanyId",          "Stores", "CompanyId");
        migrationBuilder.CreateIndex("IX_Stores_CompanyId_IsActive", "Stores", new[] { "CompanyId", "IsActive" });
        migrationBuilder.CreateIndex("IX_Stores_LicenseNumber",      "Stores", "LicenseNumber");
        migrationBuilder.CreateIndex("IX_Stores_Customer",           "Stores", "Customer");

        // ----------------------------------------------------------------
        // 2. StoreId on Orders
        // ----------------------------------------------------------------
        migrationBuilder.AddColumn<Guid>("StoreId", "Orders",
            type: "uniqueidentifier", nullable: true);

        migrationBuilder.CreateIndex("IX_Orders_StoreId", "Orders", "StoreId");

        migrationBuilder.AddForeignKey(
            name: "FK_Orders_Stores_StoreId",
            table: "Orders",
            column: "StoreId",
            principalTable: "Stores",
            principalColumn: "Id",
            onDelete: ReferentialAction.NoAction);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey("FK_Orders_Stores_StoreId", "Orders");
        migrationBuilder.DropIndex("IX_Orders_StoreId", "Orders");
        migrationBuilder.DropColumn("StoreId", "Orders");
        migrationBuilder.DropTable("Stores");
    }
}
