using AppAgasCs.Tests.Core;
using Xunit;

namespace AppAgasCs.Core.Tests.Application;

public sealed class MainWorkspaceLayerPromptControllerArchitectureTests
{
    [Fact]
    public void MainWorkspace_DelegatesLayerAndPromptMutationFlowToController()
    {
        string workspace = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/MainWorkspace.cs"));
        string controller = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/Controllers/MainWorkspaceLayerPromptController.cs"));

        Assert.Contains("MainWorkspaceLayerPromptController _layerPromptController", workspace, StringComparison.Ordinal);
        Assert.Contains("=> _layerPromptController.AddLayer();", workspace, StringComparison.Ordinal);
        Assert.Contains("=> _layerPromptController.SetCurrentLayer(layer);", workspace, StringComparison.Ordinal);
        Assert.Contains("=> _layerPromptController.AddPromptFragment(layer);", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("new CanvasUndoProjectMutationPolicy", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("_canvas.AddPromptFragment", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("_projectMutationService.MutateLayer", workspace, StringComparison.Ordinal);

        Assert.Contains("internal sealed class MainWorkspaceLayerPromptController", controller, StringComparison.Ordinal);
        Assert.Contains("CanvasUndoProjectMutationPolicy", controller, StringComparison.Ordinal);
        Assert.Contains("LayerMutationKind.SetCurrent", controller, StringComparison.Ordinal);
        Assert.Contains("PromptSucceeded", controller, StringComparison.Ordinal);
        Assert.Contains("CreateUniqueLayerName", controller, StringComparison.Ordinal);
    }
}
