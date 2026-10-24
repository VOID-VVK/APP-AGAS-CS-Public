using System;
using System.Collections.Generic;
using System.Linq;
using AppAgasCs.Mcp.Contracts;
using ModelContextProtocol.Server;

namespace AppAgasCs.Mcp;

public static class AgasMcpToolCatalog
{
    public static IReadOnlyList<McpServerTool> Create(AgasMcpToolRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        McpServerTool[] tools = AgasMcpToolDescriptorRegistry.All
            .Select(descriptor => (McpServerTool)new AgasDescriptorServerTool(runtime, descriptor))
            .ToArray();

        string[] names = tools.Select(tool => tool.ProtocolTool.Name).ToArray();
        if (tools.Length != AgasMcpToolNames.All.Count
            || names.Distinct(StringComparer.Ordinal).Count() != AgasMcpToolNames.All.Count
            || !AgasMcpToolNames.All.SequenceEqual(names))
        {
            throw new InvalidOperationException("AGAS MCP tool catalog must contain every official tool exactly once in contract order.");
        }

        return Array.AsReadOnly(tools);
    }
}
