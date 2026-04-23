using Microsoft.AspNetCore.Http;

namespace Atheres.Atlas.Data.Services;

/// <summary>
/// Reads the CompanyId from the "companyId" JWT claim via IHttpContextAccessor.
/// For SuperAdmin users, honors the X-Company-Id header to let them act on behalf of a company.
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
            var ctx = _http.HttpContext;
            if (ctx is null) return null;

            // SuperAdmins may override with X-Company-Id header (empty header = "all companies")
            var isSuperAdmin = ctx.User?.IsInRole("SuperAdmin") ?? false;
            if (isSuperAdmin &&
                ctx.Request.Headers.TryGetValue("X-Company-Id", out var headerValue))
            {
                var headerStr = headerValue.ToString();
                if (string.IsNullOrWhiteSpace(headerStr)) return null; // "all companies"
                if (Guid.TryParse(headerStr, out var hid)) return hid;
            }

            var claim = ctx.User?.FindFirst("companyId")?.Value;
            return Guid.TryParse(claim, out var id) ? id : null;
        }
    }
}
