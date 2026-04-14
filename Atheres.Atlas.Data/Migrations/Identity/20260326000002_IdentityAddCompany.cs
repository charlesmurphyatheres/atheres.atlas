using System;
using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations.Identity
{
    /// <inheritdoc />
    [DbContext(typeof(AtlasIdentityDbContext))]
    [Migration("20260326000002_IdentityAddCompany")]
    public partial class IdentityAddCompany : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // CompanyId — nullable (SuperAdmin has no company)
            migrationBuilder.AddColumn<Guid>(
                name:     "CompanyId",
                table:    "AspNetUsers",
                type:     "uniqueidentifier",
                nullable: true);

            // AssignedTruckId — set for Driver users
            migrationBuilder.AddColumn<Guid>(
                name:     "AssignedTruckId",
                table:    "AspNetUsers",
                type:     "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name:   "IX_AspNetUsers_CompanyId",
                table:  "AspNetUsers",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name:   "IX_AspNetUsers_AssignedTruckId",
                table:  "AspNetUsers",
                column: "AssignedTruckId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex("IX_AspNetUsers_CompanyId",       "AspNetUsers");
            migrationBuilder.DropIndex("IX_AspNetUsers_AssignedTruckId", "AspNetUsers");
            migrationBuilder.DropColumn("CompanyId",       "AspNetUsers");
            migrationBuilder.DropColumn("AssignedTruckId", "AspNetUsers");
        }
    }
}
