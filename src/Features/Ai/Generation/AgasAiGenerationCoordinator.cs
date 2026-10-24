#nullable enable

using System;

namespace AppAgasCs.Features.Ai.Generation;

public interface IAgasAiGenerationPermit : IDisposable
{
}

public interface IAgasAiGenerationCoordinator
{
    int MaximumConcurrentGenerations { get; }
    int ActiveCount { get; }
    event Action? CapacityAvailable;
    bool TryAcquire(out IAgasAiGenerationPermit? permit);
}

/// <summary>
/// Process-local active generation budget shared by UI and MCP callers. Queueing
/// policy deliberately belongs to each caller: the UI rejects immediately while
/// MCP keeps its own bounded FIFO queue.
/// </summary>
public sealed class AgasAiGenerationCoordinator : IAgasAiGenerationCoordinator
{
    public const int DefaultMaximumConcurrentGenerations = 5;

    private readonly object _gate = new();
    private readonly int _maximumConcurrentGenerations;
    private int _activeCount;

    public AgasAiGenerationCoordinator(int maximumConcurrentGenerations = DefaultMaximumConcurrentGenerations)
    {
        if (maximumConcurrentGenerations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentGenerations));
        }

        _maximumConcurrentGenerations = maximumConcurrentGenerations;
    }

    public int MaximumConcurrentGenerations => _maximumConcurrentGenerations;

    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _activeCount;
            }
        }
    }

    public event Action? CapacityAvailable;

    public bool TryAcquire(out IAgasAiGenerationPermit? permit)
    {
        lock (_gate)
        {
            if (_activeCount >= _maximumConcurrentGenerations)
            {
                permit = null;
                return false;
            }

            _activeCount++;
            permit = new Permit(this);
            return true;
        }
    }

    private void Release()
    {
        Action? capacityAvailable;
        lock (_gate)
        {
            if (_activeCount < 1)
            {
                throw new InvalidOperationException("AI generation permit accounting underflowed.");
            }

            _activeCount--;
            capacityAvailable = CapacityAvailable;
        }

        if (capacityAvailable == null)
        {
            return;
        }

        foreach (Action subscriber in capacityAvailable.GetInvocationList())
        {
            try
            {
                subscriber();
            }
            catch
            {
                // Releasing a shared permit must never fail because a queue
                // observer is being stopped concurrently.
            }
        }
    }

    private sealed class Permit : IAgasAiGenerationPermit
    {
        private AgasAiGenerationCoordinator? _owner;

        public Permit(AgasAiGenerationCoordinator owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            AgasAiGenerationCoordinator? owner = System.Threading.Interlocked.Exchange(ref _owner, null);
            owner?.Release();
        }
    }
}
