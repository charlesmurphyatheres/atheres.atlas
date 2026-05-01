using System;
using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations.Identity
{
    /// <summary>
    /// Adds two columns on AspNetUsers used by the OrderImporter invitation
    /// flow: AssignedWarehouseId pins the user to a single warehouse, and
    /// MustChangePassword forces a reset on first login.
    /// </summary>
    [DbContext(typeof(AtlasIdentityDbContext))]
    [Migration("20260430140000_IdentityAddWarehouseAndPasswordReset")]
    public partial class IdentityAddWarehouseAndPasswordReset : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name:     "AssignedWarehouseId",
                table:    "AspNetUsers",
                type:     "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name:         "MustChangePassword",
                table:        "AspNetUsers",
                type:         "bit",
                nullable:     false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name:   "IX_AspNetUsers_AssignedWarehouseId",
                table:  "AspNetUsers",
                column: "AssignedWarehouseId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex ("IX_AspNetUsers_AssignedWarehouseId", "AspNetUsers");
            migrationBuilder.DropColumn("AssignedWarehouseId", "AspNetUsers");
            migrationBuilder.DropColumn("MustChangePassword", "AspNetUsers");
        }
    }
}
