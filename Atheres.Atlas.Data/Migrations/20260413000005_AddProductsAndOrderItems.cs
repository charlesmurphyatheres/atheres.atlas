using System;
using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

[DbContext(typeof(AtlasDbContext))]
[Migration("20260413000005_AddProductsAndOrderItems")]
public partial class AddProductsAndOrderItems : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Products",
            columns: table => new
            {
                Id          = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Sku         = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Name        = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                Category    = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                CreatedAt   = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt   = table.Column<DateTime>(type: "datetime2", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Products", x => x.Id);
            });

        migrationBuilder.CreateIndex("IX_Products_Sku", "Products", "Sku", unique: true);

        migrationBuilder.CreateTable(
            name: "OrderItems",
            columns: table => new
            {
                Id               = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OrderId          = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProductId        = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Quantity         = table.Column<int>(type: "int", nullable: false),
                Sku              = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Name             = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                IsConfirmedReady = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                CreatedAt        = table.Column<DateTime>(type: "datetime2", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrderItems", x => x.Id);
                table.ForeignKey(
                    name: "FK_OrderItems_Orders_OrderId",
                    column: x => x.OrderId,
                    principalTable: "Orders",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_OrderItems_Products_ProductId",
                    column: x => x.ProductId,
                    principalTable: "Products",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.NoAction);
            });

        migrationBuilder.CreateIndex("IX_OrderItems_OrderId", "OrderItems", "OrderId");
        migrationBuilder.CreateIndex("IX_OrderItems_ProductId", "OrderItems", "ProductId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("OrderItems");
        migrationBuilder.DropTable("Products");
    }
}
