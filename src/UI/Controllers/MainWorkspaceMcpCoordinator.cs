using System;
using AppAgasCs.Application;
using AppAgasCs.Application.Contracts;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Workspace;
using AppAgasCs.Mcp;

namespace AGAS.UI.Controllers;

/// <summary>
/// Owns MainWorkspace's MCP adapter lifecycle: project identity binding, dirty snapshot
/// reconciliation, access-state projection, and construction of the live MCP context.
/// UI refresh behavior stays injected as narrow callbacks.
/// </summary>
internal sealed class MainWorkspaceMcpCoordinator
{
    private readonly AgasResourceRepository _repository;
    private readonly AgasMcpPathValidator _pathValidator;
    private AgasMcpDirtyState _dirtyState = new();
    private AgasMcpDirtySnapshot? _snapshot;

    internal MainWorkspaceMcpCoordinator(
        AgasResourceRepository repository,
        AgasMcpPathValidator pathValidator)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _pathValidator = pathValidator ?? throw new ArgumentNullException(nameof(pathValidator));
    }

    internal bool HasInjectedDirtyState { get; private set; }

    internal void SetDirtyState(AgasMcpDirtyState dirtyState)
    {
        _dirtyState = dirtyState ?? throw new ArgumentNullException(nameof(dirtyState));
        HasInjectedDirtyState = true;
        _snapshot = null;
    }

    internal void ResetProjectSnapshot() => _snapshot = null;

    internal bool TryGetProjectIdentity(
        string projectRoot,
        AgasProjectResource? project,
        bool hasOpenProject,
        out string validatedRoot,
        out string projectId)
    {
        validatedRoot = projectRoot;
        projectId = project?.ProjectId ?? string.Empty;
        return hasOpenProject
            && _pathValidator.ValidateProjectRoot(projectRoot).Success
            && AgasMcpPathValidator.IsSafeIdentifier(projectId);
    }

    internal bool TryBindProject(
        string projectRoot,
        AgasProjectResource? project,
        bool hasOpenProject,
        out string errorMessage)
    {
        if (!HasInjectedDirtyState)
        {
            errorMessage = "MCP dirty state has not been injected.";
            return false;
        }

        if (!TryGetProjectIdentity(
                projectRoot,
                project,
                hasOpenProject,
                out string validatedRoot,
                out string projectId))
        {
            errorMessage = "Current project identity is unavailable.";
            return false;
        }

        return _dirtyState.TryBindProject(validatedRoot, projectId, out errorMessage);
    }

    internal AgasMcpDirtySnapshot? RefreshSnapshot(
        string projectRoot,
        AgasProjectResource? project)
    {
        _snapshot = project == null || !HasInjectedDirtyState
            ? null
            : _dirtyState.SnapshotForProject(projectRoot, project.ProjectId);
        return _snapshot;
    }

    internal bool TryAcceptSnapshot(
        string projectRoot,
        AgasProjectResource? project,
        AgasMcpDirtySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (project == null || !_pathValidator.ValidateProjectRoot(projectRoot).Success)
        {
            return false;
        }

        if (!string.Equals(snapshot.ProjectId, project.ProjectId, StringComparison.Ordinal)
            || !SameRoot(snapshot.ProjectRoot, projectRoot))
        {
            return false;
        }

        _snapshot = snapshot;
        return true;
    }

    internal AgasMcpDirtySnapshot? GetCurrentSnapshot(
        string projectRoot,
        AgasProjectResource? project)
    {
        if (project == null)
        {
            return null;
        }

        if (HasInjectedDirtyState)
        {
            AgasMcpDirtySnapshot? current = _dirtyState.SnapshotForProject(projectRoot, project.ProjectId);
            if (current != null)
            {
                _snapshot = current;
                return current;
            }
        }

        return _snapshot != null
            && string.Equals(_snapshot.ProjectId, project.ProjectId, StringComparison.Ordinal)
            && SameRoot(_snapshot.ProjectRoot, projectRoot)
                ? _snapshot
                : null;
    }

    internal bool IsDirty(string projectRoot, AgasProjectResource? project) =>
        project != null
        && _snapshot is { IsDirty: true }
        && string.Equals(_snapshot.ProjectId, project.ProjectId, StringComparison.Ordinal)
        && SameRoot(_snapshot.ProjectRoot, projectRoot);

    internal void ClearSavedProject(string projectRoot, AgasProjectResource project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _dirtyState.ClearForProject(projectRoot, project.ProjectId);
        _snapshot = null;
    }

    internal AgasMcpDirtySnapshot? MarkPackageImported(
        string projectRoot,
        AgasProjectResource project,
        string sceneId)
    {
        if (!HasInjectedDirtyState
            || !_dirtyState.TryBindProject(projectRoot, project.ProjectId, out _))
            return null;
        _dirtyState.MarkProjectDirty();
        _dirtyState.MarkSceneDirty(sceneId);
        return RefreshSnapshot(projectRoot, project);
    }

    internal AgasMcpProjectAccessState GetAccessState(
        bool hasOpenProject,
        bool requiresReadOnly,
        string readOnlyReasonCode) =>
        hasOpenProject
            ? new AgasMcpProjectAccessState(true, requiresReadOnly, readOnlyReasonCode)
            : AgasMcpProjectAccessState.None;

    internal AgasMcpProjectContext? CreateContext(MainWorkspaceMcpContextRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Session == null
            || !request.Session.IsOpen
            || request.Layout == null)
        {
            return null;
        }

        if (request.Session.CurrentScene != null)
        {
            request.Session.CacheScene(request.Session.CurrentScene);
        }

        return new AgasMcpProjectContext(
            request.Session,
            request.Layout,
            _repository,
            new AgasMcpUiRefreshScope
            {
                Dispatcher = request.Dispatcher,
                SetStatus = request.SetStatus,
                MarkMcpDirty = request.SetStatus,
                SyncDirtyState = request.SyncDirtyState,
                RefreshProject = request.RefreshProject,
                RefreshCanvas = request.RefreshCanvas,
            },
            request.RequiresReadOnly,
            request.ReadOnlyReasonCode,
            request.SelectionWorkspace,
            request.AiGenerationWorkspace);
    }

    private static bool SameRoot(string first, string second) =>
        string.Equals(
            AgasMcpPathValidator.NormalizeRoot(first),
            AgasMcpPathValidator.NormalizeRoot(second),
            StringComparison.Ordinal);
}

internal sealed record MainWorkspaceMcpContextRequest(
    ProjectSession? Session,
    WorkspaceLayoutResource? Layout,
    bool RequiresReadOnly,
    string ReadOnlyReasonCode,
    ICanvasSelectionWorkspaceBridge? SelectionWorkspace,
    IAgasAiGenerationWorkspaceBridge? AiGenerationWorkspace,
    AgasMcpMainThreadDispatcher? Dispatcher,
    Action<string, bool> SetStatus,
    Action<AgasMcpDirtySnapshot> SyncDirtyState,
    Action RefreshProject,
    Action RefreshCanvas);
