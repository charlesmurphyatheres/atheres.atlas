using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

// Removes the Products + OrderItems tables. The system now tracks orders at
// sales-order level only — there is no per-line product detail, so the
// supporting tables and the migration that created them are gone too.
//
// Idempotent: uses IF OBJECT_ID(...) IS NOT NULL so it's a no-op on fresh
// databases that never had the tables (the original
// "AddProductsAndOrderItems" migration file has been deleted, so a brand-new
// install never creates them in the first place). Existing deployments that
// already applied that migration will have their tables dropped here.
[DbContext(typeof(AtlasDbContext))]
[Migration("20260430130000_RemoveProductsAndOrderItems")]
public partial class RemoveProductsAndOrderItems : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
            IF OBJECT_ID(N'[dbo].[OrderItems]', N'U') IS NOT NULL
                DROP TABLE [dbo].[OrderItems];
            IF OBJECT_ID(N'[dbo].[Products]', N'U') IS NOT NULL
                DROP TABLE [dbo].[Products];
        ");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Intentionally not reversible. The product / order-item domain is
        // gone from the codebase, so there is nothing in the application
        // that could write to recreated tables. If you ever need to roll
        // back, restore from a database backup taken before this migration
        // ran rather than re-running an empty CREATE TABLE.
    }
}
