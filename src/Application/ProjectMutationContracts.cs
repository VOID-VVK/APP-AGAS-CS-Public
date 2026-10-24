using System;
using System.Collections.Generic;
using System.Linq;
using AppAgasCs.Core.Resources;

namespace AppAgasCs.Application;

/// <summary>
/// Narrow document/session port used by project mutations. UI and MCP adapters can expose
/// their live session without handing the application service a repository or UI object graph.
/// </summary>
public interface IProjectMutationSession
{
    AgasProjectResource Project { get; }
    AgasSceneResource? CurrentScene { get; }
    IReadOnlyCollection<AgasSceneResource> LoadedScenes { get; }
    AgasSceneResource? LoadScene(string sceneId);
    AgasSceneResource? GetCachedScene(string sceneId);
    void CacheScene(AgasSceneResource scene);
    void RemoveCachedScene(string sceneId);
    void ChangeCurrentScene(AgasSceneResource? scene);
}

public sealed record ProjectMutationEffects
{
    public ProjectMutationEffects(
        bool dirtyProject,
        bool dirtyLayout,
        IReadOnlyList<string> dirtySceneIds,
        IReadOnlyList<string> deletedSceneIds)
    {
        DirtyProject = dirtyProject;
        DirtyLayout = dirtyLayout;
        DirtySceneIds = Freeze(dirtySceneIds, nameof(dirtySceneIds));
        DeletedSceneIds = Freeze(deletedSceneIds, nameof(deletedSceneIds));
    }

    public bool DirtyProject { get; }
    public bool DirtyLayout { get; }
    public IReadOnlyList<string> DirtySceneIds { get; }
    public IReadOnlyList<string> DeletedSceneIds { get; }

    public static ProjectMutationEffects Scene(string sceneId) =>
        new(false, false, new[] { sceneId }, Array.Empty<string>());
    public static ProjectMutationEffects ProjectAndScene(string sceneId) =>
        new(true, false, new[] { sceneId }, Array.Empty<string>());
    public static ProjectMutationEffects ProjectAndDeletedScene(string sceneId) =>
        new(true, false, Array.Empty<string>(), new[] { sceneId });

    public static ProjectMutationEffects Project() =>
        new(true, false, Array.Empty<string>(), Array.Empty<string>());

    private static IReadOnlyList<string> Freeze(IReadOnlyList<string> source, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        if (source.Any(value => value is null))
        {
            throw new ArgumentException("Mutation effect collections cannot contain null items.", parameterName);
        }
        return Array.AsReadOnly(source.ToArray());
    }
}

/// <summary>A reversible operation that UI policies can place on their undo stack.</summary>
public interface IProjectMutationOperation
{
    string Description { get; }
    ProjectMutationEffects Effects { get; }
    void Apply();
    void Revert();
}

/// <summary>
/// Commit-policy port. Adapters decide whether to mark dirty or save immediately and whether
/// to record the reversible operation in an undo stack; the application service never embeds
/// an MCP/UI-specific persistence or undo policy.
/// </summary>
public interface IProjectMutationExecutionPolicy
{
    void Execute(IProjectMutationOperation operation);
}

public sealed class DirectProjectMutationExecutionPolicy : IProjectMutationExecutionPolicy
{
    public void Execute(IProjectMutationOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        operation.Apply();
    }
}

public enum ProjectMutationError
{
    None, InvalidOperation, SceneNotFound, LayerNotFound, LayerHidden, LayerLocked,
    ImageNotFound, PromptNotFound,
}

public sealed record ProjectMutationResult(
    bool Success,
    ProjectMutationError Error,
    string Message,
    string? SceneId = null,
    string? LayerId = null,
    string? ItemId = null,
    ProjectMutationEffects? Effects = null)
{
    public static ProjectMutationResult Failure(ProjectMutationError error, string message) =>
        new(false, error, message);
}

public enum SceneMutationKind { Add, Rename, Delete, SetActive, SetReadme }
public sealed record SceneMutationCommand(
    SceneMutationKind Kind,
    string? SceneId = null,
    string? Name = null,
    string? Readme = null);

public enum GridMutationKind { SetSize, SetSnapEnabled, SetVisible }
public sealed record GridMutationCommand(GridMutationKind Kind, int SizePx = 0, bool Flag = false);

public enum LayerMutationKind { Add, Rename, Delete, Move, SetVisible, SetLocked, SetShared, SetCurrent }
public sealed record LayerMutationCommand(
    LayerMutationKind Kind, string SceneId, string? LayerId = null, string? Name = null,
    int Direction = 0, bool Flag = false);

public enum ImageMutationKind { Add, Delete, Update }
public sealed record ImageMutationCommand(
    ImageMutationKind Kind, string SceneId, string LayerId, string? ImageId = null,
    string? ImagePath = null, double? X = null, double? Y = null,
    double? ScaleX = null, double? ScaleY = null, double? RotationDegrees = null,
    double? Opacity = null);

public enum PromptMutationKind { Add, Delete, Update, Move, SetEnabled }
public sealed record PromptMutationCommand(
    PromptMutationKind Kind, string SceneId, string LayerId, string? FragmentId = null,
    string? Text = null, bool Enabled = true, int Direction = 0,
    int? Role = null, int? TargetIndex = null, bool? UpdatedEnabled = null);

public sealed record ViewMutationCommand(string SceneId, double CameraX, double CameraY, double Zoom);
