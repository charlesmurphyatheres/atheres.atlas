using Atheres.Atlas.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Atheres.Atlas.Data.Migrations;

// Per-store scheduling integration columns. Drives Atheres.Atlas.Scheduling's
// provider factory: Stores.SchedulingMethod (None / Booking / Calendly /
// Email) determines which set of credential columns is read at availability
// + scheduling time. All credential columns are nullable — only the ones
// relevant to the chosen method are populated. SchedulingMethod is NOT NULL
// with a default of 0 (None) so the rolling backfill picks up existing rows
// without explicit values.
[DbContext(typeof(AtlasDbContext))]
[Migration("20260531000001_AddStoreSchedulingFields")]
public partial class AddStoreSchedulingFields : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name:         "SchedulingMethod",
            table:        "Stores",
            nullable:     false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name:     "BookingClientId",
            table:    "Stores",
            type:     "nvarchar(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name:     "BookingClientSecret",
            table:    "Stores",
            type:     "nvarchar(500)",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name:     "BookingCalendarName",
            table:    "Stores",
            type:     "nvarchar(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name:     "CalendlyAccessToken",
            table:    "Stores",
            type:     "nvarchar(500)",
            maxLength: 500,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name:     "CalendlyCalendarName",
            table:    "Stores",
            type:     "nvarchar(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name:     "SchedulingEmailRecipients",
            table:    "Stores",
            type:     "nvarchar(2000)",
            maxLength: 2000,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("SchedulingEmailRecipients", "Stores");
        migrationBuilder.DropColumn("CalendlyCalendarName",      "Stores");
        migrationBuilder.DropColumn("CalendlyAccessToken",       "Stores");
        migrationBuilder.DropColumn("BookingCalendarName",       "Stores");
        migrationBuilder.DropColumn("BookingClientSecret",       "Stores");
        migrationBuilder.DropColumn("BookingClientId",           "Stores");
        migrationBuilder.DropColumn("SchedulingMethod",          "Stores");
    }
}
