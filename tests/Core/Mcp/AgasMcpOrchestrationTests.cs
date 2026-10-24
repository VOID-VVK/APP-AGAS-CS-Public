using AppAgasCs.Mcp.Runtime;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp;

public sealed class AgasMcpOrchestrationTests
{
    [Theory]
    [InlineData(AgasMcpCommitPolicy.None)]
    [InlineData(AgasMcpCommitPolicy.ShortAtomicCommit)]
    [InlineData(AgasMcpCommitPolicy.StartedMainThreadCompletion)]
    public async Task PipelineRunsCaptureBackgroundRecheckAndCompletionInOrder(AgasMcpCommitPolicy policy)
    {
        var calls = new List<string>();
        var execution = new AgasMcpCommitExecution(policy);
        var orchestrator = new AgasMcpAsyncToolOrchestrator();

        int result = await orchestrator.ExecuteAsync(
            execution,
            _ =>
            {
                calls.Add("capture");
                return ValueTask.FromResult(2);
            },
            (capture, _) =>
            {
                calls.Add("background");
                return ValueTask.FromResult(capture * 3);
            },
            (capture, prepared, _) =>
            {
                Assert.Equal(2, capture);
                Assert.Equal(6, prepared);
                calls.Add("recheck");
                return ValueTask.CompletedTask;
            },
            (capture, prepared, _) =>
            {
                calls.Add("complete");
                return ValueTask.FromResult(capture + prepared);
            },
            CancellationToken.None);

        Assert.Equal(8, result);
        Assert.Equal(new[] { "capture", "background", "recheck", "complete" }, calls);
        Assert.Equal(AgasMcpCommitExecutionState.Completed, execution.State);
    }

    [Theory]
    [InlineData(AgasMcpCommitPolicy.ShortAtomicCommit)]
    [InlineData(AgasMcpCommitPolicy.StartedMainThreadCompletion)]
    public async Task CancellationBeforeCommitSkipsCompletion(AgasMcpCommitPolicy policy)
    {
        using var cancellation = new CancellationTokenSource();
        var execution = new AgasMcpCommitExecution(policy);
        var orchestrator = new AgasMcpAsyncToolOrchestrator();
        bool completed = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await orchestrator.ExecuteAsync(
                execution,
                _ => ValueTask.FromResult(1),
                (capture, _) => ValueTask.FromResult(capture),
                (_, _, _) =>
                {
                    cancellation.Cancel();
                    return ValueTask.CompletedTask;
                },
                (_, _, _) =>
                {
                    completed = true;
                    return ValueTask.FromResult(1);
                },
                cancellation.Token));

        Assert.False(completed);
        Assert.Equal(AgasMcpCommitExecutionState.Canceled, execution.State);
    }

    [Theory]
    [InlineData(AgasMcpCommitPolicy.ShortAtomicCommit)]
    [InlineData(AgasMcpCommitPolicy.StartedMainThreadCompletion)]
    public async Task CancellationJustAfterCommitStartCannotRewriteResult(AgasMcpCommitPolicy policy)
    {
        using var cancellation = new CancellationTokenSource();
        var execution = new AgasMcpCommitExecution(policy);
        var orchestrator = new AgasMcpAsyncToolOrchestrator();

        string result = await orchestrator.ExecuteAsync(
            execution,
            _ => ValueTask.FromResult("capture"),
            (capture, _) => ValueTask.FromResult(capture + ":prepared"),
            (_, _, _) => ValueTask.CompletedTask,
            (_, prepared, completionToken) =>
            {
                Assert.False(completionToken.CanBeCanceled);
                cancellation.Cancel();
                Assert.True(execution.CancellationObservedAfterStart);
                return ValueTask.FromResult(prepared + ":committed");
            },
            cancellation.Token);

        Assert.Equal("capture:prepared:committed", result);
        Assert.Equal(AgasMcpCommitExecutionState.Completed, execution.State);
        Assert.True(execution.CancellationObservedAfterStart);
    }

    [Fact]
    public async Task StartedMainThreadCompletionWaitsForRealFailureAfterCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var execution = new AgasMcpCommitExecution(AgasMcpCommitPolicy.StartedMainThreadCompletion);
        var orchestrator = new AgasMcpAsyncToolOrchestrator();
        var completionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ValueTask<int> operation = orchestrator.ExecuteAsync<int, int, int>(
            execution,
            _ => ValueTask.FromResult(1),
            (capture, _) => ValueTask.FromResult(capture),
            (_, _, _) => ValueTask.CompletedTask,
            async (_, _, completionToken) =>
            {
                Assert.False(completionToken.CanBeCanceled);
                completionStarted.SetResult();
                await allowFailure.Task;
                throw new ExpectedCompletionException();
            },
            cancellation.Token);

        await completionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.False(operation.IsCompleted);
        allowFailure.SetResult();

        await Assert.ThrowsAsync<ExpectedCompletionException>(async () => await operation);
        Assert.Equal(AgasMcpCommitExecutionState.Completed, execution.State);
        Assert.True(execution.CancellationObservedAfterStart);
    }

    [Fact]
    public async Task NonePolicyCancellationCanWinBeforeResultPublication()
    {
        using var cancellation = new CancellationTokenSource();
        var execution = new AgasMcpCommitExecution(AgasMcpCommitPolicy.None);
        var orchestrator = new AgasMcpAsyncToolOrchestrator();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await orchestrator.ExecuteAsync(
                execution,
                _ => ValueTask.FromResult(1),
                (capture, _) => ValueTask.FromResult(capture),
                (_, _, _) => ValueTask.CompletedTask,
                (_, _, _) =>
                {
                    cancellation.Cancel();
                    return ValueTask.FromResult(1);
                },
                cancellation.Token));

        Assert.Equal(AgasMcpCommitExecutionState.Canceled, execution.State);
    }

    private sealed class ExpectedCompletionException : Exception
    {
    }
}
