namespace Atheres.Atlas.Domain.Entities;

/// <summary>
/// Classifies a <see cref="DeliveryRoute"/> by its role in the
/// warehouse-pickup → hub-sort → zone-delivery pipeline.
/// </summary>
public enum RouteType
{
    /// <summary>
    /// Legacy single-leg route created before the hub-sort flow was
    /// introduced. Existing rows backfill to this value so the new
    /// orchestration logic can leave them untouched.
    /// </summary>
    Legacy = 0,

    /// <summary>
    /// Pickup-only run: a dedicated van travels from the warehouse to the
    /// nearest hub. No delivery stops. One per warehouse per day.
    /// </summary>
    Pickup = 1,

    /// <summary>
    /// Zoned delivery: van leaves the hub after the sort wait elapses and
    /// delivers orders that all belong to a single Zone. Max stops per
    /// route is enforced as a hard cap (default 5).
    /// </summary>
    ZonedDelivery = 2,

    /// <summary>
    /// Direct delivery: when every order in a pickup happens to land in the
    /// same zone, the van can skip the hub entirely — warehouse → stops
    /// directly. Same 5-stop cap applies to the delivery stops; the
    /// warehouse pickup is not counted toward the cap.
    /// </summary>
    DirectDelivery = 3,
}
