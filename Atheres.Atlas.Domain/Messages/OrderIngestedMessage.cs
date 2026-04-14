namespace Atheres.Atlas.Domain.Messages;

public record OrderIngestedMessage(
    Guid     OrderId,
    Guid     CompanyId,
    string   StoreName,
    string   FullAddress,
    string   Email,
    string?  Phone,
    string   District,
    string   Zone,
    DateTime OrderDate,
    DateTime IngestedAt
);
