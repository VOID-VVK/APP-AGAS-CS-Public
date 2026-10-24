#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AppAgasCs.Application.Contracts;

/// <summary>A normalized, inclusive grid-cell region shared across application boundaries.</summary>
public readonly record struct CanvasGridRegion(int Column, int Row, int Width, int Height)
{
    public int EndColumnExclusive => Column + Width;

    public int EndRowExclusive => Row + Height;

    public bool Contains(CanvasGridRegion other) =>
        other.Column >= Column
        && other.Row >= Row
        && other.EndColumnExclusive <= EndColumnExclusive
        && other.EndRowExclusive <= EndRowExclusive;

    public static CanvasGridRegion FromWorldDrag(Vector2 startWorld, Vector2 endWorld, int gridSizePx)
    {
        int grid = Math.Max(1, gridSizePx);
        int startColumn = Mathf.FloorToInt(startWorld.X / grid);
        int startRow = Mathf.FloorToInt(startWorld.Y / grid);
        int endColumn = Mathf.FloorToInt(endWorld.X / grid);
        int endRow = Mathf.FloorToInt(endWorld.Y / grid);

        int minColumn = Math.Min(startColumn, endColumn);
        int minRow = Math.Min(startRow, endRow);
        int maxColumn = Math.Max(startColumn, endColumn);
        int maxRow = Math.Max(startRow, endRow);

        return new CanvasGridRegion(
            minColumn,
            minRow,
            maxColumn - minColumn + 1,
            maxRow - minRow + 1);
    }

    public Rect2 ToWorldRect(int gridSizePx)
    {
        int grid = Math.Max(1, gridSizePx);
        return new Rect2(
            new Vector2(Column * grid, Row * grid),
            new Vector2(Width * grid, Height * grid));
    }
}

public enum CanvasPatchLayerScope
{
    CurrentLayer,
    AllVisibleLayers,
    VisibleLayersExceptCurrent
}

public enum CanvasPatchGenerationMode
{
    RegionReplacement,
    FullRegionGeneration
}

/// <summary>An immutable raster snapshot of one canvas image placement.</summary>
public sealed class CanvasCompositeItem
{
    private readonly byte[] _sourcePng;

    public CanvasCompositeItem(
        string id,
        int layerOrder,
        int imageOrder,
        bool visible,
        Vector2 position,
        Vector2 scale,
        float rotationDegrees,
        float opacity,
        byte[] sourcePng)
    {
        if (sourcePng == null || sourcePng.Length == 0)
        {
            throw new ArgumentException("Source PNG must not be empty.", nameof(sourcePng));
        }

        Id = id ?? string.Empty;
        LayerOrder = layerOrder;
        ImageOrder = imageOrder;
        Visible = visible;
        Position = position;
        Scale = scale;
        RotationDegrees = rotationDegrees;
        Opacity = Math.Clamp(opacity, 0.0f, 1.0f);
        _sourcePng = (byte[])sourcePng.Clone();
    }

    public string Id { get; }
    public int LayerOrder { get; }
    public int ImageOrder { get; }
    public bool Visible { get; }
    public Vector2 Position { get; }
    public Vector2 Scale { get; }
    public float RotationDegrees { get; }
    public float Opacity { get; }
    public byte[] SourcePng => (byte[])_sourcePng.Clone();

    internal ReadOnlySpan<byte> SourcePngSpan => _sourcePng;

    internal CanvasCompositeItem Clone() => new(
        Id,
        LayerOrder,
        ImageOrder,
        Visible,
        Position,
        Scale,
        RotationDegrees,
        Opacity,
        _sourcePng);
}

public sealed class CanvasPatchCaptureRequest
{
    private readonly ReadOnlyCollection<CanvasCompositeItem> _items;

    public CanvasPatchCaptureRequest(
        CanvasGridRegion referenceRegion,
        CanvasGridRegion replacementRegion,
        int gridSizePx,
        IReadOnlyList<CanvasCompositeItem> items,
        string? targetImageId = null,
        CanvasPatchGenerationMode generationMode = CanvasPatchGenerationMode.RegionReplacement)
    {
        if (gridSizePx < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(gridSizePx));
        }

        if (referenceRegion.Width < 1 || referenceRegion.Height < 1)
        {
            throw new ArgumentException("Reference region must have a positive size.", nameof(referenceRegion));
        }

        if (replacementRegion.Width < 1 || replacementRegion.Height < 1)
        {
            throw new ArgumentException("Replacement region must have a positive size.", nameof(replacementRegion));
        }

        ReferenceRegion = referenceRegion;
        ReplacementRegion = replacementRegion;
        GridSizePx = gridSizePx;
        TargetImageId = targetImageId ?? string.Empty;
        GenerationMode = generationMode;
        _items = new ReadOnlyCollection<CanvasCompositeItem>(
            (items ?? throw new ArgumentNullException(nameof(items))).Select(item => item.Clone()).ToList());
    }

    public CanvasGridRegion ReferenceRegion { get; }
    public CanvasGridRegion ReplacementRegion { get; }
    public int GridSizePx { get; }
    public string TargetImageId { get; }
    public CanvasPatchGenerationMode GenerationMode { get; }
    public IReadOnlyList<CanvasCompositeItem> Items => _items;
}

/// <summary>
/// Immutable provider input. SourceCanvasPng and MaskPng always share the
/// reference-canvas dimensions and EditRegionPixels is fully contained by both.
/// </summary>
public sealed class CanvasPatchCapture
{
    private readonly byte[] _sourceCanvasPng;
    private readonly byte[] _maskPng;
    private readonly byte[] _replacementPatchPng;

    internal CanvasPatchCapture(
        CanvasGridRegion referenceRegion,
        CanvasGridRegion replacementRegion,
        int gridSizePx,
        Rect2I referenceWorldPixels,
        Rect2I replacementWorldPixels,
        Rect2I editRegionPixels,
        byte[] sourceCanvasPng,
        byte[] maskPng,
        byte[] replacementPatchPng,
        string contentFingerprint,
        CanvasCompositeItem? targetItem,
        CanvasPatchGenerationMode generationMode)
    {
        ReferenceRegion = referenceRegion;
        ReplacementRegion = replacementRegion;
        GridSizePx = gridSizePx;
        ReferenceWorldPixels = referenceWorldPixels;
        ReplacementWorldPixels = replacementWorldPixels;
        EditRegionPixels = editRegionPixels;
        _sourceCanvasPng = (byte[])sourceCanvasPng.Clone();
        _maskPng = (byte[])maskPng.Clone();
        _replacementPatchPng = (byte[])replacementPatchPng.Clone();
        ContentFingerprint = contentFingerprint;
        TargetItem = targetItem?.Clone();
        GenerationMode = generationMode;
    }

    public CanvasGridRegion ReferenceRegion { get; }
    public CanvasGridRegion ReplacementRegion { get; }
    public int GridSizePx { get; }
    public Rect2I ReferenceWorldPixels { get; }
    public Rect2I ReplacementWorldPixels { get; }
    public Rect2I EditRegionPixels { get; }
    public byte[] SourceCanvasPng => (byte[])_sourceCanvasPng.Clone();
    public byte[] MaskPng => (byte[])_maskPng.Clone();
    public byte[] ReplacementPatchPng => (byte[])_replacementPatchPng.Clone();
    public string ContentFingerprint { get; }
    public CanvasCompositeItem? TargetItem { get; }
    public CanvasPatchGenerationMode GenerationMode { get; }

    internal ReadOnlySpan<byte> SourceCanvasPngSpan => _sourceCanvasPng;
}
