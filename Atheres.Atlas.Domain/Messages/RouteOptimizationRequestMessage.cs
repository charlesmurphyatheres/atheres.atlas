namespace Atheres.Atlas.Domain.Messages;

public record RouteOptimizationRequestMessage(
    Guid                 RouteRequestId,
    Guid                 CompanyId,
    Guid?                TruckId,
    Guid                 HubId,
    Guid?                WarehouseId,
    DateTime             DeliveryDate,
    IReadOnlyList<Guid>  OrderIds,
    DateTime             RequestedAt
);
