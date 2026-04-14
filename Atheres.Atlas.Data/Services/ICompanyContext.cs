namespace Atheres.Atlas.Data.Services;

/// <summary>
/// Provides the current tenant's CompanyId for scoping EF Core queries via global filters.
/// Implementations:
///   HttpCompanyContext  — reads the "companyId" JWT claim from IHttpContextAccessor (HTTP triggers)
///   SystemCompanyContext — returns null (timer / background triggers that operate across all tenants)
/// </summary>
public interface ICompanyContext
{
    /// <summary>
    /// The active company ID, or null when running in a system/cross-tenant context
    /// (e.g. timer triggers, SuperAdmin operations).
    /// </summary>
    Guid? CompanyId { get; }

    bool IsSystemContext => CompanyId is null;
}
