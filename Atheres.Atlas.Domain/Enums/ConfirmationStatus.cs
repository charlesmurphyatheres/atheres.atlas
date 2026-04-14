namespace Atheres.Atlas.Domain.Enums;

public enum ConfirmationStatus
{
    Pending = 0,
    SentEmail = 1,
    SentSms = 2,
    Confirmed = 3,
    Expired = 4,
    Failed = 5
}
