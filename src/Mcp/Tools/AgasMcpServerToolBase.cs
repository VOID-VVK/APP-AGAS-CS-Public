using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Contracts.Runtime;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AppAgasCs.Mcp;

public abstract class AgasMcpServerToolBase : McpServerTool
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AgasMcpToolRuntime _runtime;

    protected AgasMcpServerToolBase(AgasMcpToolRuntime runtime, AgasMcpToolDescriptor descriptor)
        : this(
            runtime,
            descriptor.Name,
            descriptor.Description,
            descriptor.InputSchema,
            descriptor.OutputContract?.OutputSchema,
            descriptor.ReadOnlyHint,
            descriptor.DestructiveHint,
            descriptor.IdempotentHint,
            descriptor.OpenWorldHint)
    {
    }

    protected AgasMcpServerToolBase(
        AgasMcpToolRuntime runtime,
        string name,
        string description,
        JsonElement inputSchema,
        JsonElement? outputSchema,
        bool readOnlyHint,
        bool destructiveHint,
        bool idempotentHint,
        bool openWorldHint)
    {
        _runtime = runtime;
        ProtocolTool = new Tool
        {
            Name = name,
            Description = description,
            InputSchema = inputSchema,
            OutputSchema = outputSchema,
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = readOnlyHint,
                DestructiveHint = destructiveHint,
                IdempotentHint = idempotentHint,
                OpenWorldHint = openWorldHint,
            },
        };
        Metadata = Array.Empty<object>();
    }

    public override Tool ProtocolTool { get; }
    public override IReadOnlyList<object> Metadata { get; }

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, JsonElement>? arguments = request.Params?.Arguments == null
            ? null
            : new Dictionary<string, JsonElement>(request.Params.Arguments, StringComparer.Ordinal);
        string? sessionId = request.Server.SessionId;
        AgasMcpToolResponse response = await _runtime.ExecuteToolAsync(sessionId, ProtocolTool.Name, arguments, cancellationToken).ConfigureAwait(false);
        return ToCallToolResult(response);
    }

    private static CallToolResult ToCallToolResult(AgasMcpToolResponse response)
    {
        JsonElement structured = AgasMcpOutputContractRegistry.SerializeToElement(response);
        string fallback = JsonSerializer.Serialize(structured, JsonOptions);
        return new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = fallback } },
            StructuredContent = structured,
            IsError = !response.Ok,
        };
    }
}

public sealed class AgasDescriptorServerTool : AgasMcpServerToolBase
{
    public AgasDescriptorServerTool(AgasMcpToolRuntime runtime, AgasMcpToolDescriptor descriptor)
        : base(runtime, descriptor)
    {
    }
}
