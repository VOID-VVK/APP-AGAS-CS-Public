#nullable enable

using System;
using System.IO;
using AppAgasCs.Features.Ai.Generation;

namespace Agas.Features.Canvas;

/// <summary>
/// Stores immutable, locally composed patch results beside the provider result
/// in the request's fixed scene/layer directory. The canvas undo stack only changes resource
/// references; applied files remain available so redo is always valid.
/// </summary>
internal static class CanvasPatchAssetStore
{
    public static bool TryReadGenerated(string projectRoot, string relativePath, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = string.Empty;
        if (!TryResolveGeneratedPath(projectRoot, relativePath, requireFile: true, out string absolutePath))
        {
            error = "生成候选路径无效。";
            return false;
        }
        try
        {
            bytes = File.ReadAllBytes(absolutePath);
            if (bytes.Length == 0)
            {
                error = "生成候选为空。";
                return false;
            }
            return true;
        }
        catch
        {
            error = "生成候选无法读取。";
            return false;
        }
    }

    public static bool TrySavePng(string projectRoot, byte[] pngBytes, out string relativePath, out string error)
        => TrySavePng(
            projectRoot,
            pngBytes,
            new AiGenerationStorageScope(string.Empty, string.Empty, string.Empty, string.Empty),
            out relativePath,
            out error);

    public static bool TrySavePng(
        string projectRoot,
        byte[] pngBytes,
        string sceneId,
        string sceneName,
        string layerId,
        string layerName,
        out string relativePath,
        out string error) =>
        TrySavePng(
            projectRoot,
            pngBytes,
            new AiGenerationStorageScope(sceneId, sceneName, layerId, layerName),
            out relativePath,
            out error);

    public static bool TrySavePng(
        string projectRoot,
        byte[] pngBytes,
        AiGenerationStorageScope storageScope,
        out string relativePath,
        out string error)
    {
        relativePath = string.Empty;
        error = string.Empty;
        if (!HasPngSignature(pngBytes))
        {
            error = "区域合成结果不是有效 PNG。";
            return false;
        }

        string root;
        try
        {
            root = Path.GetFullPath(projectRoot);
        }
        catch
        {
            error = "生成目录无法创建。";
            return false;
        }

        try
        {
            using GeneratedImageFileReservation reservation =
                AgasGeneratedImagePathPolicy.ReserveNextResultFile(root, storageScope, ".png", asynchronous: false);
            reservation.Stream.Write(pngBytes);
            reservation.Stream.Flush(flushToDisk: true);
            reservation.Commit();
            relativePath = AgasGeneratedImagePathPolicy.ToProjectRelativePath(root, reservation.FinalPath);
            return true;
        }
        catch
        {
            error = "区域合成结果无法保存。";
            return false;
        }
    }

    public static void DeleteGeneratedCandidate(string projectRoot, string relativePath)
    {
        if (TryResolveGeneratedPath(projectRoot, relativePath, requireFile: true, out string absolutePath))
        {
            TryDeleteAbsolute(absolutePath);
        }
    }

    private static bool TryResolveGeneratedPath(string projectRoot, string relativePath, bool requireFile, out string absolutePath)
        => AgasGeneratedImagePathPolicy.TryResolveGeneratedFile(projectRoot, relativePath, requireFile, out absolutePath);

    private static bool HasPngSignature(byte[] bytes) =>
        bytes is { Length: >= 8 }
        && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71
        && bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10;

    private static void TryDeleteAbsolute(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup is best-effort and must not hide the primary result.
        }
    }
}
