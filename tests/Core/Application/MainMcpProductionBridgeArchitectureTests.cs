using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class MainMcpProductionBridgeArchitectureTests
{
    [Fact]
    public void PackageExportCaptureUsesLiveOrFreshProjectionWithoutSilentSave()
    {
        string source = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/MainWorkspace.McpLifecyclePackage.cs"));
        string capture = Between(
            source,
            "internal AgasMcpScenePackageExportCapture CaptureMcpScenePackageExport",
            "internal ValueTask<AgasMcpScenePackagePublishResult>");

        Assert.Contains("GetCachedScene", capture, StringComparison.Ordinal);
        Assert.Contains("LoadFresh", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveProject", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveCurrentScene", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("ResourceSaver", capture, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateAndPackagePublishUseOwnedAndCommitAwareBoundaries()
    {
        string main = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/Main.McpLifecyclePackage.cs"));
        string workspace = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/MainWorkspace.McpLifecyclePackage.cs"));

        Assert.Contains("ProjectAccessOwnerMarkerStore", main, StringComparison.Ordinal);
        Assert.Contains("execution.TryBeginMainThreadCompletion()", main, StringComparison.Ordinal);
        Assert.Contains("Directory.Move(staging, target.TargetPath)", main, StringComparison.Ordinal);
        Assert.Contains("RollbackOwnedCreate", main, StringComparison.Ordinal);
        Assert.Contains("_mcpLifecycleWorkspaceLeaseManager.TryAcquire", main, StringComparison.Ordinal);
        Assert.Contains("execution.TryBeginMainThreadCompletion()", workspace, StringComparison.Ordinal);
        Assert.Contains("MarkPackageImported", workspace, StringComparison.Ordinal);
        Assert.Contains("RefreshCanvasFromMcp", workspace, StringComparison.Ordinal);
    }

    private static string Between(string source, string start, string end)
    {
        int from = source.IndexOf(start, StringComparison.Ordinal);
        int to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from);
        return source[from..to];
    }
}
