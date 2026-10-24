using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AppAgasCs.Application;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Mcp;

namespace AGAS.UI.Controllers;

/// <summary>
/// Owns project/scene session loading, cache reconciliation, persistence hand-off, and
/// scene mutations. UI effects are expressed through narrow callbacks supplied by the view.
/// </summary>
internal sealed class MainWorkspaceProjectSessionController
{
    private readonly AgasResourceRepository _repository;
    private readonly AgasProjectInitializer _initializer;
    private readonly AgasMcpPathValidator _pathValidator;
    private readonly WorkspaceLayoutController _layoutController;
    private readonly ProjectSession _projectSession;
    private readonly HashSet<string> _pendingDeletedSceneIds;
    private readonly Func<IProjectMutationSession> _mutationSession;
    private readonly Func<string> _projectRoot;
    private readonly Func<AgasProjectResource?> _project;
    private readonly Func<AgasSceneResource?> _currentScene;
    private readonly Func<bool> _requiresReadOnly;
    private readonly Func<string, bool> _canMutate;
    private readonly Action<string> _resetProjectState;
    private readonly Action<AgasProjectResource, WorkspaceLayoutResource, bool, string> _acceptProject;
    private readonly Action<AgasSceneResource> _acceptCurrentScene;
    private readonly Action _completeProjectOpen;
    private readonly Action _saveCurrentSceneSilently;
    private readonly Action<AgasSceneResource?> _showCanvasScene;
    private readonly Action<string> _showActiveScene;
    private readonly Action<AgasSceneResource?> _showLayerScene;
    private readonly Action<AgasLayerResource?> _showPromptLayer;
    private readonly Action _clearPropertiesSelection;
    private readonly Action _refreshManagementUi;
    private readonly Action<string, bool> _setStatus;
    private readonly Action<string, bool> _markDirty;
    private readonly Action<string, bool, object[]> _setStatusTemplate;
    private readonly Action<string, bool, object[]> _markDirtyTemplate;

    internal MainWorkspaceProjectSessionController(
        AgasResourceRepository repository,
        AgasProjectInitializer initializer,
        AgasMcpPathValidator pathValidator,
        WorkspaceLayoutController layoutController,
        ProjectSession projectSession,
        HashSet<string> pendingDeletedSceneIds,
        Func<IProjectMutationSession> mutationSession,
        Func<string> projectRoot,
        Func<AgasProjectResource?> project,
        Func<AgasSceneResource?> currentScene,
        Func<bool> requiresReadOnly,
        Func<string, bool> canMutate,
        Action<string> resetProjectState,
        Action<AgasProjectResource, WorkspaceLayoutResource, bool, string> acceptProject,
        Action<AgasSceneResource> acceptCurrentScene,
        Action completeProjectOpen,
        Action saveCurrentSceneSilently,
        Action<AgasSceneResource?> showCanvasScene,
        Action<string> showActiveScene,
        Action<AgasSceneResource?> showLayerScene,
        Action<AgasLayerResource?> showPromptLayer,
        Action clearPropertiesSelection,
        Action refreshManagementUi,
        Action<string, bool> setStatus,
        Action<string, bool> markDirty,
        Action<string, bool, object[]>? setStatusTemplate = null,
        Action<string, bool, object[]>? markDirtyTemplate = null)
    {
        _repository = repository;
        _initializer = initializer;
        _pathValidator = pathValidator;
        _layoutController = layoutController;
        _projectSession = projectSession ?? throw new ArgumentNullException(nameof(projectSession));
        _pendingDeletedSceneIds = pendingDeletedSceneIds;
        _mutationSession = mutationSession;
        _projectRoot = projectRoot;
        _project = project;
        _currentScene = currentScene;
        _requiresReadOnly = requiresReadOnly;
        _canMutate = canMutate;
        _resetProjectState = resetProjectState;
        _acceptProject = acceptProject;
        _acceptCurrentScene = acceptCurrentScene;
        _completeProjectOpen = completeProjectOpen;
        _saveCurrentSceneSilently = saveCurrentSceneSilently;
        _showCanvasScene = showCanvasScene;
        _showActiveScene = showActiveScene;
        _showLayerScene = showLayerScene;
        _showPromptLayer = showPromptLayer;
        _clearPropertiesSelection = clearPropertiesSelection;
        _refreshManagementUi = refreshManagementUi;
        _setStatus = setStatus;
        _markDirty = markDirty;
        _setStatusTemplate = setStatusTemplate ?? ((key, error, arguments) => setStatus(string.Format(Godot.TranslationServer.Translate(key), arguments), error));
        _markDirtyTemplate = markDirtyTemplate ?? ((key, error, arguments) => markDirty(string.Format(Godot.TranslationServer.Translate(key), arguments), error));
    }

