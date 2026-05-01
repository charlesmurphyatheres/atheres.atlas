using System.Text.Json.Serialization;

namespace Atheres.Atlas.Domain.DTOs;

/// <summary>
/// Called by a warehouse system when goods are ready for pickup.
/// Pulls every Ordered order at the warehouse into a batch and triggers
/// route optimization.
/// </summary>
public class ReadyToPickupDto
{
    /// <summary>Company slug or ID.</summary>
    [JsonPropertyName("companySlug")]
    public string CompanySlug { get; set; } = string.Empty;

    [JsonPropertyName("warehouseLicenseNumber")]
    public string WarehouseLicenseNumber { get; set; } = string.Empty;

    [JsonPropertyName("pickupDateTime")]
    public DateTime PickupDateTime { get; set; }
}
