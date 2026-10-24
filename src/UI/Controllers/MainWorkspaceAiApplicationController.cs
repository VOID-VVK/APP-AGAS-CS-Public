using System;
using System.IO;
using Agas.Features.Canvas;
using AppAgasCs.Application;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Workspace;
using Godot;

namespace AGAS.UI.Controllers;

/// <summary>Owns canvas AI context capture, request preparation, and generated-image application.</summary>
internal sealed class MainWorkspaceAiApplicationController
{
    private const string LayerPatchCaptureTargetId = "__agas_layer_patch__";
    private readonly AiGenerationRequestPreparationService _requestPreparation = new();
    private readonly Func<CanvasWorkspace?> _canvas;
    private readonly Func<string> _projectRoot;
    private readonly Func<AgasProjectResource?> _project;
    private readonly Func<AgasSceneResource?> _currentScene;
    private readonly Func<AgasLayerResource?> _activeLayer;
    private readonly Func<bool> _hasOpenProject;
    private readonly Func<bool> _requiresReadOnly;
    private readonly Func<string> _readOnlyReason;
    private readonly Func<string, bool> _canMutate;
    private readonly Action _refreshAssets;
    private readonly Action _refreshToolbox;
    private readonly Action _updateAiContext;
    private readonly Func<bool> _saveProject;
    private readonly Func<IProjectMutationSession?> _mutationSession;
    private readonly Func<AgasSceneResource, bool> _saveScene;
    private int _applyNesting;

    internal MainWorkspaceAiApplicationController(
        Func<CanvasWorkspace?> canvas,
        Func<string> projectRoot,
        Func<AgasProjectResource?> project,
        Func<AgasSceneResource?> currentScene,
        Func<AgasLayerResource?> activeLayer,
        Func<bool> hasOpenProject,
        Func<bool> requiresReadOnly,
        Func<string> readOnlyReason,
        Func<string, bool> canMutate,
        Action refreshAssets,
        Action refreshToolbox,
        Action updateAiContext,
        Func<bool> saveProject,
        Func<IProjectMutationSession?> mutationSession,
        Func<AgasSceneResource, bool> saveScene)
    {
        _canvas = canvas;
        _projectRoot = projectRoot;
        _project = project;
        _currentScene = currentScene;
        _activeLayer = activeLayer;
        _hasOpenProject = hasOpenProject;
        _requiresReadOnly = requiresReadOnly;
        _readOnlyReason = readOnlyReason;
        _canMutate = canMutate;
        _refreshAssets = refreshAssets;
        _refreshToolbox = refreshToolbox;
        _updateAiContext = updateAiContext;
        _saveProject = saveProject;
        _mutationSession = mutationSession;
        _saveScene = saveScene;
    }

    internal AiGenerationCanvasContext? CreateContext(CanvasPatchLayerScope scope)
    {
        CanvasWorkspace? canvas = _canvas();
        if (canvas == null || !_hasOpenProject()) return null;
        CanvasGridRegion? selection = canvas.SelectionRegion;
        CanvasGridRegion? replacement = canvas.ReplacementRegion;
        AiGenerationRegionMetadata? regionMetadata = selection.HasValue || replacement.HasValue
            ? new AiGenerationRegionMetadata(ToAiRegion(selection), ToAiRegion(replacement))
            : null;
        AgasSceneResource? scene = _currentScene();
        AgasLayerResource? layer = _activeLayer();
        string? selectedImageId = canvas.GetSelectedImage()?.Id;
        return new AiGenerationCanvasContext(
            _projectRoot(),
            _project()?.ProjectId ?? string.Empty,
            scene?.Id ?? string.Empty,
            layer?.Id ?? string.Empty,
            !string.IsNullOrWhiteSpace(selectedImageId),
            selectedImageId,
            canvas.CanEditCurrentLayer(),
            canvas.GetGridSizePx(),
            regionMetadata,
            scope,
            scene?.Name ?? string.Empty,
            layer?.Name ?? string.Empty);
    }

    internal AiGenerationPreparationResult Prepare(AiGenerationPreparationInput input)
    {
        if (!_canMutate("准备 AI 请求"))
            return AiGenerationPreparationResult.Fail($"项目为只读状态（{_readOnlyReason()}）。");
        CanvasWorkspace? canvas = _canvas();
        if (canvas == null || _project() == null || _currentScene() == null)
            return AiGenerationPreparationResult.Fail("当前项目已关闭，无法准备区域生成请求。");
        if (!TryResolveEffectiveRegion(canvas, out CanvasGridRegion effective, out string error))
            return AiGenerationPreparationResult.Fail(error);
        try
        {
            string? targetImageId = input.Target == AiGenerationTarget.ReplaceSelected
                ? input.Context.SelectedImageId
                : LayerPatchCaptureTargetId;
            CanvasPatchCapture capture = canvas.CaptureCurrentPatch(
                targetImageId, input.Context.LayerScope, input.Context.LayerId, effective);
            return _requestPreparation.Prepare(input, capture);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Failed to prepare AI canvas patch: {exception.GetType().Name}");
            return AiGenerationPreparationResult.Fail("画布区域或参考合成图无法准备，请检查 Q/W 区域及图片资源。");
        }
    }

