using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TaskHub.Abstractions.Contracts;

namespace TaskHub.Api.Security;

/// <summary>
/// Three checks on every request, in this order:
/// <list type="number">
/// <item><b>Host</b> must be 127.0.0.1:port or localhost:port. Blocks DNS-rebinding attacks, where
/// a web page points its own domain at 127.0.0.1.</item>
/// <item><b>Origin</b>, if present, must be explicitly allowed (none by default). Browsers always
/// send Origin on cross-site requests, so this blocks web pages calling the API.</item>
/// <item><b>Authorization: Bearer &lt;token&gt;</b> must match the token file.</item>
/// </list>
/// </summary>
public sealed class LocalApiGuardMiddleware(RequestDelegate next, ApiTokenStore tokens, LocalApiGuardOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var port = context.Connection.LocalPort;
        var host = context.Request.Host;
        var hostOk = host.Port == port
                     && (string.Equals(host.Host, "127.0.0.1", StringComparison.Ordinal)
                         || string.Equals(host.Host, "localhost", StringComparison.OrdinalIgnoreCase));
        if (!hostOk)
        {
            await Reject(context, StatusCodes.Status421MisdirectedRequest, "Invalid Host header.");
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !options.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            await Reject(context, StatusCodes.Status403Forbidden, "Cross-origin requests are not allowed.");
            return;
        }

        var auth = context.Request.Headers.Authorization.ToString();
        var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
        if (!tokens.Matches(token))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await Reject(context, StatusCodes.Status401Unauthorized, "Missing or invalid API token. See %LOCALAPPDATA%\\TaskHub\\api.json.");
            return;
        }

        await next(context);
    }

    private static Task Reject(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new ApiError(message), ApiJson.Options));
    }
}

public sealed class LocalApiGuardOptions
{
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];
}
