using System.Text.Json.Serialization;

namespace Atheres.Atlas.Domain.DTOs;

/// <summary>
/// Incoming order from warehouse systems. Identified by license numbers.
/// Store and warehouse details are resolved from the master data.
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

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("items")]
    public List<OrderItemInputDto> Items { get; set; } = [];
}

public class OrderItemInputDto
{
    [JsonPropertyName("sku")]
    public string Sku { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; } = 1;

    [JsonPropertyName("category")]
    public string? Category { get; set; }
}
