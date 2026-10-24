using AppAgasCs.Application.Contracts;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp;

public sealed class AgasMcpSelectionTests
{
    [Fact]
    public void RegionValidation_AcceptsNegativeCoordinatesAndPreservesGridValues()
    {
        bool valid = AgasMcpSelectionValidation.TryCreateRegion(
            new AgasMcpGridRegionDto(-2, 3, 4, 5),
            out CanvasSelectionRegion region);

        Assert.True(valid);
        Assert.Equal(new CanvasSelectionRegion(-2, 3, 4, 5), region);
    }

    [Theory]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 0, 1, 0)]
    [InlineData(1, 0, int.MaxValue, 1)]
    [InlineData(0, 1, 1, int.MaxValue)]
    public void RegionValidation_RejectsNonPositiveOrOverflowingRegions(int column, int row, int width, int height)
    {
        Assert.False(AgasMcpSelectionValidation.TryCreateRegion(
            new AgasMcpGridRegionDto(column, row, width, height),
            out _));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void LayerValidation_MatchesCurrentCanvasHitTesting(bool visible, bool locked, bool expected)
    {
        Assert.Equal(expected, AgasMcpSelectionValidation.IsLayerEditable(visible, locked));
    }

    [Theory]
    [InlineData("scene-a", "scene-a", "scene-a", true)]
    [InlineData("scene-b", "scene-a", "scene-a", false)]
    [InlineData("scene-a", "scene-b", "scene-a", false)]
    [InlineData("scene-a", null, "scene-a", false)]
    public void ActiveSceneValidation_NeverImplicitlySwitches(
        string activeSceneId,
        string? currentSceneId,
        string requestedSceneId,
        bool expected)
    {
        Assert.Equal(expected, AgasMcpSelectionValidation.IsActiveScene(activeSceneId, currentSceneId, requestedSceneId));
    }
}
