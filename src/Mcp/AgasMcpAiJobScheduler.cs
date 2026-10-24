using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;

namespace AppAgasCs.Mcp;

public delegate Task<AgasMcpAiJobRunResult> AgasMcpAiJobRunner(CancellationToken cancellationToken);

public sealed record AgasMcpAiJobRunResult(
    AgasMcpAiJobResultMetadata Metadata,
    object? ApplicationState = null,
    Func<ValueTask>? DiscardAsync = null,
    string? EffectiveOperation = null);

public sealed record AgasMcpAiJobSubmission(
    AgasMcpAiProjectScope ProjectScope,
    string EffectiveOperation,
    AgasMcpAiExecutionIdentity Execution,
    AgasMcpAiJobRunner Runner)
{
    public AgasMcpAiJobSubmission(
        AgasMcpAiProjectScope projectScope,
        string effectiveOperation,
        AgasMcpAiJobRunner runner)
        : this(projectScope, effectiveOperation, LegacyExecution(), runner)
    {
    }

    public AgasMcpAiJobSubmission(
        string projectId,
        string effectiveOperation,
        AgasMcpAiJobRunner runner)
        : this(AgasMcpAiProjectScope.Legacy(projectId), effectiveOperation, LegacyExecution(), runner)
    {
    }

    public string ProjectId => ProjectScope.ProjectId;

    private static AgasMcpAiExecutionIdentity LegacyExecution() =>
        new("legacy", "legacy", "legacy", "legacy");
}

public enum AgasMcpAiJobSubmitRejectReason
{
    None,
    Busy,
    Stopped,
}

public sealed record AgasMcpAiJobSubmitResult(
    bool Accepted,
    AgasMcpAiJobSnapshot? Job,
    AgasMcpAiJobSubmitRejectReason RejectReason)
{
    public static AgasMcpAiJobSubmitResult Accept(AgasMcpAiJobSnapshot job) =>
        new(true, job, AgasMcpAiJobSubmitRejectReason.None);

    public static AgasMcpAiJobSubmitResult Reject(AgasMcpAiJobSubmitRejectReason reason) =>
        new(false, null, reason);
}

public sealed record AgasMcpAiJobCancelResult(
    bool Found,
    bool Canceled,
    AgasMcpAiJobSnapshot? Job);

public sealed class AgasMcpAiJobExecutionException : Exception
{
    public AgasMcpAiJobExecutionException(AgasMcpAiJobFailure failure)
        : base("The AI job failed.")
    {
        Failure = failure ?? throw new ArgumentNullException(nameof(failure));
    }

    public AgasMcpAiJobFailure Failure { get; }
}

/// <summary>
/// Owns the MCP-only bounded FIFO. Active permits come exclusively from the
/// coordinator shared with UI generation.
/// </summary>
public sealed class AgasMcpAiJobScheduler : IAsyncDisposable, IDisposable
{
    public const int QueueCapacity = 20;

    private static readonly AgasMcpAiJobFailure DefaultFailure = new(
        "AGAS_AI_PROVIDER_FAILED",
        "The image provider request failed.");

    private readonly object _gate = new();
    private readonly IAgasAiGenerationCoordinator _coordinator;
    private readonly AgasMcpAiJobStore _store;
    private readonly Func<Exception, AgasMcpAiJobFailure> _failureMapper;
    private readonly LinkedList<WorkItem> _queue = new();
    private readonly Dictionary<string, WorkItem> _queued = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActiveWork> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<AgasMcpAiProjectScope, TaskCompletionSource<object?>> _projectQuiescence = new();
    private TaskCompletionSource<object?> _quiescence = CompletedQuiescence();
    private bool _accepting = true;
    private bool _disposed;

    public AgasMcpAiJobScheduler(
        IAgasAiGenerationCoordinator coordinator,
        AgasMcpAiJobStore store,
        Func<Exception, AgasMcpAiJobFailure>? failureMapper = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _failureMapper = failureMapper ?? MapFailure;
        _coordinator.CapacityAvailable += OnCapacityAvailable;
    }

