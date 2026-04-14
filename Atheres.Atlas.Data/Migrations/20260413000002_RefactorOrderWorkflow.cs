using System;
using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

[DbContext(typeof(AtlasDbContext))]
[Migration("20260413000002_RefactorOrderWorkflow")]
public partial class RefactorOrderWorkflow : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ----------------------------------------------------------------
        // 1. Store: add Email and Phone columns
        // ----------------------------------------------------------------
        migrationBuilder.AddColumn<string>("Email", "Stores",
            type: "nvarchar(254)", maxLength: 254, nullable: true);
        migrationBuilder.AddColumn<string>("Phone", "Stores",
            type: "nvarchar(30)", maxLength: 30, nullable: true);

        // ----------------------------------------------------------------
        // 2. Orders: add license number lookup fields
        // ----------------------------------------------------------------
        migrationBuilder.AddColumn<string>("WarehouseLicenseNumber", "Orders",
            type: "nvarchar(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddColumn<string>("StoreLicenseNumber", "Orders",
            type: "nvarchar(100)", maxLength: 100, nullable: true);

        migrationBuilder.CreateIndex("IX_Orders_WarehouseLicenseNumber", "Orders", "WarehouseLicenseNumber");
        migrationBuilder.CreateIndex("IX_Orders_StoreLicenseNumber",     "Orders", "StoreLicenseNumber");

        // ----------------------------------------------------------------
        // 3. Migrate existing OrderStatus string values to new enum names
        // ----------------------------------------------------------------
        migrationBuilder.Sql(@"
            UPDATE Orders SET Status = 'Ordered'            WHERE Status IN ('Received', 'Validating', 'Validated', 'ValidationFailed', 'Rescheduled', 'DeferredToHub');
            UPDATE Orders SET Status = 'Scheduled'          WHERE Status = 'RouteOptimizationPending';
            UPDATE Orders SET Status = 'RouteOptimized'     WHERE Status = 'Routed';
            -- ConfirmationPending, Confirmed, OutForDelivery, Delivered, Cancelled stay the same
            UPDATE Orders SET Status = 'ConfirmationPending' WHERE Status = 'ConfirmationFailed';
        ");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Revert status values
        migrationBuilder.Sql(@"
            UPDATE Orders SET Status = 'Received'                WHERE Status = 'Ordered';
            UPDATE Orders SET Status = 'RouteOptimizationPending' WHERE Status = 'Scheduled';
            UPDATE Orders SET Status = 'Routed'                  WHERE Status = 'RouteOptimized';
        ");

        migrationBuilder.DropIndex("IX_Orders_StoreLicenseNumber",     "Orders");
        migrationBuilder.DropIndex("IX_Orders_WarehouseLicenseNumber", "Orders");
        migrationBuilder.DropColumn("StoreLicenseNumber",     "Orders");
        migrationBuilder.DropColumn("WarehouseLicenseNumber", "Orders");
        migrationBuilder.DropColumn("Phone", "Stores");
        migrationBuilder.DropColumn("Email", "Stores");
    }
}
