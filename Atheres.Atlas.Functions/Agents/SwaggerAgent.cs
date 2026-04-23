using System.Net;
using System.Reflection;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Atheres.Atlas.Functions.Agents;

/// <summary>
/// Serves the OpenAPI spec and Swagger UI for the Atlas API.
/// GET /api/swagger.json — OpenAPI 3.0 spec
/// GET /api/swagger     — Swagger UI
/// </summary>
public class SwaggerAgent
{
    [Function("swagger-spec")]
    public async Task<HttpResponseData> GetSpec(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "swagger.json")] HttpRequestData req,
        CancellationToken ct)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "Atheres.Atlas.Functions.swagger.json";

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            // Fallback: read from file
            var dir = Path.GetDirectoryName(assembly.Location)!;
            var filePath = Path.Combine(dir, "swagger.json");
            if (!File.Exists(filePath))
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteStringAsync("swagger.json not found.", ct);
                return notFound;
            }

            var json = await File.ReadAllTextAsync(filePath, ct);
            var resp = req.CreateResponse(HttpStatusCode.OK);
            resp.Headers.Add("Content-Type", "application/json");
            await resp.WriteStringAsync(json, ct);
            return resp;
        }

        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync(ct);
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(content, ct);
        return response;
    }

    [Function("swagger-ui")]
    public async Task<HttpResponseData> GetUI(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "swagger")] HttpRequestData req,
        CancellationToken ct)
    {
        var html = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="UTF-8">
          <title>Atlas Deliver API</title>
          <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/swagger-ui-dist@5/swagger-ui.css">
          <style>
            body { margin: 0; }
            .topbar { display: none !important; }
          </style>
        </head>
        <body>
          <div id="swagger-ui"></div>
          <script src="https://cdn.jsdelivr.net/npm/swagger-ui-dist@5/swagger-ui-bundle.js"></script>
          <script>
            SwaggerUIBundle({
              url: '/api/swagger.json',
              dom_id: '#swagger-ui',
              deepLinking: true,
              presets: [SwaggerUIBundle.presets.apis, SwaggerUIBundle.SwaggerUIStandalonePreset],
              layout: 'BaseLayout',
            });
          </script>
        </body>
        </html>
        """;

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "text/html");
        await response.WriteStringAsync(html, ct);
        return response;
    }
}
