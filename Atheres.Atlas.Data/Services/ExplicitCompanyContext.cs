namespace Atheres.Atlas.Data.Services;

/// <summary>
/// Sets CompanyId explicitly — used in Service Bus / timer triggers that receive
/// a CompanyId in the message payload and need to scope their DbContext accordingly.
/// </summary>
public sealed class ExplicitCompanyContext : ICompanyContext
{
    public Guid? CompanyId { get; }

    public ExplicitCompanyContext(Guid? companyId) => CompanyId = companyId;

    /// <summary>System context — no tenant filter applied.</summary>
    public static readonly ICompanyContext System = new ExplicitCompanyContext(null);
}
