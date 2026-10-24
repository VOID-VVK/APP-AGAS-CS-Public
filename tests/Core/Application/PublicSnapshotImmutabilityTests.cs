using System.Collections;
using AppAgasCs.Application;
using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class PublicSnapshotImmutabilityTests
{
    [Fact]
    public void ProjectAndLayoutSnapshots_DefensivelyCopyConstructorCollections()
    {
        var originalScene = new ProjectSceneIndexSnapshot("scene-1", "One", ".agas/scenes/scene-1.tres", 0);
        var scenes = new List<ProjectSceneIndexSnapshot> { originalScene };
        var project = new ProjectSnapshot(
            1, "project-1", "Project", "scene-1", new ProjectGridSnapshot(48, true, true),
            scenes, false, false);
        var layout = new WorkspaceLayoutSnapshot(
            1, 280, false, 180, false, 280, false, 240, true,
            "Files", "Prompt");

        scenes[0] = originalScene with { Id = "mutated" };

        Assert.Equal("scene-1", Assert.Single(project.Scenes).Id);
        AssertReadOnly(project.Scenes);
        Assert.Equal("Prompt", layout.ActiveBottomTab);
    }

    [Fact]
    public void SessionAndMutationEffects_DefensivelyCopyConstructorCollections()
    {
        var scenes = new List<LoadedSceneSnapshot>
        {
            new(1, "scene-1", "Scene", "layer-1", 1),
        };
        var dirty = new List<string> { "scene-1" };
        var deleted = new List<string> { "scene-2" };

        var session = new ProjectSessionSnapshot(
            true, "/project", null, null, "scene-1", "Scene", scenes);
        var effects = new ProjectMutationEffects(true, false, dirty, deleted);

        scenes.Clear();
        dirty[0] = "mutated";
        deleted.Clear();

        Assert.Equal("scene-1", Assert.Single(session.LoadedScenes).Id);
        Assert.Equal("scene-1", Assert.Single(effects.DirtySceneIds));
        Assert.Equal("scene-2", Assert.Single(effects.DeletedSceneIds));
        AssertReadOnly(session.LoadedScenes);
        AssertReadOnly(effects.DirtySceneIds);
        AssertReadOnly(effects.DeletedSceneIds);
    }

    private static void AssertReadOnly<T>(IReadOnlyList<T> values)
    {
        IList list = Assert.IsAssignableFrom<IList>(values);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Clear());
    }
}
