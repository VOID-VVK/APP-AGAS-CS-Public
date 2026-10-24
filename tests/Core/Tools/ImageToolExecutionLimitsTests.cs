using AppAgasCs.Features.Tools.Execution;
using Xunit;

namespace AppAgasCs.Tests.Core.Tools;

public sealed class ImageToolExecutionLimitsTests
{
    [Fact]
    public void LimitsCannotExceedProductionCeilings()
    {
        var limits = new ImageToolExecutionLimits(int.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue);
        Assert.Equal(2048, limits.MaxDimension);
        Assert.Equal(4_194_304, limits.MaxPixels);
        Assert.Equal(32L * 1024 * 1024, limits.MaxInputBytes);
        Assert.Equal(256L * 1024 * 1024, limits.MaxLiveBytes);
        Assert.Equal(4_194_304, limits.ValidateImage(2048, 2048));
        Assert.Throws<ImageToolRunException>(() => limits.ValidateImage(2049, 1));
    }

    [Fact]
    public void MemoryBudgetIsCheckedBeforeProcessing()
    {
        var limits = new ImageToolExecutionLimits(maxLiveBytes: 1024);
        limits.ValidateWorkingSet(limits.ValidateImage(32, 1));
        Assert.Throws<ImageToolRunException>(() => limits.ValidateWorkingSet(33));
    }
}
