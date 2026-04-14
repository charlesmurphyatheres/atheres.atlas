using System;
using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

/// <inheritdoc />
[DbContext(typeof(AtlasDbContext))]
[Migration("20260326000001_InitialCreate")]
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ----------------------------------------------------------------
        // 1. Routes  (no FK dependencies)
        // ----------------------------------------------------------------
        migrationBuilder.CreateTable(
            name: "Routes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DeliveryDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                StartAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                StartLatitude = table.Column<double>(type: "float", nullable: false),
                StartLongitude = table.Column<double>(type: "float", nullable: false),
                EndAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                EndLatitude = table.Column<double>(type: "float", nullable: false),
                EndLongitude = table.Column<double>(type: "float", nullable: false),
                TotalStops = table.Column<int>(type: "int", nullable: false),
                TotalDistanceMeters = table.Column<double>(type: "float", nullable: false),
                TotalDurationSeconds = table.Column<int>(type: "int", nullable: false),
                OptimizedWaypointOrder = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                OverviewPolyline = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                IsOptimized = table.Column<bool>(type: "bit", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Routes", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Routes_DeliveryDate",
            table: "Routes",
            column: "DeliveryDate");

        // ----------------------------------------------------------------
        // 2. UserRouteSettings  (no FK dependencies)
        // ----------------------------------------------------------------
        migrationBuilder.CreateTable(
            name: "UserRouteSettings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                StartAddress = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                StartCity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: ""),
                StartState = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, defaultValue: ""),
                StartZip = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: ""),
                StartLatitude = table.Column<double>(type: "float", nullable: true),
                StartLongitude = table.Column<double>(type: "float", nullable: true),
                EndAddress = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                EndCity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: ""),
                EndState = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, defaultValue: ""),
                EndZip = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: ""),
                EndLatitude = table.Column<double>(type: "float", nullable: true),
                EndLongitude = table.Column<double>(type: "float", nullable: true),
                DeliveryWindowStart = table.Column<TimeSpan>(type: "time", nullable: false),
                DeliveryWindowEnd = table.Column<TimeSpan>(type: "time", nullable: false),
                ConfirmationDeadlineHours = table.Column<int>(type: "int", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserRouteSettings", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_UserRouteSettings_UserId",
            table: "UserRouteSettings",
            column: "UserId",
            unique: true);

        // ----------------------------------------------------------------
        // 3. Orders  (FK to Routes — nullable/SetNull)
        // ----------------------------------------------------------------
        migrationBuilder.CreateTable(
            name: "Orders",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                StoreName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Address = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                State = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                Zip = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                County = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: ""),
                LicenseNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: ""),
                District = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: ""),
                Zone = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, defaultValue: ""),
                OrderDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                Phone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                Latitude = table.Column<double>(type: "float", nullable: true),
                Longitude = table.Column<double>(type: "float", nullable: true),
                FormattedAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                ExpectedDeliveryDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                ConfirmationDeadline = table.Column<DateTime>(type: "datetime2", nullable: true),
                RescheduleCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                ConfirmationToken = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                ConfirmedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                RouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                StopSequence = table.Column<int>(type: "int", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                ValidationErrors = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Orders", x => x.Id);
                table.ForeignKey(
                    name: "FK_Orders_Routes_RouteId",
                    column: x => x.RouteId,
                    principalTable: "Routes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Orders_ConfirmationToken",
            table: "Orders",
            column: "ConfirmationToken");

        migrationBuilder.CreateIndex(
            name: "IX_Orders_Email",
            table: "Orders",
            column: "Email");

        migrationBuilder.CreateIndex(
            name: "IX_Orders_ExpectedDeliveryDate",
            table: "Orders",
            column: "ExpectedDeliveryDate");

        migrationBuilder.CreateIndex(
            name: "IX_Orders_OrderDate",
            table: "Orders",
            column: "OrderDate");

        migrationBuilder.CreateIndex(
            name: "IX_Orders_RouteId",
            table: "Orders",
            column: "RouteId");

        migrationBuilder.CreateIndex(
            name: "IX_Orders_Status",
            table: "Orders",
            column: "Status");

        // ----------------------------------------------------------------
        // 4. RouteStops  (FK to Routes — cascade; FK to Orders — no action)
        // ----------------------------------------------------------------
        migrationBuilder.CreateTable(
            name: "RouteStops",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Sequence = table.Column<int>(type: "int", nullable: false),
                Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                Latitude = table.Column<double>(type: "float", nullable: false),
                Longitude = table.Column<double>(type: "float", nullable: false),
                StoreName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                EstimatedArrival = table.Column<DateTime>(type: "datetime2", nullable: true),
                EstimatedDeparture = table.Column<DateTime>(type: "datetime2", nullable: true),
                ServiceTimeMinutes = table.Column<int>(type: "int", nullable: false, defaultValue: 15),
                LegDistanceMeters = table.Column<double>(type: "float", nullable: false),
                LegDurationSeconds = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RouteStops", x => x.Id);
                table.ForeignKey(
                    name: "FK_RouteStops_Routes_RouteId",
                    column: x => x.RouteId,
                    principalTable: "Routes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                // NoAction avoids multiple-cascade-path conflict (Orders also cascade-deletes
                // Confirmations). Application logic removes RouteStops before deleting Orders.
                table.ForeignKey(
                    name: "FK_RouteStops_Orders_OrderId",
                    column: x => x.OrderId,
                    principalTable: "Orders",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.NoAction);
            });

        migrationBuilder.CreateIndex(
            name: "IX_RouteStops_OrderId",
            table: "RouteStops",
            column: "OrderId");

        migrationBuilder.CreateIndex(
            name: "IX_RouteStops_RouteId_Sequence",
            table: "RouteStops",
            columns: new[] { "RouteId", "Sequence" });

        // ----------------------------------------------------------------
        // 5. Confirmations  (FK to Orders — cascade)
        // ----------------------------------------------------------------
        migrationBuilder.CreateTable(
            name: "Confirmations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                Token = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                EmailSentTo = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                SmsSentTo = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                EmailSentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                SmsSentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                ConfirmedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                ConfirmedBy = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                AttemptNumber = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Confirmations", x => x.Id);
                table.ForeignKey(
                    name: "FK_Confirmations_Orders_OrderId",
                    column: x => x.OrderId,
                    principalTable: "Orders",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Confirmations_ExpiresAt",
            table: "Confirmations",
            column: "ExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_Confirmations_OrderId",
            table: "Confirmations",
            column: "OrderId");

        migrationBuilder.CreateIndex(
            name: "IX_Confirmations_Status",
            table: "Confirmations",
            column: "Status");

        migrationBuilder.CreateIndex(
            name: "IX_Confirmations_Token",
            table: "Confirmations",
            column: "Token",
            unique: true);

        // ----------------------------------------------------------------
        // 6. AuditLogs  (FK to Orders — nullable/SetNull)
        // ----------------------------------------------------------------
        migrationBuilder.CreateTable(
            name: "AuditLogs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                RouteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                EventName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Details = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                PreviousStatus = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                NewStatus = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                Success = table.Column<bool>(type: "bit", nullable: false),
                ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                AgentName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                OccurredAt = table.Column<DateTime>(type: "datetime2", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuditLogs", x => x.Id);
                table.ForeignKey(
                    name: "FK_AuditLogs_Orders_OrderId",
                    column: x => x.OrderId,
                    principalTable: "Orders",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AuditLogs_EventType",
            table: "AuditLogs",
            column: "EventType");

        migrationBuilder.CreateIndex(
            name: "IX_AuditLogs_OccurredAt",
            table: "AuditLogs",
            column: "OccurredAt");

        migrationBuilder.CreateIndex(
            name: "IX_AuditLogs_OrderId",
            table: "AuditLogs",
            column: "OrderId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Drop in reverse dependency order
        migrationBuilder.DropTable(name: "AuditLogs");
        migrationBuilder.DropTable(name: "Confirmations");
        migrationBuilder.DropTable(name: "RouteStops");
        migrationBuilder.DropTable(name: "Orders");
        migrationBuilder.DropTable(name: "UserRouteSettings");
        migrationBuilder.DropTable(name: "Routes");
    }
}