    internal AiGeneratedImageApplyResult Apply(
        AiGeneratedImageApplication application,
        AiGenerationApplyPersistence persistence,
        AiGenerationApplyContextPolicy contextPolicy)
    {
        if (_requiresReadOnly())
            return AiGeneratedImageApplyResult.Fail($"项目为只读状态（{_readOnlyReason()}），不能应用或保存生成结果。");
        CanvasWorkspace? canvas = _canvas();
        AgasProjectResource? project = _project();
        IProjectMutationSession? session = _mutationSession();
        if (canvas == null || project == null || session == null || application.PatchCapture == null)
            return AiGeneratedImageApplyResult.Fail(
                "当前项目已关闭，无法应用生成结果。",
                AiGeneratedImageApplyFailureReason.ContextStale);

        if (!SameProjectRoot(application.ExpectedProjectRoot, _projectRoot())
            || !string.Equals(application.ExpectedProjectId, project.ProjectId, StringComparison.Ordinal))
            return AiGeneratedImageApplyResult.Fail(
                "请求期间项目已切换，未应用生成结果。",
                AiGeneratedImageApplyFailureReason.ContextStale);

        AgasSceneResource? originScene = session.LoadScene(application.ExpectedSceneId);
        if (originScene == null)
            return AiGeneratedImageApplyResult.Fail(
                "原场景已不存在，未应用生成结果。",
                AiGeneratedImageApplyFailureReason.ContextStale);

        AgasSceneResource? currentScene = _currentScene();
        bool originIsCurrentView = currentScene != null
            && string.Equals(application.ExpectedSceneId, currentScene.Id, StringComparison.Ordinal)
            && string.Equals(application.ExpectedLayerId, _activeLayer()?.Id ?? string.Empty, StringComparison.Ordinal);

        if (contextPolicy == AiGenerationApplyContextPolicy.RejectStaleContent
            && originIsCurrentView
            && !IsCaptureCurrent(canvas, application))
            return AiGeneratedImageApplyResult.Fail(
                "提交后画布内容已变化，未应用生成结果。",
                AiGeneratedImageApplyFailureReason.ContextStale);

        if (_applyNesting > 0)
            return AiGeneratedImageApplyResult.Ok("生成结果正在写入，跳过重复回写。");

        _applyNesting++;
        try
        {
            return ApplyCore(application, persistence, originScene, originIsCurrentView, canvas, session);
        }
        finally
        {
            _applyNesting--;
        }
    }

