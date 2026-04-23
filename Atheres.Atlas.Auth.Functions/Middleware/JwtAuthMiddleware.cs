using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.DependencyInjection;

namespace Atheres.Atlas.Auth.Functions.Middleware;

/// <summary>
/// Runs JWT bearer authentication on every HTTP request so that
/// HttpContext.User is populated even on endpoints not decorated with
/// [Authorize]. Also pushes the HttpContext onto IHttpContextAccessor so
/// scoped services (e.g. ICompanyContext) can reach the current request.
/// Without this, the Functions isolated worker registers authentication
/// services but never invokes them.
/// </summary>
public sealed class JwtAuthMiddleware : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext is not null)
        {
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
