#nullable enable

using System;
using System.Threading;
using AppAgasCs.Mcp;

namespace AppAgasCs.Application;

public sealed class ProjectLifecycleWorkspaceLease : IDisposable
{
    private readonly ProjectLifecycleWorkspaceLeaseManager _owner;
    private int _disposed;

    internal ProjectLifecycleWorkspaceLease(
        ProjectLifecycleWorkspaceLeaseManager owner,
        Guid leaseId,
        bool hadOpenProject,
        AgasMcpAiProjectScope projectScope,
        long generation)
    {
        _owner = owner;
        LeaseId = leaseId;
        HadOpenProject = hadOpenProject;
        ProjectScope = projectScope;
        Generation = generation;
    }

    public Guid LeaseId { get; }
    public bool HadOpenProject { get; }
    public AgasMcpAiProjectScope ProjectScope { get; }
    public long Generation { get; }
    public bool IsActive => Volatile.Read(ref _disposed) == 0 && _owner.IsActive(this);

    public bool Matches(ProjectLifecycleWorkspaceSnapshot snapshot) =>
        IsActive
        && snapshot.HasOpenProject == HadOpenProject
        && snapshot.ProjectScope == ProjectScope
        && snapshot.Generation == Generation;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _owner.Release(this);
    }
}

public sealed class ProjectLifecycleWorkspaceLeaseManager
{
    private readonly object _gate = new();
    private Guid? _activeLeaseId;

    public bool TryAcquire(
        ProjectLifecycleWorkspaceSnapshot snapshot,
        out ProjectLifecycleWorkspaceLease? lease)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (_activeLeaseId is not null)
            {
                lease = null;
                return false;
            }

            Guid leaseId = Guid.NewGuid();
            _activeLeaseId = leaseId;
            lease = new(this, leaseId, snapshot.HasOpenProject, snapshot.ProjectScope, snapshot.Generation);
            return true;
        }
    }

    internal bool IsActive(ProjectLifecycleWorkspaceLease lease)
    {
        lock (_gate)
            return _activeLeaseId == lease.LeaseId;
    }

    internal void Release(ProjectLifecycleWorkspaceLease lease)
    {
        lock (_gate)
        {
            if (_activeLeaseId == lease.LeaseId)
                _activeLeaseId = null;
        }
    }
}
