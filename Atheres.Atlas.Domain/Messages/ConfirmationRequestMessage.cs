namespace Atheres.Atlas.Domain.Messages;

public record ConfirmationRequestMessage(
    Guid ConfirmationId,
    Guid OrderId,
    Guid CompanyId,
    string StoreName,
    string Email,
    string? Phone,
    string DeliveryAddress,
    DateTime ExpectedDelivery,
    DateTime ConfirmationDeadline,
    string ConfirmationToken,
    int AttemptNumber,
    DateTime RequestedAt
);
