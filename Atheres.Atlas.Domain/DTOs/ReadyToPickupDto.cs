using System.Text.Json.Serialization;

namespace Atheres.Atlas.Domain.DTOs;

/// <summary>
/// Called by a warehouse system when goods are ready for pickup.
/// Confirms the actual pickup time and specifies which orders are in the batch.
/// Creates a batch and triggers route optimization.
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

    /// <summary>
    /// SKUs of products that are ready for pickup.
    /// Only order items matching these SKUs are confirmed ready.
    /// Orders with no ready items stay in Ordered status.
    /// If empty/null, all items are assumed ready.
    /// </summary>
    [JsonPropertyName("readySkus")]
    public List<string>? ReadySkus { get; set; }
}
