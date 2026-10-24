#nullable enable

using System;
using System.Collections.Generic;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Prompting;
using AppAgasCs.Features.Ai.UI;
using AppAgasCs.Features.Ai.Workspace;
using Agas.Features.Canvas;

namespace AGAS.UI.Controllers;

/// <summary>
/// Owns the UI-facing AI workspace bridge orchestration while MainWorkspace
/// supplies only live workspace operations and state accessors.
/// </summary>
internal sealed class MainWorkspaceAiGenerationBridge : IAgasAiGenerationWorkspaceBridge
{
    private readonly Func<CanvasPatchLayerScope, AiGenerationCanvasContext?> _captureCurrentContext;
    private readonly Func<AiGenerationPanel?> _panel;
    private readonly Func<IEnumerable<PromptFragmentResource>?> _activePromptFragments;
    private readonly Func<AiGenerationPreparationInput, AiGenerationPreparationResult> _prepare;
    private readonly Func<
        AiGeneratedImageApplication,
        AiGenerationApplyPersistence,
        AiGenerationApplyContextPolicy,
        AiGeneratedImageApplyResult> _apply;
    private readonly Action<AiGeneratedImageApplication> _discard;
    private readonly Action _refreshExternalJobActivity;
    private readonly Action<string> _reflectMcpPrompt;
    private int _dirtySuppressionDepth;
    private int _applyDepth;

    internal MainWorkspaceAiGenerationBridge(
        Func<CanvasPatchLayerScope, AiGenerationCanvasContext?> captureCurrentContext,
        Func<AiGenerationPanel?> panel,
        Func<IEnumerable<PromptFragmentResource>?> activePromptFragments,
        Func<AiGenerationPreparationInput, AiGenerationPreparationResult> prepare,
        Func<
            AiGeneratedImageApplication,
            AiGenerationApplyPersistence,
            AiGenerationApplyContextPolicy,
            AiGeneratedImageApplyResult> apply,
        Action<AiGeneratedImageApplication> discard,
        Action refreshExternalJobActivity,
        Action<string>? reflectMcpPrompt = null)
    {
        _captureCurrentContext = captureCurrentContext ?? throw new ArgumentNullException(nameof(captureCurrentContext));
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _activePromptFragments = activePromptFragments ?? throw new ArgumentNullException(nameof(activePromptFragments));
        _prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _discard = discard ?? throw new ArgumentNullException(nameof(discard));
        _refreshExternalJobActivity = refreshExternalJobActivity
            ?? throw new ArgumentNullException(nameof(refreshExternalJobActivity));
        _reflectMcpPrompt = reflectMcpPrompt ?? (_ => { });
    }

    internal bool SuppressUiDirty => _dirtySuppressionDepth > 0;

    internal bool SuppressCanvasContextRefresh => _applyDepth > 0;

    public AiGenerationCanvasContext? CaptureCurrentContext(CanvasPatchLayerScope scope) =>
        _captureCurrentContext(scope);

    public AiGenerationProfileResolution ResolveProfile(string? profileId = null) =>
        _panel()?.ResolveProviderProfile(profileId)
        ?? AiGenerationProfileResolution.Fail("AI 生成控制器不可用。");

    public string ResolvePrompt(string? promptOverride = null) =>
        PromptComposer.Compose(
            _activePromptFragments(),
            promptOverride,
            PromptCompositionMode.ReferenceEdit);

    public void ReflectMcpPrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        string text = prompt.Trim();
        // Panel first so the AI dock shows the MCP text even if layer mutation is blocked.
        _panel()?.SetPromptText(text);
        _reflectMcpPrompt(text);
    }

    public AiGenerationPreparationResult Prepare(AiGenerationPreparationInput input) =>
        _prepare(input);

    public AiGeneratedImageApplyResult Apply(
        AiGeneratedImageApplication application,
        AiGenerationApplyPersistence persistence,
        AiGenerationApplyContextPolicy contextPolicy = AiGenerationApplyContextPolicy.FrozenBoundaryOnly)
    {
        _applyDepth++;
        bool suppressDirty = persistence == AiGenerationApplyPersistence.MarkDirtyOnly;
        if (suppressDirty)
        {
            _dirtySuppressionDepth++;
        }
        try
        {
            return _apply(application, persistence, contextPolicy);
        }
        finally
        {
            if (suppressDirty)
            {
                _dirtySuppressionDepth--;
            }
            _applyDepth--;
        }
    }

    public void Discard(AiGeneratedImageApplication application) => _discard(application);

    public void ReportExternalJobStatus(AiGenerationExternalJobStatus status)
    {
        _panel()?.ReportExternalJobStatus(status);
        _refreshExternalJobActivity();
    }

    public void RefreshExternalJobActivity() => _refreshExternalJobActivity();
}
