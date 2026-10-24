using AppAgasCs.Mcp;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp;

public sealed class AgasMcpSerialToolExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_RunsQueuedOperationsOneAtATimeInOrder()
    {
        await using var executor = new AgasMcpSerialToolExecutor();
        executor.AllowNewWork();
        List<int> events = new();
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> first = executor.ExecuteAsync("session-a", async token =>
        {
            events.Add(1);
            await releaseFirst.Task.WaitAsync(token);
            events.Add(2);
            return 10;
        });
        Task<int> second = executor.ExecuteAsync("session-a", token =>
        {
            events.Add(3);
            return Task.FromResult(20);
        });

        await WaitUntilAsync(() => events.SequenceEqual(new[] { 1 }));
        Assert.False(second.IsCompleted);

        releaseFirst.SetResult();

        Assert.Equal(10, await first);
        Assert.Equal(20, await second);
        Assert.Equal(new[] { 1, 2, 3 }, events);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsWhenQueueCapacityIsExhausted()
    {
        await using var executor = new AgasMcpSerialToolExecutor();
        executor.AllowNewWork();
        TaskCompletionSource enteredFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<Task<int>> accepted = new();

        accepted.Add(executor.ExecuteAsync("capacity-session", async token =>
        {
            enteredFirst.SetResult();
            await release.Task.WaitAsync(token);
            return 1;
        }));
        await enteredFirst.Task.WaitAsync(TimeSpan.FromSeconds(5));

        for (int i = 0; i < AgasMcpSerialToolExecutor.QueueCapacity; i++)
        {
            accepted.Add(executor.ExecuteAsync("capacity-session", async token =>
            {
                await release.Task.WaitAsync(token);
                return 1;
            }));
        }

        AgasMcpToolExecutionRejectedException exception = await Assert.ThrowsAsync<AgasMcpToolExecutionRejectedException>(() =>
            executor.ExecuteAsync("capacity-session", token => Task.FromResult(2)));
        Assert.Equal(AgasMcpToolExecutionRejectReason.Busy, exception.Reason);

        release.SetResult();
        await Task.WhenAll(accepted);
    }

    [Fact]
    public async Task ExecuteAsync_AppliesPerSessionRateLimitBeforeExecution()
    {
        await using var executor = new AgasMcpSerialToolExecutor();
        executor.AllowNewWork();

        for (int i = 0; i < AgasMcpSerialToolExecutor.PerSessionCallsPerMinute; i++)
        {
            int result = await executor.ExecuteAsync("rate-session", token => Task.FromResult(1));
            Assert.Equal(1, result);
        }

        AgasMcpToolExecutionRejectedException exception = await Assert.ThrowsAsync<AgasMcpToolExecutionRejectedException>(() =>
            executor.ExecuteAsync("rate-session", token => Task.FromResult(2)));
        Assert.Equal(AgasMcpToolExecutionRejectReason.RateLimited, exception.Reason);
    }

    [Fact]
    public async Task ExecuteAsync_RateLimitBucketsArePerSession()
    {
        await using var executor = new AgasMcpSerialToolExecutor();
        executor.AllowNewWork();

        for (int i = 0; i < AgasMcpSerialToolExecutor.PerSessionCallsPerMinute; i++)
        {
            Assert.Equal(1, await executor.ExecuteAsync("rate-session-a", token => Task.FromResult(1)));
        }

        AgasMcpToolExecutionRejectedException exception = await Assert.ThrowsAsync<AgasMcpToolExecutionRejectedException>(() =>
            executor.ExecuteAsync("rate-session-a", token => Task.FromResult(2)));
        Assert.Equal(AgasMcpToolExecutionRejectReason.RateLimited, exception.Reason);

        Assert.Equal(3, await executor.ExecuteAsync("rate-session-b", token => Task.FromResult(3)));
    }

    [Fact]
    public async Task Stop_CancelsQueuedWorkAndRejectsNewWork()
    {
        await using var executor = new AgasMcpSerialToolExecutor();
        executor.AllowNewWork();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> running = executor.ExecuteAsync("session-stop", async token =>
        {
            entered.SetResult();
            await Task.Delay(TimeSpan.FromMinutes(1), token);
            return 1;
        });

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        executor.Stop();

        AgasMcpToolExecutionRejectedException runningException = await Assert.ThrowsAsync<AgasMcpToolExecutionRejectedException>(() => running);
        Assert.Equal(AgasMcpToolExecutionRejectReason.Stopped, runningException.Reason);

        AgasMcpToolExecutionRejectedException exception = await Assert.ThrowsAsync<AgasMcpToolExecutionRejectedException>(() =>
            executor.ExecuteAsync("session-stop", token => Task.FromResult(2)));
        Assert.Equal(AgasMcpToolExecutionRejectReason.Stopped, exception.Reason);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsByDefaultUntilAllowNewWork()
    {
        await using var executor = new AgasMcpSerialToolExecutor();

        AgasMcpToolExecutionRejectedException beforeAllow = await Assert.ThrowsAsync<AgasMcpToolExecutionRejectedException>(() =>
            executor.ExecuteAsync("default-reject-session", token => Task.FromResult(1)));
        Assert.Equal(AgasMcpToolExecutionRejectReason.Stopped, beforeAllow.Reason);

        executor.AllowNewWork();

        Assert.Equal(2, await executor.ExecuteAsync("default-reject-session", token => Task.FromResult(2)));
    }

    [Fact]
    public async Task RejectNewWorkAndCancelActiveAndPendingAsync_CancelsWorkWaitsForQuiescenceAndCanReEnable()
    {
        await using var executor = new AgasMcpSerialToolExecutor();
        executor.AllowNewWork();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource observedCancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queuedStarted = false;

        Task<int> active = executor.ExecuteAsync("reject-session", async token =>
        {
            entered.SetResult();
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), token);
            }
            catch (OperationCanceledException)
            {
                observedCancellation.SetResult();
                throw;
            }

            return 1;
        });
        Task<int> pending = executor.ExecuteAsync("reject-session", token =>
        {
            queuedStarted = true;
            return Task.FromResult(2);
        });

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await executor.RejectNewWorkAndCancelActiveAndPendingAsync(TestTimeout());

        Assert.True(observedCancellation.Task.IsCompleted);
        Assert.False(queuedStarted);
        AgasMcpToolExecutionRejectedException activeException =
            await Assert.ThrowsAsync<AgasMcpToolExecutionRejectedException>(() => active);
        AgasMcpToolExecutionRejectedException pendingException =
            await Assert.ThrowsAsync<AgasMcpToolExecutionRejectedException>(() => pending);
        Assert.Equal(AgasMcpToolExecutionRejectReason.Stopped, activeException.Reason);
        Assert.Equal(AgasMcpToolExecutionRejectReason.Stopped, pendingException.Reason);

        AgasMcpToolExecutionRejectedException rejectedAfterQuiescence =
            await Assert.ThrowsAsync<AgasMcpToolExecutionRejectedException>(() =>
                executor.ExecuteAsync("reject-session", token => Task.FromResult(3)));
        Assert.Equal(AgasMcpToolExecutionRejectReason.Stopped, rejectedAfterQuiescence.Reason);

        executor.AllowNewWork();

        Assert.Equal(4, await executor.ExecuteAsync("reject-session", token => Task.FromResult(4)));
    }

    [Fact]
    public async Task RejectNewWorkAndCancelActiveAndPendingAsync_ClearsAllRateWindowsBeforeReEnable()
    {
        await using var executor = new AgasMcpSerialToolExecutor();
        executor.AllowNewWork();

        for (int i = 0; i < AgasMcpSerialToolExecutor.PerSessionCallsPerMinute; i++)
        {
            Assert.Equal(1, await executor.ExecuteAsync("rate-reset-session", token => Task.FromResult(1)));
        }

        AgasMcpToolExecutionRejectedException rateLimited =
            await Assert.ThrowsAsync<AgasMcpToolExecutionRejectedException>(() =>
                executor.ExecuteAsync("rate-reset-session", token => Task.FromResult(2)));
        Assert.Equal(AgasMcpToolExecutionRejectReason.RateLimited, rateLimited.Reason);

        await executor.RejectNewWorkAndCancelActiveAndPendingAsync(TestTimeout());
        executor.AllowNewWork();

        Assert.Equal(3, await executor.ExecuteAsync("rate-reset-session", token => Task.FromResult(3)));
    }

    [Fact]
    public async Task CancelSessionAsync_CancelsOnlyTargetSessionAndClearsOnlyTargetRateWindow()
    {
        await using var executor = new AgasMcpSerialToolExecutor();
        executor.AllowNewWork();
        TaskCompletionSource otherRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource otherEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool targetPendingStarted = false;
        bool otherPendingStarted = false;

        Task<int> otherActive = executor.ExecuteAsync("other-session", async token =>
        {
            otherEntered.SetResult();
            await otherRelease.Task.WaitAsync(token);
            return 1;
        });
        Task<int> targetPending = executor.ExecuteAsync("target-session", token =>
        {
            targetPendingStarted = true;
            return Task.FromResult(2);
        });
        Task<int> otherPending = executor.ExecuteAsync("other-session", token =>
        {
            otherPendingStarted = true;
            return Task.FromResult(3);
        });

        await otherEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await executor.CancelSessionAsync("target-session", TestTimeout());

        AgasMcpToolExecutionRejectedException pendingException =
            await Assert.ThrowsAsync<AgasMcpToolExecutionRejectedException>(() => targetPending);
        Assert.Equal(AgasMcpToolExecutionRejectReason.Stopped, pendingException.Reason);
        Assert.False(targetPendingStarted);

        otherRelease.SetResult();
        Assert.Equal(1, await otherActive);
        await WaitUntilAsync(() => otherPendingStarted);
        Assert.Equal(3, await otherPending);

        TaskCompletionSource targetActiveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> targetActive = executor.ExecuteAsync("target-session", async token =>
        {
            targetActiveEntered.SetResult();
            await Task.Delay(TimeSpan.FromMinutes(1), token);
            return 4;
        });
        await targetActiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await executor.CancelSessionAsync("target-session", TestTimeout());
        AgasMcpToolExecutionRejectedException activeException =
            await Assert.ThrowsAsync<AgasMcpToolExecutionRejectedException>(() => targetActive);
        Assert.Equal(AgasMcpToolExecutionRejectReason.Stopped, activeException.Reason);

        for (int i = 0; i < AgasMcpSerialToolExecutor.PerSessionCallsPerMinute - 2; i++)
        {
            Assert.Equal(4, await executor.ExecuteAsync("other-session", token => Task.FromResult(4)));
        }

        AgasMcpToolExecutionRejectedException otherRateLimited =
            await Assert.ThrowsAsync<AgasMcpToolExecutionRejectedException>(() =>
                executor.ExecuteAsync("other-session", token => Task.FromResult(5)));
        Assert.Equal(AgasMcpToolExecutionRejectReason.RateLimited, otherRateLimited.Reason);

        Assert.Equal(6, await executor.ExecuteAsync("target-session", token => Task.FromResult(6)));
    }

    [Fact]
    public async Task ExecuteAsync_StartedGodotActionReturnsRealResultAfterTimeout()
    {
        TimeSpan toolTimeout = TimeSpan.FromMilliseconds(100);
        await using var executor = new AgasMcpSerialToolExecutor(toolTimeout);
        executor.AllowNewWork();
        var dispatcher = new AgasMcpMainThreadDispatcherQueue();
        TaskCompletionSource dispatcherQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource actionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releaseAction = new();

        Task<int> action = executor.ExecuteAsync("started-timeout-session", async token =>
        {
            Task<int> dispatched = dispatcher.InvokeAsync(_ =>
            {
                actionStarted.SetResult();
                if (!releaseAction.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Timed out waiting for test release.");
                }

                return 101;
            }, token);
            dispatcherQueued.SetResult();
            return await dispatched.ConfigureAwait(false);
        });

        await dispatcherQueued.Task.WaitAsync(TestTimeoutSpan);
        Task process = Task.Run(dispatcher.Process);
        await actionStarted.Task.WaitAsync(TestTimeoutSpan);
        await Task.Delay(toolTimeout + TimeSpan.FromMilliseconds(100));
        Assert.False(action.IsCompleted);

        releaseAction.Set();

        Assert.Equal(101, await action.WaitAsync(TestTimeoutSpan));
        await process.WaitAsync(TestTimeoutSpan);
    }

    [Fact]
    public async Task Stop_StartedGodotActionReturnsRealResult()
    {
        await using var executor = new AgasMcpSerialToolExecutor();
        executor.AllowNewWork();
        var dispatcher = new AgasMcpMainThreadDispatcherQueue();
        TaskCompletionSource dispatcherQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource actionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releaseAction = new();

        Task<int> action = executor.ExecuteAsync("started-stop-session", async token =>
        {
            Task<int> dispatched = dispatcher.InvokeAsync(_ =>
            {
                actionStarted.SetResult();
                if (!releaseAction.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Timed out waiting for test release.");
                }

                return 202;
            }, token);
            dispatcherQueued.SetResult();
            return await dispatched.ConfigureAwait(false);
        });

        await dispatcherQueued.Task.WaitAsync(TestTimeoutSpan);
        Task process = Task.Run(dispatcher.Process);
        await actionStarted.Task.WaitAsync(TestTimeoutSpan);
        executor.Stop();
        Assert.False(action.IsCompleted);

        releaseAction.Set();

        Assert.Equal(202, await action.WaitAsync(TestTimeoutSpan));
        await process.WaitAsync(TestTimeoutSpan);
    }

    [Fact]
    public async Task CancelSessionAsync_StartedGodotActionReturnsRealResult()
    {
        await using var executor = new AgasMcpSerialToolExecutor();
        executor.AllowNewWork();
        var dispatcher = new AgasMcpMainThreadDispatcherQueue();
        TaskCompletionSource dispatcherQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource actionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releaseAction = new();

        Task<int> action = executor.ExecuteAsync("started-delete-session", async token =>
        {
            Task<int> dispatched = dispatcher.InvokeAsync(_ =>
            {
                actionStarted.SetResult();
                if (!releaseAction.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Timed out waiting for test release.");
                }

                return 303;
            }, token);
            dispatcherQueued.SetResult();
            return await dispatched.ConfigureAwait(false);
        });

        await dispatcherQueued.Task.WaitAsync(TestTimeoutSpan);
        Task process = Task.Run(dispatcher.Process);
        await actionStarted.Task.WaitAsync(TestTimeoutSpan);
        Task cancelSession = executor.CancelSessionAsync("started-delete-session", TestTimeout());
        Assert.False(action.IsCompleted);

        releaseAction.Set();

        Assert.Equal(303, await action.WaitAsync(TestTimeoutSpan));
        await cancelSession.WaitAsync(TestTimeoutSpan);
        await process.WaitAsync(TestTimeoutSpan);
    }

    [Fact]
    public async Task ExecuteAsync_PendingGodotActionTimesOutBeforeStart()
    {
        await using var executor = new AgasMcpSerialToolExecutor(TimeSpan.FromMilliseconds(100));
        executor.AllowNewWork();
        var dispatcher = new AgasMcpMainThreadDispatcherQueue();
        TaskCompletionSource dispatcherQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool actionStarted = false;

        Task<int> action = executor.ExecuteAsync("pending-timeout-session", async token =>
        {
            Task<int> dispatched = dispatcher.InvokeAsync(_ =>
            {
                actionStarted = true;
                return 404;
            }, token);
            dispatcherQueued.SetResult();
            return await dispatched.ConfigureAwait(false);
        });

        await dispatcherQueued.Task.WaitAsync(TestTimeoutSpan);

        await Assert.ThrowsAsync<TimeoutException>(() => action.WaitAsync(TestTimeoutSpan));
        Assert.False(actionStarted);
        await ProcessDispatcherAfterCancellationAsync(dispatcher);
        Assert.False(actionStarted);
    }

    [Fact]
    public async Task Stop_PendingGodotActionReturnsStoppedBeforeStart()
    {
        await using var executor = new AgasMcpSerialToolExecutor();
        executor.AllowNewWork();
        var dispatcher = new AgasMcpMainThreadDispatcherQueue();
        TaskCompletionSource dispatcherQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool actionStarted = false;

        Task<int> action = executor.ExecuteAsync("pending-stop-session", async token =>
        {
            Task<int> dispatched = dispatcher.InvokeAsync(_ =>
            {
                actionStarted = true;
                return 505;
            }, token);
            dispatcherQueued.SetResult();
            return await dispatched.ConfigureAwait(false);
        });

        await dispatcherQueued.Task.WaitAsync(TestTimeoutSpan);
        executor.Stop();

        AgasMcpToolExecutionRejectedException exception =
            await Assert.ThrowsAsync<AgasMcpToolExecutionRejectedException>(() => action.WaitAsync(TestTimeoutSpan));
        Assert.Equal(AgasMcpToolExecutionRejectReason.Stopped, exception.Reason);
        Assert.False(actionStarted);
        await ProcessDispatcherAfterCancellationAsync(dispatcher);
        Assert.False(actionStarted);
    }

    [Fact]
    public async Task CancelSessionAsync_PendingGodotActionReturnsStoppedBeforeStart()
    {
        await using var executor = new AgasMcpSerialToolExecutor();
        executor.AllowNewWork();
        var dispatcher = new AgasMcpMainThreadDispatcherQueue();
        TaskCompletionSource dispatcherQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool actionStarted = false;

        Task<int> action = executor.ExecuteAsync("pending-delete-session", async token =>
        {
            Task<int> dispatched = dispatcher.InvokeAsync(_ =>
            {
                actionStarted = true;
                return 606;
            }, token);
            dispatcherQueued.SetResult();
            return await dispatched.ConfigureAwait(false);
        });

        await dispatcherQueued.Task.WaitAsync(TestTimeoutSpan);
        await executor.CancelSessionAsync("pending-delete-session", TestTimeout());

        AgasMcpToolExecutionRejectedException exception =
            await Assert.ThrowsAsync<AgasMcpToolExecutionRejectedException>(() => action.WaitAsync(TestTimeoutSpan));
        Assert.Equal(AgasMcpToolExecutionRejectReason.Stopped, exception.Reason);
        Assert.False(actionStarted);
        await ProcessDispatcherAfterCancellationAsync(dispatcher);
        Assert.False(actionStarted);
    }

    [Fact]
    public async Task CancelSessionAsync_AfterCompletedCallsDoesNotThrow()
    {
        await using var executor = new AgasMcpSerialToolExecutor();
        executor.AllowNewWork();

        for (int i = 0; i < 64; i++)
        {
            string sessionId = $"completed-cancel-session-{i}";
            Task<int> action = executor.ExecuteAsync(sessionId, token => Task.FromResult(i));

            Assert.Equal(i, await action.WaitAsync(TestTimeoutSpan));
            await executor.CancelSessionAsync(sessionId).WaitAsync(TestTimeoutSpan);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TestTimeoutSpan);
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task ProcessDispatcherAfterCancellationAsync(AgasMcpMainThreadDispatcherQueue dispatcher)
    {
        dispatcher.Process();
        await Task.Yield();
        dispatcher.Process();
    }

    private static CancellationToken TestTimeout() => new CancellationTokenSource(TestTimeoutSpan).Token;

    private static readonly TimeSpan TestTimeoutSpan = TimeSpan.FromSeconds(10);
}
