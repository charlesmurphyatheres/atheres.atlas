namespace Atheres.Atlas.Scheduling.Abstractions;

/// <summary>
/// Thrown when a provider is asked to act but the per-store settings are
/// missing required fields (e.g. Calendly PAT empty, Bookings client
/// secret blank). The Functions caller surfaces the message verbatim to
/// the admin UI so the operator knows which field to fix.
/// </summary>
public sealed class SchedulingConfigurationException : Exception
{
    public SchedulingConfigurationException(string message) : base(message) { }
}
