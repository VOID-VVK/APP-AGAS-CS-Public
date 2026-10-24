using AppAgasCs.Mcp.Runtime;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp;

public sealed class AgasMcpCommitPolicyTests
{
    [Theory]
    [InlineData(AgasMcpCommitPolicy.ShortAtomicCommit)]
    [InlineData(AgasMcpCommitPolicy.StartedMainThreadCompletion)]
    public void CancellationBeforeCommitWinsAndBlocksCommit(AgasMcpCommitPolicy policy)
    {
        var execution = new AgasMcpCommitExecution(policy);

        Assert.True(execution.TryCancel());
        Assert.False(TryBegin(execution, policy));
        Assert.Equal(AgasMcpCommitExecutionState.Canceled, execution.State);
        Assert.False(execution.CancellationObservedAfterStart);
    }

    [Theory]
    [InlineData(AgasMcpCommitPolicy.ShortAtomicCommit, AgasMcpCommitExecutionState.ShortCommitting)]
    [InlineData(AgasMcpCommitPolicy.StartedMainThreadCompletion, AgasMcpCommitExecutionState.MainThreadCompleting)]
    public void CommitBeforeCancellationRetainsOutcome(
        AgasMcpCommitPolicy policy,
        AgasMcpCommitExecutionState committingState)
    {
        var execution = new AgasMcpCommitExecution(policy);

        Assert.True(TryBegin(execution, policy));
        Assert.Equal(committingState, execution.State);
        Assert.False(execution.TryCancel());
        Assert.True(execution.CancellationObservedAfterStart);
        execution.CompleteCommit();

        Assert.Equal(AgasMcpCommitExecutionState.Completed, execution.State);
    }

    [Theory]
    [InlineData(AgasMcpCommitPolicy.ShortAtomicCommit)]
    [InlineData(AgasMcpCommitPolicy.StartedMainThreadCompletion)]
    public void CancellationAfterCommitRecordsObservationWithoutChangingResult(AgasMcpCommitPolicy policy)
    {
        var execution = new AgasMcpCommitExecution(policy);
        Assert.True(TryBegin(execution, policy));
        execution.CompleteCommit();

        Assert.False(execution.TryCancel());

        Assert.Equal(AgasMcpCommitExecutionState.Completed, execution.State);
        Assert.True(execution.CancellationObservedAfterStart);
    }

    [Fact]
    public void NonePolicyCompletesWithoutCommitLease()
    {
        var execution = new AgasMcpCommitExecution(AgasMcpCommitPolicy.None);

        Assert.True(execution.TryCompleteWithoutCommit());
        Assert.False(execution.TryCancel());
        Assert.Equal(AgasMcpCommitExecutionState.Completed, execution.State);
        Assert.False(execution.CancellationObservedAfterStart);
    }

    [Fact]
    public void WrongCommitPrimitiveIsRejected()
    {
        var shortExecution = new AgasMcpCommitExecution(AgasMcpCommitPolicy.ShortAtomicCommit);
        var mainExecution = new AgasMcpCommitExecution(AgasMcpCommitPolicy.StartedMainThreadCompletion);
        var noneExecution = new AgasMcpCommitExecution(AgasMcpCommitPolicy.None);

        Assert.Throws<InvalidOperationException>(() => shortExecution.TryBeginMainThreadCompletion());
        Assert.Throws<InvalidOperationException>(() => mainExecution.TryBeginShortAtomicCommit());
        Assert.Throws<InvalidOperationException>(() => noneExecution.TryBeginShortAtomicCommit());
        Assert.Throws<InvalidOperationException>(noneExecution.CompleteCommit);
    }

    [Theory]
    [InlineData(AgasMcpCommitPolicy.ShortAtomicCommit)]
    [InlineData(AgasMcpCommitPolicy.StartedMainThreadCompletion)]
    public async Task CancellationAndCommitUseOneAtomicWinner(AgasMcpCommitPolicy policy)
    {
        for (int iteration = 0; iteration < 500; iteration++)
        {
            var execution = new AgasMcpCommitExecution(policy);
            using var start = new ManualResetEventSlim(false);

            Task<bool> cancellation = Task.Run(() =>
            {
                start.Wait();
                return execution.TryCancel();
            });
            Task<bool> commit = Task.Run(() =>
            {
                start.Wait();
                return TryBegin(execution, policy);
            });

            start.Set();
            bool[] results = await Task.WhenAll(cancellation, commit);

            Assert.NotEqual(results[0], results[1]);
            if (results[1])
            {
                execution.CompleteCommit();
                Assert.Equal(AgasMcpCommitExecutionState.Completed, execution.State);
            }
            else
            {
                Assert.Equal(AgasMcpCommitExecutionState.Canceled, execution.State);
            }
        }
    }

    private static bool TryBegin(AgasMcpCommitExecution execution, AgasMcpCommitPolicy policy) =>
        policy == AgasMcpCommitPolicy.ShortAtomicCommit
            ? execution.TryBeginShortAtomicCommit()
            : execution.TryBeginMainThreadCompletion();
}
