using System;
using System.IO;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace AppAgasCs.Mcp;

public sealed class AgasMcpProtocolHeaderMiddleware
{
    public const string ProtocolVersionHeader = "MCP-Protocol-Version";

    private static readonly PathString McpPath = new(AgasMcpSettingsResource.FixedEndpointPath);
    private readonly RequestDelegate _next;

    public AgasMcpProtocolHeaderMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(McpPath))
        {
            await _next(context);
            return;
        }

        if (context.Request.Headers.ContainsKey(ProtocolVersionHeader))
        {
            await _next(context);
            return;
        }

        if (HttpMethods.IsPost(context.Request.Method) && await IsInitializeRequestAsync(context))
        {
            await _next(context);
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method)
            || HttpMethods.IsDelete(context.Request.Method)
            || HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("MCP-Protocol-Version header is required.", context.RequestAborted);
            return;
        }

        await _next(context);
    }

    private static async Task<bool> IsInitializeRequestAsync(HttpContext context)
    {
        HttpRequest request = context.Request;
        if (request.Body == Stream.Null)
        {
            return false;
        }

        request.EnableBuffering();

        long originalPosition = request.Body.CanSeek ? request.Body.Position : 0;
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: context.RequestAborted);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!document.RootElement.TryGetProperty("method", out JsonElement methodElement))
            {
                return false;
            }

            return methodElement.ValueKind == JsonValueKind.String
                && string.Equals(methodElement.GetString(), "initialize", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            if (request.Body.CanSeek)
            {
                request.Body.Position = originalPosition;
            }
        }
    }
}
