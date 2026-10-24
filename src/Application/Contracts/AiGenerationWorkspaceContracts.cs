#nullable enable

using AppAgasCs.Features.Ai.Generation;
using Godot;

namespace AppAgasCs.Application.Contracts;

public enum AiGenerationTarget
{
    ReplaceSelected,
    AddToCurrentLayer
}

public enum AiGenerationOverlayPhase
{
    Generating,
    Pending,
    Failure,
    Canceled
}

public enum AiGenerationApplyPersistence
{
    SaveImmediately,
    MarkDirtyOnly
}

public enum AiGenerationApplyContextPolicy
{
    FrozenBoundaryOnly,
    RejectStaleContent
}

public enum AiGeneratedImageApplyFailureReason
{
    None,
    ContextStale
}

public sealed record AiGenerationOverlayState(
    int RunId,
    AiGenerationCanvasContext Context,
    Rect2 WorldRect,
    AiGenerationOverlayPhase Phase,
    int ElapsedSeconds,
    string Text);

public sealed record AiGenerationCanvasContext(
    string ProjectRoot,
    string ProjectId,
    string SceneId,
    string LayerId,
    bool HasSelectedImage,
    string? SelectedImageId,
    bool CanEditCurrentLayer,
    int GridSizePx,
    AiGenerationRegionMetadata? RegionMetadata,
    CanvasPatchLayerScope LayerScope = CanvasPatchLayerScope.AllVisibleLayers,
    string SceneName = "",
    string LayerName = "");

public sealed record AiGeneratedImageApplication(
    AiGenerationResult Result,
    AiGenerationTarget Target,
    string ExpectedProjectRoot,
    string ExpectedProjectId,
    string ExpectedSceneId,
    string ExpectedLayerId,
    string? ExpectedSelectedImageId,
    CanvasPatchCapture? PatchCapture = null,
    AiGenerationOperation RequestedOperation = AiGenerationOperation.TextToImage,
    CanvasPatchLayerScope LayerScope = CanvasPatchLayerScope.AllVisibleLayers,
    string ExpectedSceneName = "",
    string ExpectedLayerName = "");

public sealed record AiGenerationPreparationInput(
    AiGenerationProviderProfile Profile,
    AiGenerationCanvasContext Context,
    AiGenerationTarget Target,
    string Prompt,
    AiGenerationQuality? Quality = null);

public sealed record AiGenerationPreparationResult(
    bool Success,
    string Message,
    AiGenerationRequest? Request,
    CanvasPatchCapture? PatchCapture)
{
    public static AiGenerationPreparationResult Ok(AiGenerationRequest request, CanvasPatchCapture capture) =>
        new(true, string.Empty, request, capture);

    public static AiGenerationPreparationResult Fail(string message) =>
        new(false, message, null, null);
}

/// <summary>Application-owned port for mapping a captured patch into a provider-neutral request.</summary>
public interface IAiGenerationRequestPreparationService
{
    AiGenerationPreparationResult Prepare(AiGenerationPreparationInput input, CanvasPatchCapture capture);
}

public sealed record AiGeneratedImageApplyResult(
    bool Success,
    bool Applied,
    string Message,
    string? AppliedRelativePath = null,
    AiGeneratedImageApplyFailureReason FailureReason = AiGeneratedImageApplyFailureReason.None)
{
    public static AiGeneratedImageApplyResult Ok(string message, string? appliedRelativePath = null) =>
        new(true, true, message, appliedRelativePath);
    public static AiGeneratedImageApplyResult AppliedButUnsaved(string message) => new(false, true, message);
    public static AiGeneratedImageApplyResult Fail(
        string message,
        AiGeneratedImageApplyFailureReason failureReason = AiGeneratedImageApplyFailureReason.None) =>
        new(false, false, message, FailureReason: failureReason);
}

public sealed record AiGenerationProfileResolution(
    bool Success,
    string Message,
    AiGenerationProviderProfile? Profile)
{
    public static AiGenerationProfileResolution Ok(AiGenerationProviderProfile profile) =>
        new(true, string.Empty, profile);

    public static AiGenerationProfileResolution Fail(string message) =>
        new(false, message, null);
}

public sealed record AiGenerationProfileOption(string Id, string DisplayName);

public enum AiGenerationExternalJobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Canceled,
    Applied
}

/// <summary>
/// Lightweight status update for generation work submitted outside the AI panel.
/// It deliberately contains no provider credentials, image payloads, or project paths.
/// </summary>
public sealed record AiGenerationExternalJobStatus(
    string JobId,
    AiGenerationExternalJobState State,
    string Message = "");
