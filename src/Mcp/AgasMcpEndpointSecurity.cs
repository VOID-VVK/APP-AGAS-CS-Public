using System;
using System.Threading.Tasks;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace AppAgasCs.Mcp;

public sealed class AgasMcpEndpointSecurity
{
    public const string AllowedHostHeader = "127.0.0.1:54119";
    public const string AllowedOrigin = "http://127.0.0.1:54119";

    private static readonly PathString McpPath = new(AgasMcpSettingsResource.FixedEndpointPath);
    private readonly int _configuredPort;

    public AgasMcpEndpointSecurity()
        : this(AgasMcpSettingsResource.FixedPort)
    {
    }

    internal AgasMcpEndpointSecurity(int configuredPort)
    {
        if (configuredPort is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(configuredPort));
        }

        _configuredPort = configuredPort;
    }

    public bool IsAllowedRequest(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(McpPath))
        {
            return true;
        }

        int expectedPort = _configuredPort == 0 ? context.Connection.LocalPort : _configuredPort;
        if (expectedPort is < 1 or > 65535)
        {
            return false;
        }

        string allowedHostHeader = $"{AgasMcpSettingsResource.FixedHost}:{expectedPort}";
        string allowedOrigin = $"http://{AgasMcpSettingsResource.FixedHost}:{expectedPort}";
        return IsAllowedRemoteIp(context)
            && IsAllowedHost(context.Request.Host, allowedHostHeader)
            && IsAllowedOrigin(context.Request.Headers.Origin.ToString(), allowedOrigin);
    }

    public static bool IsAllowedHost(HostString host)
    {
        return IsAllowedHost(host, AllowedHostHeader);
    }

    public static bool IsAllowedOrigin(string? origin)
    {
        return IsAllowedOrigin(origin, AllowedOrigin);
    }

    private static bool IsAllowedHost(HostString host, string allowedHostHeader)
    {
        return string.Equals(host.Value, allowedHostHeader, StringComparison.Ordinal);
    }

    private static bool IsAllowedOrigin(string? origin, string allowedOrigin)
    {
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        return string.Equals(origin, allowedOrigin, StringComparison.Ordinal);
    }

    private static bool IsAllowedRemoteIp(HttpContext context)
    {
        IPAddress? remoteIp = context.Connection.RemoteIpAddress;
        return remoteIp != null && remoteIp.Equals(IPAddress.Loopback);
    }
}

public sealed class AgasMcpEndpointSecurityMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AgasMcpEndpointSecurity _security;

    public AgasMcpEndpointSecurityMiddleware(RequestDelegate next, AgasMcpEndpointSecurity security)
    {
        _next = next;
        _security = security;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_security.IsAllowedRequest(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("AGAS_MCP_FORBIDDEN", context.RequestAborted);
            return;
        }

        await _next(context);
    }
}
