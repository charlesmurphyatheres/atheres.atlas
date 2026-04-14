namespace Atheres.Atlas.Domain.Enums;

public enum AuditEventType
{
    OrderReceived = 0,
    OrderValidated = 1,
    OrderValidationFailed = 2,
    RouteOptimizationStarted = 3,
    RouteOptimizationCompleted = 4,
    RouteOptimizationFailed = 5,
    ConfirmationEmailSent = 6,
    ConfirmationSmsSent = 7,
    ConfirmationReceived = 8,
    ConfirmationExpired = 9,
    OrderRescheduled = 10,
    OrderDelivered = 11,
    OrderCancelled = 12,
    NotificationSent = 13,
    SystemError = 14,
    OrderStatusChanged = 15,
    OrderDeferredToHub = 16,
    WarehouseCreated = 17,
    WarehouseUpdated = 18,
    BatchCreatedViaPickup = 19,
    OrderArchived = 20,
    ConfirmationRejected = 21
}
