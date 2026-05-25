using System;
using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

// One row per RouteScheduler.EnqueueAsync invocation. Captures the
// operator-readable decision trail (warehouses involved, hubs picked,
// chunks formed, hub-bypass exceptions) so administrators can audit how
// any given dispatch was planned. Body lives in LogText as plain text
// for cheap PDF generation downstream.
[DbContext(typeof(AtlasDbContext))]
[Migration("20260524000004_AddOptimizationAudits")]
public partial class AddOptimizationAudits : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OptimizationAudits",
            columns: table => new
            {
                Id                  = table.Column<Guid>(nullable: false),
                CompanyId           = table.Column<Guid>(nullable: false),
                CreatedAt           = table.Column<DateTime>(nullable: false),
                TriggeredBy         = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                Trigger             = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                Summary             = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                OrderCount          = table.Column<int>(nullable: false),
                RouteCount          = table.Column<int>(nullable: false),
                WarehouseCount      = table.Column<int>(nullable: false),
                HubCount            = table.Column<int>(nullable: false),
                ZoneCount           = table.Column<int>(nullable: false),
                DirectDeliveryCount = table.Column<int>(nullable: false),
                LogText             = table.Column<string>(type: "nvarchar(max)", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OptimizationAudits", x => x.Id);
                table.ForeignKey(
                    name:            "FK_OptimizationAudits_Companies_CompanyId",
                    column:          x => x.CompanyId,
                    principalTable:  "Companies",
                    principalColumn: "Id",
                    onDelete:        ReferentialAction.NoAction);
            });

        migrationBuilder.CreateIndex("IX_OptimizationAudits_CompanyId",           "OptimizationAudits", "CompanyId");
        migrationBuilder.CreateIndex("IX_OptimizationAudits_CompanyId_CreatedAt", "OptimizationAudits", new[] { "CompanyId", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("OptimizationAudits");
    }
}
