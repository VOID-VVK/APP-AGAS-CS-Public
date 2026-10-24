#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using AppAgasCs.Application;
using AppAgasCs.Application.Contracts;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;

namespace AppAgasCs.Mcp;

/// <summary>
/// Adapter from the mutable MCP project context to the application read-model port.
/// Resource graph traversal stays here instead of leaking into protocol facades.
/// </summary>
internal sealed class AgasMcpProjectReadModelQuery : IProjectReadModelQuery
{
    private readonly AgasMcpProjectContextProvider _contextProvider;

    public AgasMcpProjectReadModelQuery(AgasMcpProjectContextProvider contextProvider)
    {
        _contextProvider = contextProvider ?? throw new ArgumentNullException(nameof(contextProvider));
    }

    public ProjectReadModelSnapshot GetSnapshot(ProjectReadModelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        AgasMcpProjectContext context = _contextProvider.GetCurrent()
            ?? throw new InvalidOperationException("No AGAS project is currently open.");
        AgasProjectResource project = context.Project;

        ProjectSceneReadModel[] scenes = request.IncludeScenes
            ? project.Scenes.OrderBy(scene => scene.Order)
                .ThenBy(scene => scene.Id, StringComparer.Ordinal)
                .Select(scene => new ProjectSceneReadModel(
                    scene.Id,
                    scene.Name,
                    scene.Order,
                    request.LoadScenes
                        ? context.TryGetLoadedScene(scene.Id) != null
                        : context.TryGetCachedScene(scene.Id) != null))
                .ToArray()
            : Array.Empty<ProjectSceneReadModel>();

        IReadOnlyCollection<AgasSceneResource> queriedScenes = request.IncludeLayers
            || request.IncludeImages
            || request.IncludePrompts
                ? context.CachedScenes
                    .OrderBy(scene => scene.Id, StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<AgasSceneResource>();

        ProjectLayerReadModel[] layers = request.IncludeLayers
            ? queriedScenes.Where(scene => Matches(request.SceneId, scene.Id))
                .SelectMany(scene => scene.Layers.OrderBy(layer => layer.Order)
                    .ThenBy(layer => layer.Id, StringComparer.Ordinal)
                    .Select(layer => new ProjectLayerReadModel(
                        scene.Id,
                        layer.Id,
                        layer.Name,
                        layer.Order,
                        layer.Visible,
                        layer.Locked,
                        layer.Shared,
                        layer.Images.Count,
                        layer.PromptFragments.Count)))
                .ToArray()
            : Array.Empty<ProjectLayerReadModel>();

        ProjectImageReadModel[] images = request.IncludeImages
            ? queriedScenes.Where(scene => Matches(request.SceneId, scene.Id))
                .SelectMany(scene => scene.Layers
                    .Where(layer => Matches(request.LayerId, layer.Id))
                    .SelectMany(layer => layer.Images.OrderBy(image => image.Order)
                        .ThenBy(image => image.Id, StringComparer.Ordinal)
                        .Select(image => new ProjectImageReadModel(
                            scene.Id,
                            layer.Id,
                            image.Id,
                            image.ImagePath,
                            image.Order,
                            image.X,
                            image.Y,
                            image.ScaleX,
                            image.ScaleY,
                            image.RotationDegrees,
                            image.Opacity))))
                .ToArray()
            : Array.Empty<ProjectImageReadModel>();

        ProjectPromptReadModel[] prompts = request.IncludePrompts
            ? queriedScenes.Where(scene => Matches(request.SceneId, scene.Id))
                .SelectMany(scene => scene.Layers
                    .Where(layer => Matches(request.LayerId, layer.Id))
                    .SelectMany(layer => layer.PromptFragments.OrderBy(prompt => prompt.Order)
                        .ThenBy(prompt => prompt.Id, StringComparer.Ordinal)
                        .Select(prompt => new ProjectPromptReadModel(
                            scene.Id,
                            layer.Id,
                            prompt.Id,
                            prompt.Text,
                            PromptRole(prompt.Role),
                            prompt.Enabled,
                            prompt.Order))))
                .ToArray()
            : Array.Empty<ProjectPromptReadModel>();

        return new ProjectReadModelSnapshot(
            project.ProjectId,
            project.Name,
            project.ActiveSceneId,
            new ProjectGridReadModel(project.Grid.SizePx, project.Grid.SnapEnabled, project.Grid.Visible),
            scenes,
            layers,
            images,
            prompts);
    }

    private static bool Matches(string? filter, string value) =>
        filter == null || string.Equals(filter, value, StringComparison.Ordinal);

    internal static string PromptRole(PromptFragmentRole role) => role switch
    {
        PromptFragmentRole.Environment => "environment",
        PromptFragmentRole.NegativeConstraint => "negativeConstraint",
        _ => "general",
    };
}

/// <summary>
/// Main-thread-only adapter that projects live or CacheMode.Ignore resources into scalar scene detail rows.
/// No Resource escapes this adapter.
/// </summary>
internal sealed class AgasMcpSceneDetailSnapshotSource : IAgasSceneDetailSnapshotSource
{
    private readonly AgasMcpProjectContextProvider _contextProvider;
    private readonly AgasResourceRepository _repository;

