#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace AppAgasCs.Application.Contracts;

/// <summary>
/// Narrow read port for project query consumers. Callers receive immutable scalar rows
/// rather than walking the mutable Godot resource graph.
/// </summary>
public interface IProjectReadModelQuery
{
    ProjectReadModelSnapshot GetSnapshot(ProjectReadModelRequest request);
}

public sealed record ProjectReadModelRequest(
    bool IncludeScenes = false,
    bool IncludeLayers = false,
    bool IncludeImages = false,
    bool IncludePrompts = false,
    string? SceneId = null,
    string? LayerId = null,
    bool LoadScenes = true);

public sealed record ProjectReadModelSnapshot
{
    public ProjectReadModelSnapshot(
        string projectId,
        string projectName,
        string activeSceneId,
        ProjectGridReadModel grid,
        IEnumerable<ProjectSceneReadModel> scenes,
        IEnumerable<ProjectLayerReadModel> layers,
        IEnumerable<ProjectImageReadModel> images,
        IEnumerable<ProjectPromptReadModel> prompts)
    {
        ProjectId = projectId ?? throw new ArgumentNullException(nameof(projectId));
        ProjectName = projectName ?? throw new ArgumentNullException(nameof(projectName));
        ActiveSceneId = activeSceneId ?? throw new ArgumentNullException(nameof(activeSceneId));
        Grid = grid ?? throw new ArgumentNullException(nameof(grid));
        Scenes = Freeze(scenes, nameof(scenes));
        Layers = Freeze(layers, nameof(layers));
        Images = Freeze(images, nameof(images));
        Prompts = Freeze(prompts, nameof(prompts));
    }

    public string ProjectId { get; }
    public string ProjectName { get; }
    public string ActiveSceneId { get; }
    public ProjectGridReadModel Grid { get; }
    public IImmutableList<ProjectSceneReadModel> Scenes { get; }
    public IImmutableList<ProjectLayerReadModel> Layers { get; }
    public IImmutableList<ProjectImageReadModel> Images { get; }
    public IImmutableList<ProjectPromptReadModel> Prompts { get; }

    private static IImmutableList<T> Freeze<T>(IEnumerable<T> source, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        ImmutableList<T>.Builder builder = ImmutableList.CreateBuilder<T>();
        foreach (T item in source)
        {
            if (item is null)
            {
                throw new ArgumentException("Read-model collections cannot contain null items.", parameterName);
            }
            builder.Add(item);
        }
        return builder.ToImmutable();
    }
}

public sealed record ProjectGridReadModel(int SizePx, bool SnapEnabled, bool Visible);

public sealed record ProjectSceneReadModel(string Id, string Name, int Order, bool Loaded);

public sealed record ProjectLayerReadModel(
    string SceneId,
    string Id,
    string Name,
    int Order,
    bool Visible,
    bool Locked,
    bool Shared,
    int ImageCount,
    int PromptCount);

public sealed record ProjectImageReadModel(
    string SceneId,
    string LayerId,
    string Id,
    string ImagePath,
    int Order,
    double X,
    double Y,
    double ScaleX,
    double ScaleY,
    double RotationDegrees,
    double Opacity);

public sealed record ProjectPromptReadModel(
    string SceneId,
    string LayerId,
    string Id,
    string Text,
    string Role,
    bool Enabled,
    int Order)
{
    public ProjectPromptReadModel(
        string sceneId,
        string layerId,
        string id,
        string text,
        bool enabled,
        int order)
        : this(sceneId, layerId, id, text, "general", enabled, order)
    {
    }
}
