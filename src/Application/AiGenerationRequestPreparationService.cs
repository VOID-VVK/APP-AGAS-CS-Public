#nullable enable

using System;
using System.Collections.Generic;
using AppAgasCs.Application.Contracts;
using AppAgasCs.Features.Ai.Generation;
using Godot;

namespace AppAgasCs.Application;

/// <summary>
/// Maps an immutable canvas capture into a provider-neutral AI request.
/// Canvas/UI state validation and capture stay at the workspace boundary.
/// </summary>
public sealed class AiGenerationRequestPreparationService : IAiGenerationRequestPreparationService
{
    public AiGenerationPreparationResult Prepare(
        AiGenerationPreparationInput input,
        CanvasPatchCapture capture)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(capture);

        if (input.Quality.HasValue && !AiGenerationQualityPolicy.Supports(input.Profile.ModelName))
        {
            return AiGenerationPreparationResult.Fail("当前模型不支持质量档位；请切换到 Grok Imagine Image 2.0。");
        }
        AiGenerationQuality? quality = AiGenerationQualityPolicy.Resolve(input.Profile.ModelName, input.Quality);

        AiGenerationAdapterCapabilities? capabilities = input.Profile.DeclaredCapabilities;
        AiGenerationRequest request;
        if (capture.GenerationMode == CanvasPatchGenerationMode.FullRegionGeneration)
        {
            Rect2I replacement = capture.ReplacementWorldPixels;
            bool supportsReferenceEdit = !capabilities.HasValue
                || capabilities.Value.HasFlag(AiGenerationAdapterCapabilities.ReferenceEdit);
            bool supportsTextToImage = !capabilities.HasValue
                || capabilities.Value.HasFlag(AiGenerationAdapterCapabilities.TextToImage);
            if (!supportsReferenceEdit && !supportsTextToImage)
            {
                return AiGenerationPreparationResult.Fail("当前模型不支持完整 Q 区域生图。");
            }
            IReadOnlyList<AiGenerationReferenceImage>? references = null;
            AiGenerationOperation operation = AiGenerationOperation.TextToImage;
            if (supportsReferenceEdit)
            {
                references = new[]
                {
                    AiGenerationReferenceImage.Create(capture.ReplacementPatchPng, "image/png"),
                };
                operation = AiGenerationOperation.ReferenceEdit;
            }
            request = new AiGenerationRequest(
                input.Context.ProjectRoot,
                input.Prompt.Trim(),
                replacement.Size.X,
                replacement.Size.Y,
                referenceImages: references,
                regionMetadata: input.Context.RegionMetadata,
                operation: operation,
                sceneId: input.Context.SceneId,
                sceneName: input.Context.SceneName,
                layerId: input.Context.LayerId,
                layerName: input.Context.LayerName,
                quality: quality);
        }
        else
        {
            if (capabilities.HasValue
                && !capabilities.Value.HasFlag(AiGenerationAdapterCapabilities.RegionEdit))
            {
                return AiGenerationPreparationResult.Fail(
                    "当前模型不支持局部区域编辑（W）。请清除 W 后生成完整 Q 区域，或切换到支持局部编辑的模型。");
            }
            AiGenerationReferenceImage source = AiGenerationReferenceImage.Create(
                capture.SourceCanvasPng,
                "image/png");
            AiGenerationReferenceImage mask = AiGenerationReferenceImage.Create(
                capture.MaskPng,
                "image/png");
            Rect2I edit = capture.EditRegionPixels;
            var editInput = new AiGenerationEditInput(
                source,
                mask,
                new AiGenerationPixelRegion(edit.Position.X, edit.Position.Y, edit.Size.X, edit.Size.Y));
            Rect2I reference = capture.ReferenceWorldPixels;
            request = new AiGenerationRequest(
                input.Context.ProjectRoot,
                input.Prompt.Trim(),
                reference.Size.X,
                reference.Size.Y,
                regionMetadata: input.Context.RegionMetadata,
                operation: AiGenerationOperation.RegionEdit,
                editInput: editInput,
                sceneId: input.Context.SceneId,
                sceneName: input.Context.SceneName,
                layerId: input.Context.LayerId,
                layerName: input.Context.LayerName,
                quality: quality);
        }

        return AiGenerationPreparationResult.Ok(request, capture);
    }
}
