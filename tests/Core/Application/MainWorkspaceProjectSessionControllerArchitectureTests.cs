using AppAgasCs.Tests.Core;
using Xunit;

namespace AppAgasCs.Core.Tests.Application;

public sealed class MainWorkspaceProjectSessionControllerArchitectureTests
{
    [Fact]
    public void MainWorkspace_DelegatesProjectAndSceneSessionCoordination()
    {
        string workspace = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/MainWorkspace.cs"));
        string controller = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/Controllers/MainWorkspaceProjectSessionController.cs"));

        Assert.Contains("MainWorkspaceProjectSessionController _projectSessionController", workspace, StringComparison.Ordinal);
        Assert.Contains("=> _projectSessionController.OpenProject(projectRoot);", workspace, StringComparison.Ordinal);
        Assert.Contains("=> _projectSessionController.LoadScene(sceneId);", workspace, StringComparison.Ordinal);
        Assert.Contains("=> _projectSessionController.AddScene();", workspace, StringComparison.Ordinal);
        Assert.Contains("=> _projectSessionController.SwitchScene(sceneId);", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("_repository.Load<AgasProjectResource>", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("_repository.Load<AgasSceneResource>", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("_projectMutationService.MutateScene", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateUniqueSceneName", workspace, StringComparison.Ordinal);

        Assert.Contains("internal sealed class MainWorkspaceProjectSessionController", controller, StringComparison.Ordinal);
        Assert.Contains("EnsureInitialized", controller, StringComparison.Ordinal);
        Assert.Contains("LoadSceneCore", controller, StringComparison.Ordinal);
        // Session cache mutations go through ProjectSession ports (not a raw Dictionary).
        Assert.Contains("ProjectSession _projectSession", controller, StringComparison.Ordinal);
        Assert.Contains("_projectSession.CacheScene", controller, StringComparison.Ordinal);
        Assert.Contains("_projectSession.GetCachedScene", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("Dictionary<string, AgasSceneResource> _sceneCache", controller, StringComparison.Ordinal);
        Assert.Contains("SceneMutationKind.SetActive", controller, StringComparison.Ordinal);
        Assert.Contains("_pendingDeletedSceneIds", controller, StringComparison.Ordinal);
    }
}
