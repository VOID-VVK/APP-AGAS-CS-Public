using AppAgasCs.Tests.Core;
using Xunit;

namespace AppAgasCs.Core.Tests.Application;

public sealed class MainWorkspaceAiApplicationControllerArchitectureTests
{
    [Fact]
    public void MainWorkspace_DelegatesAiCanvasApplicationWorkflow()
    {
        string workspace = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/UI/MainWorkspace.cs"));
        string controller = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/Controllers/MainWorkspaceAiApplicationController.cs"));

        Assert.Contains("MainWorkspaceAiApplicationController _aiApplicationController", workspace, StringComparison.Ordinal);
        Assert.Contains("=> _aiApplicationController.CreateContext(scope);", workspace, StringComparison.Ordinal);
        Assert.Contains("=> _aiApplicationController.Prepare(input);", workspace, StringComparison.Ordinal);
        Assert.Contains("=> _aiApplicationController.Apply(application, persistence, contextPolicy);", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("CanvasPatchAssetStore.TryReadGenerated", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("ExtractGeneratedReferenceCanvas", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("IsAiGenerationCaptureCurrent", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("PreviewAiGeneratedImage", workspace, StringComparison.Ordinal);

        Assert.Contains("internal sealed class MainWorkspaceAiApplicationController", controller, StringComparison.Ordinal);
        Assert.Contains("TryResolveEffectiveRegion", controller, StringComparison.Ordinal);
        Assert.Contains("CanvasPatchAssetStore.TrySavePng", controller, StringComparison.Ordinal);
        Assert.Contains("IsCaptureCurrent", controller, StringComparison.Ordinal);
    }
}
