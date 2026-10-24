using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AppAgasCs.Application;
using System.Text.Json;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using Godot;

public partial class McpResourceRuntimeAcceptance : Node
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly List<string> _tempRoots = new();

    public override void _Ready()
    {
        int exitCode = 0;
        try
        {
            RunAll();
            GD.Print("[AGAS-H2][PASS] Godot C# headless MCP Resource runtime acceptance completed.");
        }
        catch (Exception ex)
        {
            exitCode = 1;
            GD.PushError("[AGAS-H2][FAIL] " + ex);
        }
        finally
        {
            CleanupTempRoots();
            GetTree().Quit(exitCode);
        }
    }

    private void RunAll()
    {
        RuntimeProject projectA = CreateInitializedProject("project-a");
        RuntimeProject projectB = CreateInitializedProject("project-b");
        Assert.True(File.Exists(AgasPaths.ProjectFile(projectA.Root)), "Project A project.tres should exist after initialization.");
        Assert.True(File.Exists(AgasPaths.ProjectFile(projectB.Root)), "Project B project.tres should exist after initialization.");
        Assert.True(File.Exists(AgasPaths.SceneFile(projectA.Root, "scene-001")), "Project A scene-001 should exist after initialization.");
        Assert.True(File.Exists(AgasPaths.SceneFile(projectB.Root, "scene-001")), "Project B scene-001 should exist after initialization.");
        Assert.Equal("scene-001", LoadFresh<AgasProjectResource>(AgasPaths.ProjectFile(projectA.Root)).ActiveSceneId, "Project A should reload as Godot Resource.");
        Assert.Equal("scene-001", LoadFresh<AgasProjectResource>(AgasPaths.ProjectFile(projectB.Root)).ActiveSceneId, "Project B should reload as Godot Resource.");

        VerifyMutationIsMemoryOnlyThenSaveWritesResourcesAndReloads(projectA.Root);
        VerifyPendingDeleteSuccessDoesNotCrossDeleteProjectB(projectA.Root, projectB.Root);
        VerifyPendingDeleteFailureReportsFailedAndPendingDirtyFiles(CreateInitializedProject("pending-delete-failure").Root);
        VerifyScenesDirectorySymlinkSaveRejected(CreateInitializedProject("scenes-dir-symlink").Root);
        VerifyTargetSymlinkSaveRejected(CreateInitializedProject("target-file-symlink").Root);
        VerifyLockedHiddenErrorsKeepDirtyTruthAndCanRecover(CreateInitializedProject("locked-hidden").Root);
        VerifyReadOnlyProjectRejectsAllMcpMutationsAndWrites(CreateInitializedProject("future-readonly").Root);
        VerifyLegacyProjectMigratesOnlyThroughExplicitMcpSave(CreateInitializedProject("legacy-controlled-save").Root);
    }

    private void VerifyMutationIsMemoryOnlyThenSaveWritesResourcesAndReloads(string root)
    {
        RuntimeProject runtime = OpenProject(root);
        string sceneId = "scene-001";
        string layerId = runtime.Context.TryGetCachedScene(sceneId)!.ActiveLayerId;
        string projectPath = AgasPaths.ProjectFile(root);
        string scenePath = AgasPaths.SceneFile(root, sceneId);
        string layoutPath = AgasPaths.WorkspaceLayoutFile(root);
        string projectBefore = File.ReadAllText(projectPath);
        string sceneBefore = File.ReadAllText(scenePath);
        string layoutBefore = File.ReadAllText(layoutPath);

        AgasMcpToolResponse rename = runtime.Facade.SceneMutate(new AgasSceneMutateInput("rename", sceneId, "Runtime Scene Renamed"));
        Assert.True(rename.Ok, "Scene rename mutation should succeed.");
        AgasMcpToolResponse layerAdd = runtime.Facade.LayerMutate(new AgasLayerMutateInput("add", sceneId, Name: "Runtime Layer"));
        Assert.True(layerAdd.Ok, "Layer add mutation should succeed.");
        AgasMcpToolResponse viewSet = runtime.Facade.ViewSet(new AgasViewSetInput(sceneId, 12.5, -8.25, 2.0));
        Assert.True(viewSet.Ok, "View mutation should succeed.");
        runtime.Context.Layout.LeftPanelWidth = 333.0;
        runtime.Facade.DirtyState.MarkLayoutDirty();

        Assert.Equal(projectBefore, File.ReadAllText(projectPath), "MCP mutations should not change project.tres before project.save.");
        Assert.Equal(sceneBefore, File.ReadAllText(scenePath), "MCP mutations should not change scene .tres before project.save.");
        Assert.Equal(layoutBefore, File.ReadAllText(layoutPath), "In-memory layout dirty should not change workspace-layout.tres before project.save.");

        AgasMcpToolResponse save = runtime.Facade.ProjectSave(new AgasProjectSaveInput());
        Assert.True(save.Ok, "project.save should return ok.");
        Assert.True(save.Saved, "project.save should report saved=true after full success.");
        Assert.False(save.Dirty, "project.save should report dirty=false after full success.");
        Assert.False(runtime.Facade.DirtyState.IsDirty, "Dirty state should be cleared after successful save.");
        using JsonDocument saveData = DataDoc(save);
        Assert.JsonArrayContains(saveData.RootElement.GetProperty("savedFiles"), ".agas/project.tres", "Saved files should include project.tres.");
        Assert.JsonArrayContains(saveData.RootElement.GetProperty("savedFiles"), ".agas/scenes/scene-001.tres", "Saved files should include dirty scene.");
        Assert.JsonArrayContains(saveData.RootElement.GetProperty("savedFiles"), ".agas/workspace-layout.tres", "Saved files should include workspace layout.");
        Assert.Equal(0, saveData.RootElement.GetProperty("failedFiles").GetArrayLength(), "No save failures expected.");
        Assert.Equal(0, saveData.RootElement.GetProperty("pendingDirtyFiles").GetArrayLength(), "No pending dirty files expected after full save.");

        AgasProjectResource reloadedProject = LoadFresh<AgasProjectResource>(projectPath);
        AgasSceneResource reloadedScene = LoadFresh<AgasSceneResource>(scenePath);
        WorkspaceLayoutResource reloadedLayout = LoadFresh<WorkspaceLayoutResource>(layoutPath);
        Assert.Equal("Runtime Scene Renamed", reloadedProject.Scenes.First(item => item.Id == sceneId).Name, "Reloaded project index should preserve renamed scene.");
        Assert.Equal("Runtime Scene Renamed", reloadedScene.Name, "Reloaded scene should preserve renamed scene name.");
        Assert.True(reloadedScene.Layers.Any(item => item.Name == "Runtime Layer"), "Reloaded scene should preserve added layer.");
        Assert.Equal(12.5, reloadedScene.ViewState.CameraX, "Reloaded scene should preserve camera X.");
        Assert.Equal(-8.25, reloadedScene.ViewState.CameraY, "Reloaded scene should preserve camera Y.");
        Assert.Equal(2.0, reloadedScene.ViewState.Zoom, "Reloaded scene should preserve zoom.");
        Assert.Equal(333.0, reloadedLayout.LeftPanelWidth, "Reloaded layout should preserve layout mutation.");
        Assert.True(runtime.Context.TryGetCachedScene(sceneId)!.Layers.Any(item => item.Id == layerId), "Original layer should remain loaded in memory.");
    }

    private void VerifyPendingDeleteSuccessDoesNotCrossDeleteProjectB(string rootA, string rootB)
    {
        // New projects only ship one empty scene; seed a second same-id scene in both projects.
        string sceneId = "scene-extra";
        SeedExtraScene(rootA, sceneId, "Extra A");
        SeedExtraScene(rootB, sceneId, "Extra B");

        RuntimeProject runtimeA = OpenProject(rootA);
        string aScenePath = AgasPaths.SceneFile(rootA, sceneId);
        string bScenePath = AgasPaths.SceneFile(rootB, sceneId);
        Assert.True(File.Exists(aScenePath), "Project A extra scene should exist before delete.");
        Assert.True(File.Exists(bScenePath), "Project B extra scene should exist before Project A delete.");

        AgasMcpToolResponse delete = runtimeA.Facade.SceneMutate(new AgasSceneMutateInput("delete", sceneId));
        Assert.True(delete.Ok, "Scene delete mutation should succeed.");
        Assert.True(File.Exists(aScenePath), "Pending delete should not remove Project A scene before save.");
        Assert.True(File.Exists(bScenePath), "Pending delete in A should not touch Project B before save.");

        AgasMcpToolResponse save = runtimeA.Facade.ProjectSave(new AgasProjectSaveInput());
        Assert.True(save.Ok, "Save after pending delete should succeed.");
        Assert.False(save.Dirty, "Dirty should clear after successful pending delete save.");
        Assert.False(File.Exists(aScenePath), "Project A extra scene should be removed after Project A save.");
        Assert.True(File.Exists(bScenePath), "Project A pending delete must not delete Project B same-named scene.");
        using JsonDocument saveData = DataDoc(save);
        Assert.JsonArrayContains(saveData.RootElement.GetProperty("savedFiles"), ".agas/project.tres", "Saved files should include project index after delete.");
        Assert.JsonArrayContains(saveData.RootElement.GetProperty("savedFiles"), $".agas/scenes/{sceneId}.tres", "Saved files should include removed scene logical path.");
        Assert.Equal(0, saveData.RootElement.GetProperty("failedFiles").GetArrayLength(), "Pending delete success should have no failures.");
        Assert.Equal(0, saveData.RootElement.GetProperty("pendingDirtyFiles").GetArrayLength(), "Pending delete success should leave no dirty files.");
    }

    private static void SeedExtraScene(string projectRoot, string sceneId, string sceneName)
    {
        var repository = new AgasResourceRepository();
        AgasLoadResult<AgasProjectResource> projectLoad = repository.Load<AgasProjectResource>(AgasPaths.ProjectFile(projectRoot));
        Assert.True(projectLoad.Success && projectLoad.Resource != null, "SeedExtraScene could not load project.");
        AgasProjectResource project = projectLoad.Resource!;
        if (project.Scenes.Any(item => string.Equals(item.Id, sceneId, StringComparison.Ordinal)))
        {
            return;
        }

        AgasSceneResource scene = AgasProjectInitializer.CreateDefaultScene(sceneId, sceneName, project.Scenes.Count);
        Assert.True(repository.Save(scene, AgasPaths.SceneFile(projectRoot, sceneId)).Success, "SeedExtraScene could not save scene file.");
        project.AddScene(new SceneIndexResource
        {
            Id = sceneId,
            Name = sceneName,
            RelativePath = $".agas/scenes/{sceneId}.tres",
            Order = project.Scenes.Count,
        });
        Assert.True(repository.Save(project, AgasPaths.ProjectFile(projectRoot)).Success, "SeedExtraScene could not save project index.");
    }

    private void VerifyPendingDeleteFailureReportsFailedAndPendingDirtyFiles(string root)
    {
        RuntimeProject runtime = OpenProject(root);
        Assert.True(runtime.Facade.ProjectSnapshot(new AgasProjectSnapshotInput()).Ok, "Snapshot should bind dirty state before direct dirty mark.");
        runtime.Facade.DirtyState.MarkSceneDeleted("bad.scene");

        AgasMcpToolResponse save = runtime.Facade.ProjectSave(new AgasProjectSaveInput());
        Assert.True(save.Ok, "Partial save response should still be ok with structured failures.");
        Assert.False(save.Saved, "Save with failed pending delete should not report saved=true.");
        Assert.True(save.Dirty, "Save with failed pending delete should keep dirty=true.");
        Assert.True(runtime.Facade.DirtyState.IsDirty, "Dirty state should remain after failed pending delete.");
        using JsonDocument saveData = DataDoc(save);
        JsonElement failed = saveData.RootElement.GetProperty("failedFiles");
        Assert.True(failed.GetArrayLength() == 1, "One failed file expected for invalid pending delete scene id.");
        Assert.Equal(".agas/scenes/bad.scene.tres", failed[0].GetProperty("path").GetString(), "Failed path should identify pending delete logical file.");
        Assert.Equal(AgasMcpErrorCodes.PathRejected, failed[0].GetProperty("code").GetString(), "Invalid pending delete id should be path rejected.");
        Assert.JsonArrayContains(saveData.RootElement.GetProperty("pendingDirtyFiles"), ".agas/scenes/bad.scene.tres", "Failed pending delete should remain pending dirty.");
    }

    private void VerifyScenesDirectorySymlinkSaveRejected(string root)
    {
        RuntimeProject runtime = OpenProject(root);
        string sceneId = "scene-001";
        string scenesPath = AgasPaths.ScenesDirectory(root);
        string outsideScenes = NewTempRoot("outside-scenes-target");
        Directory.Delete(scenesPath, recursive: true);
        Directory.CreateSymbolicLink(scenesPath, outsideScenes);

        AgasMcpToolResponse mutate = runtime.Facade.ViewSet(new AgasViewSetInput(sceneId, 1.0, 2.0, 1.5));
        Assert.True(mutate.Ok, "View mutation should succeed before symlink save rejection.");
        AgasMcpToolResponse save = runtime.Facade.ProjectSave(new AgasProjectSaveInput());
        Assert.True(save.Ok, "Symlink rejection should be reported as structured save failure.");
        Assert.False(save.Saved, "Save should not report saved=true when .agas/scenes is a symlink.");
        Assert.True(save.Dirty, "Dirty should remain when .agas/scenes symlink blocks save.");
        using JsonDocument saveData = DataDoc(save);
        Assert.Equal(1, saveData.RootElement.GetProperty("failedFiles").GetArrayLength(), ".agas/scenes symlink should fail the dirty scene save.");
        Assert.Equal(AgasMcpErrorCodes.PathRejected, saveData.RootElement.GetProperty("failedFiles")[0].GetProperty("code").GetString(), ".agas/scenes symlink should be path rejected.");
        Assert.JsonArrayContains(saveData.RootElement.GetProperty("pendingDirtyFiles"), ".agas/scenes/scene-001.tres", "Rejected symlink save should keep scene pending dirty.");
    }

    private void VerifyTargetSymlinkSaveRejected(string root)
    {
        RuntimeProject runtime = OpenProject(root);
        string sceneId = "scene-001";
        string scenePath = AgasPaths.SceneFile(root, sceneId);
        string outsideFileRoot = NewTempRoot("outside-scene-file-target");
        Directory.CreateDirectory(outsideFileRoot);
        string outsideFile = Path.Combine(outsideFileRoot, "scene-001.tres");
        File.WriteAllText(outsideFile, "outside target must not be overwritten");
        File.Delete(scenePath);
        File.CreateSymbolicLink(scenePath, outsideFile);

        AgasMcpToolResponse mutate = runtime.Facade.ViewSet(new AgasViewSetInput(sceneId, 3.0, 4.0, 1.25));
        Assert.True(mutate.Ok, "View mutation should succeed before target symlink save rejection.");
        AgasMcpToolResponse save = runtime.Facade.ProjectSave(new AgasProjectSaveInput());
        Assert.True(save.Ok, "Target symlink rejection should be reported as structured save failure.");
        Assert.False(save.Saved, "Save should not report saved=true when target .tres is a symlink.");
        Assert.True(save.Dirty, "Dirty should remain when target symlink blocks save.");
        Assert.Equal("outside target must not be overwritten", File.ReadAllText(outsideFile), "Target symlink save rejection must not overwrite outside file.");
        using JsonDocument saveData = DataDoc(save);
        Assert.Equal(1, saveData.RootElement.GetProperty("failedFiles").GetArrayLength(), "Target symlink should fail the dirty scene save.");
        Assert.Equal(AgasMcpErrorCodes.PathRejected, saveData.RootElement.GetProperty("failedFiles")[0].GetProperty("code").GetString(), "Target symlink should be path rejected.");
        Assert.JsonArrayContains(saveData.RootElement.GetProperty("pendingDirtyFiles"), ".agas/scenes/scene-001.tres", "Rejected target symlink save should keep scene pending dirty.");
    }

    private void VerifyLockedHiddenErrorsKeepDirtyTruthAndCanRecover(string root)
    {
        RuntimeProject runtime = OpenProject(root);
        string sceneId = "scene-001";
        string layerId = runtime.Context.TryGetCachedScene(sceneId)!.ActiveLayerId;

        AgasMcpToolResponse locked = runtime.Facade.LayerMutate(new AgasLayerMutateInput("setLocked", sceneId, layerId, Locked: true));
        Assert.True(locked.Ok, "setLocked true should succeed.");
        Assert.True(locked.Dirty, "setLocked true should mark dirty.");
        AgasMcpToolResponse lockedPrompt = runtime.Facade.PromptMutate(new AgasPromptMutateInput("add", sceneId, layerId, Text: "blocked while locked"));
        Assert.False(lockedPrompt.Ok, "Prompt add on locked layer should fail.");
        Assert.Equal(AgasMcpErrorCodes.LayerLocked, lockedPrompt.Error?.Code, "Locked layer should return LayerLocked.");
        Assert.True(lockedPrompt.Dirty, "Locked error should keep true dirty state from prior mutation.");
        Assert.True(runtime.Facade.DirtyState.IsDirty, "Dirty state should remain true after locked error.");
        AgasMcpToolResponse unlock = runtime.Facade.LayerMutate(new AgasLayerMutateInput("setLocked", sceneId, layerId, Locked: false));
        Assert.True(unlock.Ok, "setLocked false should recover editability.");

        AgasMcpToolResponse hidden = runtime.Facade.LayerMutate(new AgasLayerMutateInput("setVisible", sceneId, layerId, Visible: false));
        Assert.True(hidden.Ok, "setVisible false should succeed.");
        AgasMcpToolResponse hiddenPrompt = runtime.Facade.PromptMutate(new AgasPromptMutateInput("add", sceneId, layerId, Text: "blocked while hidden"));
        Assert.False(hiddenPrompt.Ok, "Prompt add on hidden layer should fail.");
        Assert.Equal(AgasMcpErrorCodes.LayerHidden, hiddenPrompt.Error?.Code, "Hidden layer should return LayerHidden.");
        Assert.True(hiddenPrompt.Dirty, "Hidden error should keep true dirty state from prior mutation.");
        Assert.True(runtime.Facade.DirtyState.IsDirty, "Dirty state should remain true after hidden error.");
        AgasMcpToolResponse show = runtime.Facade.LayerMutate(new AgasLayerMutateInput("setVisible", sceneId, layerId, Visible: true));
        Assert.True(show.Ok, "setVisible true should recover editability.");

        AgasMcpToolResponse prompt = runtime.Facade.PromptMutate(new AgasPromptMutateInput("add", sceneId, layerId, Text: "persisted after recovery"));
        Assert.True(prompt.Ok, "Prompt add should succeed after unlocking and showing the layer.");
        AgasMcpToolResponse save = runtime.Facade.ProjectSave(new AgasProjectSaveInput());
        Assert.True(save.Ok, "Save after locked/hidden recovery should succeed.");
        Assert.False(save.Dirty, "Dirty should clear after locked/hidden recovery save.");
        AgasSceneResource reloadedScene = LoadFresh<AgasSceneResource>(AgasPaths.SceneFile(root, sceneId));
        AgasLayerResource reloadedLayer = reloadedScene.Layers.First(item => item.Id == layerId);
        Assert.True(reloadedLayer.Visible, "Recovered visible state should persist.");
        Assert.False(reloadedLayer.Locked, "Recovered unlocked state should persist.");
        Assert.True(reloadedLayer.PromptFragments.Any(item => item.Text == "persisted after recovery"), "Prompt mutation after recovery should persist.");
    }

    private void VerifyReadOnlyProjectRejectsAllMcpMutationsAndWrites(string root)
    {
        AgasResourceRepository repository = new();
        AgasProjectResource future = LoadFresh<AgasProjectResource>(AgasPaths.ProjectFile(root));
        future.ResourceVersion = AgasProjectResource.CurrentResourceVersion + 1;
        future.SetMeta("future_protocol_field", "preserve-me");
        AgasPersistenceResult futureSave = repository.Save(future, AgasPaths.ProjectFile(root));
        Assert.True(futureSave.Success, "Future project fixture save should succeed.");
        IReadOnlyList<string> before = SnapshotFileSystem(root);
        RuntimeProject runtime = OpenProject(root);
        string sceneId = runtime.Context.Project.ActiveSceneId;
        string layerId = runtime.Context.CurrentScene!.ActiveLayerId;
        int sceneCount = runtime.Context.Project.Scenes.Count;
        int layerCount = runtime.Context.CurrentScene.Layers.Count;
        double cameraX = runtime.Context.CurrentScene.ViewState.CameraX;

        AgasMcpToolResponse query = runtime.Facade.ProjectSnapshot(new AgasProjectSnapshotInput());
        Assert.True(query.Ok, "Read-only project snapshot query must remain available.");
        AgasMcpToolResponse[] blocked =
        {
            runtime.Facade.SceneMutate(new AgasSceneMutateInput("add", Name: "blocked")),
            runtime.Facade.LayerMutate(new AgasLayerMutateInput("add", sceneId, Name: "blocked")),
            runtime.Facade.ImageMutate(new AgasImageMutateInput("add", sceneId, layerId, ImagePath: "assets/missing.png")),
            runtime.Facade.PromptMutate(new AgasPromptMutateInput("add", sceneId, layerId, Text: "blocked")),
            runtime.Facade.ViewSet(new AgasViewSetInput(sceneId, 99, 88, 2)),
            runtime.Facade.SelectionSet(new AgasSelectionSetInput("clearRegions", sceneId)),
            runtime.Facade.ProjectSave(new AgasProjectSaveInput()),
        };
        foreach (AgasMcpToolResponse response in blocked)
        {
            Assert.False(response.Ok, "Every read-only mutation/save must be rejected.");
            Assert.Equal(AgasMcpErrorCodes.ProjectReadOnly, response.Error?.Code, "Every read-only mutation/save must use the stable error code.");
            Assert.False(response.Dirty, "Read-only rejection must not dirty the project.");
        }

        Assert.False(runtime.Facade.DirtyState.IsDirty, "Read-only calls must not bind a dirty mutation.");
        Assert.Equal(sceneCount, runtime.Context.Project.Scenes.Count, "Read-only scene mutation must not change project memory.");
        Assert.Equal(layerCount, runtime.Context.CurrentScene.Layers.Count, "Read-only layer/image/prompt mutations must not change scene memory.");
        Assert.Equal(cameraX, runtime.Context.CurrentScene.ViewState.CameraX, "Read-only ViewSet must not change view memory.");
        Assert.True(before.SequenceEqual(SnapshotFileSystem(root)), "Read-only MCP calls must be filesystem zero-write.");
        AgasProjectResource reloaded = LoadFresh<AgasProjectResource>(AgasPaths.ProjectFile(root));
        Assert.Equal("preserve-me", (string)reloaded.GetMeta("future_protocol_field"), "Future metadata must remain untouched.");
    }

    private void VerifyLegacyProjectMigratesOnlyThroughExplicitMcpSave(string root)
    {
        AgasResourceRepository repository = new();
        AgasProjectResource legacy = LoadFresh<AgasProjectResource>(AgasPaths.ProjectFile(root));
        legacy.ResourceVersion = 1;
        AgasPersistenceResult legacySave = repository.Save(legacy, AgasPaths.ProjectFile(root));
        Assert.True(legacySave.Success, "Legacy project fixture save should succeed.");
        RuntimeProject runtime = OpenProject(root);
        Assert.True(runtime.Context.Project.NeedsMigration, "Opening a v1 project must expose NeedsMigration without writing.");
        Assert.Equal(1, LoadFresh<AgasProjectResource>(AgasPaths.ProjectFile(root)).ResourceVersion, "Opening v1 must not migrate implicitly.");

        AgasMcpToolResponse response = runtime.Facade.ProjectSave(new AgasProjectSaveInput());

        Assert.True(response.Ok && response.Saved, "Explicit MCP project.save must complete the controlled migration.");
        Assert.Equal(AgasProjectResource.CurrentResourceVersion, LoadFresh<AgasProjectResource>(AgasPaths.ProjectFile(root)).ResourceVersion, "Explicit MCP save must persist v1 to v2.");
    }

    private RuntimeProject CreateInitializedProject(string name)
    {
        string root = NewTempRoot(name);
        Directory.CreateDirectory(root);
        AgasPersistenceResult result = new AgasProjectInitializer().EnsureInitialized(root);
        Assert.True(result.Success, "Initialization should succeed for " + root + ": " + result.ErrorMessage);
        Assert.True(Directory.Exists(AgasPaths.ScenesDirectory(root)), "Scenes directory should exist for " + root);
        return OpenProject(root);
    }

    private RuntimeProject OpenProject(string root, bool requiresReadOnly = false, string readOnlyReasonCode = "")
    {
        AgasProjectResource project = LoadFresh<AgasProjectResource>(AgasPaths.ProjectFile(root));
        WorkspaceLayoutResource layout = LoadFresh<WorkspaceLayoutResource>(AgasPaths.WorkspaceLayoutFile(root));
        Dictionary<string, AgasSceneResource> cache = new(StringComparer.Ordinal);
        foreach (SceneIndexResource sceneIndex in project.Scenes)
        {
            cache[sceneIndex.Id] = LoadFresh<AgasSceneResource>(AgasPaths.SceneFile(root, sceneIndex.Id));
        }

        ProjectSession session = new();
        session.Reset(root);
        session.Configure(loadScene: sceneId =>
            {
                string path = AgasPaths.SceneFile(root, sceneId);
                return File.Exists(path) ? LoadFresh<AgasSceneResource>(path) : null;
            });
        session.AcceptProject(project);
        foreach (AgasSceneResource scene in cache.Values)
        {
            session.CacheScene(scene);
        }
        session.SetCurrentScene(cache[project.ActiveSceneId], notify: false);

        AgasMcpProjectContext context = new(
            session,
            layout,
            requiresReadOnly: requiresReadOnly,
            readOnlyReasonCode: readOnlyReasonCode);
        AgasMcpProjectContextProvider provider = new();
        provider.RegisterContext(context);
        AgasMcpProjectFacade facade = new(provider);
        return new RuntimeProject(root, context, facade);
    }

    private static IReadOnlyList<string> SnapshotFileSystem(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (Directory.Exists(path))
                {
                    DirectoryInfo info = new(path);
                    return $"D:{relative}:{info.CreationTimeUtc.Ticks}:{info.LastWriteTimeUtc.Ticks}";
                }
                FileInfo file = new(path);
                return $"F:{relative}:{file.Length}:{file.CreationTimeUtc.Ticks}:{file.LastWriteTimeUtc.Ticks}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))}";
            })
            .ToArray();

    private string NewTempRoot(string name)
    {
        string root = Path.Combine(Path.GetTempPath(), "agas-h2-godot-runtime", name + "-" + Guid.NewGuid().ToString("N"));
        _tempRoots.Add(root);
        return root;
    }

    private void CleanupTempRoots()
    {
        foreach (string root in _tempRoots.AsEnumerable().Reverse())
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AGAS-H2][WARN] Failed to cleanup temp root ", root, ": ", ex.Message);
            }
        }
    }

    private static T LoadFresh<T>(string absolutePath) where T : Resource
    {
        Resource? loaded = ResourceLoader.Load(absolutePath, string.Empty, ResourceLoader.CacheMode.Ignore);
        Assert.True(loaded is T, "Expected to load " + typeof(T).Name + " from " + absolutePath + ", actual " + (loaded?.GetType().FullName ?? "null"));
        return (T)loaded!;
    }

    private static JsonDocument DataDoc(AgasMcpToolResponse response)
    {
        Assert.True(response.Data != null, "Response data should not be null.");
        return JsonDocument.Parse(JsonSerializer.Serialize(response.Data, JsonOptions));
    }

    private sealed record RuntimeProject(string Root, AgasMcpProjectContext Context, AgasMcpProjectFacade Facade);

    private static class Assert
    {
        public static void True(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        public static void False(bool condition, string message) => True(!condition, message);

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(message + " Expected=" + expected + " Actual=" + actual);
            }
        }

        public static void JsonArrayContains(JsonElement array, string expected, string message)
        {
            foreach (JsonElement item in array.EnumerateArray())
            {
                if (string.Equals(item.GetString(), expected, StringComparison.Ordinal))
                {
                    return;
                }
            }

            throw new InvalidOperationException(message + " Missing=" + expected + " Actual=" + array.GetRawText());
        }
    }
}