    private AiGeneratedImageApplyResult ApplyCore(
        AiGeneratedImageApplication application,
        AiGenerationApplyPersistence persistence,
        AgasSceneResource originScene,
        bool originIsCurrentView,
        CanvasWorkspace canvas,
        IProjectMutationSession session)
    {
        if (!CanvasPatchAssetStore.TryReadGenerated(
                application.ExpectedProjectRoot,
                application.Result.RelativePath,
                out byte[] providerBytes,
                out string readError))
            return AiGeneratedImageApplyResult.Fail(readError);

        byte[] appliedPng;
        try
        {
            appliedPng = CanvasCompositeRenderer.ExtractGeneratedReferenceCanvas(
                application.PatchCapture, providerBytes, ResultMapping(application.Result));
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Failed to compose complete AI Q application: {exception.GetType().Name}");
            return AiGeneratedImageApplyResult.Fail("完整 Q 生成结果无法合成，未修改画布。");
        }

        if (!CanvasPatchAssetStore.TrySavePng(
                _projectRoot(), appliedPng,
                application.ExpectedSceneId, application.ExpectedSceneName,
                application.ExpectedLayerId, application.ExpectedLayerName,
                out string finalRelativePath, out string saveError))
            return AiGeneratedImageApplyResult.Fail(saveError);

        Vector2I position = application.PatchCapture.ReferenceWorldPixels.Position;
        if (originIsCurrentView)
        {
            if (!canvas.TryAddImageFromProjectRelativePath(
                    finalRelativePath, new Vector2(position.X, position.Y), selectNewImage: false))
            {
                CanvasPatchAssetStore.DeleteGeneratedCandidate(_projectRoot(), finalRelativePath);
                return AiGeneratedImageApplyResult.Fail("目标图层不可编辑，完整 Q 生成结果无法写回。");
            }

            _refreshAssets();
            _refreshToolbox();
            if (persistence == AiGenerationApplyPersistence.MarkDirtyOnly)
                return AiGeneratedImageApplyResult.Ok(
                    "已把完整 Q 生成结果覆盖写入请求时的图层；项目尚未保存。", finalRelativePath);
            if (!_saveProject())
                return AiGeneratedImageApplyResult.AppliedButUnsaved(
                    "图片已应用，但自动保存失败，请按 Ctrl/Cmd+S 重试。");
            return AiGeneratedImageApplyResult.Ok(
                "已把完整 Q 生成结果写入请求时的图层并自动保存；可使用撤销恢复。", finalRelativePath);
        }

        ProjectMutationResult mutation = new ProjectMutationService().MutateImage(
            session,
            new ImageMutationCommand(
                ImageMutationKind.Add,
                application.ExpectedSceneId,
                application.ExpectedLayerId,
                ImagePath: finalRelativePath,
                X: position.X,
                Y: position.Y,
                ScaleX: 1.0,
                ScaleY: 1.0,
                RotationDegrees: 0.0,
                Opacity: 1.0));
        if (!mutation.Success)
        {
            CanvasPatchAssetStore.DeleteGeneratedCandidate(_projectRoot(), finalRelativePath);
            return AiGeneratedImageApplyResult.Fail(
                string.IsNullOrWhiteSpace(mutation.Message)
                    ? "目标图层不可编辑，完整 Q 生成结果无法写回原场景。"
                    : mutation.Message);
        }

        if (!_saveScene(originScene))
            return AiGeneratedImageApplyResult.AppliedButUnsaved(
                "图片已写入原场景，但自动保存失败，请切回该场景后按 Ctrl/Cmd+S 重试。");
        return AiGeneratedImageApplyResult.Ok(
            "已把完整 Q 生成结果写入请求时的场景图层并保存。切回该场景即可看到。", finalRelativePath);
    }

    internal void Discard(AiGeneratedImageApplication application)
    {
        if (!_canMutate("删除生成结果")) return;
        CanvasPatchAssetStore.DeleteGeneratedCandidate(
            application.ExpectedProjectRoot, application.Result.RelativePath);
        if (_project() != null && _currentScene() != null
            && SameProjectRoot(application.ExpectedProjectRoot, _projectRoot()))
            _refreshAssets();
    }

    private static bool TryResolveEffectiveRegion(
        CanvasWorkspace canvas,
        out CanvasGridRegion effective,
        out string error)
    {
        effective = default;
        if (canvas.SelectionRegion is not CanvasGridRegion q)
        {
            error = "请先框选 Q 参考区域。";
            return false;
        }
        effective = canvas.ReplacementRegion ?? q;
        if (!q.Contains(effective))
        {
            error = "W 必须位于 Q 区域内。";
            return false;
        }
        int grid = Math.Max(1, canvas.GetGridSizePx());
        try
        {
            if (checked(q.Width * grid) > 8192 || checked(q.Height * grid) > 8192)
            {
                error = "Q 参考区域最大支持 8192×8192 像素。";
                return false;
            }
        }
        catch (OverflowException)
        {
            error = "Q 参考区域尺寸过大。";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private static bool IsCaptureCurrent(CanvasWorkspace canvas, AiGeneratedImageApplication application)
    {
        if (application.PatchCapture == null
            || canvas.GetGridSizePx() != application.PatchCapture.GridSizePx) return false;
        try
        {
            string targetImageId = application.Target == AiGenerationTarget.ReplaceSelected
                ? application.ExpectedSelectedImageId ?? string.Empty
                : LayerPatchCaptureTargetId;
            CanvasPatchCapture current = canvas.CapturePatch(
                targetImageId, application.LayerScope, application.ExpectedLayerId,
                application.PatchCapture.ReferenceRegion, application.PatchCapture.ReplacementRegion,
                application.PatchCapture.GridSizePx, application.PatchCapture.GenerationMode);
            return string.Equals(current.ContentFingerprint,
                application.PatchCapture.ContentFingerprint, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static AiGenerationGridRegion? ToAiRegion(CanvasGridRegion? region) => region.HasValue
        ? new AiGenerationGridRegion(region.Value.Column, region.Value.Row, region.Value.Width, region.Value.Height)
        : null;

    private static CanvasProviderResultMapping ResultMapping(AiGenerationResult result) =>
        result.EffectiveOperation is AiGenerationEffectiveOperation.MaskedEdit
            or AiGenerationEffectiveOperation.GuidedRegionEdit
            ? CanvasProviderResultMapping.ReferenceCanvas
            : CanvasProviderResultMapping.ReplacementPatch;

    private static bool SameProjectRoot(string expected, string current)
    {
        try
        {
            string expectedFull = Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string currentFull = Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(expectedFull, currentFull, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
