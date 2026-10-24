using System.Collections.Generic;

namespace AppAgasCs.Application.Contracts;

public readonly record struct CanvasVisibleLayerMergeResult(
    bool Success,
    string Message,
    string RelativePath,
    string ImageId,
    double X,
    double Y,
    IReadOnlyList<string> SourceLayerIds)
{
    public static CanvasVisibleLayerMergeResult Fail(string message) =>
        new(false, message, string.Empty, string.Empty, 0, 0, System.Array.Empty<string>());

    public static CanvasVisibleLayerMergeResult Ok(
        string relativePath,
        string imageId,
        double x,
        double y,
        IReadOnlyList<string> sourceLayerIds) =>
        new(true, "已把其他可见图层合并到当前图层。", relativePath, imageId, x, y, sourceLayerIds);
}

/// <summary>
/// Merges every visible layer except the current one into the current layer.
/// Hidden layers are skipped. The raster is produced by AGAS canvas capture.
/// </summary>
public interface ICanvasLayerMergeBridge
{
    string SceneId { get; }

    bool TryMergeVisibleLayersIntoCurrent(string targetLayerId, out CanvasVisibleLayerMergeResult result);
}
