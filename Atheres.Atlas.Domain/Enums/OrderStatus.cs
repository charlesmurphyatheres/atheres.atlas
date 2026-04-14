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
    Cancelled = 9
}
