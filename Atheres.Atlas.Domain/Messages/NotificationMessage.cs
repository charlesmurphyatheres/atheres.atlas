using Atheres.Atlas.Domain.Enums;

namespace Atheres.Atlas.Domain.Messages;

public record NotificationMessage(
    Guid                         NotificationId,
    Guid                         CompanyId,
    NotificationType             Type,
    string                       Title,
    string                       Body,
    Guid?                        OrderId,
    Guid?                        RouteId,
    string?                      UserId,
    IDictionary<string, string>? Metadata,
    DateTime                     OccurredAt
);
