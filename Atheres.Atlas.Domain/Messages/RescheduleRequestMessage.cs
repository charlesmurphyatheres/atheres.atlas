namespace Atheres.Atlas.Domain.Messages;

public record RescheduleRequestMessage(
    Guid     OrderId,
    Guid     CompanyId,
    string   StoreName,
    string   FullAddress,
    string   Email,
    string?  Phone,
    string   District,
    string   Zone,
    DateTime OriginalDeliveryDate,
    DateTime NewDeliveryDate,
    int      RescheduleCount,
    string   Reason,
    DateTime RequestedAt
);
