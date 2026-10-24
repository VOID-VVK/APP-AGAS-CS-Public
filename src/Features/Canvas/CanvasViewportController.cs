#nullable enable

using System;
using System.Collections.Generic;
using AppAgasCs.Core.Resources;
using Godot;

namespace Agas.Features.Canvas;

/// <summary>Owns canvas camera state and coordinate transforms.</summary>
internal sealed class CanvasViewportController
{
    public const float DefaultZoom = 1.0f;
    public const float ZoomStepFactor = 1.10f;
    public const float DefaultFitMarginPx = 48.0f;

    public Vector2 CameraWorld { get; private set; }
    public float Zoom { get; private set; } = DefaultZoom;

    public void Load(AgasSceneResource? scene, float minZoom, float maxZoom)
    {
        SceneViewStateResource? viewState = scene?.ViewState;
        CameraWorld = new Vector2(
            (float)(viewState?.CameraX ?? 0.0),
            (float)(viewState?.CameraY ?? 0.0));
        Zoom = Mathf.Clamp((float)(viewState?.Zoom ?? DefaultZoom), minZoom, maxZoom);
    }

    public bool SetView(Vector2 cameraWorld, float zoom, float minZoom, float maxZoom)
    {
        if (!IsFinite(cameraWorld) || !float.IsFinite(zoom))
        {
            return false;
        }

        CameraWorld = cameraWorld;
        Zoom = Mathf.Clamp(zoom, minZoom, maxZoom);
        return true;
    }

    public void ResetZoom(float minZoom, float maxZoom) =>
        Zoom = Mathf.Clamp(DefaultZoom, minZoom, maxZoom);

    public bool ZoomAt(Vector2 screenPosition, float factor, Vector2 viewportSize, float minZoom, float maxZoom)
    {
        if (!IsFinite(screenPosition) || !float.IsFinite(factor) || factor <= 0.0f)
        {
            return false;
        }

        Vector2 before = ScreenToWorld(screenPosition, viewportSize);
        Zoom = Mathf.Clamp(Zoom * factor, minZoom, maxZoom);
        Vector2 after = ScreenToWorld(screenPosition, viewportSize);
        CameraWorld += before - after;
        return true;
    }

    public void PanByScreenDelta(Vector2 screenDelta)
    {
        CameraWorld -= screenDelta / Zoom;
    }

    public bool FitVisibleContent(
        IEnumerable<Vector2[]> worldCornerSets,
        Vector2 viewportSize,
        float marginPx,
        float minZoom,
        float maxZoom)
    {
        bool hasBounds = false;
        Vector2 minimum = Vector2.Zero;
        Vector2 maximum = Vector2.Zero;
        foreach (Vector2[] corners in worldCornerSets)
        {
            foreach (Vector2 corner in corners)
            {
                if (!IsFinite(corner)) continue;
                if (!hasBounds)
                {
                    minimum = corner;
                    maximum = corner;
                    hasBounds = true;
                    continue;
                }

                minimum = new Vector2(Math.Min(minimum.X, corner.X), Math.Min(minimum.Y, corner.Y));
                maximum = new Vector2(Math.Max(maximum.X, corner.X), Math.Max(maximum.Y, corner.Y));
            }
        }

        if (!hasBounds)
        {
            CameraWorld = Vector2.Zero;
            Zoom = Mathf.Clamp(DefaultZoom, minZoom, maxZoom);
            return false;
        }

        float margin = float.IsFinite(marginPx) ? Math.Max(0.0f, marginPx) : DefaultFitMarginPx;
        Vector2 available = new(
            Math.Max(1.0f, viewportSize.X - margin * 2.0f),
            Math.Max(1.0f, viewportSize.Y - margin * 2.0f));
        Vector2 boundsSize = maximum - minimum;
        float fitZoom = Math.Min(
            available.X / Math.Max(1.0f, boundsSize.X),
            available.Y / Math.Max(1.0f, boundsSize.Y));
        CameraWorld = (minimum + maximum) * 0.5f;
        Zoom = Mathf.Clamp(Math.Min(DefaultZoom, fitZoom), minZoom, maxZoom);
        return true;
    }

    public Vector2 ScreenToWorld(Vector2 screenPosition, Vector2 viewportSize) =>
        CameraWorld + (screenPosition - viewportSize * 0.5f) / Zoom;

    public Vector2 WorldToScreen(Vector2 worldPosition, Vector2 viewportSize) =>
        (worldPosition - CameraWorld) * Zoom + viewportSize * 0.5f;

    public Rect2 ToScreenRect(Rect2 worldRect, Vector2 viewportSize)
    {
        Vector2 first = WorldToScreen(worldRect.Position, viewportSize);
        Vector2 second = WorldToScreen(worldRect.End, viewportSize);
        Vector2 position = new(Math.Min(first.X, second.X), Math.Min(first.Y, second.Y));
        Vector2 end = new(Math.Max(first.X, second.X), Math.Max(first.Y, second.Y));
        return new Rect2(position, end - position);
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);
}
