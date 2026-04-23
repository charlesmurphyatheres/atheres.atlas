using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.DependencyInjection;

namespace Atheres.Atlas.Functions.Middleware;

/// <summary>
/// Authenticates incoming HTTP requests against the JWT bearer scheme and
/// assigns the resulting ClaimsPrincipal to HttpContext.User.
///
/// The Functions isolated worker + AspNetCore integration package registers
/// authentication SERVICES but does not wire AuthN/AuthZ middleware into the
/// request pipeline — so [Authorize] attributes silently pass and
/// HttpContext.User stays unauthenticated. This middleware bridges that gap.
/// </summary>
public sealed class JwtAuthMiddleware : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext is not null)
        {
            // IHttpContextAccessor is not wired up by default in Functions isolated,
            // so scoped services (ICompanyContext, AtlasDbContext) don't see the
            // current HttpContext. Push it onto the accessor explicitly.
            var accessor = context.InstanceServices.GetService<IHttpContextAccessor>();
            if (accessor is not null) accessor.HttpContext = httpContext;

            if (httpContext.Request.Headers.ContainsKey("Authorization"))
            {
                var result = await httpContext.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
                if (result.Succeeded && result.Principal is not null)
                    httpContext.User = result.Principal;
            }
        }

        await next(context);
    }
}
