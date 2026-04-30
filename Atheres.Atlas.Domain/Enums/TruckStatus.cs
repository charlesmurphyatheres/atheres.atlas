namespace Atheres.Atlas.Domain.Enums;

/// <summary>
/// Operational readiness of a delivery truck. Assignment prefers
/// <see cref="Available"/> trucks first and falls back to
/// <see cref="AvailableWithIssues"/> only when all Available trucks for a
/// hub are exhausted. <see cref="Unavailable"/> trucks are skipped.
/// </summary>
public enum TruckStatus
{
    Available = 0,
    AvailableWithIssues = 1,
    Unavailable = 2,
}
