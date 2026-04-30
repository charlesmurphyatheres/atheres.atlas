using System;
using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

// Promotes three columns from UserRouteSettings to Companies, since they
// represent company-wide policy (operating hours, route ceiling) rather than
// per-truck preferences:
//   * DeliveryWindowStart  - earliest delivery time of day        (default 08:00)
//   * DeliveryWindowEnd    - latest delivery time of day          (default 17:00)
//   * MaxStopsPerRoute     - cap on deliveries per route          (default 12)
//
// Up:
//   1. Add the three columns to Companies with sensible defaults.
//   2. Backfill from each company's UserRouteSettings.default row when one
//      exists, so settings already configured pre-migration carry over.
//   3. Drop the three columns from UserRouteSettings (the previous migration
//      created MaxStopsPerRoute; the original schema created the window
//      columns — both go away here).
//
// Down recreates the UserRouteSettings columns and copies values back, so a
// rollback preserves data even though it loses the company-scoped
// granularity.
[DbContext(typeof(AtlasDbContext))]
[Migration("20260430090200_MoveRoutePoliciesToCompany")]
public partial class MoveRoutePoliciesToCompany : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // 1. Add columns to Companies with defaults so existing rows are valid.
        migrationBuilder.AddColumn<TimeSpan>(
            name: "DeliveryWindowStart",
            table: "Companies",
            type: "time",
            nullable: false,
            defaultValue: new TimeSpan(8, 0, 0));

        migrationBuilder.AddColumn<TimeSpan>(
            name: "DeliveryWindowEnd",
            table: "Companies",
            type: "time",
            nullable: false,
            defaultValue: new TimeSpan(17, 0, 0));

        migrationBuilder.AddColumn<int>(
            name: "MaxStopsPerRoute",
            table: "Companies",
            type: "int",
            nullable: false,
            defaultValue: 12);

        // 2. Backfill from the per-company "default" UserRouteSettings row so
        //    any pre-existing customizations survive the move.
        migrationBuilder.Sql(@"
            UPDATE c
            SET   c.DeliveryWindowStart = s.DeliveryWindowStart,
                  c.DeliveryWindowEnd   = s.DeliveryWindowEnd,
                  c.MaxStopsPerRoute    = s.MaxStopsPerRoute
            FROM  Companies c
            INNER JOIN UserRouteSettings s
                  ON  s.CompanyId = c.Id
                  AND s.UserId    = N'default';");

        // 3. Drop the now-redundant columns from UserRouteSettings.
        migrationBuilder.DropColumn("MaxStopsPerRoute",    "UserRouteSettings");
        migrationBuilder.DropColumn("DeliveryWindowStart", "UserRouteSettings");
        migrationBuilder.DropColumn("DeliveryWindowEnd",   "UserRouteSettings");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Recreate the columns on UserRouteSettings.
        migrationBuilder.AddColumn<TimeSpan>(
            name: "DeliveryWindowStart",
            table: "UserRouteSettings",
            type: "time",
            nullable: false,
            defaultValue: new TimeSpan(8, 0, 0));

        migrationBuilder.AddColumn<TimeSpan>(
            name: "DeliveryWindowEnd",
            table: "UserRouteSettings",
            type: "time",
            nullable: false,
            defaultValue: new TimeSpan(17, 0, 0));

        migrationBuilder.AddColumn<int>(
            name: "MaxStopsPerRoute",
            table: "UserRouteSettings",
            type: "int",
            nullable: false,
            defaultValue: 12);

        // Push values back from Company onto every UserRouteSettings row of
        // that company so rolling back doesn't silently reset to defaults.
        migrationBuilder.Sql(@"
            UPDATE s
            SET   s.DeliveryWindowStart = c.DeliveryWindowStart,
                  s.DeliveryWindowEnd   = c.DeliveryWindowEnd,
                  s.MaxStopsPerRoute    = c.MaxStopsPerRoute
            FROM  UserRouteSettings s
            INNER JOIN Companies c ON c.Id = s.CompanyId;");

        // Drop the columns from Companies.
        migrationBuilder.DropColumn("MaxStopsPerRoute",    "Companies");
        migrationBuilder.DropColumn("DeliveryWindowEnd",   "Companies");
        migrationBuilder.DropColumn("DeliveryWindowStart", "Companies");
    }
}
