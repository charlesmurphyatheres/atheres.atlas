using System;
using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

// Schema foundation for the new pickup → hub-sort → zoned-delivery flow.
//
// Adds:
//   * Hubs.SortingWaitMinutes  — per-hub configurable wait between a pickup
//                                van arriving and the per-zone delivery vans
//                                dispatching. Default 30.
//   * Routes.RouteType         — Legacy / Pickup / ZonedDelivery / DirectDelivery
//                                (see RouteType.cs). Existing rows backfill
//                                to Legacy so the new orchestration leaves
//                                them alone.
//   * Routes.ZoneId            — single zone every stop on a delivery route
//                                belongs to. Null for Pickup / Legacy.
//   * Routes.HubArrivalTime    — pickup ETA at the hub (drives paired
//                                delivery routes' ScheduledDepartTime).
//   * Routes.ScheduledDepartTime — earliest dispatch time for delivery routes,
//                                = HubArrivalTime + Hub.SortingWaitMinutes
//                                for ZonedDelivery, or warehouse pickup time
//                                for DirectDelivery.
//
// Does NOT change the routing logic itself yet — that's a follow-up so the
// orchestration design (especially how the wait-period mechanic interacts
// with the optimizer queue) can be reviewed before commit.
[DbContext(typeof(AtlasDbContext))]
[Migration("20260524000003_HubSortAndZonedRoutes")]
public partial class HubSortAndZonedRoutes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name:        "SortingWaitMinutes",
            table:       "Hubs",
            nullable:    false,
            defaultValue: 30);

        migrationBuilder.AddColumn<int>(
            name:        "RouteType",
            table:       "Routes",
            nullable:    false,
            defaultValue: 0); // 0 = RouteType.Legacy

        migrationBuilder.AddColumn<Guid>(
            name:     "ZoneId",
            table:    "Routes",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name:     "HubArrivalTime",
            table:    "Routes",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name:     "ScheduledDepartTime",
            table:    "Routes",
            nullable: true);

        migrationBuilder.CreateIndex("IX_Routes_ZoneId", "Routes", "ZoneId");

        migrationBuilder.AddForeignKey(
            name:            "FK_Routes_Zones_ZoneId",
            table:           "Routes",
            column:          "ZoneId",
            principalTable:  "Zones",
            principalColumn: "Id",
            onDelete:        ReferentialAction.SetNull);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey("FK_Routes_Zones_ZoneId", "Routes");
        migrationBuilder.DropIndex("IX_Routes_ZoneId",            "Routes");
        migrationBuilder.DropColumn("ScheduledDepartTime",        "Routes");
        migrationBuilder.DropColumn("HubArrivalTime",             "Routes");
        migrationBuilder.DropColumn("ZoneId",                     "Routes");
        migrationBuilder.DropColumn("RouteType",                  "Routes");
        migrationBuilder.DropColumn("SortingWaitMinutes",         "Hubs");
    }
}
