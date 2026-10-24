#nullable enable

using System;
using System.Linq;
using AppAgasCs.Core.Resources;
using Godot;

namespace Agas.Features.Canvas;

/// <summary>Draw-only collaborator for <see cref="CanvasWorkspace"/>.</summary>
internal sealed class CanvasWorkspaceRenderer
{
    private const float InactiveLayerBrightness = 0.40f;
    private const float SelectionHandlePaddingPx = 2.0f;

    public void Draw(CanvasWorkspace canvas)
    {
        canvas.DrawRect(new Rect2(Vector2.Zero, canvas.Size), canvas.BackgroundColor, filled: true);
        if (canvas.IsGridVisible()) DrawGrid(canvas);

        AgasLayerResource? currentLayer = canvas.GetCurrentLayer();
        foreach (AgasLayerResource layer in canvas.GetOrderedLayers())
        {
            if (!layer.Visible) continue;
            bool isCurrentLayer = currentLayer != null && ReferenceEquals(layer, currentLayer);
            foreach (ImageInstanceResource image in CanvasWorkspace.GetOrderedImages(layer))
            {
                DrawImage(canvas, image, layer.Locked, isCurrentLayer);
            }
        }

        DrawRegion(canvas, canvas.SelectionRegion, canvas.SelectionRegionColor);
        DrawRegion(canvas, canvas.ReplacementRegion, canvas.ReplacementRegionColor);
        if (canvas.SelectedImage is ImageInstanceResource selected && canvas.IsImageRenderable(selected))
        {
            DrawSelection(canvas, selected);
        }
        DrawAiOverlays(canvas);
    }

    private static void DrawGrid(CanvasWorkspace canvas)
    {
        int grid = canvas.GetGridSizePx();
        float screenGrid = grid * canvas.Zoom;
        if (screenGrid < 4.0f)
        {
            int multiplier = Mathf.CeilToInt(4.0f / Math.Max(screenGrid, 0.001f));
            grid *= Math.Max(1, multiplier);
        }

        Vector2 topLeft = canvas.ScreenToWorld(Vector2.Zero);
        Vector2 bottomRight = canvas.ScreenToWorld(canvas.Size);
        int startX = Mathf.FloorToInt(topLeft.X / grid) * grid;
        int endX = Mathf.CeilToInt(bottomRight.X / grid) * grid;
        int startY = Mathf.FloorToInt(topLeft.Y / grid) * grid;
        int endY = Mathf.CeilToInt(bottomRight.Y / grid) * grid;

        for (int x = startX; x <= endX; x += grid)
        {
            bool major = x % (grid * 5) == 0;
            canvas.DrawLine(
                canvas.WorldToScreen(new Vector2(x, topLeft.Y)),
                canvas.WorldToScreen(new Vector2(x, bottomRight.Y)),
                major ? canvas.GridMajorColor : canvas.GridMinorColor,
                major ? 1.5f : 1.0f);
        }
        for (int y = startY; y <= endY; y += grid)
        {
            bool major = y % (grid * 5) == 0;
            canvas.DrawLine(
                canvas.WorldToScreen(new Vector2(topLeft.X, y)),
                canvas.WorldToScreen(new Vector2(bottomRight.X, y)),
                major ? canvas.GridMajorColor : canvas.GridMinorColor,
                major ? 1.5f : 1.0f);
        }
    }

    private static void DrawRegion(CanvasWorkspace canvas, CanvasGridRegion? region, Color color)
    {
        if (!region.HasValue) return;
        Rect2 worldRect = region.Value.ToWorldRect(canvas.GetGridSizePx());
        Rect2 screenRect = new(canvas.WorldToScreen(worldRect.Position), worldRect.Size * canvas.Zoom);
        canvas.DrawRect(screenRect, new Color(color.R, color.G, color.B, 0.10f), filled: true);
        canvas.DrawRect(screenRect, color, filled: false, width: 2.0f);
    }