    internal void OpenProject(string projectRoot)
    {
        string normalizedRoot = Path.GetFullPath(projectRoot);
        // Session.Reset (via _resetProjectState) clears the shared scene cache.
        _pendingDeletedSceneIds.Clear();
        _resetProjectState(normalizedRoot);

        AgasPersistenceResult init = _initializer.EnsureInitialized(normalizedRoot);
        bool rejectedAsTooOld = !init.Success
            && init.ErrorMessage?.StartsWith("PROJECT_VERSION_INVALID", StringComparison.Ordinal) == true;
        if (!init.Success && !rejectedAsTooOld)
        {
            _setStatus(init.ErrorMessage ?? "初始化失败。", true);
            return;
        }

        AgasLoadResult<AgasProjectResource> projectLoad =
            _repository.Load<AgasProjectResource>(AgasPaths.ProjectFile(normalizedRoot));
        if (!projectLoad.Success || projectLoad.Resource == null)
        {
            _setStatus(projectLoad.ErrorMessage ?? "加载 project.tres 失败。", true);
            return;
        }

        AgasProjectResource project = projectLoad.Resource;
        bool requiresReadOnly = project.RequiresReadOnly
            || AgasProjectResource.IsOlderThanSupportedResourceVersion(project.ResourceVersion);
        string reasonCode = AgasProjectResource.IsOlderThanSupportedResourceVersion(project.ResourceVersion)
            ? "PROJECT_VERSION_INVALID"
            : project.ReadOnlyReasonCode;
        WorkspaceLayoutResource layout = LoadLayoutOrDefault(normalizedRoot, requiresReadOnly);
        _acceptProject(project, layout, requiresReadOnly, reasonCode);

        AgasSceneResource? currentScene = LoadSceneCore(normalizedRoot, project, project.ActiveSceneId);
        if (currentScene == null)
        {
            _setStatusTemplate("加载当前场景失败：{0}", true, new object[] { project.ActiveSceneId });
            return;
        }

        _acceptCurrentScene(currentScene);
        _completeProjectOpen();
    }

    internal AgasSceneResource? LoadScene(string sceneId)
    {
        AgasProjectResource? project = _project();
        return project == null || string.IsNullOrWhiteSpace(sceneId)
            ? null
            : LoadSceneCore(_projectRoot(), project, sceneId);
    }

    internal void AddScene()
    {
        if (!_canMutate("新增场景")) return;
        AgasProjectResource? project = _project();
        if (project == null) return;
        _saveCurrentSceneSilently();
        string sceneName = CreateUniqueSceneName(project);
        ProjectMutationResult mutation = MutateScene(new SceneMutationCommand(
            SceneMutationKind.Add, Name: sceneName));
        if (!mutation.Success)
        {
            _setStatusTemplate("新增场景失败：{0}", true, new object[] { mutation.Message });
            return;
        }
        _showCanvasScene(_currentScene());
        _clearPropertiesSelection();
        _refreshManagementUi();
        _markDirtyTemplate("已新增场景：{0}", false, new object[] { sceneName });
    }

    internal void RenameScene(string sceneId, string newName)
    {
        if (!_canMutate("重命名场景")) return;
        if (_project() == null || string.IsNullOrWhiteSpace(newName)) return;
        string sanitized = newName.Trim();
        ProjectMutationResult mutation = MutateScene(new SceneMutationCommand(
            SceneMutationKind.Rename, sceneId, sanitized));
        if (!mutation.Success)
        {
            _setStatusTemplate("重命名场景失败：{0}", true, new object[] { mutation.Message });
            return;
        }
        _refreshManagementUi();
        _markDirtyTemplate("已重命名场景：{0}", false, new object[] { sanitized });
    }

