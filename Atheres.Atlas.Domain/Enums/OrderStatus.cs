namespace Atheres.Atlas.Domain.Enums;

public enum OrderStatus
{
    Ordered = 0,
    Scheduled = 1,
    RouteOptimized = 2,
    ConfirmationPending = 3,
    Confirmed = 4,
    Rejected = 5,
    OutForDelivery = 6,
    Delivered = 7,
    Archived = 8,
    Cancelled = 9,

    /// <summary>
    /// Order was a candidate for routing but is more than a day old. The
    /// system flips stale rows here instead of dispatching a truck for work
    /// the warehouse has likely already resolved another way. Terminal —
    /// rows in this state are not picked up by future optimization runs.
    /// </summary>
    RouteOmitted = 10,

    /// <summary>
    /// Routed and scheduled via the Email scheduling method, but the store
    /// recipient has not yet clicked Confirm or Cancel in the email. Treated
    /// as a soft-confirmation: the route runs as planned unless the
    /// recipient cancels, at which point the order flips to Standby.
    /// </summary>
    Tentative = 11,

    /// <summary>
    /// Store explicitly declined the proposed delivery slot (via the Cancel
    /// link in the scheduling email, or via the scheduling provider). Held
    /// out of routing until an administrator re-promotes or cancels it.
    /// </summary>
    Standby = 12,
}
