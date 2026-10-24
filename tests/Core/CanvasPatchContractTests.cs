using Agas.Features.Canvas;
using Godot;
using Xunit;

namespace AppAgasCs.Tests.Core;

public sealed class CanvasPatchContractTests
{
    [Fact]
    public void CompositeItem_ClonesSourceBytesAndClampsOpacity()
    {
        byte[] source = { 1, 2, 3, 4 };
        var item = new CanvasCompositeItem("image", 1, 2, true, new Vector2(-3, 4), new Vector2(-2, 0.5f), 90, 2, source);
        source[0] = 99;
        byte[] firstRead = item.SourcePng;
        firstRead[1] = 88;

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, item.SourcePng);
        Assert.Equal(1, item.Opacity);
        Assert.Equal(new Vector2(-2, 0.5f), item.Scale);
    }

    [Fact]
    public void CaptureRequest_ClonesItemsAndValidatesRegions()
    {
        var item = new CanvasCompositeItem("image", 0, 0, true, Vector2.Zero, Vector2.One, 0, 1, new byte[] { 1 });
        var items = new System.Collections.Generic.List<CanvasCompositeItem> { item };
        var request = new CanvasPatchCaptureRequest(
            new CanvasGridRegion(-1, -2, 3, 4),
            new CanvasGridRegion(0, 0, 1, 1),
            48,
            items,
            "image");
        items.Clear();

        Assert.Single(request.Items);
        Assert.NotSame(item, request.Items[0]);
        Assert.Equal("image", request.TargetImageId);
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new CanvasPatchCaptureRequest(
            request.ReferenceRegion,
            request.ReplacementRegion,
            0,
            request.Items));
    }
}
