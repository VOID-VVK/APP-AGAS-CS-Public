using AppAgasCs.Tests.Core;
using Xunit;

namespace AppAgasCs.Core.Tests.Application;

public sealed class MainWorkspaceSaveControllerArchitectureTests
{
    [Fact]
    public void MainWorkspace_DelegatesSaveOrchestrationToController()
    {
        string workspace = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/MainWorkspace.cs"));
        string controller = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/Controllers/MainWorkspaceSaveController.cs"));

        Assert.Contains("MainWorkspaceSaveController _saveController", workspace, StringComparison.Ordinal);
        Assert.Contains("=> _saveController.SaveProject();", workspace, StringComparison.Ordinal);
        Assert.Contains("=> _saveController.SaveCurrentScene(silent);", workspace, StringComparison.Ordinal);
        Assert.Contains("=> _saveController.SaveProjectOnceForShutdown();", workspace, StringComparison.Ordinal);
        Assert.Contains("=> _saveController.FailSaveForRetry(message, partialSaved);", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("new ProjectSaveRequest(", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("private bool _finalSaveAttempted", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("_projectSaveCoordinator.Save(", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("_projectSaveCoordinator.SaveCurrentScene(", workspace, StringComparison.Ordinal);

        Assert.Contains("internal sealed class MainWorkspaceSaveController", controller, StringComparison.Ordinal);
        Assert.Contains("new ProjectSaveRequest(", controller, StringComparison.Ordinal);
        Assert.Contains("_projectSaveCoordinator.Save(", controller, StringComparison.Ordinal);
        Assert.Contains("_projectSaveCoordinator.SaveCurrentScene(", controller, StringComparison.Ordinal);
        Assert.Contains("SaveProjectOnceForShutdown", controller, StringComparison.Ordinal);
        Assert.Contains("FailSaveForRetry", controller, StringComparison.Ordinal);
        Assert.Contains("_clearUiDirty", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("_markBlueprintDocumentsSaved", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("_saveBlueprintDocuments", controller, StringComparison.Ordinal);
    }
}
