using AppAgasCs.Application;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Runtime;
using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class ProjectLifecycleCloseServiceTests
{
    [Theory]
    [InlineData(true, false, false, ProjectLifecycleFailureReason.ToolBusy)]
    [InlineData(false, true, false, ProjectLifecycleFailureReason.ProjectDirty)]
    [InlineData(false, false, true, ProjectLifecycleFailureReason.AiJobsActive)]
    public async Task ReadOnlyGatesRejectBeforeCancelSaveOrFinalize(
        bool toolBusy,
        bool dirty,
        bool aiJobs,
        ProjectLifecycleFailureReason expected)
    {
        var port = new RecordingMainThreadPort(Snapshot(dirty, toolBusy, aiJobs));
        var quiescence = new RecordingQuiescencePort();
        var service = Service(port, quiescence);
        var execution = Execution();

        ProjectLifecycleCloseResult result = await service.CloseAsync(
            saveIfDirty: false,
            cancelAiJobs: false,
            execution,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(expected, result.FailureReason);
        Assert.Equal(ProjectLifecycleCloseReceipt.Initial, result.Receipt);
        Assert.Empty(quiescence.Scopes);
        Assert.DoesNotContain("save", port.Calls);
        Assert.DoesNotContain("finalize", port.Calls);
        Assert.Equal(new[] { "capture" }, port.Calls);
    }

    [Fact]
    public async Task CancellationAndQuiescenceAreProjectScopedBeforeMainThreadSave()
    {
        var port = new RecordingMainThreadPort(
            Snapshot(dirty: true, aiJobs: true),
            Snapshot(dirty: true, aiJobs: false),
            Snapshot(dirty: false, aiJobs: false))
        {
            SaveOutcome = new(true, true, ProjectLifecycleSaveState.Complete),
        };
        var quiescence = new RecordingQuiescencePort(port.Calls);
        var service = Service(port, quiescence);
        var execution = Execution();

        ProjectLifecycleCloseResult result = await service.CloseAsync(
            saveIfDirty: true,
            cancelAiJobs: true,
            execution,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new[] { Scope() }, quiescence.Scopes);
        Assert.Equal(
            new[] { "capture", "cancel", "wait", "recheck", "save", "recheck", "finalize" },
            port.Calls);
        Assert.Equal(new(false, true, true, ProjectLifecycleSaveState.Complete), result.Receipt);
        Assert.Equal(AgasMcpCommitExecutionState.Completed, execution.State);
    }

    [Fact]
    public async Task CancelThenPartialSaveFailureReturnsAccurateImmutableReceipt()
    {
        var port = new RecordingMainThreadPort(
            Snapshot(dirty: true, aiJobs: true),
            Snapshot(dirty: true, aiJobs: false))
        {
            SaveOutcome = new(false, false, ProjectLifecycleSaveState.Partial),
        };
        var service = Service(port, new RecordingQuiescencePort());
        var execution = Execution();

        ProjectLifecycleCloseResult result = await service.CloseAsync(
            saveIfDirty: true,
            cancelAiJobs: true,
            execution,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ProjectLifecycleFailureReason.SaveFailed, result.FailureReason);
        Assert.Equal(new(true, false, true, ProjectLifecycleSaveState.Partial), result.Receipt);
        Assert.DoesNotContain("finalize", port.Calls);
        Assert.Equal(AgasMcpCommitExecutionState.Completed, execution.State);
    }

    [Fact]
    public async Task CancellationBeforeCommitKeepsProjectOpenAndSkipsSave()
    {
        using var cancellation = new CancellationTokenSource();
        var port = new RecordingMainThreadPort(
            Snapshot(dirty: true, aiJobs: true),
            Snapshot(dirty: true, aiJobs: false));
        var quiescence = new RecordingQuiescencePort(onWait: cancellation.Cancel);
        var service = Service(port, quiescence);
        var execution = Execution();

        ProjectLifecycleCloseResult result = await service.CloseAsync(
            saveIfDirty: true,
            cancelAiJobs: true,
            execution,
            cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal(ProjectLifecycleFailureReason.CanceledBeforeCommit, result.FailureReason);
        Assert.Equal(new(true, false, true, ProjectLifecycleSaveState.None), result.Receipt);
        Assert.DoesNotContain("save", port.Calls);
        Assert.Equal(AgasMcpCommitExecutionState.Canceled, execution.State);
    }

    [Fact]
    public async Task CancellationAfterSaveStartsCannotRewriteSuccessfulClose()
    {
        using var cancellation = new CancellationTokenSource();
        var port = new RecordingMainThreadPort(
            Snapshot(dirty: true),
            Snapshot(dirty: true),
            Snapshot(dirty: false))
        {
            SaveOutcome = new(true, true, ProjectLifecycleSaveState.Complete),
            OnSave = cancellation.Cancel,
        };
        var service = Service(port, new RecordingQuiescencePort());
        var execution = Execution();

        ProjectLifecycleCloseResult result = await service.CloseAsync(
            saveIfDirty: true,
            cancelAiJobs: false,
            execution,
            cancellation.Token);

        Assert.True(result.Success);
        Assert.False(result.Receipt.ProjectStillOpen);
        Assert.True(execution.CancellationObservedAfterStart);
        Assert.Equal(AgasMcpCommitExecutionState.Completed, execution.State);
        Assert.All(port.CommitTokens, token => Assert.False(token.CanBeCanceled));
    }

    [Fact]
    public async Task ToolStartedDuringSavePreventsCloseAfterPostSaveRecheck()
    {
        var port = new RecordingMainThreadPort(
            Snapshot(dirty: true),
            Snapshot(dirty: true),
            Snapshot(dirty: false, toolBusy: true))
        {
            SaveOutcome = new(true, true, ProjectLifecycleSaveState.Complete),
        };
        var service = Service(port, new RecordingQuiescencePort());

        ProjectLifecycleCloseResult result = await service.CloseAsync(
            saveIfDirty: true,
            cancelAiJobs: false,
            Execution(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ProjectLifecycleFailureReason.ToolBusy, result.FailureReason);
        Assert.Equal(new[] { "capture", "recheck", "save", "recheck" }, port.Calls);
        Assert.Equal(new(true, true, false, ProjectLifecycleSaveState.Complete), result.Receipt);
    }

    [Fact]
    public async Task RecheckGenerationDriftPreventsCommit()
    {
        var port = new RecordingMainThreadPort(
            Snapshot(dirty: false),
            Snapshot(dirty: false) with { Generation = 2 });
        var service = Service(port, new RecordingQuiescencePort());
        var execution = Execution();

        ProjectLifecycleCloseResult result = await service.CloseAsync(
            false,
            false,
            execution,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ProjectLifecycleFailureReason.WorkspaceChanged, result.FailureReason);
        Assert.DoesNotContain("finalize", port.Calls);
        Assert.Equal(AgasMcpCommitExecutionState.Pending, execution.State);
    }

    [Fact]
    public async Task CleanCloseReportsSavedTrueWithoutClaimingASaveWrite()
    {
        var port = new RecordingMainThreadPort(Snapshot(dirty: false), Snapshot(dirty: false));

        ProjectLifecycleCloseResult result = await Service(port, new RecordingQuiescencePort()).CloseAsync(
            false,
            false,
            Execution(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new(false, true, false, ProjectLifecycleSaveState.None), result.Receipt);
        Assert.DoesNotContain("save", port.Calls);
    }

    [Fact]
    public async Task TrackerPublishesCancellationBeforeQuiescenceCompletes()
    {
        var waitEntered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWait = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var port = new RecordingMainThreadPort(
            Snapshot(dirty: false, aiJobs: true),
            Snapshot(dirty: false, aiJobs: false));
        var quiescence = new RecordingQuiescencePort(
            port.Calls,
            waitAsync: async cancellationToken =>
            {
                waitEntered.TrySetResult(null);
                await releaseWait.Task.WaitAsync(cancellationToken);
            });
        var tracker = new ProjectLifecycleCloseReceiptTracker();
        var execution = Execution();
        ProjectLifecycleCloseService service = Service(port, quiescence);

        Task<ProjectLifecycleCloseResult> closing = service.CloseAsync(
            saveIfDirty: false,
            cancelAiJobs: true,
            execution,
            tracker,
            CancellationToken.None).AsTask();
        await waitEntered.Task;

        Assert.Equal(
            new(true, false, true, ProjectLifecycleSaveState.None),
            tracker.Current);

        releaseWait.TrySetResult(null);
        ProjectLifecycleCloseResult result = await closing;
        Assert.True(result.Success);
        Assert.Equal(result.Receipt, tracker.Current);
    }

    [Fact]
    public async Task TrackerRetainsPartialSaveReceiptOnFailure()
    {
        var port = new RecordingMainThreadPort(
            Snapshot(dirty: true),
            Snapshot(dirty: true))
        {
            SaveOutcome = new(false, false, ProjectLifecycleSaveState.Partial),
        };
        var tracker = new ProjectLifecycleCloseReceiptTracker();

        ProjectLifecycleCloseResult result = await Service(port, new RecordingQuiescencePort()).CloseAsync(
            true,
            false,
            Execution(),
            tracker,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(new(true, false, false, ProjectLifecycleSaveState.Partial), tracker.Current);
        Assert.Equal(result.Receipt, tracker.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalizeFailureRecapturesWhetherOriginalProjectActuallyClosed(bool throwAfterClose)
    {
        var port = new RecordingMainThreadPort(
            Snapshot(dirty: false),
            Snapshot(dirty: false),
            new ProjectLifecycleWorkspaceSnapshot(false, default, 2, false, false, false))
        {
            FinalizeResult = false,
            ThrowAfterFinalize = throwAfterClose,
        };
        var service = Service(port, new RecordingQuiescencePort());
        var execution = Execution();

        ProjectLifecycleCloseResult result = await service.CloseAsync(
            saveIfDirty: false,
            cancelAiJobs: false,
            execution,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ProjectLifecycleFailureReason.FinalizeFailed, result.FailureReason);
        Assert.False(result.Receipt.ProjectStillOpen);
        Assert.True(result.Receipt.Saved);
        Assert.Equal(ProjectLifecycleSaveState.None, result.Receipt.SaveState);
        Assert.Equal(AgasMcpCommitExecutionState.Completed, execution.State);
    }

    private static ProjectLifecycleCloseService Service(
        IProjectLifecycleMainThreadPort port,
        IProjectLifecycleQuiescencePort quiescence) =>
        new(new ProjectLifecycleWorkspaceLeaseManager(), port, quiescence);

    private static AgasMcpCommitExecution Execution() =>
        new(AgasMcpCommitPolicy.StartedMainThreadCompletion);

    private static ProjectLifecycleWorkspaceSnapshot Snapshot(
        bool dirty = false,
        bool toolBusy = false,
        bool aiJobs = false) =>
        new(true, Scope(), 1, dirty, toolBusy, aiJobs);

    private static AgasMcpAiProjectScope Scope() =>
        AgasMcpAiProjectScope.Create(
            Path.Combine(Path.GetTempPath(), "agas-project-lifecycle-tests", "scope-a"),
            "scope-a");

    private sealed class RecordingQuiescencePort : IProjectLifecycleQuiescencePort
    {
        private readonly List<string>? _calls;
        private readonly Action? _onWait;
        private readonly Func<CancellationToken, Task>? _waitAsync;

        public RecordingQuiescencePort(
            List<string>? calls = null,
            Action? onWait = null,
            Func<CancellationToken, Task>? waitAsync = null)
        {
            _calls = calls;
            _onWait = onWait;
            _waitAsync = waitAsync;
        }

        public List<AgasMcpAiProjectScope> Scopes { get; } = new();

        public void RequestProjectCancellation(AgasMcpAiProjectScope projectScope)
        {
            Scopes.Add(projectScope);
            _calls?.Add("cancel");
        }

        public async ValueTask WaitForProjectQuiescenceAsync(
            AgasMcpAiProjectScope projectScope,
            CancellationToken cancellationToken)
        {
            _calls?.Add("wait");
            _onWait?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (_waitAsync != null)
                await _waitAsync(cancellationToken);
        }
    }

    private sealed class RecordingMainThreadPort : IProjectLifecycleMainThreadPort
    {
        private readonly Queue<ProjectLifecycleWorkspaceSnapshot> _snapshots;

        public RecordingMainThreadPort(params ProjectLifecycleWorkspaceSnapshot[] snapshots)
        {
            _snapshots = new(snapshots);
        }

        public List<string> Calls { get; } = new();
        public List<CancellationToken> CommitTokens { get; } = new();
        public ProjectLifecycleSaveOutcome SaveOutcome { get; set; } =
            new(true, true, ProjectLifecycleSaveState.Complete);
        public Action? OnSave { get; set; }
        public bool FinalizeResult { get; set; } = true;
        public bool ThrowAfterFinalize { get; set; }

        public ValueTask<ProjectLifecycleWorkspaceSnapshot> CaptureAsync(CancellationToken cancellationToken)
        {
            Calls.Add("capture");
            return ValueTask.FromResult(_snapshots.Dequeue());
        }

        public ValueTask<ProjectLifecycleWorkspaceSnapshot> RecheckAsync(
            ProjectLifecycleWorkspaceLease lease,
            CancellationToken cancellationToken)
        {
            Assert.True(lease.IsActive);
            Calls.Add("recheck");
            if (!cancellationToken.CanBeCanceled)
                CommitTokens.Add(cancellationToken);
            return ValueTask.FromResult(_snapshots.Dequeue());
        }

        public ValueTask<ProjectLifecycleSaveOutcome> ControlledSaveAsync(
            ProjectLifecycleWorkspaceLease lease,
            CancellationToken cancellationToken)
        {
            Assert.True(lease.IsActive);
            Calls.Add("save");
            CommitTokens.Add(cancellationToken);
            OnSave?.Invoke();
            return ValueTask.FromResult(SaveOutcome);
        }

        public ValueTask<bool> FinalizeCloseAsync(
            ProjectLifecycleWorkspaceLease lease,
            CancellationToken cancellationToken)
        {
            Assert.True(lease.IsActive);
            Calls.Add("finalize");
            CommitTokens.Add(cancellationToken);
            if (ThrowAfterFinalize) throw new InvalidOperationException("finalize failed after closing");
            return ValueTask.FromResult(FinalizeResult);
        }
    }
}