    internal void DeleteScene(string sceneId)
    {
        if (!_canMutate("删除场景")) return;
        AgasProjectResource? project = _project();
        if (project == null || project.Scenes.Count <= 1)
        {
            _setStatus("至少保留 1 个场景。", true);
            return;
        }

        string deletedName = project.Scenes.FirstOrDefault(scene => scene.Id == sceneId)?.Name ?? sceneId;
        string? previousActiveId = _currentScene()?.Id;
        ProjectMutationResult mutation = MutateScene(new SceneMutationCommand(
            SceneMutationKind.Delete, sceneId));
        if (!mutation.Success)
        {
            _setStatusTemplate("删除场景失败：{0}", true, new object[] { mutation.Message });
            return;
        }
        foreach (string deletedId in mutation.Effects?.DeletedSceneIds ?? Array.Empty<string>())
            _pendingDeletedSceneIds.Add(deletedId);
        if (!string.Equals(previousActiveId, _currentScene()?.Id, StringComparison.Ordinal))
        {
            _showCanvasScene(_currentScene());
            _clearPropertiesSelection();
        }
        _refreshManagementUi();
        _markDirtyTemplate("已删除场景：{0}", false, new object[] { deletedName });
    }

    /// <summary>
    /// Exports one scene package ZIP under {project}/.agas/scene-exports/ (disk only, not clipboard).
    /// Returns the ZIP path on success so the UI can show a visible confirmation.
    /// </summary>
    internal ScenePackageExportResult ExportScenePackage(string? sceneId = null)
    {
        if (!_canMutate("导出场景包"))
        {
            return ScenePackageExportResult.Fail("当前项目不可修改，无法导出场景包。");
        }

        AgasProjectResource? project = _project();
        string root = _projectRoot();
        if (project == null || string.IsNullOrWhiteSpace(root))
        {
            ScenePackageExportResult missing = ScenePackageExportResult.Fail("导出失败：项目未打开。");
            _setStatus(missing.Message, true);
            return missing;
        }

        _saveCurrentSceneSilently();

        string targetId = string.IsNullOrWhiteSpace(sceneId) ? _currentScene()?.Id ?? string.Empty : sceneId.Trim();
        AgasSceneResource? scene = _currentScene() != null
            && string.Equals(_currentScene()!.Id, targetId, StringComparison.Ordinal)
            ? _currentScene()
            : LoadScene(targetId);
        if (scene == null)
        {
            ScenePackageExportResult unavailable = ScenePackageExportResult.Fail("导出失败：场景不可用。");
            _setStatus(unavailable.Message, true);
            return unavailable;
        }

        ScenePackageExportResult exported = new ScenePackageStore().Export(root, scene);
        if (!exported.Success)
        {
            _setStatus(exported.Message, true);
            return exported;
        }

        // Refresh left files tree so .agas/scene-exports becomes visible immediately.
        _refreshManagementUi();
        string relativeHint = exported.PackagePath != null
            ? MakeProjectRelative(root, exported.PackagePath)
            : ".agas/scene-exports/";
        _setStatusTemplate("单个 ZIP 场景包已导出到 {0}", false, new object[] { relativeHint });
        return exported;
    }

