using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;

[assembly: InternalsVisibleTo("APP-AGAS-CS.Core.Tests")]

namespace AppAgasCs.Mcp;

public partial class AgasMcpMainThreadDispatcher : Node
{
    private readonly AgasMcpMainThreadDispatcherQueue _dispatcherQueue = new();

    public override void _Process(double delta)
    {
        _dispatcherQueue.Process();
    }

    public override void _ExitTree()
    {
        _dispatcherQueue.Exit();
    }

    public Task InvokeAsync(Action<CancellationToken> action, CancellationToken cancellationToken = default)
    {
        return _dispatcherQueue.InvokeAsync(action, cancellationToken);
    }

    public Task<T> InvokeAsync<T>(Func<CancellationToken, T> action, CancellationToken cancellationToken = default)
    {
        return _dispatcherQueue.InvokeAsync(action, cancellationToken);
    }
}

internal sealed class AgasMcpMainThreadDispatcherQueue
{
    private readonly ConcurrentQueue<IQueuedCall> _queue = new();
    private readonly CancellationTokenSource _exitCancellation = new();
    private readonly object _lifecycleLock = new();
    private bool _exited;

    public void Process()
    {
        while (true)
        {
            IQueuedCall? call;
            lock (_lifecycleLock)
            {
                if (_exited || !_queue.TryDequeue(out call))
                {
                    return;
                }
            }

            call.Execute(_exitCancellation.Token);
        }
    }

    public void Exit()
    {
        lock (_lifecycleLock)
        {
            if (_exited)
            {
                return;
            }

            _exited = true;
            _exitCancellation.Cancel();
            while (_queue.TryDequeue(out IQueuedCall? call))
            {
                call.Cancel();
            }
        }
    }

    public Task InvokeAsync(Action<CancellationToken> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeAsync<object?>(token =>
        {
            action(token);
            return null;
        }, cancellationToken);
    }

    public Task<T> InvokeAsync<T>(Func<CancellationToken, T> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        var call = new QueuedCall<T>(action, cancellationToken);
        lock (_lifecycleLock)
        {
            if (_exited || _exitCancellation.IsCancellationRequested)
            {
                call.Cancel();
                return call.Task;
            }

            _queue.Enqueue(call);
        }

        return call.Task;
    }

    private interface IQueuedCall
    {
        void Execute(CancellationToken exitCancellationToken);
        void Cancel();
    }

    private sealed class QueuedCall<T> : IQueuedCall
    {
        private readonly object _stateLock = new();
        private readonly Func<CancellationToken, T> _action;
        private readonly CancellationToken _callerCancellationToken;
        private readonly CancellationTokenRegistration _callerCancellationRegistration;
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _started;
        private bool _completed;

        public QueuedCall(Func<CancellationToken, T> action, CancellationToken callerCancellationToken)
        {
            _action = action;
            _callerCancellationToken = callerCancellationToken;
            if (callerCancellationToken.CanBeCanceled)
            {
                _callerCancellationRegistration = callerCancellationToken.Register(static state =>
                {
                    var call = (QueuedCall<T>)state!;
                    call.TryCompleteBeforeStart(() => call._completion.TrySetCanceled(call._callerCancellationToken));
                }, this);
            }
        }

        public Task<T> Task => _completion.Task;

        public void Execute(CancellationToken exitCancellationToken)
        {
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(exitCancellationToken, _callerCancellationToken);

            try
            {
                if (!TryMarkStarted())
                {
                    return;
                }

                if (linked.IsCancellationRequested)
                {
                    if (exitCancellationToken.IsCancellationRequested)
                    {
                        TryCompleteAfterStart(() => _completion.TrySetException(new AgasMcpToolExecutionRejectedException(AgasMcpToolExecutionRejectReason.Stopped)));
                    }
                    else
                    {
                        TryCompleteAfterStart(() => _completion.TrySetCanceled(linked.Token));
                    }

                    return;
                }

                T result = _action(linked.Token);
                TryCompleteAfterStart(() => _completion.TrySetResult(result));
            }
            catch (OperationCanceledException) when (exitCancellationToken.IsCancellationRequested)
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
            }
        }

        public void Cancel()
        {
            TryCompleteBeforeStart(() => _completion.TrySetException(new AgasMcpToolExecutionRejectedException(AgasMcpToolExecutionRejectReason.Stopped)));
            _callerCancellationRegistration.Dispose();
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
}
