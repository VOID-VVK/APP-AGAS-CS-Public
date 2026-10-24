using System;
using System.Collections.Generic;
using System.Linq;
using AppAgasCs.Application;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Mcp;

namespace AGAS.UI.Controllers;

/// <summary>
/// Owns controlled project save orchestration for MainWorkspace: request assembly via
/// <see cref="ProjectSaveCoordinator"/>, silent scene saves, and one-shot shutdown save.
/// Dirty clear / status effects go through host callbacks; the coordinator remains SSOT
/// for save ordering.
/// </summary>
internal sealed class MainWorkspaceSaveController
{
    private readonly ProjectSaveCoordinator _projectSaveCoordinator;
    private readonly ISet<string> _uiPendingDeletedSceneIds;
    private readonly Func<string> _projectRoot;
    private readonly Func<AgasProjectResource?> _project;
    private readonly Func<WorkspaceLayoutResource?> _layout;
    private readonly Func<AgasSceneResource?> _currentScene;
    private readonly Func<IReadOnlyCollection<AgasSceneResource>> _loadedScenes;
    private readonly Func<bool> _requiresReadOnly;
    private readonly Func<string> _readOnlyReasonCode;
    private readonly Func<bool> _isWorkspaceDirty;
    private readonly Func<AgasMcpDirtySnapshot?> _currentDirtySnapshot;
    private readonly Action _captureLayoutFromUi;
    private readonly Action _cacheCurrentScene;
    private readonly Action _clearSavedMcpProject;
    private readonly Action<string, bool> _clearUiDirty;
    private readonly Action<string, bool> _setStatus;
    private readonly Action<string, bool, object[]> _setStatusTemplate;

    private bool _finalSaveAttempted;
    private bool _lastSavePartial;

    internal MainWorkspaceSaveController(
        ProjectSaveCoordinator projectSaveCoordinator,
        ISet<string> uiPendingDeletedSceneIds,
        Func<string> projectRoot,
        Func<AgasProjectResource?> project,
        Func<WorkspaceLayoutResource?> layout,
        Func<AgasSceneResource?> currentScene,
        Func<IReadOnlyCollection<AgasSceneResource>> loadedScenes,
        Func<bool> requiresReadOnly,
        Func<string> readOnlyReasonCode,
        Func<bool> isWorkspaceDirty,
        Func<AgasMcpDirtySnapshot?> currentDirtySnapshot,
        Action captureLayoutFromUi,
        Action cacheCurrentScene,
        Action clearSavedMcpProject,
        Action<string, bool> clearUiDirty,
        Action<string, bool> setStatus,
        Action<string, bool, object[]>? setStatusTemplate = null)
    {
        _projectSaveCoordinator = projectSaveCoordinator
            ?? throw new ArgumentNullException(nameof(projectSaveCoordinator));
        _uiPendingDeletedSceneIds = uiPendingDeletedSceneIds
            ?? throw new ArgumentNullException(nameof(uiPendingDeletedSceneIds));
        _projectRoot = projectRoot ?? throw new ArgumentNullException(nameof(projectRoot));
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _currentScene = currentScene ?? throw new ArgumentNullException(nameof(currentScene));
        _loadedScenes = loadedScenes ?? throw new ArgumentNullException(nameof(loadedScenes));
        _requiresReadOnly = requiresReadOnly ?? throw new ArgumentNullException(nameof(requiresReadOnly));
        _readOnlyReasonCode = readOnlyReasonCode ?? throw new ArgumentNullException(nameof(readOnlyReasonCode));
        _isWorkspaceDirty = isWorkspaceDirty ?? throw new ArgumentNullException(nameof(isWorkspaceDirty));
        _currentDirtySnapshot = currentDirtySnapshot
            ?? throw new ArgumentNullException(nameof(currentDirtySnapshot));
        _captureLayoutFromUi = captureLayoutFromUi
            ?? throw new ArgumentNullException(nameof(captureLayoutFromUi));
        _cacheCurrentScene = cacheCurrentScene ?? throw new ArgumentNullException(nameof(cacheCurrentScene));
        _clearSavedMcpProject = clearSavedMcpProject
            ?? throw new ArgumentNullException(nameof(clearSavedMcpProject));
        _clearUiDirty = clearUiDirty ?? throw new ArgumentNullException(nameof(clearUiDirty));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _setStatusTemplate = setStatusTemplate ?? ((key, error, arguments) => setStatus(string.Format(Godot.TranslationServer.Translate(key), arguments), error));
    }

