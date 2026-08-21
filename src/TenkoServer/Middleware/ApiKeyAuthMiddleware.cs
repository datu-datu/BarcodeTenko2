using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using TenkoServer.Models;

namespace TenkoServer.Middleware
{
    public class ApiKeyAuthMiddleware
    {
        public const string ApiKeyHeaderName = "X-API-Key";
        private readonly RequestDelegate _next;

        public ApiKeyAuthMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context, IOptions<TenkoServerOptions> options)
        {
            var path = context.Request.Path;

            // 端末用エンドポイント (/api/v1/scans) のみ X-API-Key を必須とする
            if (path.StartsWithSegments("/api/v1/scans"))
            {
                if (!context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var extractedApiKey))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("{\"error\":\"Unauthorized: Missing X-API-Key header\"}");
                    return;
                }

                var configuredApiKey = options.Value.ApiKey;
                if (string.IsNullOrEmpty(configuredApiKey) || !string.Equals(configuredApiKey, extractedApiKey))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("{\"error\":\"Unauthorized: Invalid API Key\"}");
                    return;
                }
            }

            await _next(context);
        }
    }
}
