#nullable enable

using System;
using System.Threading;

namespace AppAgasCs.Mcp.Runtime;

public enum AgasMcpCommitPolicy
{
    None,
    ShortAtomicCommit,
    StartedMainThreadCompletion,
}

public enum AgasMcpCommitExecutionState
{
    Pending,
    Canceled,
    ShortCommitting,
    MainThreadCompleting,
    Completed,
}

/// <summary>
/// Owns the single CAS boundary shared by timeout/caller cancellation and a tool's commit point.
/// Lifecycle and persistence services consume this type instead of defining private state machines.
/// </summary>
public sealed class AgasMcpCommitExecution
{
    private readonly AgasMcpCommitPolicy _policy;
    private int _state = (int)AgasMcpCommitExecutionState.Pending;
    private int _commitStarted;
    private int _cancellationObservedAfterStart;

    public AgasMcpCommitExecution(AgasMcpCommitPolicy policy)
    {
        if (!Enum.IsDefined(policy))
            throw new ArgumentOutOfRangeException(nameof(policy));

        _policy = policy;
    }

    public AgasMcpCommitPolicy Policy => _policy;

    public AgasMcpCommitExecutionState State =>
        (AgasMcpCommitExecutionState)Volatile.Read(ref _state);

    public bool CancellationObservedAfterStart =>
        Volatile.Read(ref _cancellationObservedAfterStart) != 0;

    public bool TryCancel()
    {
        while (true)
        {
            AgasMcpCommitExecutionState state = State;
            switch (state)
            {
                case AgasMcpCommitExecutionState.Pending:
                    if (Interlocked.CompareExchange(
                            ref _state,
                            (int)AgasMcpCommitExecutionState.Canceled,
                            (int)AgasMcpCommitExecutionState.Pending)
                        == (int)AgasMcpCommitExecutionState.Pending)
                    {
                        return true;
                    }

                    continue;

                case AgasMcpCommitExecutionState.ShortCommitting:
                case AgasMcpCommitExecutionState.MainThreadCompleting:
                    Interlocked.Exchange(ref _cancellationObservedAfterStart, 1);
                    return false;

                case AgasMcpCommitExecutionState.Completed:
                    if (Volatile.Read(ref _commitStarted) != 0)
                        Interlocked.Exchange(ref _cancellationObservedAfterStart, 1);
                    return false;

                case AgasMcpCommitExecutionState.Canceled:
                    return false;

                default:
                    throw new InvalidOperationException($"Unknown MCP commit execution state '{state}'.");
            }
        }
    }

    public bool TryBeginShortAtomicCommit()
    {
        RequirePolicy(AgasMcpCommitPolicy.ShortAtomicCommit);
        return TryBeginCommit(AgasMcpCommitExecutionState.ShortCommitting);
    }

    /// <summary>
    /// The sole StartedMainThreadCompletion CAS primitive. A successful call owns completion;
    /// subsequent cancellation is observational and must not replace the resulting receipt.
    /// </summary>
    public bool TryBeginMainThreadCompletion()
    {
        RequirePolicy(AgasMcpCommitPolicy.StartedMainThreadCompletion);
        return TryBeginCommit(AgasMcpCommitExecutionState.MainThreadCompleting);
    }

    public bool TryCompleteWithoutCommit()
    {
        RequirePolicy(AgasMcpCommitPolicy.None);
        return Interlocked.CompareExchange(
                   ref _state,
                   (int)AgasMcpCommitExecutionState.Completed,
                   (int)AgasMcpCommitExecutionState.Pending)
               == (int)AgasMcpCommitExecutionState.Pending;
    }

    public void CompleteCommit()
    {
        AgasMcpCommitExecutionState expected = _policy switch
        {
            AgasMcpCommitPolicy.ShortAtomicCommit => AgasMcpCommitExecutionState.ShortCommitting,
            AgasMcpCommitPolicy.StartedMainThreadCompletion => AgasMcpCommitExecutionState.MainThreadCompleting,
            _ => throw new InvalidOperationException("A None-policy execution has no commit to complete."),
        };

        if (Interlocked.CompareExchange(
                ref _state,
                (int)AgasMcpCommitExecutionState.Completed,
                (int)expected)
            != (int)expected)
        {
            throw new InvalidOperationException($"Cannot complete MCP commit from state '{State}'.");
        }
    }

    private bool TryBeginCommit(AgasMcpCommitExecutionState committingState)
    {
        if (Interlocked.CompareExchange(
                ref _state,
                (int)committingState,
                (int)AgasMcpCommitExecutionState.Pending)
            != (int)AgasMcpCommitExecutionState.Pending)
        {
            return false;
        }

        Volatile.Write(ref _commitStarted, 1);
        return true;
    }

    private void RequirePolicy(AgasMcpCommitPolicy required)
    {
        if (_policy != required)
        {
            throw new InvalidOperationException(
                $"MCP execution policy '{_policy}' cannot enter a '{required}' commit.");
        }
    }
}
