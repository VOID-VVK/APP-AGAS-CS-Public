using AppAgasCs.Application;
using AppAgasCs.Application.Contracts;
using AppAgasCs.Features.Ai.Generation;
using Godot;
using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class AiGenerationRequestPreparationServiceTests
{
    private static readonly byte[] TransparentPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGNgAAIAAAUAAen63NgAAAAASUVORK5CYII=");

    [Fact]
    public void FullRegionGeneration_TextOnlyModelBecomesTextToImageWithoutReference()
    {
        AiGenerationPreparationResult result = new AiGenerationRequestPreparationService().Prepare(
            Input(AiGenerationAdapterCapabilities.TextToImage),
            Capture(CanvasPatchGenerationMode.FullRegionGeneration));

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.Request);
        Assert.Equal(AiGenerationOperation.TextToImage, result.Request!.Operation);
        Assert.Empty(result.Request.ReferenceImages);
        Assert.Null(result.Request.EditInput);
    }

    [Fact]
    public void RegionReplacement_TextOnlyModelFailsBeforeBuildingAnEditRequest()
    {
        AiGenerationPreparationResult result = new AiGenerationRequestPreparationService().Prepare(
            Input(AiGenerationAdapterCapabilities.TextToImage),
            Capture(CanvasPatchGenerationMode.RegionReplacement));

        Assert.False(result.Success);
        Assert.Null(result.Request);
        Assert.Null(result.PatchCapture);
        Assert.Contains("不支持局部区域编辑", result.Message);
        Assert.Contains("W", result.Message);
    }

    [Fact]
    public void FullRegionGeneration_ReferenceCapableModelKeepsCompleteCanvasReference()
    {
        AiGenerationPreparationResult result = new AiGenerationRequestPreparationService().Prepare(
            Input(AiGenerationAdapterCapabilities.TextToImage | AiGenerationAdapterCapabilities.ReferenceEdit),
            Capture(CanvasPatchGenerationMode.FullRegionGeneration));

        Assert.True(result.Success, result.Message);
        Assert.Equal(AiGenerationOperation.ReferenceEdit, result.Request!.Operation);
        Assert.Single(result.Request.ReferenceImages);
        Assert.Equal(TransparentPng, result.Request.ReferenceImages[0].Bytes);
    }

    private static AiGenerationPreparationInput Input(AiGenerationAdapterCapabilities capabilities)
    {
        var profile = new AiGenerationProviderProfile(
            "connection",
            "Shared connection",
            "axoncat-newapi",
            "newapi-compatible",
            "grok-imagine-image",
            "shared-key",
            "https://relay.example/v1",
            capabilities);
        var context = new AiGenerationCanvasContext(
            "/tmp/project",
            "project",
            "scene",
            "layer",
            HasSelectedImage: false,
            SelectedImageId: null,
            CanEditCurrentLayer: true,
            GridSizePx: 1,
            RegionMetadata: null);
        return new AiGenerationPreparationInput(
            profile,
            context,
            AiGenerationTarget.AddToCurrentLayer,
            "generate image");
    }

    private static CanvasPatchCapture Capture(CanvasPatchGenerationMode mode) => new(
        new CanvasGridRegion(0, 0, 1, 1),
        new CanvasGridRegion(0, 0, 1, 1),
        1,
        new Rect2I(0, 0, 1, 1),
        new Rect2I(0, 0, 1, 1),
        new Rect2I(0, 0, 1, 1),
        TransparentPng,
        TransparentPng,
        TransparentPng,
        "fingerprint",
        targetItem: null,
        mode);
}
