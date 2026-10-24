using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace AppAgasCs.Mcp;

public sealed class AgasMcpSessionDeleteMiddleware
{
    public const string SessionIdHeader = "Mcp-Session-Id";

    private static readonly PathString McpPath = new(AgasMcpSettingsResource.FixedEndpointPath);
    private readonly RequestDelegate _next;
    private readonly IAgasMcpToolExecutor _toolExecutor;

    public AgasMcpSessionDeleteMiddleware(RequestDelegate next, IAgasMcpToolExecutor toolExecutor)
    {
        _next = next;
        _toolExecutor = toolExecutor;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        bool isSessionDelete = HttpMethods.IsDelete(context.Request.Method)
            && context.Request.Path.StartsWithSegments(McpPath);

        await _next(context).ConfigureAwait(false);

        if (!isSessionDelete || !IsSuccessfulSdkDelete(context.Response.StatusCode))
        {
            return;
        }

        string? sessionId = context.Request.Headers[SessionIdHeader].ToString();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        await _toolExecutor.CancelSessionAsync(sessionId).ConfigureAwait(false);
    }

    private static bool IsSuccessfulSdkDelete(int statusCode)
    {
        return statusCode >= StatusCodes.Status200OK
            && statusCode < StatusCodes.Status300MultipleChoices;
    }
}
