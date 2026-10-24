using AppAgasCs.Application;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Mcp;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp;

public sealed class AgasMcpAiJobTests
{
    [Fact]
    public void ProjectScope_NormalizesRootAndIsolatesClonesWithSameProjectId()
    {
        string baseRoot = Path.Combine(Path.GetTempPath(), "agas-scope-tests");
        AgasMcpAiProjectScope first = AgasMcpAiProjectScope.Create(
            Path.Combine(baseRoot, "clone-a", "."),
            " shared-id ");
        AgasMcpAiProjectScope equivalent = AgasMcpAiProjectScope.Create(
            Path.Combine(baseRoot, "clone-a"),
            "shared-id");
        AgasMcpAiProjectScope clone = AgasMcpAiProjectScope.Create(
            Path.Combine(baseRoot, "clone-b"),
            "shared-id");

        Assert.Equal(first, equivalent);
        Assert.NotEqual(first, clone);
        Assert.Equal("shared-id", first.ToString());
        Assert.DoesNotContain(baseRoot, first.ToString(), StringComparison.Ordinal);

        using var store = new AgasMcpAiJobStore();
        AgasMcpAiJobSnapshot firstJob = store.CreateQueued(first, "TextToImage");
        AgasMcpAiJobSnapshot cloneJob = store.CreateQueued(clone, "TextToImage");

        Assert.True(store.TryGet(first, firstJob.JobId, out _));
        Assert.False(store.TryGet(clone, firstJob.JobId, out _));
        Assert.False(store.TryGet(first, cloneJob.JobId, out _));
        Assert.Equal(firstJob.JobId, Assert.Single(store.GetRecent(first)).JobId);
        Assert.Equal(cloneJob.JobId, Assert.Single(store.GetRecent(clone)).JobId);
    }

    [Fact]
    public async Task Store_CancelAndCompletionRace_PreservesFirstTerminalState()
    {
        using var store = new AgasMcpAiJobStore(maximumRetainedTerminalJobs: 1000);

        for (int index = 0; index < 50; index++)
        {
            AgasMcpAiJobSnapshot created = store.CreateQueued("project-race", "RegionEdit");
            Assert.True(store.TryMarkRunning(created.JobId, out _));
            using var start = new ManualResetEventSlim(false);
            bool canceled = false;
            bool succeeded = false;

            Task cancel = Task.Run(() =>
            {
                start.Wait();
                canceled = store.TryCancel(created.JobId, out _);
            });
            Task succeed = Task.Run(() =>
            {
                start.Wait();
                succeeded = store.TrySucceed(
                    created.JobId,
                    Result(index),
                    applicationState: null,
                    out _);
            });

            start.Set();
            await Task.WhenAll(cancel, succeed);

            Assert.NotEqual(canceled, succeeded);
            Assert.True(store.TryGet(created.JobId, out AgasMcpAiJobSnapshot? terminal));
            Assert.Equal(
                canceled ? AgasMcpAiJobState.Canceled : AgasMcpAiJobState.Succeeded,
                terminal!.State);
            Assert.False(store.TryCancel(created.JobId, out AgasMcpAiJobSnapshot? unchanged));
            Assert.Equal(terminal.State, unchanged!.State);
        }
    }

    [Fact]
    public void Store_Cleanup_RemovesExpiredAndExcessTerminalJobsButNotLiveJobs()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        using var store = new AgasMcpAiJobStore(
            clock,
            maximumRetainedTerminalJobs: 2,
            terminalRetention: TimeSpan.FromMinutes(30));
        AgasMcpAiJobSnapshot live = store.CreateQueued("project-live", "TextToImage");
        Assert.True(store.TryMarkRunning(live.JobId, out _));

        AgasMcpAiJobSnapshot first = CreateCanceled(store, "project-1");
        AgasMcpAiJobSnapshot second = CreateCanceled(store, "project-2");
        AgasMcpAiJobSnapshot third = CreateCanceled(store, "project-3");

