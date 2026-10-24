using System;
using System.Collections.Generic;
using System.Linq;
using AppAgasCs.Core.Resources;

namespace AppAgasCs.Application;

internal static class ProjectSnapshotCollections
{
    internal static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> source, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        if (source.Any(item => item is null))
        {
            throw new ArgumentException("Snapshot collections cannot contain null items.", parameterName);
        }
        return Array.AsReadOnly(source.ToArray());
    }
}

public sealed record ProjectGridSnapshot(int SizePx, bool SnapEnabled, bool Visible);

public sealed record ProjectSceneIndexSnapshot(string Id, string Name, string RelativePath, int Order);

public sealed record ProjectSnapshot
{
    public ProjectSnapshot(
        int resourceVersion,
        string projectId,
        string name,
        string activeSceneId,
        ProjectGridSnapshot grid,
        IReadOnlyList<ProjectSceneIndexSnapshot> scenes,
        bool needsMigration,
        bool requiresReadOnly)
    {
        ResourceVersion = resourceVersion;
        ProjectId = projectId ?? throw new ArgumentNullException(nameof(projectId));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        ActiveSceneId = activeSceneId ?? throw new ArgumentNullException(nameof(activeSceneId));
        Grid = grid ?? throw new ArgumentNullException(nameof(grid));
        Scenes = ProjectSnapshotCollections.Freeze(scenes, nameof(scenes));
        NeedsMigration = needsMigration;
        RequiresReadOnly = requiresReadOnly;
    }

    public int ResourceVersion { get; }
    public string ProjectId { get; }
    public string Name { get; }
    public string ActiveSceneId { get; }
    public ProjectGridSnapshot Grid { get; }
    public IReadOnlyList<ProjectSceneIndexSnapshot> Scenes { get; }
    public bool NeedsMigration { get; }
    public bool RequiresReadOnly { get; }

}

public sealed record WorkspaceLayoutSnapshot
{
    public WorkspaceLayoutSnapshot(
        int resourceVersion,
        double leftPanelWidth,
        bool leftPanelCollapsed,
        double previewHeight,
        bool previewCollapsed,
        double rightPanelWidth,
        bool rightPanelCollapsed,
        double bottomDockHeight,
        bool bottomDockCollapsed,
        string activeLeftTab,
        string activeBottomTab)
    {
        ResourceVersion = resourceVersion;
        LeftPanelWidth = leftPanelWidth;
        LeftPanelCollapsed = leftPanelCollapsed;
        PreviewHeight = previewHeight;
        PreviewCollapsed = previewCollapsed;
        RightPanelWidth = rightPanelWidth;
        RightPanelCollapsed = rightPanelCollapsed;
        BottomDockHeight = bottomDockHeight;
        BottomDockCollapsed = bottomDockCollapsed;
        ActiveLeftTab = activeLeftTab ?? throw new ArgumentNullException(nameof(activeLeftTab));
        ActiveBottomTab = activeBottomTab ?? throw new ArgumentNullException(nameof(activeBottomTab));
    }

    public int ResourceVersion { get; }
    public double LeftPanelWidth { get; }
    public bool LeftPanelCollapsed { get; }
    public double PreviewHeight { get; }
    public bool PreviewCollapsed { get; }
    public double RightPanelWidth { get; }
    public bool RightPanelCollapsed { get; }
    public double BottomDockHeight { get; }
    public bool BottomDockCollapsed { get; }
    public string ActiveLeftTab { get; }
    public string ActiveBottomTab { get; }

}

public sealed record LoadedSceneSnapshot(
    int ResourceVersion,
    string Id,
    string Name,
    string ActiveLayerId,
    int LayerCount);

/// <summary>
/// Immutable, non-Godot projection of a workspace session. Resource instances remain behind
/// the UI/application ports so callers cannot mutate serialized state through public getters.
/// </summary>
public sealed record ProjectSessionSnapshot
{
    public ProjectSessionSnapshot(
        bool hasOpenProject,
        string projectRoot,
        ProjectSnapshot? project,
        WorkspaceLayoutSnapshot? layout,
        string currentSceneId,
        string currentSceneName,
        IReadOnlyList<LoadedSceneSnapshot> loadedScenes)
    {
        HasOpenProject = hasOpenProject;
        ProjectRoot = projectRoot ?? throw new ArgumentNullException(nameof(projectRoot));
        Project = project;
        Layout = layout;
        CurrentSceneId = currentSceneId ?? throw new ArgumentNullException(nameof(currentSceneId));
        CurrentSceneName = currentSceneName ?? throw new ArgumentNullException(nameof(currentSceneName));
        LoadedScenes = ProjectSnapshotCollections.Freeze(loadedScenes, nameof(loadedScenes));
    }

    public bool HasOpenProject { get; }
    public string ProjectRoot { get; }
    public ProjectSnapshot? Project { get; }
    public WorkspaceLayoutSnapshot? Layout { get; }
    public string CurrentSceneId { get; }
    public string CurrentSceneName { get; }
    public IReadOnlyList<LoadedSceneSnapshot> LoadedScenes { get; }

    public static ProjectSessionSnapshot Capture(
        string projectRoot,
        AgasProjectResource? project,
        WorkspaceLayoutResource? layout,
        AgasSceneResource? currentScene,
        IEnumerable<AgasSceneResource> loadedScenes)
    {
        GridSettingsResource? grid = project?.Grid;
        ProjectSnapshot? projectSnapshot = project == null
            ? null
            : new ProjectSnapshot(
                project.ResourceVersion,
                project.ProjectId,
                project.Name,
                project.ActiveSceneId,
                new ProjectGridSnapshot(
                    grid?.SizePx ?? 0,
                    grid?.SnapEnabled ?? false,
                    grid?.Visible ?? false),
                Array.AsReadOnly((project.Scenes ?? new Godot.Collections.Array<SceneIndexResource>())
                    .Select(scene => new ProjectSceneIndexSnapshot(scene.Id, scene.Name, scene.RelativePath, scene.Order))
                    .ToArray()),
                project.NeedsMigration,
                project.RequiresReadOnly);

        WorkspaceLayoutSnapshot? layoutSnapshot = layout == null
            ? null
            : new WorkspaceLayoutSnapshot(
                layout.ResourceVersion,
                layout.LeftPanelWidth,
                layout.LeftPanelCollapsed,
                layout.PreviewHeight,
                layout.PreviewCollapsed,
                layout.RightPanelWidth,
                layout.RightPanelCollapsed,
                layout.BottomDockHeight,
                layout.BottomDockCollapsed,
                layout.ActiveLeftTab,
                layout.ActiveBottomTab);

        LoadedSceneSnapshot[] sceneSnapshots = loadedScenes
            .Distinct()
            .OrderBy(scene => scene.Id, StringComparer.Ordinal)
            .Select(scene => new LoadedSceneSnapshot(
                scene.ResourceVersion,
                scene.Id,
                scene.Name,
                scene.ActiveLayerId,
                scene.Layers.Count))
            .ToArray();

        bool hasOpenProject = projectSnapshot != null
            && layoutSnapshot != null
            && !string.IsNullOrWhiteSpace(projectRoot);
        return new ProjectSessionSnapshot(
            hasOpenProject,
            projectRoot,
            projectSnapshot,
            layoutSnapshot,
            currentScene?.Id ?? string.Empty,
            currentScene?.Name ?? string.Empty,
            Array.AsReadOnly(sceneSnapshots));
    }
}
