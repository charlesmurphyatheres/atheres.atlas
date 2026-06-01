using Atheres.Atlas.Domain.Entities;
using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Borders;
using iText.Layout.Element;
using iText.Layout.Properties;

namespace Atheres.Atlas.Functions.Services;

/// <summary>
/// Server-side PDF generation backed by iText. Used for two artifacts
/// today:
///   * Route itineraries (one page per route, table of stops with
///     name / address / leg miles / arrive / depart).
///   * Optimization audits (multi-page reproduction of the audit body
///     captured by <see cref="RouteScheduler"/>).
///
/// Stateless — every method writes to a fresh MemoryStream and returns
/// the bytes so the caller can stamp content-disposition and ship it
/// straight to the client.
///
/// LICENSE NOTE: iText 7 on NuGet is AGPL. Atlas needs a commercial
/// iText license for non-OSS production use; the package reference in
/// the .csproj carries the same warning.
/// </summary>
public class PdfService
{
    /// <summary>
    /// Single-page itinerary for one delivery route. Lists every leg the
    /// driver will perform — the route's start (hub or warehouse), the
    /// optional warehouse pickup, every numbered delivery stop, and the
    /// return to the hub — with leg distance, arrival time, and a
    /// derived departure time per row.
    /// </summary>
    /// <param name="route">Route entity with Stops + Stops.Order + Warehouse + Hub eager-loaded.</param>
    /// <param name="serviceMinutesPerStop">Per-stop service time used to derive the
    /// departure column from each row's arrival. Pass the truck's
    /// <see cref="UserRouteSettings.WaitMinutesPerStop"/> or the
    /// company default; falls back to 15 if zero is passed.</param>
    public byte[] BuildRouteItinerary(DeliveryRoute route, int serviceMinutesPerStop)
    {
        if (serviceMinutesPerStop <= 0)
            serviceMinutesPerStop = UserRouteSettings.DefaultWaitMinutesPerStop;

        using var ms = new MemoryStream();
        using (var writer = new PdfWriter(ms))
        using (var pdf    = new PdfDocument(writer))
        {
            var doc = new Document(pdf, PageSize.LETTER);
            doc.SetMargins(36, 36, 36, 36);

            // ---- Title + meta ------------------------------------------------
            doc.Add(new Paragraph("Atheres Atlas — Route Itinerary")
                .SetBold()
                .SetFontSize(14));

            doc.Add(new Paragraph(BuildRouteSubtitle(route))
                .SetFontSize(9)
                .SetFontColor(ColorConstants.GRAY)
                .SetMarginTop(-4));

            // Two-column meta block: depart/return + dist/duration + counts.
            var meta = new Table(UnitValue.CreatePercentArray(new float[] { 1f, 1f }))
                .UseAllAvailableWidth()
                .SetMarginTop(8)
                .SetBorder(Border.NO_BORDER);
            meta.AddCell(MetaCell("Delivery date",  route.DeliveryDate.ToString("yyyy-MM-dd")));
            meta.AddCell(MetaCell("Route type",     route.RouteType.ToString()));
            if (route.ScheduledDepartTime.HasValue)
                meta.AddCell(MetaCell("Depart",     route.ScheduledDepartTime.Value.ToString("HH:mm")));
            else
                meta.AddCell(MetaCell("Depart",     "—"));
            if (route.HubArrivalTime.HasValue)
                meta.AddCell(MetaCell("Return to hub", route.HubArrivalTime.Value.ToString("HH:mm")));
            else
                meta.AddCell(MetaCell("Return to hub", "—"));
            meta.AddCell(MetaCell("Total distance", $"{route.TotalDistanceMiles:F1} mi"));
            meta.AddCell(MetaCell("Total duration", route.TotalDuration.ToString(@"h\:mm")));
            meta.AddCell(MetaCell("Stops",          route.TotalStops.ToString()));
            meta.AddCell(MetaCell("Route id",       route.Id.ToString("N").Substring(0, 8)));
            doc.Add(meta);

            // ---- Itinerary table --------------------------------------------
            // Column widths sum to 100%. Stop # is narrow; Store + Address
            // take the bulk; leg miles + arrive + depart sit on the right.
            var table = new Table(UnitValue.CreatePercentArray(new float[] { 4f, 22f, 38f, 9f, 9f, 9f, 9f }))
                .UseAllAvailableWidth()
                .SetMarginTop(14);

            // Header row
            foreach (var label in new[] { "#", "Store / Stop", "Address", "City / ST / Zip", "Miles", "Arrive", "Depart" })
                table.AddHeaderCell(HeaderCell(label));

            // Build the rows in driver-order: Start, optional Warehouse, then numbered stops, then End.
            var rows = BuildItineraryRows(route, serviceMinutesPerStop);
            foreach (var row in rows)
            {
                table.AddCell(BodyCell(row.Marker,   alignRight: false));
                table.AddCell(BodyCell(row.Name,     alignRight: false));
                table.AddCell(BodyCell(row.Address,  alignRight: false));
                table.AddCell(BodyCell(row.CityLine, alignRight: false));
                table.AddCell(BodyCell(row.Miles,    alignRight: true));
                table.AddCell(BodyCell(row.Arrive,   alignRight: true));
                table.AddCell(BodyCell(row.Depart,   alignRight: true));
            }

            doc.Add(table);

            doc.Add(new Paragraph($"Per-stop service: {serviceMinutesPerStop} min (default applied when no per-truck setting found). Depart times derived from Arrive + service time.")
                .SetFontSize(7)
                .SetFontColor(ColorConstants.GRAY)
                .SetMarginTop(12));

            doc.Close();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Multi-page reproduction of the optimization audit body — the same
    /// monospace block the AdminPanel detail view renders, but as a
    /// proper PDF for archive / share. Paginated automatically by iText
    /// (no manual cursor tracking like jsPDF needed).
    /// </summary>
    public byte[] BuildAuditPdf(OptimizationAudit audit)
    {
        using var ms = new MemoryStream();
        using (var writer = new PdfWriter(ms))
        using (var pdf    = new PdfDocument(writer))
        {
            var doc = new Document(pdf, PageSize.LETTER);
            doc.SetMargins(36, 36, 36, 36);

            doc.Add(new Paragraph("Atheres Atlas — Optimization Audit")
                .SetBold()
                .SetFontSize(14));

            var meta = new Table(UnitValue.CreatePercentArray(new float[] { 1f, 4f }))
                .UseAllAvailableWidth()
                .SetMarginTop(6)
                .SetBorder(Border.NO_BORDER);
            AppendMetaRow(meta, "Run",     audit.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss") + " UTC");
            AppendMetaRow(meta, "Trigger", audit.Trigger);
            AppendMetaRow(meta, "By",      string.IsNullOrWhiteSpace(audit.TriggeredBy) ? "(system)" : audit.TriggeredBy!);
            AppendMetaRow(meta, "Summary", audit.Summary);
            doc.Add(meta);

            // Counts banner
            doc.Add(new Paragraph(
                $"Orders {audit.OrderCount}  ·  Routes {audit.RouteCount}  ·  "
                + $"Warehouses {audit.WarehouseCount}  ·  Hubs {audit.HubCount}  ·  "
                + $"Zones {audit.ZoneCount}  ·  Hub-bypass {audit.DirectDeliveryCount}")
                .SetFontSize(9)
                .SetFontColor(ColorConstants.DARK_GRAY)
                .SetMarginTop(6));

            // Body — Courier so the alignment of the audit log (which the
            // scheduler builds with leading spaces / dashes) stays intact.
            // iText handles pagination when content overflows the page.
            var body = new Paragraph(audit.LogText)
                .SetFont(iText.Kernel.Font.PdfFontFactory.CreateFont(
                    iText.IO.Font.Constants.StandardFonts.COURIER))
                .SetFontSize(8)
                .SetMarginTop(12);
            doc.Add(body);

            doc.Close();
        }
        return ms.ToArray();
    }

    // -----------------------------------------------------------------------
    // Itinerary row builder
    // -----------------------------------------------------------------------

    private sealed record ItineraryRow(
        string Marker, string Name, string Address, string CityLine,
        string Miles, string Arrive, string Depart);

    private static IReadOnlyList<ItineraryRow> BuildItineraryRows(DeliveryRoute route, int serviceMinutesPerStop)
    {
        var rows = new List<ItineraryRow>();

        // Start row — the depot the driver leaves from. For Pickup +
        // ZonedDelivery + Legacy this is the hub; for DirectDelivery it
        // is the warehouse (since DirectDelivery vans skip the hub).
        rows.Add(new ItineraryRow(
            Marker:  "S",
            Name:    StartLabel(route),
            Address: route.StartAddress,
            CityLine: "",
            Miles:   "—",
            Arrive:  "—",
            Depart:  route.ScheduledDepartTime?.ToString("HH:mm") ?? "—"));

        // Warehouse pickup, when this route visits one and it isn't the
        // route's literal start. For Pickup the "warehouse" is in the
        // middle of the trip (hub → warehouse → hub); for Legacy
        // with-warehouse it's the pinned first waypoint after the hub.
        if (route.Warehouse is not null && route.RouteType != RouteType.DirectDelivery)
        {
            var (whArrive, whDepart) = ComputeWarehouseTimes(route);
            rows.Add(new ItineraryRow(
                Marker:  "W",
                Name:    string.IsNullOrWhiteSpace(route.Warehouse.AlternateName)
                            ? route.Warehouse.BusinessName
                            : route.Warehouse.AlternateName!,
                Address: route.Warehouse.Address,
                CityLine: $"{route.Warehouse.City}, {route.Warehouse.State} {route.Warehouse.Zip}".Trim(' ', ','),
                Miles:   "—",
                Arrive:  whArrive,
                Depart:  whDepart));
        }

        // Numbered delivery stops, sequenced. Each stop's Miles column is
        // its inbound leg distance (driver's mileage from the previous
        // node to this stop). Arrive is the stop's persisted ETA;
        // Depart is Arrive + the per-stop service time.
        var orderedStops = route.Stops.OrderBy(s => s.Sequence).ToList();
        foreach (var stop in orderedStops)
        {
            string arrive = stop.EstimatedArrival?.ToString("HH:mm") ?? "—";
            string depart = stop.EstimatedArrival.HasValue
                ? stop.EstimatedArrival.Value.AddMinutes(serviceMinutesPerStop).ToString("HH:mm")
                : "—";

            var order = stop.Order;
            string city = order is null
                ? ""
                : $"{order.City}, {order.State} {order.Zip}".Trim(' ', ',');

            rows.Add(new ItineraryRow(
                Marker:  stop.Sequence.ToString(),
                Name:    stop.StoreName,
                Address: stop.Address,
                CityLine: city,
                Miles:   $"{(stop.LegDistanceMeters * 0.000621371):F1}",
                Arrive:  arrive,
                Depart:  depart));
        }

        // End row — return to hub (or wherever the route terminates). For
        // Pickup we know the exact return time from HubArrivalTime; for
        // delivery routes we currently don't persist final-leg-complete
        // (the optimizer stamps per-stop ETAs but not the close-out leg
        // duration on the route), so leave Arrive blank.
        rows.Add(new ItineraryRow(
            Marker:  "E",
            Name:    "End · Depot",
            Address: route.EndAddress,
            CityLine: "",
            Miles:   "—",
            Arrive:  route.HubArrivalTime?.ToString("HH:mm") ?? "—",
            Depart:  "—"));

        return rows;
    }

    private static (string Arrive, string Depart) ComputeWarehouseTimes(DeliveryRoute route)
    {
        // Pickup: warehouse load is the midpoint of the round trip
        // (50/50 split of outbound + inbound). Depart = arrive + loading
        // wait from the warehouse row.
        if (route.RouteType == RouteType.Pickup
            && route.ScheduledDepartTime.HasValue
            && route.HubArrivalTime.HasValue)
        {
            var d = route.ScheduledDepartTime.Value;
            var a = route.HubArrivalTime.Value;
            if (a > d)
            {
                var mid = new DateTime((d.Ticks + a.Ticks) / 2, d.Kind);
                var depart = mid.AddMinutes(route.Warehouse?.LoadingWaitMinutes ?? 0);
                return (mid.ToString("HH:mm"), depart.ToString("HH:mm"));
            }
        }

        // Legacy with-warehouse / DirectDelivery: no per-leg persistence
        // for outbound vs inbound, so we can't pinpoint warehouse arrival
        // from the route alone. Surface dashes rather than guessing.
        return ("—", "—");
    }

    private static string StartLabel(DeliveryRoute route) => route.RouteType switch
    {
        RouteType.DirectDelivery => "Start · Warehouse",
        _                        => "Start · Depot",
    };

    private static string BuildRouteSubtitle(DeliveryRoute route)
    {
        var kind = route.RouteType switch
        {
            RouteType.Pickup         => "Pickup van — hub → warehouse → hub",
            RouteType.ZonedDelivery  => "Zoned delivery — hub → stops → hub",
            RouteType.DirectDelivery => "Direct delivery — warehouse → stops → hub (hub bypass)",
            _                        => "Legacy route",
        };
        return kind;
    }

    // -----------------------------------------------------------------------
    // Cell helpers
    // -----------------------------------------------------------------------

    private static Cell HeaderCell(string text) => new Cell()
        .Add(new Paragraph(text).SetBold().SetFontSize(8))
        .SetBackgroundColor(new DeviceRgb(245, 245, 245))
        .SetBorder(new SolidBorder(ColorConstants.LIGHT_GRAY, 0.5f))
        .SetPaddingTop(4).SetPaddingBottom(4)
        .SetPaddingLeft(4).SetPaddingRight(4);

    private static Cell BodyCell(string text, bool alignRight) => new Cell()
        .Add(new Paragraph(text ?? "").SetFontSize(8))
        .SetBorder(new SolidBorder(ColorConstants.LIGHT_GRAY, 0.5f))
        .SetTextAlignment(alignRight ? TextAlignment.RIGHT : TextAlignment.LEFT)
        .SetPaddingTop(3).SetPaddingBottom(3)
        .SetPaddingLeft(4).SetPaddingRight(4);

    private static Cell MetaCell(string label, string value)
    {
        var c = new Cell().SetBorder(Border.NO_BORDER);
        c.Add(new Paragraph(label).SetFontSize(7).SetFontColor(ColorConstants.GRAY));
        c.Add(new Paragraph(value).SetFontSize(10).SetBold());
        return c;
    }

    private static void AppendMetaRow(Table t, string label, string value)
    {
        t.AddCell(new Cell()
            .Add(new Paragraph(label).SetFontSize(8).SetFontColor(ColorConstants.GRAY))
            .SetBorder(Border.NO_BORDER));
        t.AddCell(new Cell()
            .Add(new Paragraph(value).SetFontSize(9))
            .SetBorder(Border.NO_BORDER));
    }
}
