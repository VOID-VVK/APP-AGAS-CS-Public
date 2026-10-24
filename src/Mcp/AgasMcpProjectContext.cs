using System;
using System.Collections.Generic;
using AppAgasCs.Application;
using AppAgasCs.Application.Contracts;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Workspace;
using Godot;

namespace AppAgasCs.Mcp;

/// <summary>
/// MCP-facing view of the open project. Mutation session semantics are delegated to the
/// shared <see cref="ProjectSession"/> so UI and agent tools share one scene cache/current.
/// </summary>
public sealed class AgasMcpProjectContext : IProjectMutationSession
{
    private readonly ProjectSession _session;
    private readonly AgasResourceRepository _repository;
    private readonly AgasMcpUiRefreshScope _uiRefresh;

    internal AgasMcpProjectContext(
        ProjectSession session,
        WorkspaceLayoutResource layout,
        AgasResourceRepository? repository = null,
        AgasMcpUiRefreshScope? uiRefresh = null,
        bool requiresReadOnly = false,
        string readOnlyReasonCode = "",
        ICanvasSelectionWorkspaceBridge? selectionWorkspace = null,
        IAgasAiGenerationWorkspaceBridge? aiGenerationWorkspace = null,
        ICanvasLayerMergeBridge? layerMergeWorkspace = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        if (!_session.IsOpen)
        {
            throw new InvalidOperationException("ProjectSession must be open before creating MCP context.");
        }

        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _repository = repository ?? new AgasResourceRepository();
        _uiRefresh = uiRefresh ?? AgasMcpUiRefreshScope.Empty;
        SelectionWorkspace = selectionWorkspace;
        AiGenerationWorkspace = aiGenerationWorkspace;
        LayerMergeWorkspace = layerMergeWorkspace ?? selectionWorkspace as ICanvasLayerMergeBridge;

        AgasProjectResource project = _session.Project;
        bool projectVersionInvalid = AgasProjectResource.IsOlderThanSupportedResourceVersion(project.ResourceVersion);
        bool projectVersionFuture = AgasProjectResource.IsFutureResourceVersion(project.ResourceVersion);
        bool projectModelReadOnly = projectVersionInvalid || projectVersionFuture;
        string projectModelReason = projectVersionInvalid
            ? "PROJECT_VERSION_INVALID"
            : projectVersionFuture ? AgasProjectResource.FutureProjectReadOnlyCode : string.Empty;
        RequiresReadOnly = requiresReadOnly || projectModelReadOnly;
        ReadOnlyReasonCode = RequiresReadOnly
            ? projectModelReadOnly
                ? projectModelReason
                : string.IsNullOrWhiteSpace(readOnlyReasonCode) ? AgasProjectResource.FutureProjectReadOnlyCode : readOnlyReasonCode
            : string.Empty;

        // Keep current scene in the shared cache when present.
        if (_session.CurrentScene != null)
        {
            _session.CacheScene(_session.CurrentScene);
        }
    }

    /// <summary>Shared live session (project + cache + current).</summary>
    internal ProjectSession Session => _session;

    public string ProjectRoot => _session.ProjectRoot;
    public string ProjectId => Project.ProjectId;
    internal AgasProjectResource Project => _session.Project;
    internal WorkspaceLayoutResource Layout { get; }
    internal AgasSceneResource? CurrentScene
    {
        get => _session.CurrentScene;
        set => _session.ChangeCurrentScene(value);
    }
    internal IReadOnlyCollection<AgasSceneResource> CachedScenes => _session.LoadedScenes;
    internal ICanvasSelectionWorkspaceBridge? SelectionWorkspace { get; }
    internal IAgasAiGenerationWorkspaceBridge? AiGenerationWorkspace { get; }
    internal ICanvasLayerMergeBridge? LayerMergeWorkspace { get; }
    public bool RequiresReadOnly { get; }
    public string ReadOnlyReasonCode { get; }
    public AgasMcpProjectAccessState AccessState => new(true, RequiresReadOnly, ReadOnlyReasonCode);

    AgasProjectResource IProjectMutationSession.Project => _session.Project;

    IReadOnlyCollection<AgasSceneResource> IProjectMutationSession.LoadedScenes => _session.LoadedScenes;

    AgasSceneResource? IProjectMutationSession.CurrentScene => _session.CurrentScene;

    AgasSceneResource? IProjectMutationSession.LoadScene(string sceneId) => _session.LoadScene(sceneId);

    AgasSceneResource? IProjectMutationSession.GetCachedScene(string sceneId) => _session.GetCachedScene(sceneId);

    void IProjectMutationSession.CacheScene(AgasSceneResource scene) => _session.CacheScene(scene);

    void IProjectMutationSession.RemoveCachedScene(string sceneId) => _session.RemoveCachedScene(sceneId);

    void IProjectMutationSession.ChangeCurrentScene(AgasSceneResource? scene) => _session.ChangeCurrentScene(scene);

    internal AgasSceneResource? TryGetLoadedScene(string sceneId) => _session.LoadScene(sceneId);

    internal AgasSceneResource? TryGetCachedScene(string sceneId) => _session.GetCachedScene(sceneId);

    internal AgasPersistenceResult SaveResource<T>(T resource, string absolutePath) where T : Resource =>
        _repository.Save(resource, absolutePath);

    internal void NotifyDirtyState(AgasMcpDirtySnapshot snapshot) => _uiRefresh.NotifyDirtyState(snapshot);

    internal void NotifyMutation(string message, bool error = false) => _uiRefresh.NotifyMutation(message, error);

    internal void NotifyStatus(string message, bool error = false) => _uiRefresh.NotifyStatus(message, error);

    internal AgasMcpProjectContext WithAiGenerationWorkspace(IAgasAiGenerationWorkspaceBridge workspace) =>
        new(
            _session,
            Layout,
            _repository,
            _uiRefresh,
            RequiresReadOnly,
            ReadOnlyReasonCode,
            SelectionWorkspace,
            workspace,
            LayerMergeWorkspace);
}
