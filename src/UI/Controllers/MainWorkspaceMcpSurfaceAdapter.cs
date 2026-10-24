#nullable enable

using System;
using AppAgasCs.Mcp;

namespace AGAS.UI.Controllers;

/// <summary>
/// Narrow MCP-facing surface over <see cref="MainWorkspace"/>.
/// Keeps the Godot Control from implementing MCP ports directly (ISP / SRP):
/// the workspace remains a view shell; this adapter is the agent API boundary.
/// </summary>
internal sealed class MainWorkspaceMcpSurfaceAdapter : IAgasMcpProjectContextSource
{
    private readonly MainWorkspace _workspace;

    internal MainWorkspaceMcpSurfaceAdapter(MainWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    public AgasMcpProjectContext? GetCurrentMcpProjectContext() =>
        _workspace.GetCurrentMcpProjectContext();

    public AgasMcpProjectAccessState GetCurrentMcpProjectAccessState() =>
        _workspace.GetCurrentMcpProjectAccessState();

}
