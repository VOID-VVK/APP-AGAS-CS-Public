using AppAgasCs.Features.Ai.Generation;
using Xunit;

namespace AppAgasCs.Tests.Core.Ai;

public sealed class AgasAiGenerationCoordinatorTests
{
    [Fact]
    public void TryAcquire_SharesOneBoundedActiveBudgetAndRejectsImmediatelyWhenFull()
    {
        var coordinator = new AgasAiGenerationCoordinator(maximumConcurrentGenerations: 2);

        Assert.True(coordinator.TryAcquire(out IAgasAiGenerationPermit? first));
        Assert.True(coordinator.TryAcquire(out IAgasAiGenerationPermit? second));
        Assert.False(coordinator.TryAcquire(out IAgasAiGenerationPermit? rejected));
        Assert.Null(rejected);
        Assert.Equal(2, coordinator.ActiveCount);

        first!.Dispose();
        Assert.Equal(1, coordinator.ActiveCount);
        Assert.True(coordinator.TryAcquire(out IAgasAiGenerationPermit? replacement));
        Assert.Equal(2, coordinator.ActiveCount);

        second!.Dispose();
        replacement!.Dispose();
        Assert.Equal(0, coordinator.ActiveCount);
    }

    [Fact]
    public void PermitRelease_IsIdempotentAndPublishesCapacityOnce()
    {
        var coordinator = new AgasAiGenerationCoordinator(maximumConcurrentGenerations: 1);
        int notifications = 0;
        coordinator.CapacityAvailable += () => notifications++;
        Assert.True(coordinator.TryAcquire(out IAgasAiGenerationPermit? permit));

        permit!.Dispose();
        permit.Dispose();

        Assert.Equal(0, coordinator.ActiveCount);
        Assert.Equal(1, notifications);
    }
}
