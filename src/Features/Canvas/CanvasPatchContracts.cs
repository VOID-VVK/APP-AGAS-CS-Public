#nullable enable

using Godot;

namespace Agas.Features.Canvas;

public enum CanvasProviderResultMapping
{
    Auto,
    ReferenceCanvas,
    ReplacementPatch
}

public enum CanvasAiGenerationOverlayTone
{
    Generating,
    Pending,
    Failure,
    Canceled
}

public sealed record CanvasAiGenerationOverlay(
    int RunId,
    Rect2 WorldRect,
    string Text,
    CanvasAiGenerationOverlayTone Tone)
{
    public CanvasAiGenerationOverlay(
        Rect2 worldRect,
        string text,
        CanvasAiGenerationOverlayTone tone)
        : this(0, worldRect, text, tone)
    {
    }
}
