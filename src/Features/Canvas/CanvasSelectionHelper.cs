#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using AppAgasCs.Core.Resources;
using Godot;

namespace Agas.Features.Canvas;

/// <summary>Owns selected-image state and selection geometry queries.</summary>
internal sealed class CanvasSelectionHelper
{
    public ImageInstanceResource? SelectedImage { get; private set; }

    public void Select(ImageInstanceResource? image) => SelectedImage = image;

    public bool Clear()
    {
        if (SelectedImage == null) return false;
        SelectedImage = null;
        return true;
    }

    public ImageInstanceResource? HitTestCurrentEditableLayer(
        AgasLayerResource? layer,
        Vector2 worldPosition,
        Func<ImageInstanceResource, Vector2?> getPixelSize)
    {
        if (layer is not { Visible: true, Locked: false }) return null;
        return layer.ImagesSnapshot
            .OrderByDescending(image => image.Order)
            .FirstOrDefault(image => HitTestImage(image, worldPosition, getPixelSize(image)));
    }

    public static AgasLayerResource? FindContainingLayer(
        IEnumerable<AgasLayerResource> layers,
        ImageInstanceResource? image)
    {
        if (image == null) return null;
        return layers.FirstOrDefault(layer => layer.ImagesSnapshot.Any(item => ReferenceEquals(item, image)));
    }

    public static Vector2[] GetWorldCorners(
        ImageInstanceResource image,
        Vector2? pixelSize,
        Vector2? positionOverride = null)
    {
        Vector2 position = positionOverride ?? new Vector2((float)image.X, (float)image.Y);
        Vector2 size = pixelSize ?? new Vector2(96, 64);
        Vector2 scale = new((float)image.ScaleX, (float)image.ScaleY);
        float rotation = Mathf.DegToRad((float)image.RotationDegrees);
        Vector2 right = new Vector2(size.X * scale.X, 0).Rotated(rotation);
        Vector2 down = new Vector2(0, size.Y * scale.Y).Rotated(rotation);
        return new[] { position, position + right, position + right + down, position + down };
    }

    private static bool HitTestImage(ImageInstanceResource image, Vector2 worldPosition, Vector2? pixelSize)
    {
        Vector2 position = new((float)image.X, (float)image.Y);
        Vector2 scale = new((float)image.ScaleX, (float)image.ScaleY);
        if (Mathf.IsZeroApprox(scale.X) || Mathf.IsZeroApprox(scale.Y)) return false;

        float rotation = Mathf.DegToRad((float)image.RotationDegrees);
        Vector2 local = (worldPosition - position).Rotated(-rotation);
        local = new Vector2(local.X / scale.X, local.Y / scale.Y);
        Vector2 size = pixelSize ?? new Vector2(96, 64);
        return local.X >= 0 && local.Y >= 0 && local.X <= size.X && local.Y <= size.Y;
    }
}
