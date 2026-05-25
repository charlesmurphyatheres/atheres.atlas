using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

// Per-warehouse loading wait. Applied wherever a van visits a warehouse:
//   Pickup round trip: between the Google outbound + inbound legs.
//   DirectDelivery:    added to runningTime before the first delivery.
//   Legacy w/ warehouse: same — runningTime + LoadingWaitMinutes after pickup leg.
// Scheduler also folds it into the Haversine pickup-return estimate so
// downstream ZonedDelivery vans wait the right amount of time before
// dispatching from the hub. Default 15 chosen to mirror the
// per-stop wait (DefaultWaitMinutesPerStop) — adjust per-warehouse in
// the Admin → Warehouses tab.
[DbContext(typeof(AtlasDbContext))]
[Migration("20260524000005_AddWarehouseLoadingWaitMinutes")]
public partial class AddWarehouseLoadingWaitMinutes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name:         "LoadingWaitMinutes",
            table:        "Warehouses",
            nullable:     false,
            defaultValue: 15);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("LoadingWaitMinutes", "Warehouses");
    }
}
