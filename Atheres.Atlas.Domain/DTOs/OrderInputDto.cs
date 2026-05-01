using System.Text.Json.Serialization;

namespace Atheres.Atlas.Domain.DTOs;

/// <summary>
/// Incoming order from warehouse systems. Identified by license numbers.
/// Store and warehouse details are resolved from the master data; the order
/// is created at sales-order level (no per-line product detail).
/// </summary>
public class OrderInputDto
{
    /// <summary>Company slug or ID to identify which transport company this order belongs to.</summary>
    [JsonPropertyName("companySlug")]
    public string CompanySlug { get; set; } = string.Empty;

    [JsonPropertyName("warehouseLicenseNumber")]
    public string WarehouseLicenseNumber { get; set; } = string.Empty;

    [JsonPropertyName("storeLicenseNumber")]
    public string StoreLicenseNumber { get; set; } = string.Empty;

    [JsonPropertyName("orderDate")]
    public DateTime OrderDate { get; set; }

    [JsonPropertyName("salesOrderNumber")]
    public string? SalesOrderNumber { get; set; }

    [JsonPropertyName("purchaseOrderNumber")]
    public string? PurchaseOrderNumber { get; set; }

    [JsonPropertyName("customer")]
    public string? Customer { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}
