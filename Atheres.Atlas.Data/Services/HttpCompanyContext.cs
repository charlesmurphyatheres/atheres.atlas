using Microsoft.AspNetCore.Http;

namespace Atheres.Atlas.Data.Services;

/// <summary>
/// Reads the CompanyId from the "companyId" JWT claim via IHttpContextAccessor.
/// Registered as Scoped — one instance per HTTP request.
/// </summary>
public sealed class HttpCompanyContext : ICompanyContext
{
    private readonly IHttpContextAccessor _http;

    public HttpCompanyContext(IHttpContextAccessor http) => _http = http;

    public Guid? CompanyId
    {
        get
        {
            var claim = _http.HttpContext?.User?.FindFirst("companyId")?.Value;
            return Guid.TryParse(claim, out var id) ? id : null;
        }
    }
}
