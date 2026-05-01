using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

// Enforces sales-order uniqueness inside a tenant. The constraint is
// scoped to (CompanyId, SalesOrderNumber) and filtered to non-null sales
// orders so legacy rows with no sales-order metadata don't collide on
// NULL — SQL Server's default "consider NULLs equal" behavior would
// otherwise reject every row past the first.
//
// Application code (OrderImportAgent + the manual-entry endpoint) is the
// primary gate, surfacing a friendly per-row error. This index is the
// backstop that makes "duplicate sales order" impossible at the storage
// layer even if a future caller forgets the check.
[DbContext(typeof(AtlasDbContext))]
[Migration("20260501100000_UniqueSalesOrderNumber")]
public partial class UniqueSalesOrderNumber : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
            CREATE UNIQUE INDEX IX_Orders_CompanyId_SalesOrderNumber_Unique
            ON Orders (CompanyId, SalesOrderNumber)
            WHERE SalesOrderNumber IS NOT NULL;
        ");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
            IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Orders_CompanyId_SalesOrderNumber_Unique' AND object_id = OBJECT_ID('Orders'))
                DROP INDEX IX_Orders_CompanyId_SalesOrderNumber_Unique ON Orders;
        ");
    }
}
