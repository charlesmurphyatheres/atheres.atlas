namespace Atheres.Atlas.Domain.Entities;

/// <summary>
/// A product in the catalog. Created or updated automatically when orders
/// come in. Keyed by SKU for reuse across warehouses and orders.
/// </summary>
public class Product
{
    public Guid   Id   { get; set; } = Guid.NewGuid();
    public string Sku  { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public string? Category    { get; set; }
    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ICollection<OrderItem> OrderItems { get; set; } = [];
}
