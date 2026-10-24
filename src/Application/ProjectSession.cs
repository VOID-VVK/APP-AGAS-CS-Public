using System;
using System.Collections.Generic;
using System.Linq;
using AppAgasCs.Core.Resources;

namespace AppAgasCs.Application;

/// <summary>
/// Single live project session: project resource, current scene, and scene cache.
/// UI, canvas mutations, and MCP should share one instance so multi-scene loads
/// and cross-entry mutations see the same in-memory objects.
/// </summary>
public sealed class ProjectSession : IProjectMutationSession
{
    private readonly Dictionary<string, AgasSceneResource> _sceneCache = new(StringComparer.Ordinal);
    private Func<string, AgasSceneResource?>? _loadScene;
    private Action<AgasSceneResource?>? _currentSceneChanged;

    public string ProjectRoot { get; private set; } = string.Empty;

    public AgasProjectResource? ProjectOrNull { get; private set; }

    public AgasSceneResource? CurrentScene { get; private set; }

    /// <summary>
    /// Composition-only mutable cache for session controllers in this assembly.
    /// External callers must use <see cref="CacheScene"/>, <see cref="GetCachedScene"/>,
    /// <see cref="RemoveCachedScene"/>, and <see cref="LoadedScenes"/> (Law of Demeter).
    /// </summary>
    internal Dictionary<string, AgasSceneResource> SceneCache => _sceneCache;

    public bool IsOpen =>
        ProjectOrNull != null && !string.IsNullOrWhiteSpace(ProjectRoot);

    public IReadOnlyCollection<AgasSceneResource> LoadedScenes =>
        _sceneCache.Values.Distinct().ToArray();

    AgasProjectResource IProjectMutationSession.Project => Project;

    public AgasProjectResource Project =>
        ProjectOrNull ?? throw new InvalidOperationException("No project is open.");

    AgasSceneResource? IProjectMutationSession.CurrentScene => CurrentScene;

    IReadOnlyCollection<AgasSceneResource> IProjectMutationSession.LoadedScenes => LoadedScenes;

    /// <summary>
    /// Configures disk/cache load and optional UI side effects when the current scene changes.
    /// Safe to call multiple times (e.g. after controller construction).
    /// </summary>
    public void Configure(
        Func<string, AgasSceneResource?>? loadScene = null,
        Action<AgasSceneResource?>? currentSceneChanged = null)
    {
        if (loadScene != null)
        {
            _loadScene = loadScene;
        }

        if (currentSceneChanged != null)
        {
            _currentSceneChanged = currentSceneChanged;
        }
    }

    /// <summary>Clears session state and sets the project root for a new open attempt.</summary>
    public void Reset(string projectRoot)
    {
        _sceneCache.Clear();
        ProjectOrNull = null;
        CurrentScene = null;
        ProjectRoot = projectRoot ?? string.Empty;
    }

    public void Clear() => Reset(string.Empty);

    public void AcceptProject(AgasProjectResource project)
    {
        ProjectOrNull = project ?? throw new ArgumentNullException(nameof(project));
    }

    public AgasSceneResource? LoadScene(string sceneId)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            return null;
        }

        if (CurrentScene != null && string.Equals(CurrentScene.Id, sceneId, StringComparison.Ordinal))
        {
            return CurrentScene;
        }

        if (_sceneCache.TryGetValue(sceneId, out AgasSceneResource? cached))
        {
            return cached;
        }

        AgasSceneResource? loaded = _loadScene?.Invoke(sceneId);
        if (loaded != null)
        {
            _sceneCache[loaded.Id] = loaded;
        }

        return loaded;
    }

    public AgasSceneResource? GetCachedScene(string sceneId)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            return null;
        }

        if (CurrentScene != null && string.Equals(CurrentScene.Id, sceneId, StringComparison.Ordinal))
        {
            return CurrentScene;
        }

        return _sceneCache.TryGetValue(sceneId, out AgasSceneResource? scene) ? scene : null;
    }

    public void CacheScene(AgasSceneResource scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _sceneCache[scene.Id] = scene;
    }

    public void RemoveCachedScene(string sceneId)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            return;
        }

        // Match prior MainWorkspace behavior: cache eviction only.
        // Active-scene deletion updates current via ChangeCurrentScene separately.
        _sceneCache.Remove(sceneId);
    }

    public void ChangeCurrentScene(AgasSceneResource? scene) =>
        SetCurrentScene(scene, notify: true);

    /// <summary>
    /// Updates current scene and cache. Use <paramref name="notify"/> = false when the caller
    /// is already applying UI side effects (avoids re-entrancy through the change handler).
    /// </summary>
    public void SetCurrentScene(AgasSceneResource? scene, bool notify = true)
    {
        if (ReferenceEquals(CurrentScene, scene))
        {
            return;
        }

        CurrentScene = scene;
        if (scene != null)
        {
            _sceneCache[scene.Id] = scene;
        }

        if (notify)
        {
            _currentSceneChanged?.Invoke(scene);
        }
    }
}
