using System;
using System.IO;
using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class ProjectSaveCoordinatorArchitectureTests
{
    [Fact]
    public void ControlledSaveTransaction_KeepsStableResourceAndCleanupOrder()
    {
        string source = File.ReadAllText(Path.Combine(
            ProjectRoot.Path,
            "src/Application/ProjectSaveCoordinator.cs"));

        int sceneSave = source.IndexOf("_persistence.SaveScene(request.ProjectRoot, scene)", StringComparison.Ordinal);
        int projectSave = source.IndexOf("_persistence.SaveProject(request.ProjectRoot, request.Project)", StringComparison.Ordinal);
        int layoutSave = source.IndexOf("_persistence.SaveLayout(request.ProjectRoot, request.Layout)", StringComparison.Ordinal);
        int uiDelete = source.IndexOf("request.UiPendingDeletedSceneIds.ToArray()", StringComparison.Ordinal);
        int mcpDelete = source.IndexOf("request.McpPendingDeletedSceneIds", StringComparison.Ordinal);

        Assert.True(sceneSave < projectSave);
        Assert.True(projectSave < layoutSave);
        Assert.True(layoutSave < uiDelete);
        Assert.True(uiDelete < mcpDelete);
        Assert.DoesNotContain("SaveBlueprintDocuments", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CommitBlueprintPendingDeletes", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatedPersistencePort_OwnsPathValidationAndResourceIo()
    {
        string workspace = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/UI/MainWorkspace.cs"));
        string saveController = File.ReadAllText(Path.Combine(
            ProjectRoot.Path,
            "src/UI/Controllers/MainWorkspaceSaveController.cs"));
        string persistence = File.ReadAllText(Path.Combine(
            ProjectRoot.Path,
            "src/UI/Controllers/ValidatedProjectSavePersistence.cs"));

        Assert.Contains("ValidateAgasSceneFile", persistence, StringComparison.Ordinal);
        Assert.Contains("ValidateAgasProjectFile", persistence, StringComparison.Ordinal);
        Assert.Contains("ValidateAgasLayoutFile", persistence, StringComparison.Ordinal);
        Assert.Contains("_repository.Save", persistence, StringComparison.Ordinal);
        Assert.DoesNotContain("_repository.Save(_currentScene", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete(", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete(", saveController, StringComparison.Ordinal);
        Assert.Contains("new ProjectSaveRequest(", saveController, StringComparison.Ordinal);
        Assert.DoesNotContain("new ProjectSaveRequest(", workspace, StringComparison.Ordinal);
    }
}
