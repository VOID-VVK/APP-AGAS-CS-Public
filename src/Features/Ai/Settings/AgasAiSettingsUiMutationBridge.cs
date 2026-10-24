using System;

namespace AppAgasCs.Features.Ai.Settings;

/// <summary>
/// Pure-scalar staged bridge between main-thread UI state and background settings RMW.
/// Godot controls are touched only by Stage/Finalize callers on the main thread.
/// </summary>
public sealed class AgasAiSettingsUiMutationBridge : IAgasAiSettingsMutationLeaseBridge
{
    private readonly object _gate = new();
    private readonly Action<AgasAiProfilesDocument> _applyCommittedOnMainThread;
    private AgasAiSettingsDraftState _drafts;
    private long _generation;
    private string _workspaceId = string.Empty;
    private string? _activeProfileId;
    private AgasAiSettingsMutationLease? _lease;
    private bool _workerClaimed;
    private AgasAiProfilesDocument? _pendingCommitted;

    public AgasAiSettingsUiMutationBridge(Action<AgasAiProfilesDocument> applyCommittedOnMainThread) =>
        _applyCommittedOnMainThread = applyCommittedOnMainThread
            ?? throw new ArgumentNullException(nameof(applyCommittedOnMainThread));

    public AgasAiSettingsDraftState DraftState
    {
        get { lock (_gate) return _drafts; }
    }

    public bool IsLeased
    {
        get { lock (_gate) return _lease != null; }
    }

    public void PublishMainThreadSnapshot(
        AgasAiSettingsDraftState drafts,
        long generation,
        string workspaceId,
        string? activeProfileId)
    {
        lock (_gate)
        {
            _drafts = drafts;
            _generation = generation;
            _workspaceId = workspaceId ?? string.Empty;
            _activeProfileId = activeProfileId;
        }
    }

    public bool StageAcquireOnMainThread()
    {
        lock (_gate)
        {
            if (_lease != null || _drafts.HasUnsavedChanges)
                return false;
            _lease = new AgasAiSettingsMutationLease(
                Guid.NewGuid().ToString("N"),
                _generation,
                _workspaceId,
                _activeProfileId);
            _workerClaimed = false;
            _pendingCommitted = null;
            return true;
        }
    }

    public bool TryAcquire(out AgasAiSettingsMutationLease? lease)
    {
        lock (_gate)
        {
            if (_lease == null || _workerClaimed || _drafts.HasUnsavedChanges)
            {
                lease = null;
                return false;
            }
            _workerClaimed = true;
            lease = _lease;
            return true;
        }
    }

    public bool ValidateBeforeCommit(
        AgasAiSettingsMutationLease lease,
        AgasAiSettingsPersistedFingerprint persistedFingerprint)
    {
        lock (_gate)
        {
            return MatchesCurrentLocked(lease) && !_drafts.HasUnsavedChanges;
        }
    }

    public bool SynchronizeAfterCommit(
        AgasAiSettingsMutationLease lease,
        AgasAiProfilesDocument committedDocument)
    {
        lock (_gate)
        {
            if (!MatchesCurrentLocked(lease))
                return false;
            _pendingCommitted = committedDocument;
            // Actual Godot UI application is a separate main-thread stage.
            return false;
        }
    }

    public void Release(AgasAiSettingsMutationLease lease)
    {
        lock (_gate)
        {
            if (_lease == null || !string.Equals(_lease.LeaseId, lease.LeaseId, StringComparison.Ordinal))
                return;
            if (_pendingCommitted == null)
                ClearLeaseLocked();
        }
    }

    public bool FinalizeOnMainThread()
    {
        AgasAiProfilesDocument? committed;
        AgasAiSettingsMutationLease? lease;
        lock (_gate)
        {
            committed = _pendingCommitted;
            lease = _lease;
            if (committed == null || lease == null || !MatchesCurrentLocked(lease))
            {
                ClearLeaseLocked();
                return false;
            }
        }

        bool applied = false;
        try
        {
            _applyCommittedOnMainThread(committed);
            applied = true;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            lock (_gate)
            {
                if (applied)
                {
                    _activeProfileId = committed.ActiveProfileId;
                    _drafts = default;
                }
                ClearLeaseLocked();
            }
        }
    }

    public void FinishWithoutCommitOnMainThread()
    {
        lock (_gate) ClearLeaseLocked();
    }

    private bool MatchesCurrentLocked(AgasAiSettingsMutationLease lease) =>
        _lease != null
        && string.Equals(_lease.LeaseId, lease.LeaseId, StringComparison.Ordinal)
        && lease.Generation == _generation
        && string.Equals(lease.WorkspaceId, _workspaceId, StringComparison.Ordinal)
        && string.Equals(lease.ActiveProfileId, _activeProfileId, StringComparison.Ordinal);

    private void ClearLeaseLocked()
    {
        _lease = null;
        _workerClaimed = false;
        _pendingCommitted = null;
    }
}
