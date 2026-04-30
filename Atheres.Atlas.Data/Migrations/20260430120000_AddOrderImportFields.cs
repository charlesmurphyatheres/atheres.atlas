using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

// Adds three nullable columns to Orders to support the CSV import flow,
// which can create orders that have customer/sales-order/purchase-order
// metadata but no upstream warehouse system to look them up against.
//
// All three are NULL-allowed so existing rows transition cleanly.
[DbContext(typeof(AtlasDbContext))]
[Migration("20260430120000_AddOrderImportFields")]
public partial class AddOrderImportFields : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Customer",
            table: "Orders",
            type: "nvarchar(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SalesOrderNumber",
            table: "Orders",
            type: "nvarchar(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PurchaseOrderNumber",
            table: "Orders",
            type: "nvarchar(100)",
            maxLength: 100,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("PurchaseOrderNumber", "Orders");
        migrationBuilder.DropColumn("SalesOrderNumber",    "Orders");
        migrationBuilder.DropColumn("Customer",            "Orders");
    }
}
