using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PerformanceMonitor.Headless.Models;
using PerformanceMonitor.Headless.Security;

namespace PerformanceMonitor.Headless.Services;

public sealed class McpAccessService
{
    private readonly IOptionsMonitor<MonitorOptions> _options;

    public McpAccessService(IOptionsMonitor<MonitorOptions> options)
    {
        _options = options;
    }

    public async Task<bool> AuthorizeMcpRequestAsync(HttpContext context)
    {
        var access = _options.CurrentValue.McpAccess;
        if (!access.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("MCP endpoint is disabled.", context.RequestAborted);
            return false;
        }

        if (IsLocalRequest(context) && access.AllowLocalWithoutApiKey)
        {
            return true;
        }

        return NormalizeAuthMode(access.AuthMode) switch
        {
            "BearerToken" => await AuthorizeBearerTokenAsync(context, access),
            _ => true
        };
    }

    private static async Task<bool> AuthorizeBearerTokenAsync(HttpContext context, McpAccessOptions access)
    {
        var expected = LocalSecretProtector.Unprotect(access.ProtectedApiKey);
        if (string.IsNullOrWhiteSpace(expected))
        {
            await RejectAsync(context, "MCP API key is not configured.");
            return false;
        }

        var provided = GetBearerToken(context.Request);
        if (string.IsNullOrWhiteSpace(provided) || !FixedEquals(provided, expected))
        {
            await RejectAsync(context, "A valid MCP API key is required.");
            return false;
        }

        return true;
    }

    private static async Task RejectAsync(
        HttpContext context,
        string description)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsync(description, context.RequestAborted);
    }

    private static string? GetBearerToken(HttpRequest request)
    {
        if (request.Headers.TryGetValue("Authorization", out var authorization))
        {
            var value = authorization.ToString();
            if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return value["Bearer ".Length..].Trim();
            }
        }

        return request.Headers.TryGetValue("X-PerformanceMonitor-MCP-Key", out var apiKey)
            ? apiKey.ToString()
            : null;
    }

    private static string NormalizeAuthMode(string? authMode)
        => string.Equals(authMode, "BearerToken", StringComparison.OrdinalIgnoreCase)
            ? "BearerToken"
            : "None";

    private static bool IsLocalRequest(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        var local = context.Connection.LocalIpAddress;
        return remote is null
            || IPAddress.IsLoopback(remote)
            || (local is not null && remote.Equals(local));
    }

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
