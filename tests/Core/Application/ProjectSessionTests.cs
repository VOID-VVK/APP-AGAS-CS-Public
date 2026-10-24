using System.Runtime.CompilerServices;
using AppAgasCs.Application;
using AppAgasCs.Core.Resources;
using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class ProjectSessionTests
{
    [Fact]
    public void ProjectSession_IsTheSharedMutationSessionContract()
    {
        Assert.True(typeof(IProjectMutationSession).IsAssignableFrom(typeof(ProjectSession)));
    }

    [Fact]
    public void LoadScene_UsesCacheAndLoader_AndChangeCurrentUpdatesCache()
    {
        var session = new ProjectSession();
        AgasProjectResource project = CreateProject("p1", "scene-a");
        AgasSceneResource sceneA = CreateScene("scene-a", "A");
        AgasSceneResource sceneB = CreateScene("scene-b", "B");
        int loadCalls = 0;

        session.Configure(loadScene: id =>
        {
            loadCalls++;
            return id switch
            {
                "scene-a" => sceneA,
                "scene-b" => sceneB,
                _ => null,
            };
        });
        session.Reset("/tmp/agas-session-test");
        session.AcceptProject(project);

        AgasSceneResource? loadedA = session.LoadScene("scene-a");
        Assert.Same(sceneA, loadedA);
        Assert.Equal(1, loadCalls);

        // Second load hits shared cache — no second disk/loader call.
        AgasSceneResource? cachedA = session.LoadScene("scene-a");
        Assert.Same(sceneA, cachedA);
        Assert.Equal(1, loadCalls);

        session.ChangeCurrentScene(sceneB);
        Assert.Same(sceneB, session.CurrentScene);
        Assert.Same(sceneB, session.GetCachedScene("scene-b"));
        Assert.Contains(session.LoadedScenes, item => ReferenceEquals(item, sceneB));
    }

    [Fact]
    public void ChangeCurrentScene_NotifiesOnce_AndSilentSetSkipsNotify()
    {
        var session = new ProjectSession();
        AgasProjectResource project = CreateProject("p1", "scene-1");
        AgasSceneResource scene = CreateScene("scene-1", "One");
        int notifies = 0;
        session.Configure(currentSceneChanged: _ => notifies++);
        session.Reset("/tmp/agas-session-test");
        session.AcceptProject(project);

        session.ChangeCurrentScene(scene);
        Assert.Equal(1, notifies);
        session.ChangeCurrentScene(scene);
        Assert.Equal(1, notifies);

        AgasSceneResource other = CreateScene("scene-2", "Two");
        session.SetCurrentScene(other, notify: false);
        Assert.Equal(1, notifies);
        Assert.Same(other, session.CurrentScene);
    }

    [Fact]
    public void CanvasAndMcp_ShareOneSessionInstance_ForCrossSceneLoad()
    {
        var session = new ProjectSession();
        AgasProjectResource project = CreateProject("p1", "scene-a");
        AgasSceneResource sceneA = CreateScene("scene-a", "A");
        AgasSceneResource sceneB = CreateScene("scene-b", "B");
        session.Configure(loadScene: id => id == "scene-b" ? sceneB : id == "scene-a" ? sceneA : null);
        session.Reset("/tmp/shared");
        session.AcceptProject(project);
        session.SetCurrentScene(sceneA, notify: false);

        // Canvas-style mutation session handle is the same object MCP would use.
        IProjectMutationSession canvasHandle = session;
        IProjectMutationSession mcpHandle = session;

        Assert.Same(session, canvasHandle);
        Assert.Same(sceneB, mcpHandle.LoadScene("scene-b"));
        Assert.Same(sceneB, canvasHandle.GetCachedScene("scene-b") ?? canvasHandle.LoadScene("scene-b"));
    }

    private static AgasProjectResource CreateProject(string projectId, string activeSceneId)
    {
        var project = (AgasProjectResource)RuntimeHelpers.GetUninitializedObject(typeof(AgasProjectResource));
        project.ProjectId = projectId;
        project.Name = projectId;
        project.ActiveSceneId = activeSceneId;
        project.ResourceVersion = AgasProjectResource.CurrentResourceVersion;
        return project;
    }

    private static AgasSceneResource CreateScene(string id, string name)
    {
        var scene = (AgasSceneResource)RuntimeHelpers.GetUninitializedObject(typeof(AgasSceneResource));
        scene.Id = id;
        scene.Name = name;
        return scene;
    }
}
