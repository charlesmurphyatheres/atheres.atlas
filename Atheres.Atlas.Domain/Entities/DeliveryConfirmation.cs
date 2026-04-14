using Atheres.Atlas.Domain.Enums;

namespace Atheres.Atlas.Domain.Entities;

public class DeliveryConfirmation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }

    public ConfirmationStatus Status { get; set; } = ConfirmationStatus.Pending;
    public string Token { get; set; } = Guid.NewGuid().ToString("N");

    public string? EmailSentTo { get; set; }
    public string? SmsSentTo { get; set; }
    public DateTime? EmailSentAt { get; set; }
    public DateTime? SmsSentAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public string? ConfirmedBy { get; set; } // "email" or "sms"

    public DateTime ExpiresAt { get; set; }
    public int AttemptNumber { get; set; } = 1;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation property
    public Order? Order { get; set; }

    public bool IsExpired => DateTime.UtcNow > ExpiresAt && Status != ConfirmationStatus.Confirmed;
}