    public AgasMcpSceneDetailSnapshotSource(
        AgasMcpProjectContextProvider contextProvider,
        AgasResourceRepository? repository = null)
    {
        _contextProvider = contextProvider ?? throw new ArgumentNullException(nameof(contextProvider));
        _repository = repository ?? new AgasResourceRepository();
    }

    public bool TryCaptureLoadedLive(string sceneId, out AgasSceneDetailSnapshot? snapshot)
    {
        snapshot = null;
        AgasMcpProjectContext? context = _contextProvider.GetCurrent();
        AgasSceneResource? scene = context?.TryGetCachedScene(sceneId);
        if (scene == null) return false;
        snapshot = Project(scene);
        return true;
    }

    public AgasSceneDetailSnapshot? LoadFreshAndProject(string sceneId)
    {
        AgasMcpProjectContext? context = _contextProvider.GetCurrent();
        if (context == null
            || context.Project.Scenes.All(item => !string.Equals(item.Id, sceneId, StringComparison.Ordinal)))
            return null;
        AgasLoadResult<AgasSceneResource> loaded = _repository.LoadFresh<AgasSceneResource>(
            AgasPaths.SceneFile(context.ProjectRoot, sceneId));
        return loaded.Success && loaded.Resource != null ? Project(loaded.Resource) : null;
    }

    private static AgasSceneDetailSnapshot Project(AgasSceneResource scene)
    {
        var items = new List<AgasSceneDetailItem>();
        foreach (AgasLayerResource layer in scene.Layers)
        {
            items.Add(new AgasSceneDetailLayerItem(
                scene.Id, layer.Id, layer.Name, layer.Order, layer.Visible, layer.Locked, layer.Shared,
                layer.Images.Count, layer.PromptFragments.Count));
            items.AddRange(layer.Images.Select(image => new AgasSceneDetailImageItem(
                scene.Id, layer.Id, image.Id, image.ImagePath, image.Order,
                image.X, image.Y, image.ScaleX, image.ScaleY, image.RotationDegrees, image.Opacity)));
            items.AddRange(layer.PromptFragments.Select(prompt => new AgasSceneDetailPromptItem(
                scene.Id, layer.Id, prompt.Id, prompt.Text,
                AgasMcpProjectReadModelQuery.PromptRole(prompt.Role), prompt.Enabled, prompt.Order)));
        }
        return new AgasSceneDetailSnapshot(
            scene.Id,
            scene.Name,
            string.IsNullOrWhiteSpace(scene.ActiveLayerId) ? null : scene.ActiveLayerId,
            new AgasSceneDetailViewState(scene.ViewState.CameraX, scene.ViewState.CameraY, scene.ViewState.Zoom),
            items,
            scene.Readme);
    }
}