        Assert.False(store.TryGet(first.JobId, out _));
        Assert.True(store.TryGet(second.JobId, out _));
        Assert.True(store.TryGet(third.JobId, out _));
        Assert.True(store.TryGet(live.JobId, out AgasMcpAiJobSnapshot? running));
        Assert.Equal(AgasMcpAiJobState.Running, running!.State);

        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(2, store.Cleanup());
        Assert.False(store.TryGet(second.JobId, out _));
        Assert.False(store.TryGet(third.JobId, out _));
        Assert.True(store.TryGet(live.JobId, out _));
    }

    [Fact]
    public async Task Store_TtlCleanupRunsWithoutAStatusOrCleanupCall()
    {
        using var store = new AgasMcpAiJobStore(terminalRetention: TimeSpan.FromMilliseconds(100));
        AgasMcpAiJobSnapshot job = store.CreateQueued("project-ttl", "TextToImage");
        Assert.True(store.TryMarkRunning(job.JobId, out _));
        Assert.True(store.TrySucceed(job.JobId, Result(1), new object(), out _));
        Assert.Equal(1, store.StoredCount);

        await WaitUntilAsync(() => store.StoredCount == 0);
    }

    [Fact]
    public void Store_ApplicationStateCanBeConsumedOrClearedWithoutRemovingStatus()
    {
        using var store = new AgasMcpAiJobStore();
        object payload = new();
        AgasMcpAiJobSnapshot consumedJob = store.CreateQueued("project-apply", "RegionEdit");
        Assert.True(store.TryMarkRunning(consumedJob.JobId, out _));
        Assert.True(store.TrySucceed(consumedJob.JobId, Result(1), payload, out _));

        Assert.True(store.TryMarkApplied(
            consumedJob.JobId,
            "assets/applied.png",
            "Applied once.",
            out AgasMcpAiJobSnapshot? applied));
        Assert.Equal("assets/applied.png", applied!.Applied!.AppliedRelativePath);
        Assert.False(store.TryMarkApplied(
            consumedJob.JobId,
            "assets/other.png",
            "Must not replace the first result.",
            out AgasMcpAiJobSnapshot? unchanged));
        Assert.Equal("assets/applied.png", unchanged!.Applied!.AppliedRelativePath);
        Assert.True(store.TryConsumeApplicationState<object>(consumedJob.JobId, out object? consumed));
        Assert.Same(payload, consumed);
        Assert.False(store.TryConsumeApplicationState<object>(consumedJob.JobId, out _));
        Assert.True(store.TryGet(consumedJob.JobId, out AgasMcpAiJobSnapshot? status));
        Assert.Equal(AgasMcpAiJobState.Succeeded, status!.State);
        Assert.Equal("Applied once.", status.Applied!.Message);

        AgasMcpAiJobSnapshot clearedJob = store.CreateQueued("project-apply", "ReferenceEdit");
        Assert.True(store.TryMarkRunning(clearedJob.JobId, out _));
        Assert.True(store.TrySucceed(clearedJob.JobId, Result(2), new object(), out _));
        Assert.True(store.ClearApplicationState(clearedJob.JobId));
        Assert.False(store.ClearApplicationState(clearedJob.JobId));
        Assert.False(store.TryGetApplicationState<object>(clearedJob.JobId, out _));
    }

    [Fact]
    public void Store_ApplyLeasePinsTtlAndCapacityCleanup_ThenAtomicallyAppliesAndReleasesState()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        using var store = new AgasMcpAiJobStore(
            clock,
            maximumRetainedTerminalJobs: 2,
            terminalRetention: TimeSpan.FromMinutes(30));
        AgasMcpAiProjectScope scope = Scope("pinned", "shared-id");
        object applicationState = new();
        AgasMcpAiJobSnapshot pinned = CreateSucceeded(store, scope, applicationState);
        Assert.True(store.TryBeginApply<object>(scope, pinned.JobId, out AgasMcpAiJobApplyLease<object>? lease));
        Assert.Same(applicationState, lease!.ApplicationState);
        Assert.False(store.TryBeginApply<object>(scope, pinned.JobId, out _));
        Assert.False(store.TryGetApplicationState<object>(pinned.JobId, out _));
        Assert.False(store.TryGetApplicationState<object>(scope, pinned.JobId, out _));

        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(0, store.Cleanup());
        Assert.True(store.TryGet(scope, pinned.JobId, out _));

        AgasMcpAiJobSnapshot second = CreateCanceled(store, Scope("second", "shared-id"));
        AgasMcpAiJobSnapshot third = CreateCanceled(store, Scope("third", "shared-id"));
        Assert.True(store.TryGet(scope, pinned.JobId, out _));
        Assert.False(store.TryGet(second.ProjectScope, second.JobId, out _));
        Assert.True(store.TryGet(third.ProjectScope, third.JobId, out _));

        Assert.True(lease.MarkApplied("assets/applied.png", "Applied.", out AgasMcpAiJobSnapshot? applied));
        Assert.Equal("assets/applied.png", applied!.Applied!.AppliedRelativePath);
        Assert.False(store.TryGetApplicationState<object>(scope, pinned.JobId, out _));
        Assert.Equal(clock.GetUtcNow(), applied.Applied.AppliedAtUtc);
        Assert.False(store.TryGet(scope, pinned.JobId, out _));
        lease.Dispose();
    }

    [Fact]
    public void Store_FailedApplyLeaseReleaseUnpinsExpiredJobWithoutConsumingState()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        using var store = new AgasMcpAiJobStore(
            clock,
            terminalRetention: TimeSpan.FromMinutes(30));
        AgasMcpAiProjectScope scope = Scope("failed-apply", "project");
        AgasMcpAiJobSnapshot job = CreateSucceeded(store, scope, new object());
        Assert.True(store.TryBeginApply<object>(scope, job.JobId, out AgasMcpAiJobApplyLease<object>? lease));

        clock.Advance(TimeSpan.FromMinutes(30));
        lease!.Dispose();

        Assert.False(store.TryGet(scope, job.JobId, out _));
    }

    [Fact]
    public void Store_ApplyDoesNotExtendTerminalRetentionDeadline()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        using var store = new AgasMcpAiJobStore(
            clock,
            terminalRetention: TimeSpan.FromMinutes(30));
        AgasMcpAiProjectScope scope = Scope("apply-hard-ttl", "project");
        AgasMcpAiJobSnapshot job = CreateSucceeded(store, scope, new object());

        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.True(store.TryBeginApply<object>(scope, job.JobId, out AgasMcpAiJobApplyLease<object>? lease));
        Assert.True(lease!.MarkApplied("assets/applied.png", "Applied.", out _));
        Assert.True(store.TryGet(scope, job.JobId, out _));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, store.Cleanup());
        Assert.False(store.TryGet(scope, job.JobId, out _));
        lease.Dispose();
    }

    [Fact]
    public async Task Store_ApplyLeaseCompletionWinsCleanupRaceWithoutLosingAppliedRecord()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero));
        using var store = new AgasMcpAiJobStore(
            clock,
            terminalRetention: TimeSpan.FromMinutes(30));
        AgasMcpAiProjectScope scope = Scope("apply-race", "project");
        AgasMcpAiJobSnapshot job = CreateSucceeded(store, scope, new object());
        Assert.True(store.TryBeginApply<object>(scope, job.JobId, out AgasMcpAiJobApplyLease<object>? lease));
        clock.Advance(TimeSpan.FromMinutes(30));
        using var start = new ManualResetEventSlim(false);
        AgasMcpAiJobSnapshot? applied = null;

        Task cleanup = Task.Run(() =>
        {
            start.Wait();
            store.Cleanup();
        });
        Task apply = Task.Run(() =>
        {
            start.Wait();
            Assert.True(lease!.MarkApplied("assets/race.png", "Applied in race.", out applied));
        });
        start.Set();
        await Task.WhenAll(cleanup, apply);

        Assert.NotNull(applied?.Applied);
        Assert.Equal("assets/race.png", applied!.Applied!.AppliedRelativePath);
        Assert.False(store.TryGet(scope, job.JobId, out _));
        Assert.False(store.TryGetApplicationState<object>(scope, job.JobId, out _));
        lease!.Dispose();
    }

    [Fact]
    public async Task Scheduler_ScopedCancelDoesNotAffectCloneWithSameProjectId()
    {
        var coordinator = new AgasAiGenerationCoordinator();
        using var store = new AgasMcpAiJobStore();
        await using var scheduler = new AgasMcpAiJobScheduler(coordinator, store);
        List<IAgasAiGenerationPermit> uiPermits = AcquireAll(coordinator);
        AgasMcpAiProjectScope first = Scope("cancel-a", "shared-id");
        AgasMcpAiProjectScope clone = Scope("cancel-b", "shared-id");
        bool cloneRan = false;
        AgasMcpAiJobSubmitResult firstJob = scheduler.Submit(new AgasMcpAiJobSubmission(
            first,
            "TextToImage",
            token => Task.FromResult(new AgasMcpAiJobRunResult(Result(1)))));
        AgasMcpAiJobSubmitResult cloneJob = scheduler.Submit(new AgasMcpAiJobSubmission(
            clone,
            "TextToImage",
            token =>
            {
                cloneRan = true;
                return Task.FromResult(new AgasMcpAiJobRunResult(Result(2)));
            }));

        Assert.False(scheduler.Cancel(clone, firstJob.Job!.JobId).Found);
        Assert.Equal(1, scheduler.CancelProject(first));
        Assert.Equal(AgasMcpAiJobState.Canceled, store.GetRecent(first).Single().State);
        Assert.Equal(AgasMcpAiJobState.Queued, store.GetRecent(clone).Single().State);

        uiPermits[0].Dispose();
        await WaitUntilAsync(() => cloneRan
            && store.TryGet(clone, cloneJob.Job!.JobId, out AgasMcpAiJobSnapshot? job)
            && job!.State == AgasMcpAiJobState.Succeeded);
        foreach (IAgasAiGenerationPermit permit in uiPermits.Skip(1))
        {
            permit.Dispose();
        }
    }

    [Fact]
    public async Task Scheduler_ProjectWaitCountsQueuedWorkAndQueuedCancelCompletesWithoutOtherScope()
    {
        var coordinator = new AgasAiGenerationCoordinator();
        using var store = new AgasMcpAiJobStore();
        await using var scheduler = new AgasMcpAiJobScheduler(coordinator, store);
        List<IAgasAiGenerationPermit> uiPermits = AcquireAll(coordinator);
        AgasMcpAiProjectScope target = Scope("queued-target", "shared-id");
        AgasMcpAiProjectScope other = Scope("queued-other", "shared-id");
        AgasMcpAiJobSubmitResult targetJob = scheduler.Submit(new AgasMcpAiJobSubmission(
            target,
            "TextToImage",
            token => Task.FromResult(new AgasMcpAiJobRunResult(Result(1)))));
        AgasMcpAiJobSubmitResult otherJob = scheduler.Submit(new AgasMcpAiJobSubmission(
            other,
            "TextToImage",
            token => Task.FromResult(new AgasMcpAiJobRunResult(Result(2)))));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            scheduler.WaitForProjectQuiescenceAsync(target, TimeSpan.FromMilliseconds(100)));
        int canceled = await scheduler.CancelProjectAndWaitForQuiescenceAsync(
            target,
            TimeSpan.FromSeconds(2));

        Assert.Equal(1, canceled);
        Assert.Equal(AgasMcpAiJobState.Canceled, store.GetRecent(target).Single().State);
        Assert.True(store.TryGet(other, otherJob.Job!.JobId, out AgasMcpAiJobSnapshot? otherSnapshot));
        Assert.Equal(AgasMcpAiJobState.Queued, otherSnapshot!.State);
        Assert.False(store.TryGet(other, targetJob.Job!.JobId, out _));

        foreach (IAgasAiGenerationPermit permit in uiPermits)
        {
            permit.Dispose();
        }
        await scheduler.WaitForProjectQuiescenceAsync(other, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task SchedulerLifecyclePortWaitsForCanceledRunnerAndPermitButNotOtherProject()
    {
        var coordinator = new AgasAiGenerationCoordinator();
        using var store = new AgasMcpAiJobStore();
        await using var scheduler = new AgasMcpAiJobScheduler(coordinator, store);
        AgasMcpAiProjectScope target = Scope("running-target", "shared-id");
        AgasMcpAiProjectScope other = Scope("running-other", "shared-id");
        var targetEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var targetObservedCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishTarget = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishOther = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.Submit(new AgasMcpAiJobSubmission(
            target,
            "TextToImage",
            async token =>
            {
                targetEntered.SetResult();
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), token);
                }
                catch (OperationCanceledException)
                {
                    targetObservedCancellation.SetResult();
                    await finishTarget.Task;
                    throw;
                }
                return new AgasMcpAiJobRunResult(Result(1));
            }));
        scheduler.Submit(new AgasMcpAiJobSubmission(
            other,
            "TextToImage",
            async token =>
            {
                otherEntered.SetResult();
                await finishOther.Task;
                return new AgasMcpAiJobRunResult(Result(2));
            }));
        await Task.WhenAll(targetEntered.Task, otherEntered.Task).WaitAsync(TimeSpan.FromSeconds(2));
        var port = new ProjectLifecycleAiJobSchedulerQuiescencePort(
            scheduler,
            TimeSpan.FromSeconds(2));

        port.RequestProjectCancellation(target);
        Task wait = port.WaitForProjectQuiescenceAsync(target, CancellationToken.None).AsTask();
        await targetObservedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(wait.IsCompleted);
        Assert.Equal(2, coordinator.ActiveCount);

        finishTarget.SetResult();
        await wait;

        Assert.Equal(1, coordinator.ActiveCount);
        Assert.Equal(0, scheduler.CancelProject(target));
        Assert.Equal(AgasMcpAiJobState.Canceled, store.GetRecent(target).Single().State);
        Assert.Equal(AgasMcpAiJobState.Running, store.GetRecent(other).Single().State);

        finishOther.SetResult();
        await scheduler.WaitForProjectQuiescenceAsync(other, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task SchedulerCombinedCancelTimesOutUntilRunnerActuallyExits()
    {
        var coordinator = new AgasAiGenerationCoordinator();
        using var store = new AgasMcpAiJobStore();
        await using var scheduler = new AgasMcpAiJobScheduler(coordinator, store);
        AgasMcpAiProjectScope target = Scope("timeout-target", "project");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.Submit(new AgasMcpAiJobSubmission(
            target,
            "TextToImage",
            async token =>
            {
                entered.SetResult();
                await finish.Task;
                token.ThrowIfCancellationRequested();
                return new AgasMcpAiJobRunResult(Result(1));
            }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            scheduler.CancelProjectAndWaitForQuiescenceAsync(
                target,
                TimeSpan.FromMilliseconds(100)));

        Assert.Equal(1, coordinator.ActiveCount);
        Assert.Equal(AgasMcpAiJobState.Canceled, store.GetRecent(target).Single().State);
        finish.SetResult();
        await scheduler.WaitForProjectQuiescenceAsync(target, TimeSpan.FromSeconds(2));
        Assert.Equal(0, coordinator.ActiveCount);
    }

    [Fact]
    public async Task SchedulerProjectWaitHonorsCallerCancellationWithoutCancelingJob()
    {
        var coordinator = new AgasAiGenerationCoordinator();
        using var store = new AgasMcpAiJobStore();
        await using var scheduler = new AgasMcpAiJobScheduler(coordinator, store);
        AgasMcpAiProjectScope target = Scope("caller-cancel-target", "project");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AgasMcpAiJobSubmitResult submitted = scheduler.Submit(new AgasMcpAiJobSubmission(
            target,
            "TextToImage",
            async token =>
            {
                entered.SetResult();
                await finish.Task;
                return new AgasMcpAiJobRunResult(Result(1));
            }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scheduler.WaitForProjectQuiescenceAsync(
                target,
                TimeSpan.FromSeconds(2),
                cancellation.Token));

        Assert.True(store.TryGet(target, submitted.Job!.JobId, out AgasMcpAiJobSnapshot? running));
        Assert.Equal(AgasMcpAiJobState.Running, running!.State);
        finish.SetResult();
        await scheduler.WaitForProjectQuiescenceAsync(target, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Scheduler_UsesSharedPermits_AndRunsBoundedQueueInFifoOrder()
    {
        var coordinator = new AgasAiGenerationCoordinator();
        var store = new AgasMcpAiJobStore();
        await using var scheduler = new AgasMcpAiJobScheduler(coordinator, store);
        List<IAgasAiGenerationPermit> uiPermits = AcquireAll(coordinator);
        List<int> runOrder = new();
        List<AgasMcpAiJobSnapshot> jobs = new();

        for (int index = 0; index < AgasMcpAiJobScheduler.QueueCapacity; index++)
        {
            int captured = index;
            AgasMcpAiJobSubmitResult submitted = scheduler.Submit(new AgasMcpAiJobSubmission(
                "project-fifo",
                "RegionEdit",
                token =>
                {
                    lock (runOrder)
                    {
                        runOrder.Add(captured);
                    }
                    return Task.FromResult(new AgasMcpAiJobRunResult(Result(captured)));
                }));
            Assert.True(submitted.Accepted);
            jobs.Add(submitted.Job!);
        }

        AgasMcpAiJobSubmitResult full = scheduler.Submit(new AgasMcpAiJobSubmission(
            "project-fifo",
            "RegionEdit",
            token => Task.FromResult(new AgasMcpAiJobRunResult(Result(99)))));
        Assert.False(full.Accepted);
        Assert.Equal(AgasMcpAiJobSubmitRejectReason.Busy, full.RejectReason);
        Assert.Empty(runOrder);

        AgasMcpAiJobCancelResult canceled = scheduler.Cancel(jobs[0].JobId);
        Assert.True(canceled.Found);
        Assert.True(canceled.Canceled);
        int replacementIndex = AgasMcpAiJobScheduler.QueueCapacity;
        AgasMcpAiJobSubmitResult replacement = scheduler.Submit(new AgasMcpAiJobSubmission(
            "project-fifo",
            "RegionEdit",
            token =>
            {
                lock (runOrder)
                {
                    runOrder.Add(replacementIndex);
                }
                return Task.FromResult(new AgasMcpAiJobRunResult(Result(replacementIndex)));
            }));
        Assert.True(replacement.Accepted);
        jobs.Add(replacement.Job!);

        uiPermits[0].Dispose();
        await WaitUntilAsync(() => jobs.Skip(1).All(job =>
            store.TryGet(job.JobId, out AgasMcpAiJobSnapshot? current)
            && current!.State == AgasMcpAiJobState.Succeeded));

        Assert.Equal(Enumerable.Range(1, AgasMcpAiJobScheduler.QueueCapacity), runOrder);
        Assert.Equal(0, scheduler.QueuedCount);
        Assert.Equal(0, scheduler.ActiveCount);
        foreach (IAgasAiGenerationPermit permit in uiPermits.Skip(1))
        {
            permit.Dispose();
        }
    }

    [Fact]
    public async Task Scheduler_NeverExceedsSharedActiveLimit()
    {
        var coordinator = new AgasAiGenerationCoordinator();
        var store = new AgasMcpAiJobStore();
        await using var scheduler = new AgasMcpAiJobScheduler(coordinator, store);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int maximumObserved = 0;
        List<AgasMcpAiJobSnapshot> jobs = new();

        for (int index = 0; index < 10; index++)
        {
            AgasMcpAiJobSubmitResult submitted = scheduler.Submit(new AgasMcpAiJobSubmission(
                "project-limit",
                "TextToImage",
                async token =>
                {
                    int current = Interlocked.Increment(ref active);
                    UpdateMaximum(ref maximumObserved, current);
                    try
                    {
                        await release.Task.WaitAsync(token);
                        return new AgasMcpAiJobRunResult(Result(current));
                    }
                    finally
                    {
                        Interlocked.Decrement(ref active);
                    }
                }));
            Assert.True(submitted.Accepted);
            jobs.Add(submitted.Job!);
        }

        await WaitUntilAsync(() => Volatile.Read(ref active) == 5);
        Assert.Equal(5, coordinator.ActiveCount);
        Assert.Equal(5, scheduler.QueuedCount);
        Assert.False(coordinator.TryAcquire(out _));

        release.SetResult();
        await WaitUntilAsync(() => jobs.All(job =>
            store.TryGet(job.JobId, out AgasMcpAiJobSnapshot? current)
            && current!.State == AgasMcpAiJobState.Succeeded));
        Assert.Equal(5, maximumObserved);
        Assert.Equal(0, coordinator.ActiveCount);
    }

    [Fact]
    public async Task Scheduler_NewSubmissionCannotBypassAnExistingQueueWhenCapacityRacesRelease()
    {
        var coordinator = new ManualCoordinator();
        var store = new AgasMcpAiJobStore();
        await using var scheduler = new AgasMcpAiJobScheduler(coordinator, store);
        List<int> runOrder = new();

        coordinator.CapacityAvailableNow = false;
        AgasMcpAiJobSubmitResult first = scheduler.Submit(new AgasMcpAiJobSubmission(
            "project-fifo-race",
            "RegionEdit",
            token =>
            {
                lock (runOrder) runOrder.Add(1);
                return Task.FromResult(new AgasMcpAiJobRunResult(Result(1)));
            }));
        Assert.True(first.Accepted);
        Assert.Equal(1, scheduler.QueuedCount);

        // Model the window after a permit becomes available but before the
        // capacity callback has acquired the scheduler lock.
        coordinator.CapacityAvailableNow = true;
        AgasMcpAiJobSubmitResult second = scheduler.Submit(new AgasMcpAiJobSubmission(
            "project-fifo-race",
            "RegionEdit",
            token =>
            {
                lock (runOrder) runOrder.Add(2);
                return Task.FromResult(new AgasMcpAiJobRunResult(Result(2)));
            }));
        Assert.True(second.Accepted);
        Assert.Equal(2, scheduler.QueuedCount);
        Assert.Empty(runOrder);

        coordinator.RaiseCapacityAvailable();
        await WaitUntilAsync(() =>
            store.TryGet(first.Job!.JobId, out AgasMcpAiJobSnapshot? firstState)
            && firstState!.State == AgasMcpAiJobState.Succeeded
            && store.TryGet(second.Job!.JobId, out AgasMcpAiJobSnapshot? secondState)
            && secondState!.State == AgasMcpAiJobState.Succeeded);

        Assert.Equal(new[] { 1, 2 }, runOrder);
    }

    [Fact]
    public async Task Scheduler_CancelRunningJob_IsImmediateAndIdempotent()
    {
        var coordinator = new AgasAiGenerationCoordinator();
        var store = new AgasMcpAiJobStore();
        await using var scheduler = new AgasMcpAiJobScheduler(coordinator, store);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource observedCancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AgasMcpAiJobSubmitResult submitted = scheduler.Submit(new AgasMcpAiJobSubmission(
            "project-cancel",
            "ReferenceEdit",
            async token =>
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
                return new AgasMcpAiJobRunResult(Result(1));
            }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        AgasMcpAiJobCancelResult first = scheduler.Cancel(submitted.Job!.JobId);
        AgasMcpAiJobCancelResult second = scheduler.Cancel(submitted.Job.JobId);

        Assert.True(first.Found);
        Assert.True(first.Canceled);
        Assert.Equal(AgasMcpAiJobState.Canceled, first.Job!.State);
        Assert.True(second.Found);
        Assert.False(second.Canceled);
        Assert.Equal(AgasMcpAiJobState.Canceled, second.Job!.State);
        await observedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => scheduler.ActiveCount == 0);
    }

    [Fact]
    public async Task Scheduler_CompletedJobCancelIsIdempotent_AndUnexpectedFailureIsSanitized()
    {
        var coordinator = new AgasAiGenerationCoordinator();
        var store = new AgasMcpAiJobStore();
        await using var scheduler = new AgasMcpAiJobScheduler(coordinator, store);
        AgasMcpAiJobSubmitResult success = scheduler.Submit(new AgasMcpAiJobSubmission(
            "project-terminal",
            "TextToImage",
            token => Task.FromResult(new AgasMcpAiJobRunResult(Result(1)))));
        AgasMcpAiJobSubmitResult failure = scheduler.Submit(new AgasMcpAiJobSubmission(
            "project-terminal",
            "TextToImage",
            token => throw new InvalidOperationException("secret-api-key")));

        await WaitUntilAsync(() =>
            store.TryGet(success.Job!.JobId, out AgasMcpAiJobSnapshot? succeeded)
            && succeeded!.State == AgasMcpAiJobState.Succeeded
            && store.TryGet(failure.Job!.JobId, out AgasMcpAiJobSnapshot? failed)
            && failed!.State == AgasMcpAiJobState.Failed);

        AgasMcpAiJobCancelResult cancel = scheduler.Cancel(success.Job!.JobId);
        Assert.True(cancel.Found);
        Assert.False(cancel.Canceled);
        Assert.Equal(AgasMcpAiJobState.Succeeded, cancel.Job!.State);
        Assert.True(store.TryGet(failure.Job!.JobId, out AgasMcpAiJobSnapshot? failureState));
        Assert.Equal("AGAS_AI_PROVIDER_FAILED", failureState!.Failure!.ErrorCode);
        Assert.DoesNotContain("secret", failureState.Failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scheduler_StopCancelsAllWorkAndRejectsNewSubmissions()
    {
        var coordinator = new AgasAiGenerationCoordinator();
        var store = new AgasMcpAiJobStore();
        await using var scheduler = new AgasMcpAiJobScheduler(coordinator, store);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AgasMcpAiJobSubmitResult running = scheduler.Submit(new AgasMcpAiJobSubmission(
            "project-stop",
            "TextToImage",
            async token =>
            {
                entered.SetResult();
                await Task.Delay(TimeSpan.FromMinutes(1), token);
                return new AgasMcpAiJobRunResult(Result(1));
            }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await scheduler.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(store.TryGet(running.Job!.JobId, out AgasMcpAiJobSnapshot? canceled));
        Assert.Equal(AgasMcpAiJobState.Canceled, canceled!.State);
        Assert.Equal(0, coordinator.ActiveCount);
        Assert.Equal(0, scheduler.ActiveCount);
        AgasMcpAiJobSubmitResult rejected = scheduler.Submit(new AgasMcpAiJobSubmission(
            "project-stop",
            "TextToImage",
            token => Task.FromResult(new AgasMcpAiJobRunResult(Result(2)))));
        Assert.False(rejected.Accepted);
        Assert.Equal(AgasMcpAiJobSubmitRejectReason.Stopped, rejected.RejectReason);
    }

    private static AgasMcpAiJobSnapshot CreateCanceled(AgasMcpAiJobStore store, string projectId)
    {
        AgasMcpAiJobSnapshot job = store.CreateQueued(projectId, "TextToImage");
        Assert.True(store.TryCancel(job.JobId, out AgasMcpAiJobSnapshot? canceled));
        return canceled!;
    }

    private static AgasMcpAiJobSnapshot CreateCanceled(
        AgasMcpAiJobStore store,
        AgasMcpAiProjectScope scope)
    {
        AgasMcpAiJobSnapshot job = store.CreateQueued(scope, "TextToImage");
        Assert.True(store.TryCancel(job.JobId, out AgasMcpAiJobSnapshot? canceled));
        return canceled!;
    }

    private static AgasMcpAiJobSnapshot CreateSucceeded(
        AgasMcpAiJobStore store,
        AgasMcpAiProjectScope scope,
        object applicationState)
    {
        AgasMcpAiJobSnapshot job = store.CreateQueued(scope, "RegionEdit");
        Assert.True(store.TryMarkRunning(job.JobId, out _));
        Assert.True(store.TrySucceed(job.JobId, Result(1), applicationState, out AgasMcpAiJobSnapshot? succeeded));
        return succeeded!;
    }

    private static AgasMcpAiProjectScope Scope(string directoryName, string projectId) =>
        AgasMcpAiProjectScope.Create(
            Path.Combine(Path.GetTempPath(), "agas-job-scope", directoryName),
            projectId);

    private static AgasMcpAiJobResultMetadata Result(int index) =>
        new($".agas/generated/result-{index}.png", null, "Generated.");

    private static List<IAgasAiGenerationPermit> AcquireAll(AgasAiGenerationCoordinator coordinator)
    {
        List<IAgasAiGenerationPermit> permits = new();
        for (int index = 0; index < coordinator.MaximumConcurrentGenerations; index++)
        {
            Assert.True(coordinator.TryAcquire(out IAgasAiGenerationPermit? permit));
            permits.Add(permit!);
        }
        return permits;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static void UpdateMaximum(ref int maximum, int current)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref maximum);
            if (observed >= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref maximum, current, observed) != observed);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class ManualCoordinator : IAgasAiGenerationCoordinator
    {
        private int _activeCount;

        public int MaximumConcurrentGenerations => 1;
        public int ActiveCount => _activeCount;
        public bool CapacityAvailableNow { get; set; }
        public event Action? CapacityAvailable;

        public bool TryAcquire(out IAgasAiGenerationPermit? permit)
        {
            if (!CapacityAvailableNow || _activeCount != 0)
            {
                permit = null;
                return false;
            }

            _activeCount = 1;
            CapacityAvailableNow = false;
            permit = new ManualPermit(this);
            return true;
        }

        public void RaiseCapacityAvailable() => CapacityAvailable?.Invoke();

        private sealed class ManualPermit : IAgasAiGenerationPermit
        {
            private ManualCoordinator? _owner;

            public ManualPermit(ManualCoordinator owner) => _owner = owner;

            public void Dispose()
            {
                ManualCoordinator? owner = Interlocked.Exchange(ref _owner, null);
                if (owner == null) return;
                owner._activeCount = 0;
                owner.CapacityAvailableNow = true;
                owner.RaiseCapacityAvailable();
            }
        }
    }
}
