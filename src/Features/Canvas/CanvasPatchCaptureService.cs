#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AppAgasCs.Application;
using AppAgasCs.Core.Resources;
using Godot;

namespace Agas.Features.Canvas;

/// <summary>
/// Builds immutable provider input from the typed canvas resource graph.
/// It deliberately owns image decoding so patch capture is independent from editor rendering.
/// </summary>
internal sealed class CanvasPatchCaptureService
{
    private readonly ProjectImagePathResolver _pathResolver = new();

    public CanvasPatchCapture Capture(
        AgasSceneResource scene,
        string projectRoot,
        string? selectedImageId,
        string? targetImageId,
        CanvasPatchLayerScope layerScope,
        string? currentLayerId,
        CanvasGridRegion referenceRegion,
        CanvasGridRegion replacementRegion,
        int gridSizePx,
        CanvasPatchGenerationMode generationMode)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (gridSizePx < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(gridSizePx));
        }
        if (!referenceRegion.Contains(replacementRegion))
        {
            throw new InvalidOperationException("W 必须位于 Q 区域内。");
        }

        string resolvedTargetId = string.IsNullOrWhiteSpace(targetImageId)
            ? selectedImageId ?? string.Empty
            : targetImageId.Trim();
        List<CanvasCompositeItem> items = new();
        foreach (AgasLayerResource layer in scene.Layers.OrderBy(layer => layer.Order))
        {
            if (!layer.Visible)
            {
                continue;
            }
            if (layerScope == CanvasPatchLayerScope.CurrentLayer
                && !string.Equals(layer.Id, currentLayerId, StringComparison.Ordinal))
            {
                continue;
            }
            if (layerScope == CanvasPatchLayerScope.VisibleLayersExceptCurrent
                && string.Equals(layer.Id, currentLayerId, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (ImageInstanceResource image in layer.Images.OrderBy(image => image.Order))
            {
                float opacity = Mathf.Clamp((float)image.Opacity, 0.0f, 1.0f);
                if (opacity <= 0.0f && !string.Equals(image.Id, resolvedTargetId, StringComparison.Ordinal))
                {
                    continue;
                }

                items.Add(new CanvasCompositeItem(
                    image.Id,
                    layer.Order,
                    image.Order,
                    layer.Visible,
                    new Vector2((float)image.X, (float)image.Y),
                    new Vector2((float)image.ScaleX, (float)image.ScaleY),
                    (float)image.RotationDegrees,
                    opacity,
                    LoadPng(projectRoot, image)));
            }
        }

        return CanvasCompositeRenderer.Capture(new CanvasPatchCaptureRequest(
            referenceRegion,
            replacementRegion,
            gridSizePx,
            items,
            resolvedTargetId,
            generationMode));
    }

    private byte[] LoadPng(string projectRoot, ImageInstanceResource imageInstance)
    {
        string relativePath = imageInstance.ImagePath;
        if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new InvalidOperationException($"Canvas image '{imageInstance.Id}' has no project image path.");
        }

        ProjectImagePathResult validation = _pathResolver.Resolve(projectRoot, relativePath, mustExist: true);
        if (!validation.Success)
        {
            throw new InvalidOperationException($"Canvas image path is unsafe: {relativePath}");
        }

        using Image image = Image.LoadFromFile(validation.AbsolutePath);
        if (image == null || image.IsEmpty())
        {
            throw new InvalidDataException($"Canvas image could not be decoded: {relativePath}");
        }

        byte[] png = image.SavePngToBuffer();
        if (png.Length == 0)
        {
            throw new InvalidDataException($"Canvas image could not be encoded as PNG: {relativePath}");
        }
        return png;
    }
}
