using System;
using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AtlasDbContext))]
    [Migration("20260326000002_AddMultiTenancy")]
    public partial class AddMultiTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ----------------------------------------------------------------
            // 1. Companies
            // ----------------------------------------------------------------
            migrationBuilder.CreateTable(
                name: "Companies",
                columns: table => new
                {
                    Id           = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name         = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Slug         = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ContactEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    ContactPhone = table.Column<string>(type: "nvarchar(30)",  maxLength: 30,  nullable: true),
                    Timezone     = table.Column<string>(type: "nvarchar(60)",  maxLength: 60,  nullable: false, defaultValue: "America/Chicago"),
                    IsActive     = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAt    = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt    = table.Column<DateTime>(type: "datetime2", nullable: false),
                },
                constraints: table => table.PrimaryKey("PK_Companies", x => x.Id));

            migrationBuilder.CreateIndex("IX_Companies_Slug",     "Companies", "Slug",     unique: true);
            migrationBuilder.CreateIndex("IX_Companies_IsActive", "Companies", "IsActive");

            // ----------------------------------------------------------------
            // 2. CompanyId on existing tables — nullable first, then backfill, then constrain
            // ----------------------------------------------------------------

            // Seed a default company for existing data
            var defaultCompanyId = "00000000-0000-0000-0000-000000000001";

            migrationBuilder.Sql($@"
                INSERT INTO Companies (Id, Name, Slug, Timezone, IsActive, CreatedAt, UpdatedAt)
                VALUES ('{defaultCompanyId}', 'Default Company', 'default', 'America/Chicago', 1, GETUTCDATE(), GETUTCDATE())
            ");

            // Orders
            migrationBuilder.AddColumn<Guid>("CompanyId", "Orders",
                type: "uniqueidentifier", nullable: true);
            migrationBuilder.Sql($"UPDATE Orders SET CompanyId = '{defaultCompanyId}'");
            migrationBuilder.AlterColumn<Guid>("CompanyId", "Orders",
                type: "uniqueidentifier", nullable: false,
                oldClrType: typeof(Guid), oldType: "uniqueidentifier", oldNullable: true);

            migrationBuilder.AddForeignKey(
                name:            "FK_Orders_Companies_CompanyId",
                table:           "Orders",
                column:          "CompanyId",
                principalTable:  "Companies",
                principalColumn: "Id",
                onDelete:        ReferentialAction.Restrict);

            migrationBuilder.CreateIndex("IX_Orders_CompanyId",         "Orders", "CompanyId");
            migrationBuilder.CreateIndex("IX_Orders_CompanyId_Status",  "Orders",
                ["CompanyId", "Status"]);

            // Routes
            migrationBuilder.AddColumn<Guid>("CompanyId", "Routes",
                type: "uniqueidentifier", nullable: true);
            migrationBuilder.AddColumn<Guid>("TruckId", "Routes",
                type: "uniqueidentifier", nullable: true);
            migrationBuilder.Sql($"UPDATE Routes SET CompanyId = '{defaultCompanyId}'");
            migrationBuilder.AlterColumn<Guid>("CompanyId", "Routes",
                type: "uniqueidentifier", nullable: false,
                oldClrType: typeof(Guid), oldType: "uniqueidentifier", oldNullable: true);

            migrationBuilder.AddForeignKey(
                name:            "FK_Routes_Companies_CompanyId",
                table:           "Routes",
                column:          "CompanyId",
                principalTable:  "Companies",
                principalColumn: "Id",
                onDelete:        ReferentialAction.Restrict);

            migrationBuilder.CreateIndex("IX_Routes_CompanyId",              "Routes", "CompanyId");
            migrationBuilder.CreateIndex("IX_Routes_TruckId",               "Routes", "TruckId");
            migrationBuilder.CreateIndex("IX_Routes_CompanyId_DeliveryDate", "Routes",
                ["CompanyId", "DeliveryDate"]);

            // AuditLogs
            migrationBuilder.AddColumn<Guid>("CompanyId", "AuditLogs",
                type: "uniqueidentifier", nullable: true);
            migrationBuilder.Sql($"UPDATE AuditLogs SET CompanyId = '{defaultCompanyId}'");
            migrationBuilder.AlterColumn<Guid>("CompanyId", "AuditLogs",
                type: "uniqueidentifier", nullable: false,
                oldClrType: typeof(Guid), oldType: "uniqueidentifier", oldNullable: true);

            migrationBuilder.AddForeignKey(
                name:            "FK_AuditLogs_Companies_CompanyId",
                table:           "AuditLogs",
                column:          "CompanyId",
                principalTable:  "Companies",
                principalColumn: "Id",
                onDelete:        ReferentialAction.Cascade);

            migrationBuilder.CreateIndex("IX_AuditLogs_CompanyId", "AuditLogs", "CompanyId");

            // UserRouteSettings
            migrationBuilder.AddColumn<Guid>("CompanyId", "UserRouteSettings",
                type: "uniqueidentifier", nullable: true);
            migrationBuilder.Sql($"UPDATE UserRouteSettings SET CompanyId = '{defaultCompanyId}'");
            migrationBuilder.AlterColumn<Guid>("CompanyId", "UserRouteSettings",
                type: "uniqueidentifier", nullable: false,
                oldClrType: typeof(Guid), oldType: "uniqueidentifier", oldNullable: true);

            // Drop old single-column unique index; replace with compound
            migrationBuilder.DropIndex("IX_UserRouteSettings_UserId", "UserRouteSettings");

            migrationBuilder.AddForeignKey(
                name:            "FK_UserRouteSettings_Companies_CompanyId",
                table:           "UserRouteSettings",
                column:          "CompanyId",
                principalTable:  "Companies",
                principalColumn: "Id",
                onDelete:        ReferentialAction.Cascade);

            migrationBuilder.CreateIndex("IX_UserRouteSettings_CompanyId",          "UserRouteSettings", "CompanyId");
            migrationBuilder.CreateIndex("IX_UserRouteSettings_CompanyId_UserId",   "UserRouteSettings",
                ["CompanyId", "UserId"], unique: true);

            // ----------------------------------------------------------------
            // 3. Trucks
            // ----------------------------------------------------------------
            migrationBuilder.CreateTable(
                name: "Trucks",
                columns: table => new
                {
                    Id               = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId        = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name             = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AssignedDriverId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    RouteSettingsId  = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive         = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAt        = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt        = table.Column<DateTime>(type: "datetime2", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trucks", x => x.Id);
                    table.ForeignKey(
                        name:            "FK_Trucks_Companies_CompanyId",
                        column:          x => x.CompanyId,
                        principalTable:  "Companies",
                        principalColumn: "Id",
                        onDelete:        ReferentialAction.Cascade);
                    table.ForeignKey(
                        name:            "FK_Trucks_UserRouteSettings_RouteSettingsId",
                        column:          x => x.RouteSettingsId,
                        principalTable:  "UserRouteSettings",
                        principalColumn: "Id",
                        onDelete:        ReferentialAction.NoAction);
                });

            migrationBuilder.CreateIndex("IX_Trucks_CompanyId",        "Trucks", "CompanyId");
            migrationBuilder.CreateIndex("IX_Trucks_AssignedDriverId", "Trucks", "AssignedDriverId");

            // Routes FK to Trucks
            migrationBuilder.AddForeignKey(
                name:            "FK_Routes_Trucks_TruckId",
                table:           "Routes",
                column:          "TruckId",
                principalTable:  "Trucks",
                principalColumn: "Id",
                onDelete:        ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey("FK_Routes_Trucks_TruckId",                   "Routes");
            migrationBuilder.DropForeignKey("FK_Routes_Companies_CompanyId",              "Routes");
            migrationBuilder.DropForeignKey("FK_Orders_Companies_CompanyId",              "Orders");
            migrationBuilder.DropForeignKey("FK_AuditLogs_Companies_CompanyId",           "AuditLogs");
            migrationBuilder.DropForeignKey("FK_UserRouteSettings_Companies_CompanyId",   "UserRouteSettings");

            migrationBuilder.DropTable("Trucks");

            migrationBuilder.DropIndex("IX_Routes_CompanyId",              "Routes");
            migrationBuilder.DropIndex("IX_Routes_TruckId",               "Routes");
            migrationBuilder.DropIndex("IX_Routes_CompanyId_DeliveryDate", "Routes");
            migrationBuilder.DropIndex("IX_Orders_CompanyId",             "Orders");
            migrationBuilder.DropIndex("IX_Orders_CompanyId_Status",      "Orders");
            migrationBuilder.DropIndex("IX_AuditLogs_CompanyId",          "AuditLogs");
            migrationBuilder.DropIndex("IX_UserRouteSettings_CompanyId",          "UserRouteSettings");
            migrationBuilder.DropIndex("IX_UserRouteSettings_CompanyId_UserId",   "UserRouteSettings");

            migrationBuilder.DropColumn("CompanyId", "Routes");
            migrationBuilder.DropColumn("TruckId",   "Routes");
            migrationBuilder.DropColumn("CompanyId", "Orders");
            migrationBuilder.DropColumn("CompanyId", "AuditLogs");
            migrationBuilder.DropColumn("CompanyId", "UserRouteSettings");

            migrationBuilder.CreateIndex("IX_UserRouteSettings_UserId", "UserRouteSettings", "UserId", unique: true);

            migrationBuilder.DropTable("Companies");
        }
    }
}