    internal void ResetFinalSaveAttempt() => _finalSaveAttempted = false;

    internal bool SaveProject()
    {
        _lastSavePartial = false;
        AgasProjectResource? project = _project();
        WorkspaceLayoutResource? layout = _layout();
        AgasSceneResource? currentScene = _currentScene();
        if (project == null || layout == null || currentScene == null)
        {
            _setStatus("保存失败：当前项目未完整加载。", true);
            return false;
        }

        if (_requiresReadOnly())
        {
            _setStatusTemplate("保存失败：项目为只读状态（{0}）。", true, new object[] { _readOnlyReasonCode() });
            return false;
        }

        _captureLayoutFromUi();
        _cacheCurrentScene();
        AgasMcpDirtySnapshot? dirtySnapshot = _currentDirtySnapshot();
        ProjectSaveResult save = _projectSaveCoordinator.Save(new ProjectSaveRequest(
            _projectRoot(),
            project,
            layout,
            currentScene,
            _loadedScenes().ToArray(),
            dirtySnapshot?.DirtySceneIds ?? Array.Empty<string>(),
            _uiPendingDeletedSceneIds,
            dirtySnapshot?.PendingDeletedSceneIds ?? Array.Empty<string>(),
            _requiresReadOnly(),
            _readOnlyReasonCode()));
        if (!save.Success)
        {
            return FailSaveForRetry(save.Message, save.PartialSaved);
        }

        _clearSavedMcpProject();
        _clearUiDirty("已保存项目、布局和全部已加载场景。", false);
        return true;
    }

    internal bool FailSaveForRetry(string message, bool partialSaved)
    {
        _lastSavePartial = partialSaved;
        _setStatusTemplate(partialSaved ? "部分保存/待重试：{0}" : "保存失败/待重试：{0}", true, new object[] { message });
        return false;
    }

    internal ProjectLifecycleSaveOutcome SaveProjectForMcpLifecycle()
    {
        bool success = SaveProject();
        bool clean = success && !_isWorkspaceDirty();
        ProjectLifecycleSaveState state = clean
            ? ProjectLifecycleSaveState.Complete
            : _lastSavePartial ? ProjectLifecycleSaveState.Partial : ProjectLifecycleSaveState.None;
        return new ProjectLifecycleSaveOutcome(success, clean, state);
    }

    internal AgasPersistenceResult SaveScene(AgasSceneResource scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return _projectSaveCoordinator.SaveCurrentScene(
            _projectRoot(),
            scene,
            _requiresReadOnly(),
            _readOnlyReasonCode());
    }

    internal AgasPersistenceResult SaveCurrentScene(bool silent)
    {
        AgasPersistenceResult result = _projectSaveCoordinator.SaveCurrentScene(
            _projectRoot(),
            _currentScene(),
            _requiresReadOnly(),
            _readOnlyReasonCode());
        if (!silent)
        {
            _setStatus(
                result.Success ? "当前场景已保存。" : result.ErrorMessage ?? "当前场景保存失败。",
                !result.Success);
        }
        return result;
    }

    internal bool SaveProjectOnceForShutdown()
    {
        if (_project() == null)
        {
            return true;
        }

        if (_requiresReadOnly())
        {
            _finalSaveAttempted = true;
            return true;
        }

        if (!_isWorkspaceDirty())
        {
            _finalSaveAttempted = true;
            return true;
        }

        if (_finalSaveAttempted)
        {
            return true;
        }

        bool saved = SaveProject();
        if (saved)
        {
            _finalSaveAttempted = true;
        }

        return saved;
    }
}
