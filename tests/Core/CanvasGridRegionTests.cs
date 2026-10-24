using Agas.Features.Canvas;
using Godot;
using Xunit;

namespace AppAgasCs.Tests.Core;

public sealed class CanvasGridRegionTests
{
    [Fact]
    public void FromWorldDrag_NormalizesForwardAndReverseDragsToSameInclusiveCells()
    {
        CanvasGridRegion forward = CanvasGridRegion.FromWorldDrag(new Vector2(52, 100), new Vector2(180, 25), 48);
        CanvasGridRegion reverse = CanvasGridRegion.FromWorldDrag(new Vector2(180, 25), new Vector2(52, 100), 48);

        Assert.Equal(new CanvasGridRegion(1, 0, 3, 3), forward);
        Assert.Equal(forward, reverse);
        Assert.Equal(new Rect2(48, 0, 144, 144), forward.ToWorldRect(48));
    }

    [Fact]
    public void FromWorldDrag_UsesFloorCellsForNegativeWorldCoordinates()
    {
        CanvasGridRegion region = CanvasGridRegion.FromWorldDrag(new Vector2(-1, -49), new Vector2(-96, 47), 48);

        Assert.Equal(new CanvasGridRegion(-2, -2, 2, 3), region);
        Assert.Equal(new Rect2(-96, -96, 96, 144), region.ToWorldRect(48));
    }

    [Fact]
    public void Contains_RejectsReplacementOutsideSelection()
    {
        // Mirrors the multi-box map fill bug: first Q/W on the left, second Q to the right.
        var firstQ = new CanvasGridRegion(0, -8, 20, 25);
        var hole = new CanvasGridRegion(5, -4, 4, 4);
        var secondQ = new CanvasGridRegion(36, -8, 42, 25);

        Assert.True(firstQ.Contains(hole));
        Assert.False(secondQ.Contains(hole));
    }
}
