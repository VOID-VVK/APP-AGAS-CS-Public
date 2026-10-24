using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class ScenePackageMcpV2ArchitectureTests
{
    [Fact]
    public void McpExportUsesTypedStagingCaptureWithoutLegacyDependencyPlanner()
    {
        string source = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/MainWorkspace.McpLifecyclePackage.cs"));
        string capture = Between(
            source,
            "internal AgasMcpScenePackageExportCapture CaptureMcpScenePackageExport",
            "internal ValueTask<AgasMcpScenePackagePublishResult>");

        Assert.Contains("_mcpScenePackageStaging.CaptureExport(scene", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("ScenePackageDependencyExportPlanner", capture, StringComparison.Ordinal);
    }

    [Fact]
    public void McpImportBuildsSceneBeforeProjectIndexMutation()
    {
        string source = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/MainWorkspace.McpLifecyclePackage.cs"));
        string publish = Between(
            source,
            "private AgasMcpScenePackagePublishResult PublishMcpPreparedScenePackageCore",
            "private long GetInstanceIdAsGeneration");

        int buildScene = publish.IndexOf("BuildSceneFromPreparedImport", StringComparison.Ordinal);
        int mutateProject = publish.IndexOf("_project.Scenes.Add", StringComparison.Ordinal);
        Assert.True(buildScene >= 0 && mutateProject > buildScene);
        Assert.DoesNotContain("ScenePackageDependencyInstaller", source, StringComparison.Ordinal);
    }

    private static string Between(string source, string start, string end)
    {
        int from = source.IndexOf(start, StringComparison.Ordinal);
        int to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from);
        return source[from..to];
    }
}