    private static string MakeProjectRelative(string projectRoot, string absolutePath)
    {
        try
        {
            string root = Path.GetFullPath(projectRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(absolutePath);
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || full.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
                || string.Equals(full, root, StringComparison.Ordinal))
            {
                return full[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');
            }
        }
        catch
        {
            // Fall through to absolute path display.
        }

        return absolutePath.Replace('\\', '/');
    }

    internal void ImportScenePackage(string packagePath)
    {
        if (!_canMutate("导入场景包")) return;
        AgasProjectResource? project = _project();
        string root = _projectRoot();
        if (project == null || string.IsNullOrWhiteSpace(root)) return;
        _saveCurrentSceneSilently();

        ScenePackageImportResult imported = new ScenePackageStore().Import(root, packagePath);
        if (!imported.Success || imported.Scene == null)
        {
            _setStatus(imported.Message, true);
            return;
        }

        AgasSceneResource scene = imported.Scene;
        string relativePath = $".agas/scenes/{scene.Id}.tres";
        AgasPersistenceResult saveScene = _repository.Save(scene, AgasPaths.SceneFile(root, scene.Id));
        if (!saveScene.Success)
        {
            _setStatus(saveScene.ErrorMessage ?? "导入失败：无法保存场景文件。", true);
            return;
        }

        var index = new SceneIndexResource
        {
            Id = scene.Id,
            Name = scene.Name,
            RelativePath = relativePath,
            Order = project.Scenes.Select(item => item.Order).DefaultIfEmpty(-1).Max() + 1,
        };
        project.Scenes.Add(index);
        _projectSession.CacheScene(scene);
        project.ActiveSceneId = scene.Id;
        _mutationSession().ChangeCurrentScene(scene);
        PresentScene(scene, scene.Id);
        _refreshManagementUi();
        bool importedFromZip = string.Equals(Path.GetExtension(packagePath), ".zip", StringComparison.OrdinalIgnoreCase);
        string sourceKind = importedFromZip ? "ZIP 场景包" : "旧版 scene.json 场景包";
        _markDirty($"已从 {sourceKind}导入场景：{scene.Name}", false);
        _setStatus($"已从 {sourceKind}导入场景：{scene.Name}（参考图 {imported.CopiedRelativeImagePaths?.Count ?? 0} 张）", false);
    }

    internal void SwitchScene(string sceneId)
    {
        AgasProjectResource? project = _project();
        if (project == null || _currentScene()?.Id == sceneId) return;
        if (_requiresReadOnly())
        {
            AgasSceneResource? readOnlyScene = LoadScene(sceneId);
            if (readOnlyScene == null) return;
            _mutationSession().ChangeCurrentScene(readOnlyScene);
            PresentScene(readOnlyScene, sceneId);
            _setStatusTemplate("只读浏览场景：{0}", false, new object[] { sceneId });
            return;
        }

        _saveCurrentSceneSilently();
        ProjectMutationResult mutation = MutateScene(new SceneMutationCommand(
            SceneMutationKind.SetActive, sceneId));
        AgasSceneResource? currentScene = _currentScene();
        if (!mutation.Success || currentScene == null)
        {
            _setStatusTemplate("切换场景失败：{0}", true, new object[] { mutation.Message });
            return;
        }
        PresentScene(currentScene, sceneId);
        _markDirtyTemplate("已切换场景：{0}", false, new object[] { sceneId });
    }

    private ProjectMutationResult MutateScene(SceneMutationCommand command) =>
        new ProjectMutationService().MutateScene(_mutationSession(), command);

    private void PresentScene(AgasSceneResource scene, string sceneId)
    {
        _showCanvasScene(scene);
        _showActiveScene(sceneId);
        _showLayerScene(scene);
        _showPromptLayer(GetActiveLayer(scene));
        _clearPropertiesSelection();
    }

    private AgasSceneResource? LoadSceneCore(
        string projectRoot,
        AgasProjectResource project,
        string sceneId)
    {
        AgasSceneResource? cached = _projectSession.GetCachedScene(sceneId);
        if (cached != null) return cached;
        AgasMcpPathValidationResult scenePath = _pathValidator.ValidateAgasSceneFile(projectRoot, sceneId);
        if (!scenePath.Success)
        {
            _setStatus(scenePath.Message, true);
            return null;
        }
        AgasLoadResult<AgasSceneResource> sceneLoad =
            _repository.Load<AgasSceneResource>(scenePath.AbsolutePath);
        if (!sceneLoad.Success || sceneLoad.Resource == null)
        {
            if (sceneLoad.ErrorMessage != null) _setStatus(sceneLoad.ErrorMessage, true);
            else _setStatusTemplate("加载场景失败：{0}", true, new object[] { sceneId });
            return null;
        }
        _projectSession.CacheScene(sceneLoad.Resource);
        return sceneLoad.Resource;
    }

    private WorkspaceLayoutResource LoadLayoutOrDefault(string projectRoot, bool requiresReadOnly)
    {
        AgasLoadResult<WorkspaceLayoutResource> load =
            _repository.Load<WorkspaceLayoutResource>(AgasPaths.WorkspaceLayoutFile(projectRoot));
        WorkspaceLayoutResource layout = load.Success && load.Resource != null
            ? load.Resource
            : new WorkspaceLayoutResource();
        return _layoutController.NormalizeLoadedLayout(layout, requiresReadOnly);
    }

    private static AgasLayerResource? GetActiveLayer(AgasSceneResource scene) =>
        scene.Layers.FirstOrDefault(layer => layer.Id == scene.ActiveLayerId)
        ?? scene.Layers.OrderBy(layer => layer.Order).FirstOrDefault();

    private static string CreateUniqueSceneName(AgasProjectResource project)
    {
        int number = project.Scenes.Count + 1;
        string candidate;
        do
        {
            candidate = $"场景 {number:000}";
            number++;
        }
        while (project.Scenes.Any(scene => string.Equals(
            scene.Name, candidate, StringComparison.OrdinalIgnoreCase)));
        return candidate;
    }
}
