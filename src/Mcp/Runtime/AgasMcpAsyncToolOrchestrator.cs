#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace AppAgasCs.Mcp.Runtime;

/// <summary>
/// Generic capture/background/recheck/completion pipeline. Callers remain responsible for
/// dispatching capture, recheck, and engine-facing completion delegates to the main thread.
/// </summary>
public sealed class AgasMcpAsyncToolOrchestrator
{
    public async ValueTask<TResult> ExecuteAsync<TCapture, TPrepared, TResult>(
        AgasMcpCommitExecution execution,
        Func<CancellationToken, ValueTask<TCapture>> captureAsync,
        Func<TCapture, CancellationToken, ValueTask<TPrepared>> backgroundAsync,
        Func<TCapture, TPrepared, CancellationToken, ValueTask> recheckAsync,
        Func<TCapture, TPrepared, CancellationToken, ValueTask<TResult>> completeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(captureAsync);
        ArgumentNullException.ThrowIfNull(backgroundAsync);
        ArgumentNullException.ThrowIfNull(recheckAsync);
        ArgumentNullException.ThrowIfNull(completeAsync);

        using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
            static state => ((AgasMcpCommitExecution)state!).TryCancel(),
            execution);

        ThrowIfCanceled(execution, cancellationToken);
        TCapture capture = await captureAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfCanceled(execution, cancellationToken);

        TPrepared prepared = await backgroundAsync(capture, cancellationToken).ConfigureAwait(false);
        ThrowIfCanceled(execution, cancellationToken);

        await recheckAsync(capture, prepared, cancellationToken).ConfigureAwait(false);
        ThrowIfCanceled(execution, cancellationToken);

        return execution.Policy switch
        {
            AgasMcpCommitPolicy.None => await CompleteWithoutCommitAsync(
                execution,
                capture,
                prepared,
                completeAsync,
                cancellationToken).ConfigureAwait(false),
            AgasMcpCommitPolicy.ShortAtomicCommit => await CompleteCommittedAsync(
                execution,
                capture,
                prepared,
                completeAsync,
                execution.TryBeginShortAtomicCommit,
                cancellationToken).ConfigureAwait(false),
            AgasMcpCommitPolicy.StartedMainThreadCompletion => await CompleteCommittedAsync(
                execution,
                capture,
                prepared,
                completeAsync,
                execution.TryBeginMainThreadCompletion,
                cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unknown MCP commit policy '{execution.Policy}'."),
        };
    }

    private static async ValueTask<TResult> CompleteWithoutCommitAsync<TCapture, TPrepared, TResult>(
        AgasMcpCommitExecution execution,
        TCapture capture,
        TPrepared prepared,
        Func<TCapture, TPrepared, CancellationToken, ValueTask<TResult>> completeAsync,
        CancellationToken cancellationToken)
    {
        TResult result = await completeAsync(capture, prepared, cancellationToken).ConfigureAwait(false);
        if (!execution.TryCompleteWithoutCommit())
            ThrowCanceled(cancellationToken);
        return result;
    }

    private static async ValueTask<TResult> CompleteCommittedAsync<TCapture, TPrepared, TResult>(
        AgasMcpCommitExecution execution,
        TCapture capture,
        TPrepared prepared,
        Func<TCapture, TPrepared, CancellationToken, ValueTask<TResult>> completeAsync,
        Func<bool> tryBeginCommit,
        CancellationToken cancellationToken)
    {
        if (!tryBeginCommit())
            ThrowCanceled(cancellationToken);

        try
        {
            // Once the CAS commit point wins, later caller/timeout cancellation is recorded by
            // the execution but cannot interrupt or rewrite the actual completion outcome.
            return await completeAsync(capture, prepared, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            execution.CompleteCommit();
        }
    }

    private static void ThrowIfCanceled(
        AgasMcpCommitExecution execution,
        CancellationToken cancellationToken)
    {
        if (execution.State == AgasMcpCommitExecutionState.Canceled)
            ThrowCanceled(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void ThrowCanceled(CancellationToken cancellationToken)
    {
        throw new OperationCanceledException("MCP operation was canceled before its commit point.", cancellationToken);
    }
}