    private static void DrawAiOverlays(CanvasWorkspace canvas)
    {
        foreach (CanvasAiGenerationOverlay overlay in canvas.AiGenerationOverlays)
        {
            Rect2 rect = canvas.ToScreenRect(overlay.WorldRect);
            if (rect.Size.X <= 0.0f || rect.Size.Y <= 0.0f) continue;
            Color accent = overlay.Tone switch
            {
                CanvasAiGenerationOverlayTone.Pending => new Color(0.30f, 1.0f, 0.58f, 1.0f),
                CanvasAiGenerationOverlayTone.Failure => new Color(1.0f, 0.28f, 0.28f, 1.0f),
                CanvasAiGenerationOverlayTone.Canceled => new Color(1.0f, 0.72f, 0.20f, 1.0f),
                _ => new Color(0.18f, 0.78f, 1.0f, 1.0f),
            };
            canvas.DrawRect(rect, new Color(accent.R, accent.G, accent.B, 0.24f), filled: true);
            canvas.DrawRect(rect, accent, filled: false, width: 3.0f);

            Font font = ThemeDB.FallbackFont;
            const int fontSize = 16;
            Vector2 labelSize = font.GetStringSize(overlay.Text, HorizontalAlignment.Left, -1, fontSize) + new Vector2(20, 12);
            Rect2 labelRect = new(rect.GetCenter() - labelSize * 0.5f, labelSize);
            canvas.DrawRect(labelRect, new Color(0.02f, 0.025f, 0.035f, 0.92f), filled: true);
            canvas.DrawRect(labelRect, accent, filled: false, width: 2.0f);
            Vector2 baseline = labelRect.Position + new Vector2(10, 6 + font.GetAscent(fontSize));
            canvas.DrawString(font, baseline, overlay.Text, HorizontalAlignment.Left, -1, fontSize, Colors.White);
        }
    }

    private static void DrawImage(CanvasWorkspace canvas, ImageInstanceResource image, bool layerLocked, bool isCurrentLayer)
    {
        Texture2D? texture = canvas.GetImageTexture(image);
        if (texture == null)
        {
            Vector2 position = canvas.WorldToScreen(canvas.GetImagePosition(image));
            Rect2 rect = new(position, new Vector2(96 * canvas.Zoom, 64 * canvas.Zoom));
            float dim = isCurrentLayer ? 1.0f : InactiveLayerBrightness;
            canvas.DrawRect(rect, new Color(0.35f * dim, 0.12f * dim, 0.12f * dim, 0.85f), filled: true);
            canvas.DrawRect(rect, new Color(dim, 0.35f * dim, 0.35f * dim, 0.9f), filled: false, width: 1.0f);
            canvas.DrawString(ThemeDB.FallbackFont, rect.Position + new Vector2(6, 20), "图片缺失", HorizontalAlignment.Left, -1, 12, Colors.White);
            return;
        }

        float opacity = Mathf.Clamp((float)image.Opacity, 0.0f, 1.0f);
        float alpha = opacity * (layerLocked ? 0.72f : 1.0f);
        float brightness = isCurrentLayer ? 1.0f : InactiveLayerBrightness;
        canvas.DrawSetTransform(
            canvas.WorldToScreen(canvas.GetImagePosition(image)),
            Mathf.DegToRad((float)image.RotationDegrees),
            canvas.GetImageScale(image) * canvas.Zoom);
        canvas.DrawTexture(texture, Vector2.Zero, new Color(brightness, brightness, brightness, alpha));
        canvas.DrawSetTransform(Vector2.Zero, 0.0f, Vector2.One);
    }

    private static void DrawSelection(CanvasWorkspace canvas, ImageInstanceResource image)
    {
        Vector2[] corners = canvas.GetImageWorldCorners(image).Select(canvas.WorldToScreen).ToArray();
        for (int index = 0; index < corners.Length; index++)
        {
            canvas.DrawLine(corners[index], corners[(index + 1) % corners.Length], canvas.SelectionColor, 2.0f);
        }
        foreach (Vector2 corner in corners)
        {
            canvas.DrawRect(new Rect2(corner - Vector2.One * (3 + SelectionHandlePaddingPx), Vector2.One * 6), canvas.SelectionColor, filled: true);
        }
    }
}
