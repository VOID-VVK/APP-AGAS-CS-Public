#nullable enable

using AppAgasCs.Application.Contracts;

namespace AppAgasCs.Features.Ai.Workspace;

/// <summary>
/// Feature adapter port. All cross-feature DTOs are owned by Application.Contracts,
/// so the workspace feature depends inward and does not depend on Canvas.
/// </summary>
public interface IAgasAiGenerationWorkspaceBridge
{
    AiGenerationCanvasContext? CaptureCurrentContext(CanvasPatchLayerScope scope);
    AiGenerationProfileResolution ResolveProfile(string? profileId = null);
    string ResolvePrompt(string? promptOverride = null);
    AiGenerationPreparationResult Prepare(AiGenerationPreparationInput input);
    AiGeneratedImageApplyResult Apply(
        AiGeneratedImageApplication application,
        AiGenerationApplyPersistence persistence,
        AiGenerationApplyContextPolicy contextPolicy = AiGenerationApplyContextPolicy.FrozenBoundaryOnly);
    void Discard(AiGeneratedImageApplication application);

    /// <summary>
    /// Reflect an MCP-supplied prompt into the live workspace UI (AI panel + layer prompt dock)
    /// so operators can see the same text that was submitted for generation.
    /// Default no-op for headless / stub bridges.
    /// </summary>
    void ReflectMcpPrompt(string prompt)
    {
    }

    void ReportExternalJobStatus(AiGenerationExternalJobStatus status)
    {
    }

    void RefreshExternalJobActivity()
    {
    }
}
