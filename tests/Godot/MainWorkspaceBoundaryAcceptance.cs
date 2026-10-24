#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AGAS.UI;
using AGAS.UI.Controllers;
using AppAgasCs.Application;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class MainWorkspaceBoundaryAcceptance : Node
{
    private const string Prefix = "[AGAS][MAIN-WORKSPACE-BOUNDARY]";
    private string? _root;

    public override async void _Ready()
    {
        int exitCode = 0;
        try
        {
            VerifySaveCoordinatorOrdering();
            await VerifyWorkspaceSnapshotAndPersistence();
            GD.Print($"{Prefix}[PASS]");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {exception}");
        }
        finally
        {
            try { if (_root != null && Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
            GetTree().Quit(exitCode);
        }
    }

    private void VerifySaveCoordinatorOrdering()
    {
        var calls = new List<string>();
        var coordinator = new ProjectSaveCoordinator(new RecordingPersistence(calls));
        AgasSceneResource sceneB = Scene("scene-b");
        AgasSceneResource sceneA = Scene("scene-a");
        AgasProjectResource project = Project("scene-b", "scene-a");
        var pendingUiDeletes = new HashSet<string>(StringComparer.Ordinal) { "scene-ui-deleted" };

        ProjectSaveResult result = coordinator.Save(new ProjectSaveRequest(
            "/tmp/project",
            project,
            new WorkspaceLayoutResource(),
            sceneA,
            new[] { sceneB, sceneA },
            new[] { "scene-b" },
            pendingUiDeletes,
            new[] { "scene-mcp-deleted" },
            false,
            string.Empty));

        Require(result.Success, $"Save coordinator failed: {result.Message}");
        Require(calls.SequenceEqual(new[]
        {
            "scene:scene-a",
            "scene:scene-b",
            "project",
            "layout",
            "delete:scene-ui-deleted",
            "delete:scene-mcp-deleted",
        }), $"Controlled save order changed: {string.Join(",", calls)}");
        Require(pendingUiDeletes.Count == 0, "Successful UI pending delete was not committed.");
    }

    private async Task VerifyWorkspaceSnapshotAndPersistence()
    {
        PropertyInfo[] publicProperties = typeof(MainWorkspace)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public);
        Require(publicProperties.All(property => property.Name is not "Project" and not "Layout" and not "SceneCache" and not "CurrentScene"),
            "Mutable project/layout/scene/cache resources leaked through the public workspace boundary.");
        Require(publicProperties.All(property => property.DeclaringType != typeof(MainWorkspace)
            || !typeof(Resource).IsAssignableFrom(property.PropertyType)),
            "A mutable Godot Resource leaked through the public workspace boundary.");

        _root = Path.Combine(Path.GetTempPath(), "agas-main-workspace-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var repository = new AgasResourceRepository();
        Require(new AgasProjectInitializer(repository).EnsureInitialized(_root).Success,
            "Fixture initialization failed.");

        var workspace = new MainWorkspace();
        AddChild(workspace);
        workspace.OpenProject(_root);
        await Frames(3);

        ProjectSessionSnapshot before = workspace.SessionSnapshot;
        string sceneId = workspace.CurrentScene?.Id ?? throw new InvalidOperationException("Current scene missing.");
        workspace.Project!.Name = "After Project";
        workspace.Project.Scenes.Single(scene => scene.Id == sceneId).Name = "After Scene Index";
        workspace.Layout!.LeftPanelWidth = 333;
        MainWorkspaceDockChromeController dockChrome = GetPrivateField<MainWorkspaceDockChromeController>(
            workspace,
            "_dockChrome");
        dockChrome.SetLeftDockCollapsed(true, notify: false);
        Require(dockChrome.LeftDockCollapsed && workspace.Layout.LeftPanelCollapsed,
            "Dock chrome controller did not apply the left-dock collapsed state.");
        workspace.CurrentScene!.Name = "After Scene";
        InvokePrivate(workspace, "MarkDirty", "boundary acceptance", false);

        Require(before.Project?.Name != "After Project", "Previously captured project snapshot changed with Resource mutation.");
        Require(before.Layout?.LeftPanelWidth != 333, "Previously captured layout snapshot changed with Resource mutation.");
        Require(before.LoadedScenes.Single(scene => scene.Id == sceneId).Name != "After Scene",
            "Previously captured scene-cache snapshot changed with Resource mutation.");

        ProjectSessionSnapshot after = workspace.SessionSnapshot;
        Require(after.Project?.Name == "After Project", "New project snapshot did not reflect current Resource state.");
        Require(after.Project?.Scenes.Single(scene => scene.Id == sceneId).Name == "After Scene Index",
            "New scene-index snapshot did not reflect current Resource state.");
        Require(after.Layout?.LeftPanelWidth == 333, "New layout snapshot did not reflect current Resource state.");
        Require(after.Layout?.LeftPanelCollapsed == true,
            "New layout snapshot did not reflect dock chrome controller state.");
        Require(after.LoadedScenes.Single(scene => scene.Id == sceneId).Name == "After Scene",
            "New loaded-scene snapshot did not reflect current Resource state.");

        bool rejectedMutation = false;
        try
        {
            ((IList<LoadedSceneSnapshot>)after.LoadedScenes).Add(
                new LoadedSceneSnapshot(1, "x", "x", string.Empty, 0));
        }
        catch (NotSupportedException)
        {
            rejectedMutation = true;
        }
        Require(rejectedMutation, "Loaded scene snapshot collection must reject mutation.");

        Require(workspace.TrySaveForApplicationExit(), "Coordinator-backed workspace save failed.");
        AgasLoadResult<AgasProjectResource> savedProject = repository.Load<AgasProjectResource>(AgasPaths.ProjectFile(_root));
        AgasLoadResult<WorkspaceLayoutResource> savedLayout = repository.Load<WorkspaceLayoutResource>(AgasPaths.WorkspaceLayoutFile(_root));
        AgasLoadResult<AgasSceneResource> savedScene = repository.Load<AgasSceneResource>(AgasPaths.SceneFile(_root, sceneId));
        Require(savedProject.Resource?.Name == "After Project", "Project save did not pass through coordinator persistence.");
        Require(savedLayout.Resource?.LeftPanelWidth == 333, "Layout save did not pass through coordinator persistence.");
        Require(savedLayout.Resource?.LeftPanelCollapsed == true,
            "Dock chrome controller state did not pass through coordinator persistence.");
        Require(savedScene.Resource?.Name == "After Scene", "Scene save did not pass through coordinator persistence.");

        workspace.QueueFree();
        await Frames(2);
    }

    private async Task Frames(int count)
    {
        for (int index = 0; index < count; index++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static void InvokePrivate(object target, string name, params object[] arguments)
    {
        MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().FullName, name);
        method.Invoke(target, arguments);
    }

    private static T GetPrivateField<T>(object target, string name) where T : class
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, name);
        return field.GetValue(target) as T
            ?? throw new InvalidCastException($"Field '{target.GetType().FullName}.{name}' is not {typeof(T).FullName}.");
    }

    private static AgasProjectResource Project(params string[] sceneIds)
    {
        var project = new AgasProjectResource { ProjectId = "project-1", ActiveSceneId = sceneIds[0] };
        for (int index = 0; index < sceneIds.Length; index++)
        {
            project.Scenes.Add(new SceneIndexResource { Id = sceneIds[index], Order = index });
        }
        return project;
    }

    private static AgasSceneResource Scene(string id) => new() { Id = id, Name = id };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RecordingPersistence(List<string> calls) : IProjectSavePersistence
    {
        public AgasPersistenceResult SaveScene(string projectRoot, AgasSceneResource scene)
        {
            calls.Add($"scene:{scene.Id}");
            return AgasPersistenceResult.Ok();
        }

        public AgasPersistenceResult SaveProject(string projectRoot, AgasProjectResource project)
        {
            calls.Add("project");
            return AgasPersistenceResult.Ok();
        }

        public AgasPersistenceResult SaveLayout(string projectRoot, WorkspaceLayoutResource layout)
        {
            calls.Add("layout");
            return AgasPersistenceResult.Ok();
        }

        public AgasPersistenceResult DeleteScene(string projectRoot, string sceneId)
        {
            calls.Add($"delete:{sceneId}");
            return AgasPersistenceResult.Ok();
        }
    }
}
