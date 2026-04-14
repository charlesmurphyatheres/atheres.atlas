using System;
using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations.Identity
{
    /// <inheritdoc />
    [DbContext(typeof(AtlasIdentityDbContext))]
    [Migration("20260326000001_InitialIdentity")]
    public partial class InitialIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ----------------------------------------------------------------
            // ASP.NET Core Identity tables
            // ----------------------------------------------------------------
            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id             = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name           = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                },
                constraints: table => table.PrimaryKey("PK_AspNetRoles", x => x.Id));

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id                   = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    FirstName            = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LastName             = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RouteSettingsUserId  = table.Column<Guid?>(type: "uniqueidentifier", nullable: true),
                    IsActive             = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAt            = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastLoginAt          = table.Column<DateTime?>(type: "datetime2", nullable: true),
                    UserName             = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedUserName   = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Email                = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedEmail      = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EmailConfirmed       = table.Column<bool>(type: "bit", nullable: false),
                    PasswordHash         = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SecurityStamp        = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConcurrencyStamp     = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumber          = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    TwoFactorEnabled     = table.Column<bool>(type: "bit", nullable: false),
                    LockoutEnd           = table.Column<DateTimeOffset?>(type: "datetimeoffset", nullable: true),
                    LockoutEnabled       = table.Column<bool>(type: "bit", nullable: false),
                    AccessFailedCount    = table.Column<int>(type: "int", nullable: false),
                },
                constraints: table => table.PrimaryKey("PK_AspNetUsers", x => x.Id));

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id         = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                    RoleId     = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ClaimType  = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name:                "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column:              x => x.RoleId,
                        principalTable:      "AspNetRoles",
                        principalColumn:     "Id",
                        onDelete:            ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id         = table.Column<int>(type: "int", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                    UserId     = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ClaimType  = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name:            "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column:          x => x.UserId,
                        principalTable:  "AspNetUsers",
                        principalColumn: "Id",
                        onDelete:        ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider       = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProviderKey         = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UserId              = table.Column<string>(type: "nvarchar(450)", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name:            "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column:          x => x.UserId,
                        principalTable:  "AspNetUsers",
                        principalColumn: "Id",
                        onDelete:        ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    RoleId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name:            "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column:          x => x.RoleId,
                        principalTable:  "AspNetRoles",
                        principalColumn: "Id",
                        onDelete:        ReferentialAction.Cascade);
                    table.ForeignKey(
                        name:            "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column:          x => x.UserId,
                        principalTable:  "AspNetUsers",
                        principalColumn: "Id",
                        onDelete:        ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId        = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name          = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Value         = table.Column<string>(type: "nvarchar(max)", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens",
                        x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name:            "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column:          x => x.UserId,
                        principalTable:  "AspNetUsers",
                        principalColumn: "Id",
                        onDelete:        ReferentialAction.Cascade);
                });

            // ----------------------------------------------------------------
            // RefreshTokens
            // ----------------------------------------------------------------
            migrationBuilder.CreateTable(
                name: "RefreshTokens",
                columns: table => new
                {
                    Id              = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId          = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Token           = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ExpiresAt       = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IsRevoked       = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    RevokedReason   = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReplacedByToken = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt       = table.Column<DateTime>(type: "datetime2", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name:            "FK_RefreshTokens_AspNetUsers_UserId",
                        column:          x => x.UserId,
                        principalTable:  "AspNetUsers",
                        principalColumn: "Id",
                        onDelete:        ReferentialAction.Cascade);
                });

            // ----------------------------------------------------------------
            // Indexes
            // ----------------------------------------------------------------
            migrationBuilder.CreateIndex("IX_AspNetRoleClaims_RoleId",  "AspNetRoleClaims",  "RoleId");
            migrationBuilder.CreateIndex("RoleNameIndex",               "AspNetRoles",       "NormalizedName", unique: true, filter: "[NormalizedName] IS NOT NULL");
            migrationBuilder.CreateIndex("IX_AspNetUserClaims_UserId",  "AspNetUserClaims",  "UserId");
            migrationBuilder.CreateIndex("IX_AspNetUserLogins_UserId",  "AspNetUserLogins",  "UserId");
            migrationBuilder.CreateIndex("IX_AspNetUserRoles_RoleId",   "AspNetUserRoles",   "RoleId");
            migrationBuilder.CreateIndex("EmailIndex",                  "AspNetUsers",       "NormalizedEmail",    unique: false);
            migrationBuilder.CreateIndex("UserNameIndex",               "AspNetUsers",       "NormalizedUserName", unique: true, filter: "[NormalizedUserName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name:    "IX_RefreshTokens_Token",
                table:   "RefreshTokens",
                column:  "Token",
                unique:  true);

            migrationBuilder.CreateIndex(
                name:   "IX_RefreshTokens_UserId",
                table:  "RefreshTokens",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable("RefreshTokens");
            migrationBuilder.DropTable("AspNetUserTokens");
            migrationBuilder.DropTable("AspNetUserRoles");
            migrationBuilder.DropTable("AspNetUserLogins");
            migrationBuilder.DropTable("AspNetUserClaims");
            migrationBuilder.DropTable("AspNetRoleClaims");
            migrationBuilder.DropTable("AspNetUsers");
            migrationBuilder.DropTable("AspNetRoles");
        }
    }
}
