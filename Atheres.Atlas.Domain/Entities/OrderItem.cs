namespace Atheres.Atlas.Domain.Entities;

/// <summary>
/// A line item on an order — a specific product and quantity to be delivered.
/// </summary>
public class OrderItem
{
    public Guid Id       { get; set; } = Guid.NewGuid();
    public Guid OrderId  { get; set; }
    public Guid ProductId { get; set; }

    public int    Quantity { get; set; } = 1;

    /// <summary>Denormalized from Product at creation time for display.</summary>
    public string Sku     { get; set; } = string.Empty;
    public string Name    { get; set; } = string.Empty;

    /// <summary>
    /// True when the warehouse confirms this item is ready for pickup.
    /// Items not confirmed are excluded from the batch.
    /// </summary>
    public bool IsConfirmedReady { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Order   Order   { get; set; } = null!;
    public Product Product { get; set; } = null!;
}
