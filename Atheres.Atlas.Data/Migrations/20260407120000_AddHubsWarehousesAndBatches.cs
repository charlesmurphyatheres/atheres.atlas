using System;
using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

/// <inheritdoc />
[DbContext(typeof(AtlasDbContext))]
[Migration("20260407120000_AddHubsWarehousesAndBatches")]
public partial class AddHubsWarehousesAndBatches : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ----------------------------------------------------------------
        // 1. Hubs — company home base, route origin and destination
        // ----------------------------------------------------------------
        migrationBuilder.CreateTable(
            name: "Hubs",
            columns: table => new
            {
                Id               = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CompanyId        = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name             = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Address          = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                City             = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                State            = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                Zip              = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                Latitude         = table.Column<double>(type: "float", nullable: true),
                Longitude        = table.Column<double>(type: "float", nullable: true),
                FormattedAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                IsActive         = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                CreatedAt        = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt        = table.Column<DateTime>(type: "datetime2", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Hubs", x => x.Id);
                table.ForeignKey(
                    name: "FK_Hubs_Companies_CompanyId",
                    column: x => x.CompanyId,
                    principalTable: "Companies",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_Hubs_CompanyId",          "Hubs", "CompanyId");
        migrationBuilder.CreateIndex("IX_Hubs_CompanyId_IsActive", "Hubs", ["CompanyId", "IsActive"]);

        // ----------------------------------------------------------------
        // 2. Warehouses — client pickup locations
        // ----------------------------------------------------------------
        migrationBuilder.CreateTable(
            name: "Warehouses",
            columns: table => new
            {
                Id                    = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CompanyId             = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                BusinessName          = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                AlternateName         = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                Address               = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                City                  = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                State                 = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                Zip                   = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                LicenseNumber         = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                LegacyLicenseNumber   = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                Latitude              = table.Column<double>(type: "float", nullable: true),
                Longitude             = table.Column<double>(type: "float", nullable: true),
                FormattedAddress      = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                IsActive              = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                CreatedAt             = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt             = table.Column<DateTime>(type: "datetime2", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Warehouses", x => x.Id);
                table.ForeignKey(
                    name: "FK_Warehouses_Companies_CompanyId",
                    column: x => x.CompanyId,
                    principalTable: "Companies",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_Warehouses_CompanyId",          "Warehouses", "CompanyId");
        migrationBuilder.CreateIndex("IX_Warehouses_CompanyId_IsActive", "Warehouses", ["CompanyId", "IsActive"]);
        migrationBuilder.CreateIndex("IX_Warehouses_LicenseNumber",      "Warehouses", "LicenseNumber");

        // ----------------------------------------------------------------
        // 3. OrderBatches — groups orders for pickup + delivery
        // ----------------------------------------------------------------
        migrationBuilder.CreateTable(
            name: "OrderBatches",
            columns: table => new
            {
                Id          = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CompanyId   = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name        = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                HubId       = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                WarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TruckId     = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Status      = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                PickupDate  = table.Column<DateTime>(type: "datetime2", nullable: true),
                RouteId     = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CreatedAt   = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt   = table.Column<DateTime>(type: "datetime2", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrderBatches", x => x.Id);
                table.ForeignKey(
                    name: "FK_OrderBatches_Companies_CompanyId",
                    column: x => x.CompanyId,
                    principalTable: "Companies",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_OrderBatches_Hubs_HubId",
                    column: x => x.HubId,
                    principalTable: "Hubs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_OrderBatches_Warehouses_WarehouseId",
                    column: x => x.WarehouseId,
                    principalTable: "Warehouses",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_OrderBatches_Trucks_TruckId",
                    column: x => x.TruckId,
                    principalTable: "Trucks",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.NoAction);
                table.ForeignKey(
                    name: "FK_OrderBatches_Routes_RouteId",
                    column: x => x.RouteId,
                    principalTable: "Routes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.NoAction);
            });

        migrationBuilder.CreateIndex("IX_OrderBatches_CompanyId",        "OrderBatches", "CompanyId");
        migrationBuilder.CreateIndex("IX_OrderBatches_HubId",            "OrderBatches", "HubId");
        migrationBuilder.CreateIndex("IX_OrderBatches_WarehouseId",      "OrderBatches", "WarehouseId");
        migrationBuilder.CreateIndex("IX_OrderBatches_Status",           "OrderBatches", "Status");
        migrationBuilder.CreateIndex("IX_OrderBatches_CompanyId_Status", "OrderBatches", ["CompanyId", "Status"]);

        // ----------------------------------------------------------------
        // 4. New columns on Routes
        // ----------------------------------------------------------------
        migrationBuilder.AddColumn<Guid>("HubId", "Routes",
            type: "uniqueidentifier", nullable: true);
        migrationBuilder.AddColumn<Guid>("WarehouseId", "Routes",
            type: "uniqueidentifier", nullable: true);

        migrationBuilder.CreateIndex("IX_Routes_HubId",       "Routes", "HubId");
        migrationBuilder.CreateIndex("IX_Routes_WarehouseId", "Routes", "WarehouseId");

        migrationBuilder.AddForeignKey(
            name: "FK_Routes_Hubs_HubId",
            table: "Routes",
            column: "HubId",
            principalTable: "Hubs",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_Routes_Warehouses_WarehouseId",
            table: "Routes",
            column: "WarehouseId",
            principalTable: "Warehouses",
            principalColumn: "Id");

        // ----------------------------------------------------------------
        // 5. New columns on Orders
        // ----------------------------------------------------------------
        migrationBuilder.AddColumn<Guid>("BatchId", "Orders",
            type: "uniqueidentifier", nullable: true);
        migrationBuilder.AddColumn<Guid>("WarehouseId", "Orders",
            type: "uniqueidentifier", nullable: true);
        migrationBuilder.AddColumn<bool>("IsAtHub", "Orders",
            type: "bit", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<DateTime>("DeferredAt", "Orders",
            type: "datetime2", nullable: true);
        migrationBuilder.AddColumn<Guid>("OriginalRouteId", "Orders",
            type: "uniqueidentifier", nullable: true);

        migrationBuilder.CreateIndex("IX_Orders_BatchId",      "Orders", "BatchId");
        migrationBuilder.CreateIndex("IX_Orders_WarehouseId",  "Orders", "WarehouseId");

        migrationBuilder.AddForeignKey(
            name: "FK_Orders_OrderBatches_BatchId",
            table: "Orders",
            column: "BatchId",
            principalTable: "OrderBatches",
            principalColumn: "Id",
            onDelete: ReferentialAction.NoAction);

        migrationBuilder.AddForeignKey(
            name: "FK_Orders_Warehouses_WarehouseId",
            table: "Orders",
            column: "WarehouseId",
            principalTable: "Warehouses",
            principalColumn: "Id",
            onDelete: ReferentialAction.NoAction);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Orders columns
        migrationBuilder.DropForeignKey("FK_Orders_Warehouses_WarehouseId",   "Orders");
        migrationBuilder.DropForeignKey("FK_Orders_OrderBatches_BatchId",     "Orders");
        migrationBuilder.DropIndex("IX_Orders_WarehouseId",  "Orders");
        migrationBuilder.DropIndex("IX_Orders_BatchId",      "Orders");
        migrationBuilder.DropColumn("OriginalRouteId", "Orders");
        migrationBuilder.DropColumn("DeferredAt",      "Orders");
        migrationBuilder.DropColumn("IsAtHub",         "Orders");
        migrationBuilder.DropColumn("WarehouseId",     "Orders");
        migrationBuilder.DropColumn("BatchId",         "Orders");

        // Routes columns
        migrationBuilder.DropForeignKey("FK_Routes_Warehouses_WarehouseId", "Routes");
        migrationBuilder.DropForeignKey("FK_Routes_Hubs_HubId",            "Routes");
        migrationBuilder.DropIndex("IX_Routes_WarehouseId", "Routes");
        migrationBuilder.DropIndex("IX_Routes_HubId",       "Routes");
        migrationBuilder.DropColumn("WarehouseId", "Routes");
        migrationBuilder.DropColumn("HubId",       "Routes");

        // Tables
        migrationBuilder.DropTable("OrderBatches");
        migrationBuilder.DropTable("Warehouses");
        migrationBuilder.DropTable("Hubs");
    }
}
