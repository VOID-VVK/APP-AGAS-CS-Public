using System;
using System.Collections.Generic;
using System.Linq;
using AppAgasCs.Mcp.Contracts;

namespace AppAgasCs.Application;

public readonly record struct AgasSceneDetailProjectScope(string ProjectRootIdentity, string ProjectId)
{
    public static AgasSceneDetailProjectScope Create(string projectRootIdentity, string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRootIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return new AgasSceneDetailProjectScope(projectRootIdentity.Trim(), projectId.Trim());
    }
}

public sealed record AgasSceneDetailViewState(double CameraX, double CameraY, double Zoom);

public abstract record AgasSceneDetailItem(string SceneId, string Id, int Order)
{
    public abstract AgasSceneDetailItemKind Kind { get; }
}

public enum AgasSceneDetailItemKind
{
    Layer = 0,
    Image = 1,
    Prompt = 2,
}

public sealed record AgasSceneDetailLayerItem(
    string SceneId,
    string LayerId,
    string Name,
    int Order,
    bool Visible,
    bool Locked,
    bool Shared,
    int ImageCount,
    int PromptCount)
    : AgasSceneDetailItem(SceneId, LayerId, Order)
{
    public override AgasSceneDetailItemKind Kind => AgasSceneDetailItemKind.Layer;
}

public sealed record AgasSceneDetailImageItem(
    string SceneId,
    string LayerId,
    string ImageId,
    string ImagePath,
    int Order,
    double X,
    double Y,
    double ScaleX,
    double ScaleY,
    double RotationDegrees,
    double Opacity)
    : AgasSceneDetailItem(SceneId, ImageId, Order)
{
    public override AgasSceneDetailItemKind Kind => AgasSceneDetailItemKind.Image;
}

public sealed record AgasSceneDetailPromptItem(
    string SceneId,
    string LayerId,
    string FragmentId,
    string Text,
    string Role,
    bool Enabled,
    int Order)
    : AgasSceneDetailItem(SceneId, FragmentId, Order)
{
    public override AgasSceneDetailItemKind Kind => AgasSceneDetailItemKind.Prompt;
}

public sealed class AgasSceneDetailSnapshot
{
    public AgasSceneDetailSnapshot(
        string sceneId,
        string name,
        string? activeLayerId,
        AgasSceneDetailViewState viewState,
        IEnumerable<AgasSceneDetailItem> items,
        string? readme = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneId);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(viewState);
        ArgumentNullException.ThrowIfNull(items);
        AgasSceneDetailItem[] frozenItems = items.ToArray();
        if (frozenItems.Any(item => item == null))
            throw new ArgumentException("Scene detail items cannot contain null.", nameof(items));

        SceneId = sceneId;
        Name = name;
        ActiveLayerId = activeLayerId;
        ViewState = viewState;
        Items = Array.AsReadOnly(frozenItems);
        Readme = readme ?? string.Empty;
    }

    public string SceneId { get; }
    public string Name { get; }
    public string? ActiveLayerId { get; }
    public AgasSceneDetailViewState ViewState { get; }
    public IReadOnlyList<AgasSceneDetailItem> Items { get; }
    public string Readme { get; }
}

public enum AgasSceneDetailSourceKind
{
    LoadedLive,
    FreshDisk,
}

public sealed record AgasSceneDetailCaptureResult(
    AgasSceneDetailSourceKind Source,
    AgasSceneDetailSnapshot Snapshot);

/// <summary>
/// Main-thread adapter contract. Implementations may touch Godot Resource objects internally,
/// but must project them completely before returning this scalar-only snapshot.
/// </summary>
public interface IAgasSceneDetailSnapshotSource
{
    bool TryCaptureLoadedLive(string sceneId, out AgasSceneDetailSnapshot? snapshot);
    AgasSceneDetailSnapshot? LoadFreshAndProject(string sceneId);
}

public interface IAgasSceneDetailMainThreadGuard
{
    void EnsureMainThread();
}

public sealed class AgasSceneDetailMainThreadGuard : IAgasSceneDetailMainThreadGuard
{
    private readonly int _mainThreadId;

    public AgasSceneDetailMainThreadGuard(int mainThreadId)
    {
        if (mainThreadId <= 0) throw new ArgumentOutOfRangeException(nameof(mainThreadId));
        _mainThreadId = mainThreadId;
    }

    public void EnsureMainThread()
    {
        if (Environment.CurrentManagedThreadId != _mainThreadId)
            throw new InvalidOperationException("Scene Resource load and scalar projection must run on the captured main thread.");
    }
}

public static class AgasSceneDetailErrorCodes
{
    public const string NotFound = AgasMcpErrorCodes.SceneNotFound;
    public const string Invalid = AgasMcpErrorCodes.SceneInvalid;
    public const string SnapshotTooLarge = AgasMcpErrorCodes.SceneSnapshotTooLarge;
    public const string CursorCapacity = AgasMcpErrorCodes.CursorCapacity;
    public const string CursorInvalid = AgasMcpErrorCodes.CursorInvalid;
}

public readonly record struct AgasSceneDetailResult<T>(bool Success, T? Value, string ErrorCode)
{
    public static AgasSceneDetailResult<T> Ok(T value) => new(true, value, string.Empty);
    public static AgasSceneDetailResult<T> Fail(string errorCode) => new(false, default, errorCode);
}