    public int QueuedCount
    {
        get
        {
            lock (_gate)
            {
                return _queued.Count;
            }
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _active.Count;
            }
        }
    }

    /// <summary>
    /// Raised after a job transition is committed. Subscribers must marshal
    /// UI work to the Godot main thread; scheduler runners invoke this event
    /// from worker threads.
    /// </summary>
    public event Action<AgasMcpAiJobSnapshot>? JobStateChanged;
    public event Action? ActivityChanged;

    public void AllowNewWork()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AgasMcpAiJobScheduler));
            }

            _accepting = true;
        }
    }

    public AgasMcpAiJobSubmitResult Submit(AgasMcpAiJobSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(submission.Runner);
        if (string.IsNullOrWhiteSpace(submission.ProjectScope.ProjectRoot)
            || string.IsNullOrWhiteSpace(submission.ProjectScope.ProjectId))
        {
            throw new ArgumentException("A normalized project scope is required.", nameof(submission));
        }
        if (string.IsNullOrWhiteSpace(submission.EffectiveOperation))
        {
            throw new ArgumentException("Effective operation is required.", nameof(submission));
        }

        WorkStart? start = null;
        IAgasAiGenerationPermit? unusedPermit = null;
        AgasMcpAiJobSnapshot initial;
        lock (_gate)
        {
            if (_disposed || !_accepting)
            {
                return AgasMcpAiJobSubmitResult.Reject(AgasMcpAiJobSubmitRejectReason.Stopped);
            }

            // Once an MCP job is queued, later MCP submissions must join the
            // tail even if they race a permit-release callback. Otherwise the
            // newer submission can steal the freshly released permit before
            // OnCapacityAvailable has a chance to start the FIFO head.
            if (_queue.Count > 0)
            {
                if (_queued.Count >= QueueCapacity)
                {
                    return AgasMcpAiJobSubmitResult.Reject(AgasMcpAiJobSubmitRejectReason.Busy);
                }

                initial = _store.CreateQueued(submission.ProjectScope, submission.EffectiveOperation, submission.Execution);
                var queuedBehindExisting = new WorkItem(initial.JobId, submission.ProjectScope, submission.Runner);
                AddQueuedLastLocked(queuedBehindExisting);
                return AgasMcpAiJobSubmitResult.Accept(initial);
            }

            if (!_coordinator.TryAcquire(out IAgasAiGenerationPermit? permit))
            {
                if (_queued.Count >= QueueCapacity)
                {
                    return AgasMcpAiJobSubmitResult.Reject(AgasMcpAiJobSubmitRejectReason.Busy);
                }

                initial = _store.CreateQueued(submission.ProjectScope, submission.EffectiveOperation, submission.Execution);
                var queued = new WorkItem(initial.JobId, submission.ProjectScope, submission.Runner);
                AddQueuedLastLocked(queued);
                return AgasMcpAiJobSubmitResult.Accept(initial);
            }

            initial = _store.CreateQueued(submission.ProjectScope, submission.EffectiveOperation, submission.Execution);
            var immediate = new WorkItem(initial.JobId, submission.ProjectScope, submission.Runner);
            start = PrepareStartLocked(immediate, permit!);
            unusedPermit = start == null ? permit : null;
        }

        unusedPermit?.Dispose();
        Launch(start);
        return AgasMcpAiJobSubmitResult.Accept(initial);
    }

    public AgasMcpAiJobCancelResult Cancel(string jobId)
    {
        return CancelCore(projectScope: null, jobId);
    }

    public AgasMcpAiJobCancelResult Cancel(AgasMcpAiProjectScope projectScope, string jobId)
    {
        return CancelCore(projectScope, jobId);
    }

    private AgasMcpAiJobCancelResult CancelCore(
        AgasMcpAiProjectScope? projectScope,
        string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("Job id is required.", nameof(jobId));
        }

        CancellationTokenSource? cancellation = null;
        bool canceled;
        AgasMcpAiJobSnapshot? snapshot;
        lock (_gate)
        {
            if (projectScope.HasValue
                && !_store.TryGet(projectScope.Value, jobId, out _))
            {
                return new AgasMcpAiJobCancelResult(false, false, null);
            }
            canceled = _store.TryCancel(jobId, out snapshot);
            if (snapshot == null)
            {
                return new AgasMcpAiJobCancelResult(false, false, null);
            }

            if (canceled)
            {
                if (_queued.TryGetValue(snapshot.JobId, out WorkItem? queued))
                {
                    RemoveQueuedLocked(queued, dispose: true);
                    CompleteProjectQuiescenceIfIdleLocked(queued.ProjectScope);
                }
                if (_active.TryGetValue(snapshot.JobId, out ActiveWork? active))
                {
                    cancellation = active.Item.Cancellation;
                }
            }
        }

        SafeCancel(cancellation);
        if (canceled && snapshot != null)
        {
            PublishJobState(snapshot);
        }
        return new AgasMcpAiJobCancelResult(true, canceled, snapshot);
    }

    public int CancelProject(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException("Project id is required.", nameof(projectId));
        }
        return CancelMatching(item => string.Equals(item.ProjectId, projectId, StringComparison.Ordinal));
    }

    public int CancelProject(AgasMcpAiProjectScope projectScope) =>
        CancelMatching(item => item.ProjectScope == projectScope);

    public int CancelAll() => CancelMatching(_ => true);

    public Task WaitForProjectQuiescenceAsync(
        AgasMcpAiProjectScope projectScope,
        CancellationToken cancellationToken = default)
    {
        Task quiescence;
        lock (_gate)
        {
            quiescence = GetProjectQuiescenceTaskLocked(projectScope);
        }

        return quiescence.WaitAsync(cancellationToken);
    }

    public Task WaitForProjectQuiescenceAsync(
        AgasMcpAiProjectScope projectScope,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        Task quiescence;
        lock (_gate)
        {
            quiescence = GetProjectQuiescenceTaskLocked(projectScope);
        }

        return quiescence.WaitAsync(timeout, cancellationToken);
    }

    public async Task<int> CancelProjectAndWaitForQuiescenceAsync(
        AgasMcpAiProjectScope projectScope,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        CancelBatch batch;
        Task quiescence;
        lock (_gate)
        {
            batch = CancelMatchingLocked(item => item.ProjectScope == projectScope);
            quiescence = GetProjectQuiescenceTaskLocked(projectScope);
        }

        SignalCancellationsAndPublish(batch);
        await quiescence.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        return batch.CanceledCount;
    }

    private int CancelMatching(Func<WorkItem, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        CancelBatch batch;
        lock (_gate)
        {
            batch = CancelMatchingLocked(matches);
        }

        SignalCancellationsAndPublish(batch);
        return batch.CanceledCount;
    }

    private CancelBatch CancelMatchingLocked(Func<WorkItem, bool> matches)
    {
        List<CancellationTokenSource> cancellations = new();
        List<AgasMcpAiJobSnapshot> notifications = new();
        HashSet<AgasMcpAiProjectScope> affectedScopes = new();
        int canceled = 0;
        foreach (WorkItem item in _queued.Values.ToArray())
        {
            if (!matches(item)) continue;
            affectedScopes.Add(item.ProjectScope);
            if (_store.TryCancel(item.JobId, out AgasMcpAiJobSnapshot? canceledJob))
            {
                canceled++;
                if (canceledJob != null) notifications.Add(canceledJob);
            }
            RemoveQueuedLocked(item, dispose: true);
        }

        foreach (ActiveWork active in _active.Values)
        {
            if (!matches(active.Item)) continue;
            affectedScopes.Add(active.Item.ProjectScope);
            if (_store.TryCancel(active.Item.JobId, out AgasMcpAiJobSnapshot? canceledJob))
            {
                canceled++;
                cancellations.Add(active.Item.Cancellation);
                if (canceledJob != null) notifications.Add(canceledJob);
            }
        }

        foreach (AgasMcpAiProjectScope affectedScope in affectedScopes)
        {
            CompleteProjectQuiescenceIfIdleLocked(affectedScope);
        }
        return new CancelBatch(canceled, cancellations, notifications);
    }

    private void SignalCancellationsAndPublish(CancelBatch batch)
    {
        foreach (CancellationTokenSource cancellation in batch.Cancellations)
        {
            SafeCancel(cancellation);
        }
        foreach (AgasMcpAiJobSnapshot notification in batch.Notifications)
        {
            PublishJobState(notification);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task quiescence;
        lock (_gate)
        {
            _accepting = false;
        }

        CancelAll();
        lock (_gate)
        {
            quiescence = _quiescence.Task;
        }
        await quiescence.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _accepting = false;
        }

        _coordinator.CapacityAvailable -= OnCapacityAvailable;
        await StopAsync().ConfigureAwait(false);
    }

    private void OnCapacityAvailable()
    {
        List<WorkStart> starts = new();
        List<IAgasAiGenerationPermit> unusedPermits = new();
        lock (_gate)
        {
            if (_disposed || !_accepting)
            {
                return;
            }

            while (TryTakeNextLiveQueuedLocked(out WorkItem? item))
            {
                WorkItem queuedItem = item!;
                if (!_coordinator.TryAcquire(out IAgasAiGenerationPermit? permit))
                {
                    _queue.AddFirst(queuedItem);
                    queuedItem.QueueNode = _queue.First;
                    _queued.Add(queuedItem.JobId, queuedItem);
                    break;
                }

                WorkStart? start = PrepareStartLocked(queuedItem, permit!);
                if (start != null)
                {
                    starts.Add(start);
                }
                else
                {
                    unusedPermits.Add(permit!);
                }
            }
        }

        foreach (IAgasAiGenerationPermit permit in unusedPermits)
        {
            permit.Dispose();
        }
        foreach (WorkStart start in starts)
        {
            Launch(start);
        }
    }

    private bool TryTakeNextLiveQueuedLocked(out WorkItem? item)
    {
        if (_queue.Count > 0)
        {
            WorkItem candidate = _queue.First!.Value;
            _queue.RemoveFirst();
            candidate.QueueNode = null;
            _queued.Remove(candidate.JobId);
            item = candidate;
            return true;
        }

        item = null;
        return false;
    }

    private void AddQueuedLastLocked(WorkItem item)
    {
        item.QueueNode = _queue.AddLast(item);
        _queued.Add(item.JobId, item);
    }

    private void RemoveQueuedLocked(WorkItem item, bool dispose)
    {
        _queued.Remove(item.JobId);
        if (item.QueueNode != null)
        {
            _queue.Remove(item.QueueNode);
            item.QueueNode = null;
        }
        if (dispose)
        {
            item.Cancellation.Dispose();
        }
    }

    private WorkStart? PrepareStartLocked(WorkItem item, IAgasAiGenerationPermit permit)
    {
        if (!_store.TryMarkRunning(item.JobId, out AgasMcpAiJobSnapshot? running)
            || running == null)
        {
            CompleteProjectQuiescenceIfIdleLocked(item.ProjectScope);
            return null;
        }

        EnsureQuiescencePendingLocked();
        var active = new ActiveWork(item, permit);
        _active.Add(item.JobId, active);
        return new WorkStart(active, running);
    }

    private void Launch(WorkStart? start)
    {
        if (start != null)
        {
            PublishJobState(start.Job);
            start.Active.Task = Task.Run(() => RunAsync(start.Active));
        }
    }

    private async Task RunAsync(ActiveWork active)
    {
        AgasMcpAiJobRunResult? result = null;
        AgasMcpAiJobSnapshot? notification = null;
        try
        {
            result = await active.Item.Runner(active.Item.Cancellation.Token).ConfigureAwait(false);
            if (result == null)
            {
                throw new InvalidOperationException("The AI job runner returned no result.");
            }

            bool stored = _store.TrySucceed(
                active.Item.JobId,
                result.Metadata,
                result.ApplicationState,
                result.EffectiveOperation,
                out notification);
            if (!stored && result.DiscardAsync != null)
            {
                await result.DiscardAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (active.Item.Cancellation.IsCancellationRequested)
        {
            _store.TryCancel(active.Item.JobId, out notification);
        }
        catch (Exception exception)
        {
            AgasMcpAiJobFailure failure;
            try
            {
                failure = _failureMapper(exception) ?? DefaultFailure;
            }
            catch
            {
                failure = DefaultFailure;
            }
            _store.TryFail(active.Item.JobId, failure, out notification);
        }
        finally
        {
            if (notification != null)
            {
                // Observers enqueue main-thread work. Publish before releasing
                // the permit so a queued successor's Running notification is
                // ordered after this terminal state; the callback itself runs
                // after the permit accounting below has settled.
                PublishJobState(notification);
            }

            // Quiescence means the shared budget is actually released, not merely
            // that the runner left its try block.
            active.Item.Cancellation.Dispose();
            active.Permit.Dispose();

            lock (_gate)
            {
                _active.Remove(active.Item.JobId);
                CompleteQuiescenceIfIdleLocked();
                CompleteProjectQuiescenceIfIdleLocked(active.Item.ProjectScope);
            }
            PublishActivityChanged();
        }
    }

    private void PublishJobState(AgasMcpAiJobSnapshot job)
    {
        Action<AgasMcpAiJobSnapshot>? subscribers = JobStateChanged;
        if (subscribers == null)
        {
            return;
        }

        foreach (Action<AgasMcpAiJobSnapshot> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(job);
            }
            catch
            {
                // Observers must never break scheduler state transitions.
            }
        }
    }

    private void PublishActivityChanged()
    {
        Action? subscribers = ActivityChanged;
        if (subscribers == null)
        {
            return;
        }

        foreach (Action subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber();
            }
            catch
            {
                // Activity observers are informational and cannot affect jobs.
            }
        }
    }

    private void EnsureQuiescencePendingLocked()
    {
        if (_quiescence.Task.IsCompleted)
        {
            _quiescence = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void CompleteQuiescenceIfIdleLocked()
    {
        if (_active.Count == 0)
        {
            _quiescence.TrySetResult(null);
        }
    }

    private Task GetProjectQuiescenceTaskLocked(AgasMcpAiProjectScope projectScope)
    {
        if (!HasProjectWorkLocked(projectScope))
        {
            return Task.CompletedTask;
        }

        if (!_projectQuiescence.TryGetValue(projectScope, out TaskCompletionSource<object?>? completion))
        {
            completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _projectQuiescence.Add(projectScope, completion);
        }
        return completion.Task;
    }

    private bool HasProjectWorkLocked(AgasMcpAiProjectScope projectScope) =>
        _queued.Values.Any(item => item.ProjectScope == projectScope)
        || _active.Values.Any(active => active.Item.ProjectScope == projectScope);

    private void CompleteProjectQuiescenceIfIdleLocked(AgasMcpAiProjectScope projectScope)
    {
        if (HasProjectWorkLocked(projectScope)
            || !_projectQuiescence.Remove(projectScope, out TaskCompletionSource<object?>? completion))
        {
            return;
        }

        completion.TrySetResult(null);
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive or infinite.");
        }
    }

    private static TaskCompletionSource<object?> CompletedQuiescence()
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult(null);
        return completion;
    }

    private static AgasMcpAiJobFailure MapFailure(Exception exception) =>
        exception is AgasMcpAiJobExecutionException jobFailure
            ? jobFailure.Failure
            : DefaultFailure;

    private static void SafeCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation == null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion won after the state transition but before token signaling.
        }
    }

    private sealed class WorkItem
    {
        public WorkItem(
            string jobId,
            AgasMcpAiProjectScope projectScope,
            AgasMcpAiJobRunner runner)
        {
            JobId = jobId;
            ProjectScope = projectScope;
            Runner = runner;
        }

        public string JobId { get; }
        public AgasMcpAiProjectScope ProjectScope { get; }
        public string ProjectId => ProjectScope.ProjectId;
        public AgasMcpAiJobRunner Runner { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public LinkedListNode<WorkItem>? QueueNode { get; set; }
    }

    private sealed class ActiveWork
    {
        public ActiveWork(WorkItem item, IAgasAiGenerationPermit permit)
        {
            Item = item;
            Permit = permit;
        }

        public WorkItem Item { get; }
        public IAgasAiGenerationPermit Permit { get; }
        public Task? Task { get; set; }
    }

    private sealed class WorkStart
    {
        public WorkStart(ActiveWork active, AgasMcpAiJobSnapshot job)
        {
            Active = active;
            Job = job;
        }

        public ActiveWork Active { get; }
        public AgasMcpAiJobSnapshot Job { get; }
    }

    private sealed record CancelBatch(
        int CanceledCount,
        IReadOnlyList<CancellationTokenSource> Cancellations,
        IReadOnlyList<AgasMcpAiJobSnapshot> Notifications);
}
