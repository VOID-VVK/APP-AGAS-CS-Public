using AppAgasCs.Tests.Core;
using Xunit;

namespace AppAgasCs.Core.Tests.Application;

public sealed class MainWorkspaceMcpCoordinatorArchitectureTests
{
    [Fact]
    public void MainWorkspace_DelegatesMcpLifecycleAndContextCompositionToCoordinator()
    {
        string workspace = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/MainWorkspace.cs"));
        string coordinator = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/Controllers/MainWorkspaceMcpCoordinator.cs"));

        Assert.Contains("MainWorkspaceMcpCoordinator _mcpCoordinator", workspace, StringComparison.Ordinal);
        Assert.Contains("MainWorkspaceMcpSurfaceAdapter _mcpSurfaceAdapter", workspace, StringComparison.Ordinal);
        Assert.Contains("_mcpCoordinator.CreateContext", workspace, StringComparison.Ordinal);
        Assert.Contains("_mcpCoordinator.TryBindProject", workspace, StringComparison.Ordinal);
        Assert.Contains("_mcpCoordinator.TryAcceptSnapshot", workspace, StringComparison.Ordinal);
        Assert.Contains("_mcpSurfaceAdapter", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("new AgasMcpProjectContext(", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("AgasMcpDirtyState _mcpDirtyState", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("AgasMcpDirtySnapshot? _mcpDirtySnapshot", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("public AgasSceneResource? LoadSceneToCache", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("TryLoadSceneToCache", workspace, StringComparison.Ordinal);
        // Control no longer implements MCP ports; adapter owns those interfaces (ISP).
        Assert.DoesNotContain(
            "class MainWorkspace : Control, IAgasMcpProjectContextSource",
            workspace,
            StringComparison.Ordinal);

        Assert.Contains("internal sealed class MainWorkspaceMcpCoordinator", coordinator, StringComparison.Ordinal);
        Assert.Contains("new AgasMcpProjectContext(", coordinator, StringComparison.Ordinal);
        Assert.Contains("AgasMcpUiRefreshScope", coordinator, StringComparison.Ordinal);
        Assert.Contains("TryGetProjectIdentity", coordinator, StringComparison.Ordinal);
        Assert.Contains("TryAcceptSnapshot", coordinator, StringComparison.Ordinal);
        Assert.Contains("ClearSavedProject", coordinator, StringComparison.Ordinal);
    }

}
