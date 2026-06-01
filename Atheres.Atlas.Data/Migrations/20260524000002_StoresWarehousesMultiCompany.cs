using System;
using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

// Converts Stores and Warehouses from single-tenant (Stores.CompanyId FK,
// Warehouses.CompanyId FK) to multi-tenant via join tables (StoreCompanies,
// WarehouseCompanies). The old CompanyId column is backfilled into the join
// table first so no existing row loses its company assignment.
//
// Per-tenant tooling (the global query filter in AtlasDbContext, list/create
// endpoints, the AdminPanel UI, and import-data.ps1) all read/write through
// the join now. The Down migration restores the single-tenant FK by copying
// the first matching company back into Stores.CompanyId / Warehouses.CompanyId
// — adequate for rollback but loses any additional company memberships.
[DbContext(typeof(AtlasDbContext))]
[Migration("20260524000002_StoresWarehousesMultiCompany")]
public partial class StoresWarehousesMultiCompany : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ---- StoreCompanies --------------------------------------------------
        migrationBuilder.CreateTable(
            name: "StoreCompanies",
            columns: table => new
            {
                StoreId   = table.Column<Guid>(nullable: false),
                CompanyId = table.Column<Guid>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StoreCompanies", x => new { x.StoreId, x.CompanyId });
                table.ForeignKey(
                    name:            "FK_StoreCompanies_Stores_StoreId",
                    column:          x => x.StoreId,
                    principalTable:  "Stores",
                    principalColumn: "Id",
                    onDelete:        ReferentialAction.Cascade);
                // Companies-side FK is NO ACTION on purpose. SQL Server (error
                // 1785) refuses multiple cascade paths into the same table, and
                // Companies already cascade-delete into Hubs/Trucks/Routes which
                // can reach Stores via Order.StoreId. Companies are
                // soft-deactivated, never hard-deleted in practice, so manual
                // cleanup on the Companies side is acceptable.
                table.ForeignKey(
                    name:            "FK_StoreCompanies_Companies_CompanyId",
                    column:          x => x.CompanyId,
                    principalTable:  "Companies",
                    principalColumn: "Id",
                    onDelete:        ReferentialAction.NoAction);
            });

        migrationBuilder.CreateIndex("IX_StoreCompanies_CompanyId", "StoreCompanies", "CompanyId");

        // Backfill: every existing Store stays linked to the company it
        // already belonged to. Idempotent against partial re-runs via
        // NOT EXISTS so a second invocation doesn't fail the PK.
        migrationBuilder.Sql(@"
            INSERT INTO StoreCompanies (StoreId, CompanyId)
            SELECT s.Id, s.CompanyId
            FROM   Stores s
            WHERE  NOT EXISTS (
                SELECT 1 FROM StoreCompanies sc
                WHERE  sc.StoreId = s.Id AND sc.CompanyId = s.CompanyId);
        ");

        // ---- WarehouseCompanies ---------------------------------------------
        migrationBuilder.CreateTable(
            name: "WarehouseCompanies",
            columns: table => new
            {
                WarehouseId = table.Column<Guid>(nullable: false),
                CompanyId   = table.Column<Guid>(nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WarehouseCompanies", x => new { x.WarehouseId, x.CompanyId });
                table.ForeignKey(
                    name:            "FK_WarehouseCompanies_Warehouses_WarehouseId",
                    column:          x => x.WarehouseId,
                    principalTable:  "Warehouses",
                    principalColumn: "Id",
                    onDelete:        ReferentialAction.Cascade);
                // NoAction here for the same reason StoreCompanies uses it:
                // SQL Server (1785) bans multiple cascade paths and Companies
                // already cascade-delete into other entities that reach
                // Warehouses (Order.WarehouseId, Route.WarehouseId).
                table.ForeignKey(
                    name:            "FK_WarehouseCompanies_Companies_CompanyId",
                    column:          x => x.CompanyId,
                    principalTable:  "Companies",
                    principalColumn: "Id",
                    onDelete:        ReferentialAction.NoAction);
            });

        migrationBuilder.CreateIndex("IX_WarehouseCompanies_CompanyId", "WarehouseCompanies", "CompanyId");

        migrationBuilder.Sql(@"
            INSERT INTO WarehouseCompanies (WarehouseId, CompanyId)
            SELECT w.Id, w.CompanyId
            FROM   Warehouses w
            WHERE  NOT EXISTS (
                SELECT 1 FROM WarehouseCompanies wc
                WHERE  wc.WarehouseId = w.Id AND wc.CompanyId = w.CompanyId);
        ");

        // ---- Drop the legacy CompanyId columns + dependent indexes ----------
        // SQL Server requires us to drop both the FK constraint and any
        // indexes that reference the column before the column itself can go.
        // The FK name comes from the original CompanyConfiguration mapping
        // (FK_Stores_Companies_CompanyId / FK_Warehouses_Companies_CompanyId),
        // but we look it up by referenced table to stay resilient to any
        // legacy installations that picked a different name.
        migrationBuilder.Sql(@"
            DECLARE @fk_stores sysname = (
                SELECT TOP 1 name FROM sys.foreign_keys
                WHERE  parent_object_id     = OBJECT_ID('Stores')
                  AND  referenced_object_id = OBJECT_ID('Companies'));
            IF @fk_stores IS NOT NULL
                EXEC('ALTER TABLE Stores DROP CONSTRAINT ' + @fk_stores);

            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Stores_CompanyId' AND object_id = OBJECT_ID('Stores'))
                DROP INDEX IX_Stores_CompanyId ON Stores;
            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Stores_CompanyId_IsActive' AND object_id = OBJECT_ID('Stores'))
                DROP INDEX IX_Stores_CompanyId_IsActive ON Stores;
        ");
        migrationBuilder.DropColumn("CompanyId", "Stores");

        migrationBuilder.Sql(@"
            DECLARE @fk_warehouses sysname = (
                SELECT TOP 1 name FROM sys.foreign_keys
                WHERE  parent_object_id     = OBJECT_ID('Warehouses')
                  AND  referenced_object_id = OBJECT_ID('Companies'));
            IF @fk_warehouses IS NOT NULL
                EXEC('ALTER TABLE Warehouses DROP CONSTRAINT ' + @fk_warehouses);

            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Warehouses_CompanyId' AND object_id = OBJECT_ID('Warehouses'))
                DROP INDEX IX_Warehouses_CompanyId ON Warehouses;
            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Warehouses_CompanyId_IsActive' AND object_id = OBJECT_ID('Warehouses'))
                DROP INDEX IX_Warehouses_CompanyId_IsActive ON Warehouses;
        ");
        migrationBuilder.DropColumn("CompanyId", "Warehouses");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Re-add CompanyId as NULL-able, restore one company per row from
        // the join table (the lowest-ordered CompanyId for determinism),
        // then enforce NOT NULL again. Drop the join tables last.
        migrationBuilder.AddColumn<Guid>(
            name:     "CompanyId",
            table:    "Stores",
            nullable: true);
        migrationBuilder.Sql(@"
            UPDATE s
            SET    s.CompanyId = (
                SELECT TOP 1 sc.CompanyId
                FROM   StoreCompanies sc
                WHERE  sc.StoreId = s.Id
                ORDER BY sc.CompanyId)
            FROM Stores s;
        ");
        migrationBuilder.Sql("ALTER TABLE Stores ALTER COLUMN CompanyId UNIQUEIDENTIFIER NOT NULL;");
        migrationBuilder.CreateIndex("IX_Stores_CompanyId",          "Stores", "CompanyId");
        migrationBuilder.CreateIndex("IX_Stores_CompanyId_IsActive", "Stores", new[] { "CompanyId", "IsActive" });

        migrationBuilder.AddColumn<Guid>(
            name:     "CompanyId",
            table:    "Warehouses",
            nullable: true);
        migrationBuilder.Sql(@"
            UPDATE w
            SET    w.CompanyId = (
                SELECT TOP 1 wc.CompanyId
                FROM   WarehouseCompanies wc
                WHERE  wc.WarehouseId = w.Id
                ORDER BY wc.CompanyId)
            FROM Warehouses w;
        ");
        migrationBuilder.Sql("ALTER TABLE Warehouses ALTER COLUMN CompanyId UNIQUEIDENTIFIER NOT NULL;");
        migrationBuilder.CreateIndex("IX_Warehouses_CompanyId",          "Warehouses", "CompanyId");
        migrationBuilder.CreateIndex("IX_Warehouses_CompanyId_IsActive", "Warehouses", new[] { "CompanyId", "IsActive" });

        migrationBuilder.DropTable("WarehouseCompanies");
        migrationBuilder.DropTable("StoreCompanies");
    }
}
