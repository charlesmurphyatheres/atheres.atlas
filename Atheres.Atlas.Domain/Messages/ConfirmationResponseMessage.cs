namespace Atheres.Atlas.Domain.Messages;

public record ConfirmationResponseMessage(
    Guid ConfirmationId,
    Guid OrderId,
    string ConfirmationToken,
    string ConfirmedBy,   // "email" or "sms"
    DateTime ConfirmedAt
);
