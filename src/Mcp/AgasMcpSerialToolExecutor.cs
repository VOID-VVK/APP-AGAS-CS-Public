using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace AppAgasCs.Mcp;

public interface IAgasMcpToolExecutor
{
    Task<T> ExecuteAsync<T>(
        string? sessionId,
        TimeSpan timeout,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(sessionId, operation, cancellationToken);

    Task<T> ExecuteAsync<T>(
        string? sessionId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);

    Task ExecuteAsync(
        string? sessionId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    Task ExecuteAsync(
        string? sessionId,
        TimeSpan timeout,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(sessionId, operation, cancellationToken);

    void CancelPending()
    {
    }

    void BeginRejectingNewWork()
    {
    }

    void AllowNewWork()
    {
    }

    void CancelActiveAndPending()
    {
    }

    Task RejectNewWorkAndCancelActiveAndPendingAsync(CancellationToken cancellationToken = default)
    {
        CancelActiveAndPending();
        return Task.CompletedTask;
    }

    Task CancelSessionAsync(string? sessionId, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    void Stop();
}

public sealed class AgasMcpSerialToolExecutor : IAgasMcpToolExecutor, IAsyncDisposable, IDisposable
{
    public const int QueueCapacity = 32;
    public const int PerSessionCallsPerMinute = 120;
    public static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentQueue<IQueuedToolCall> _queue = new();
    private readonly SemaphoreSlim _queueSlots = new(QueueCapacity, QueueCapacity);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly Task _worker;
    private readonly TimeSpan _toolTimeout;
    private readonly object _lifecycleLock = new();
    private readonly object _rateLock = new();
    private readonly Dictionary<string, RateWindow> _rateWindows = new(StringComparer.Ordinal);

    private bool _accepting;
    private bool _stopped;
    private IQueuedToolCall? _activeCall;
    private TaskCompletionSource<object?> _quiescence = CompletedQuiescence();

    public AgasMcpSerialToolExecutor(TimeSpan? toolTimeout = null)
    {
        _toolTimeout = toolTimeout ?? ToolTimeout;
        if (_toolTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(toolTimeout), "Tool timeout must be greater than zero.");
        }

        _worker = Task.Run(ProcessQueueAsync);
    }

    public Task ExecuteAsync(
        string? sessionId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteAsync<object?>(sessionId, _toolTimeout, async token =>
        {
            await operation(token);
            return null;
        }, cancellationToken);
    }

    public Task<T> ExecuteAsync<T>(
        string? sessionId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(sessionId, _toolTimeout, operation, cancellationToken);

    public Task ExecuteAsync(
        string? sessionId,
        TimeSpan timeout,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteAsync<object?>(sessionId, timeout, async token =>
        {
            await operation(token);
            return null;
        }, cancellationToken);
    }

    public Task<T> ExecuteAsync<T>(
        string? sessionId,
        TimeSpan timeout,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Tool timeout must be greater than zero.");

        lock (_lifecycleLock)
        {
            if (_stopped || !_accepting || _disposeCancellation.IsCancellationRequested)
            {
                throw new AgasMcpToolExecutionRejectedException(AgasMcpToolExecutionRejectReason.Stopped);
            }

            if (!_queueSlots.Wait(0, cancellationToken))
            {
                throw new AgasMcpToolExecutionRejectedException(AgasMcpToolExecutionRejectReason.Busy);
            }

            try
            {
                string normalizedSessionId = NormalizeSessionId(sessionId);
                if (!TryConsumeRate(normalizedSessionId))
                {
                    throw new AgasMcpToolExecutionRejectedException(AgasMcpToolExecutionRejectReason.RateLimited);
                }

                var call = new QueuedToolCall<T>(normalizedSessionId, operation, cancellationToken, timeout);
                EnsureQuiescencePendingLocked();
                _queue.Enqueue(call);
                _signal.Release();
                return call.Task;
            }
            catch
            {
                _queueSlots.Release();
                throw;
            }
        }
    }

    public void CancelPending()
    {
        lock (_lifecycleLock)
        {
            CancelPendingLocked(AgasMcpToolExecutionRejectReason.Stopped);
            CompleteQuiescenceIfIdleLocked();
        }
    }

    public void BeginRejectingNewWork()
    {
        lock (_lifecycleLock)
        {
            _accepting = false;
        }
    }

    public void AllowNewWork()
    {
        lock (_lifecycleLock)
        {
            if (_stopped || _disposeCancellation.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(AgasMcpSerialToolExecutor));
            }

            _accepting = true;
        }
    }

    public void CancelActiveAndPending()
    {
        lock (_lifecycleLock)
        {
            _activeCall?.Cancel(AgasMcpToolExecutionRejectReason.Stopped);
            CancelPendingLocked(AgasMcpToolExecutionRejectReason.Stopped);
            CompleteQuiescenceIfIdleLocked();
        }

        _signal.Release();
    }

    public async Task RejectNewWorkAndCancelActiveAndPendingAsync(CancellationToken cancellationToken = default)
    {
        Task quiescenceTask;
        lock (_lifecycleLock)
        {
            _accepting = false;
            _activeCall?.Cancel(AgasMcpToolExecutionRejectReason.Stopped);
            CancelPendingLocked(AgasMcpToolExecutionRejectReason.Stopped);
            ClearAllRateWindows();
            CompleteQuiescenceIfIdleLocked();
            quiescenceTask = _quiescence.Task;
        }

        _signal.Release();
        await quiescenceTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelSessionAsync(string? sessionId, CancellationToken cancellationToken = default)
    {
        string normalizedSessionId = NormalizeSessionId(sessionId);
        Task? quiescenceTask = null;
        lock (_lifecycleLock)
        {
            bool activeMatches = _activeCall?.HasSession(normalizedSessionId) == true;
            if (activeMatches)
            {
                _activeCall?.Cancel(AgasMcpToolExecutionRejectReason.Stopped);
                quiescenceTask = _quiescence.Task;
            }

            CancelPendingForSessionLocked(normalizedSessionId, AgasMcpToolExecutionRejectReason.Stopped);
            ClearRateWindow(normalizedSessionId);
            CompleteQuiescenceIfIdleLocked();
        }

        _signal.Release();
        if (quiescenceTask != null)
        {
            await quiescenceTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            _accepting = false;
            _disposeCancellation.Cancel();
            _activeCall?.Cancel(AgasMcpToolExecutionRejectReason.Stopped);
            CancelPendingLocked(AgasMcpToolExecutionRejectReason.Stopped);
            ClearAllRateWindows();
            CompleteQuiescenceIfIdleLocked();
        }

        _signal.Release();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _signal.Dispose();
        _queueSlots.Dispose();
        _disposeCancellation.Dispose();
    }

    private async Task ProcessQueueAsync()
    {
        while (true)
        {
            try
            {
                await _signal.WaitAsync(_disposeCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
            {
                return;
            }

            IQueuedToolCall? call;
            lock (_lifecycleLock)
            {
                if (_disposeCancellation.IsCancellationRequested)
                {
                    return;
                }

                if (!_queue.TryDequeue(out call))
                {
                    continue;
                }

                _queueSlots.Release();
                _activeCall = call;
            }

            try
            {
                await call.ExecuteAsync(_disposeCancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_lifecycleLock)
                {
                    if (ReferenceEquals(_activeCall, call))
                    {
                        _activeCall = null;
                    }
                    CompleteQuiescenceIfIdleLocked();
                }
            }
        }
    }

    private void CancelPendingLocked(AgasMcpToolExecutionRejectReason reason)
    {
        while (_queue.TryDequeue(out IQueuedToolCall? queued))
        {
            _queueSlots.Release();
            queued.Cancel(reason);
            queued.Dispose();
        }
    }

    private void CancelPendingForSessionLocked(string sessionId, AgasMcpToolExecutionRejectReason reason)
    {
        var kept = new Queue<IQueuedToolCall>();
        while (_queue.TryDequeue(out IQueuedToolCall? queued))
        {
            if (queued.HasSession(sessionId))
            {
                _queueSlots.Release();
                queued.Cancel(reason);
                queued.Dispose();
            }
            else
            {
                kept.Enqueue(queued);
            }
        }

        while (kept.TryDequeue(out IQueuedToolCall? queued))
        {
            _queue.Enqueue(queued);
        }
    }

    private bool TryConsumeRate(string sessionId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_rateLock)
        {
            PruneExpiredRateWindowsLocked(now);
            if (!_rateWindows.TryGetValue(sessionId, out RateWindow window)
                || now - window.StartedAt >= TimeSpan.FromMinutes(1))
            {
                _rateWindows[sessionId] = new RateWindow(now, 1);
                return true;
            }

            if (window.Count >= PerSessionCallsPerMinute)
            {
                return false;
            }

            _rateWindows[sessionId] = window with { Count = window.Count + 1 };
            return true;
        }
    }

    private void ClearRateWindow(string sessionId)
    {
        lock (_rateLock)
        {
            _rateWindows.Remove(sessionId);
        }
    }

    private void ClearAllRateWindows()
    {
        lock (_rateLock)
        {
            _rateWindows.Clear();
        }
    }

    private void PruneExpiredRateWindowsLocked(DateTimeOffset now)
    {
        List<string>? expired = null;
        foreach ((string sessionId, RateWindow window) in _rateWindows)
        {
            if (now - window.StartedAt >= TimeSpan.FromMinutes(1))
            {
                expired ??= new List<string>();
                expired.Add(sessionId);
            }
        }

        if (expired == null)
        {
            return;
        }

        foreach (string sessionId in expired)
        {
            _rateWindows.Remove(sessionId);
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
        if (_activeCall == null && _queue.IsEmpty)
        {
            _quiescence.TrySetResult(null);
        }
    }

    private static TaskCompletionSource<object?> CompletedQuiescence()
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult(null);
        return completion;
    }

    private static string NormalizeSessionId(string? sessionId)
    {
        return string.IsNullOrWhiteSpace(sessionId) ? "_missing_session" : sessionId.Trim();
    }

    private interface IQueuedToolCall : IDisposable
    {
        bool HasSession(string sessionId);
        Task ExecuteAsync(CancellationToken stopCancellationToken);
        void Cancel(AgasMcpToolExecutionRejectReason reason);
    }

    private sealed class QueuedToolCall<T> : IQueuedToolCall
    {
        private readonly object _stateLock = new();
        private readonly string _sessionId;
        private readonly Func<CancellationToken, Task<T>> _operation;
        private readonly CancellationToken _callerCancellationToken;
        private readonly TimeSpan _toolTimeout;
        private readonly object _executorCancellationLock = new();
        private readonly CancellationTokenSource _executorCancellation = new();
        private readonly CancellationTokenRegistration _callerCancellationRegistration;
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _started;
        private bool _completed;
        private bool _executorCancellationCanceled;
        private bool _executorCancellationDisposed;

        public QueuedToolCall(
            string sessionId,
            Func<CancellationToken, Task<T>> operation,
            CancellationToken callerCancellationToken,
            TimeSpan toolTimeout)
        {
            _sessionId = sessionId;
            _operation = operation;
            _callerCancellationToken = callerCancellationToken;
            _toolTimeout = toolTimeout;
            if (callerCancellationToken.CanBeCanceled)
            {
                _callerCancellationRegistration = callerCancellationToken.Register(static state =>
                {
                    var call = (QueuedToolCall<T>)state!;
                    call.TryCompleteBeforeStart(() => call._completion.TrySetCanceled(call._callerCancellationToken));
                }, this);
            }
        }

        public Task<T> Task => _completion.Task;

        public bool HasSession(string sessionId) => string.Equals(_sessionId, sessionId, StringComparison.Ordinal);

        public async Task ExecuteAsync(CancellationToken stopCancellationToken)
        {
            using CancellationTokenSource timeoutCancellation = new(_toolTimeout);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                stopCancellationToken,
                _callerCancellationToken,
                _executorCancellation.Token,
                timeoutCancellation.Token);

            try
            {
                if (!TryMarkStarted())
                {
                    return;
                }

                if (linked.IsCancellationRequested)
                {
                    if (timeoutCancellation.IsCancellationRequested
                        && !stopCancellationToken.IsCancellationRequested
                        && !_executorCancellation.IsCancellationRequested
                        && !_callerCancellationToken.IsCancellationRequested)
                    {
                        TryCompleteAfterStart(() => _completion.TrySetException(new TimeoutException("AGAS MCP tool execution timed out.")));
                    }
                    else if (stopCancellationToken.IsCancellationRequested || _executorCancellation.IsCancellationRequested)
                    {
                        TryCompleteAfterStart(() => _completion.TrySetException(new AgasMcpToolExecutionRejectedException(AgasMcpToolExecutionRejectReason.Stopped)));
                    }
                    else
                    {
                        TryCompleteAfterStart(() => _completion.TrySetCanceled(linked.Token));
                    }

                    return;
                }

                T result = await _operation(linked.Token).ConfigureAwait(false);
                TryCompleteAfterStart(() => _completion.TrySetResult(result));
            }
            catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested
                                                     && !stopCancellationToken.IsCancellationRequested
                                                     && !_executorCancellation.IsCancellationRequested
                                                     && !_callerCancellationToken.IsCancellationRequested)
            {
                TryCompleteAfterStart(() => _completion.TrySetException(new TimeoutException("AGAS MCP tool execution timed out.")));
            }
            catch (OperationCanceledException) when (stopCancellationToken.IsCancellationRequested
                                                     || _executorCancellation.IsCancellationRequested)
            {
                TryCompleteAfterStart(() => _completion.TrySetException(new AgasMcpToolExecutionRejectedException(AgasMcpToolExecutionRejectReason.Stopped)));
            }
            catch (OperationCanceledException ex)
            {
                TryCompleteAfterStart(() => _completion.TrySetCanceled(ex.CancellationToken));
            }
            catch (Exception ex)
            {
                TryCompleteAfterStart(() => _completion.TrySetException(ex));
            }
            finally
            {
                _callerCancellationRegistration.Dispose();
                DisposeExecutorCancellation();
            }
        }

        public void Cancel(AgasMcpToolExecutionRejectReason reason)
        {
            CancelExecutorCancellation();
            TryCompleteBeforeStart(() => _completion.TrySetException(new AgasMcpToolExecutionRejectedException(reason)));
        }

        public void Dispose()
        {
            _callerCancellationRegistration.Dispose();
            DisposeExecutorCancellation();
        }

        private void CancelExecutorCancellation()
        {
            lock (_executorCancellationLock)
            {
                if (_executorCancellationCanceled || _executorCancellationDisposed)
                {
                    return;
                }

                _executorCancellationCanceled = true;
                _executorCancellation.Cancel();
            }
        }

        private void DisposeExecutorCancellation()
        {
            lock (_executorCancellationLock)
            {
                if (_executorCancellationDisposed)
                {
                    return;
                }

                _executorCancellationDisposed = true;
                _executorCancellation.Dispose();
            }
        }

        private bool TryMarkStarted()
        {
            lock (_stateLock)
            {
                if (_completed)
                {
                    return false;
                }

                _started = true;
                return true;
            }
        }

        private bool TryCompleteBeforeStart(Func<bool> complete)
        {
            lock (_stateLock)
            {
                if (_started || _completed)
                {
                    return false;
                }

                _completed = true;
            }

            return complete();
        }

        private void TryCompleteAfterStart(Func<bool> complete)
        {
            lock (_stateLock)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
            }

            complete();
        }
    }

    private readonly record struct RateWindow(DateTimeOffset StartedAt, int Count);
}

public sealed class AgasMcpToolExecutionRejectedException : Exception
{
    public AgasMcpToolExecutionRejectedException(AgasMcpToolExecutionRejectReason reason)
        : base(reason switch
        {
            AgasMcpToolExecutionRejectReason.Busy => "AGAS_BUSY",
            AgasMcpToolExecutionRejectReason.RateLimited => "AGAS_RATE_LIMITED",
            AgasMcpToolExecutionRejectReason.Stopped => "AGAS_STOPPED",
            AgasMcpToolExecutionRejectReason.Timeout => "AGAS_TIMEOUT",
            _ => "AGAS_REJECTED"
        })
    {
        Reason = reason;
    }

    public AgasMcpToolExecutionRejectReason Reason { get; }
}

public enum AgasMcpToolExecutionRejectReason
{
    Busy,
    RateLimited,
    Stopped,
    Timeout
}
