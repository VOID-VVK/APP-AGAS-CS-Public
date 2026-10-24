using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AppAgasCs.Features.Ai.Settings;

namespace AppAgasCs.Mcp;

public readonly struct AgasMcpAiProjectScope : IEquatable<AgasMcpAiProjectScope>
{
    private static readonly StringComparer ProjectRootComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private AgasMcpAiProjectScope(string projectRoot, string projectId)
    {
        ProjectRoot = projectRoot;
        ProjectId = projectId;
    }

    public string ProjectRoot { get; }
    public string ProjectId { get; }

    public static AgasMcpAiProjectScope Create(string projectRoot, string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new ArgumentException("Project root is required.", nameof(projectRoot));
        }
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException("Project id is required.", nameof(projectId));
        }

        string normalizedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(projectRoot.Trim()));
        return new AgasMcpAiProjectScope(normalizedRoot, projectId.Trim());
    }

    internal static AgasMcpAiProjectScope Legacy(string projectId) =>
        Create(Environment.CurrentDirectory, projectId);

    public bool Equals(AgasMcpAiProjectScope other) =>
        ProjectRootComparer.Equals(ProjectRoot, other.ProjectRoot)
        && string.Equals(ProjectId, other.ProjectId, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is AgasMcpAiProjectScope other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        ProjectRootComparer.GetHashCode(ProjectRoot ?? string.Empty),
        StringComparer.Ordinal.GetHashCode(ProjectId ?? string.Empty));

    public static bool operator ==(AgasMcpAiProjectScope left, AgasMcpAiProjectScope right) => left.Equals(right);
    public static bool operator !=(AgasMcpAiProjectScope left, AgasMcpAiProjectScope right) => !left.Equals(right);

    public override string ToString() => ProjectId ?? string.Empty;
}

public enum AgasMcpAiJobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Canceled,
}

public sealed record AgasMcpAiJobResultMetadata(
    string ResultRelativePath,
    string? PreviewRelativePath,
    string Message);

public sealed record AgasMcpAiJobFailure(
    string ErrorCode,
    string Message,
    string? Hint = null,
    AgasMcpAiJobDiagnostic? Diagnostic = null);

public sealed record AgasMcpAiJobDiagnostic(
    string DiagnosticId,
    string ErrorCode,
    string Phase,
    string Category,
    int? HttpStatus = null,
    string? UpstreamCode = null);

public sealed record AgasMcpAiJobAppliedMetadata(
    string AppliedRelativePath,
    string Message,
    DateTimeOffset AppliedAtUtc);

public sealed record AgasMcpAiJobSnapshot(
    string JobId,
    AgasMcpAiProjectScope ProjectScope,
    string EffectiveOperation,
    AgasMcpAiExecutionIdentity Execution,
    AgasMcpAiJobState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    AgasMcpAiJobResultMetadata? Result,
    AgasMcpAiJobFailure? Failure,
    AgasMcpAiJobAppliedMetadata? Applied)
{
    public string ProjectRoot => ProjectScope.ProjectRoot;
    public string ProjectId => ProjectScope.ProjectId;
}

/// <summary>
/// Thread-safe in-process AI job state. Terminal transitions are first-writer-wins
/// and therefore cannot be reversed by cancel/completion races.
/// </summary>
public sealed class AgasMcpAiJobStore : IDisposable
{
    public const int DefaultMaximumRetainedTerminalJobs = 100;
    public static readonly TimeSpan DefaultTerminalRetention = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly int _maximumRetainedTerminalJobs;
    private readonly TimeSpan _terminalRetention;
    private readonly ITimer _cleanupTimer;
    private long _nextSequence;
    private long _nextTerminalSequence;
    private long _nextApplyLeaseId;
    private bool _disposed;

    public AgasMcpAiJobStore(
        TimeProvider? timeProvider = null,
        int maximumRetainedTerminalJobs = DefaultMaximumRetainedTerminalJobs,
        TimeSpan? terminalRetention = null)
    {
        if (maximumRetainedTerminalJobs < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRetainedTerminalJobs));
        }

        TimeSpan retention = terminalRetention ?? DefaultTerminalRetention;
        if (retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(terminalRetention));
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _maximumRetainedTerminalJobs = maximumRetainedTerminalJobs;
        _terminalRetention = retention;
        _cleanupTimer = _timeProvider.CreateTimer(
            static state => ((AgasMcpAiJobStore)state!).RunScheduledCleanup(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public AgasMcpAiJobSnapshot CreateQueued(
        AgasMcpAiProjectScope projectScope,
        string effectiveOperation,
        AgasMcpAiExecutionIdentity execution)
    {
        ValidateScope(projectScope);
        ArgumentNullException.ThrowIfNull(execution);
        string normalizedOperation = NormalizeEffectiveOperation(effectiveOperation, nameof(effectiveOperation));

        lock (_gate)
        {
            ThrowIfDisposedLocked();
            CleanupLocked(_timeProvider.GetUtcNow());
            string jobId;
            do
            {
                jobId = $"aijob-{Guid.NewGuid():N}";
            }
            while (_entries.ContainsKey(jobId));

            var entry = new Entry(
                jobId,
                projectScope,
                normalizedOperation,
                execution,
                _timeProvider.GetUtcNow(),
                checked(++_nextSequence));
            _entries.Add(jobId, entry);
            return Snapshot(entry);
        }
    }

    public AgasMcpAiJobSnapshot CreateQueued(string projectId, string effectiveOperation) =>
        CreateQueued(AgasMcpAiProjectScope.Legacy(projectId), effectiveOperation, LegacyExecution());

    public AgasMcpAiJobSnapshot CreateQueued(
        AgasMcpAiProjectScope projectScope,
        string effectiveOperation) => CreateQueued(projectScope, effectiveOperation, LegacyExecution());

    public bool TryGet(
        AgasMcpAiProjectScope projectScope,
        string jobId,
        out AgasMcpAiJobSnapshot? snapshot)
    {
        ValidateScope(projectScope);
        string normalizedJobId = NormalizeJobId(jobId);
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            CleanupLocked(_timeProvider.GetUtcNow());
            if (!_entries.TryGetValue(normalizedJobId, out Entry? entry)
                || entry.ProjectScope != projectScope)
            {
                snapshot = null;
                return false;
            }

            snapshot = Snapshot(entry);
            return true;
        }
    }

    public bool TryGet(string jobId, out AgasMcpAiJobSnapshot? snapshot)
    {
        string normalizedJobId = NormalizeJobId(jobId);
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            CleanupLocked(_timeProvider.GetUtcNow());
            if (!_entries.TryGetValue(normalizedJobId, out Entry? entry))
            {
                snapshot = null;
                return false;
            }

            snapshot = Snapshot(entry);
            return true;
        }
    }

    public IReadOnlyList<AgasMcpAiJobSnapshot> GetRecent(int limit = 10)
    {
        return GetRecentCore(projectScope: null, limit);
    }

    public IReadOnlyList<AgasMcpAiJobSnapshot> GetRecent(
        AgasMcpAiProjectScope projectScope,
        int limit = 10)
    {
        ValidateScope(projectScope);
        return GetRecentCore(projectScope, limit);
    }

    private IReadOnlyList<AgasMcpAiJobSnapshot> GetRecentCore(
        AgasMcpAiProjectScope? projectScope,
        int limit)
    {
        if (limit is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 50.");
        }

        lock (_gate)
        {
            ThrowIfDisposedLocked();
            CleanupLocked(_timeProvider.GetUtcNow());
            IEnumerable<Entry> entries = _entries.Values;
            if (projectScope.HasValue)
            {
                entries = entries.Where(entry => entry.ProjectScope == projectScope.Value);
            }
            return Array.AsReadOnly(entries
                .OrderByDescending(entry => entry.CreatedAtUtc)
                .ThenBy(entry => entry.JobId, StringComparer.Ordinal)
                .Take(limit)
                .Select(Snapshot)
                .ToArray());
        }
    }

    public bool TryMarkRunning(string jobId, out AgasMcpAiJobSnapshot? snapshot)
    {
        string normalizedJobId = NormalizeJobId(jobId);
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            CleanupLocked(_timeProvider.GetUtcNow());
            if (!_entries.TryGetValue(normalizedJobId, out Entry? entry)
                || entry.State != AgasMcpAiJobState.Queued)
            {
                snapshot = entry == null ? null : Snapshot(entry);
                return false;
            }

            entry.State = AgasMcpAiJobState.Running;
            entry.StartedAtUtc = _timeProvider.GetUtcNow();
            snapshot = Snapshot(entry);
            return true;
        }
    }

    public bool TrySucceed(
        string jobId,
        AgasMcpAiJobResultMetadata result,
        object? applicationState,
        out AgasMcpAiJobSnapshot? snapshot)
        => TrySucceed(jobId, result, applicationState, effectiveOperation: null, out snapshot);

    public bool TrySucceed(
        string jobId,
        AgasMcpAiJobResultMetadata result,
        object? applicationState,
        string? effectiveOperation,
        out AgasMcpAiJobSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(result);
        string normalizedJobId = NormalizeJobId(jobId);
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            CleanupLocked(_timeProvider.GetUtcNow());
            if (!_entries.TryGetValue(normalizedJobId, out Entry? entry)
                || entry.State != AgasMcpAiJobState.Running)
            {
                snapshot = entry == null ? null : Snapshot(entry);
                return false;
            }

            entry.State = AgasMcpAiJobState.Succeeded;
            entry.CompletedAtUtc = _timeProvider.GetUtcNow();
            entry.RetentionAnchorUtc = entry.CompletedAtUtc;
            entry.Result = result;
            if (!string.IsNullOrWhiteSpace(effectiveOperation))
            {
                entry.EffectiveOperation = NormalizeEffectiveOperation(effectiveOperation, nameof(effectiveOperation));
            }
            entry.ApplicationState = applicationState;
            entry.TerminalSequence = checked(++_nextTerminalSequence);
            snapshot = Snapshot(entry);
            CleanupLocked(entry.CompletedAtUtc.Value);
            return true;
        }
    }

    public bool TryFail(
        string jobId,
        AgasMcpAiJobFailure failure,
        out AgasMcpAiJobSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(failure);
        string normalizedJobId = NormalizeJobId(jobId);
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            CleanupLocked(_timeProvider.GetUtcNow());
            if (!_entries.TryGetValue(normalizedJobId, out Entry? entry)
                || entry.State != AgasMcpAiJobState.Running)
            {
                snapshot = entry == null ? null : Snapshot(entry);
                return false;
            }

            entry.State = AgasMcpAiJobState.Failed;
            entry.CompletedAtUtc = _timeProvider.GetUtcNow();
            entry.RetentionAnchorUtc = entry.CompletedAtUtc;
            entry.Failure = failure;
            entry.TerminalSequence = checked(++_nextTerminalSequence);
            snapshot = Snapshot(entry);
            CleanupLocked(entry.CompletedAtUtc.Value);
            return true;
        }
    }

    public bool TryCancel(string jobId, out AgasMcpAiJobSnapshot? snapshot)
    {
        string normalizedJobId = NormalizeJobId(jobId);
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            CleanupLocked(_timeProvider.GetUtcNow());
            if (!_entries.TryGetValue(normalizedJobId, out Entry? entry))
            {
                snapshot = null;
                return false;
            }

            if (IsTerminal(entry.State))
            {
                snapshot = Snapshot(entry);
                return false;
            }

            entry.State = AgasMcpAiJobState.Canceled;
            entry.CompletedAtUtc = _timeProvider.GetUtcNow();
            entry.RetentionAnchorUtc = entry.CompletedAtUtc;
            entry.TerminalSequence = checked(++_nextTerminalSequence);
            snapshot = Snapshot(entry);
            CleanupLocked(entry.CompletedAtUtc.Value);
            return true;
        }
    }

    public bool TryGetApplicationState<T>(string jobId, out T? applicationState)
        where T : class
    {
        string normalizedJobId = NormalizeJobId(jobId);
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            CleanupLocked(_timeProvider.GetUtcNow());
            if (_entries.TryGetValue(normalizedJobId, out Entry? entry)
                && entry.State == AgasMcpAiJobState.Succeeded
                && !entry.ApplyLeaseId.HasValue
                && entry.ApplicationState is T typed)
            {
                applicationState = typed;
                return true;
            }

            applicationState = null;
            return false;
        }
    }

    public bool TryGetApplicationState<T>(
        AgasMcpAiProjectScope projectScope,
        string jobId,
        out T? applicationState)
        where T : class
    {
        ValidateScope(projectScope);
        string normalizedJobId = NormalizeJobId(jobId);
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            CleanupLocked(_timeProvider.GetUtcNow());
            if (_entries.TryGetValue(normalizedJobId, out Entry? entry)
                && entry.ProjectScope == projectScope
                && entry.State == AgasMcpAiJobState.Succeeded
                && !entry.ApplyLeaseId.HasValue
                && entry.ApplicationState is T typed)
            {
                applicationState = typed;
                return true;
            }

            applicationState = null;
            return false;
        }
    }

    public bool TryBeginApply<T>(
        AgasMcpAiProjectScope projectScope,
        string jobId,
        out AgasMcpAiJobApplyLease<T>? lease)
        where T : class
    {
        ValidateScope(projectScope);
        string normalizedJobId = NormalizeJobId(jobId);
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            CleanupLocked(_timeProvider.GetUtcNow());
            if (!_entries.TryGetValue(normalizedJobId, out Entry? entry)
                || entry.ProjectScope != projectScope
                || entry.State != AgasMcpAiJobState.Succeeded
                || entry.Applied != null
                || entry.ApplyLeaseId.HasValue
                || entry.ApplicationState is not T typed)
            {
                lease = null;
                return false;
            }

            long leaseId = checked(++_nextApplyLeaseId);
            entry.ApplyLeaseId = leaseId;
            lease = new AgasMcpAiJobApplyLease<T>(
                this,
                projectScope,
                normalizedJobId,
                leaseId,
                typed);
            return true;
        }
    }

    /// <summary>
    /// Atomically takes the apply-only state so successful application can release
    /// large workspace captures before the status metadata itself expires.
    /// </summary>
    public bool TryConsumeApplicationState<T>(string jobId, out T? applicationState)
        where T : class
    {
        string normalizedJobId = NormalizeJobId(jobId);
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            CleanupLocked(_timeProvider.GetUtcNow());
            if (_entries.TryGetValue(normalizedJobId, out Entry? entry)
                && entry.State == AgasMcpAiJobState.Succeeded
                && entry.ApplicationState is T typed)
            {
                entry.ApplicationState = null;
                applicationState = typed;
                return true;
            }

            applicationState = null;
            return false;
        }
    }

    public bool TryMarkApplied(
        string jobId,
        string appliedRelativePath,
        string message,
        out AgasMcpAiJobSnapshot? snapshot)
    {
        string normalizedJobId = NormalizeJobId(jobId);
        string normalizedPath = Required(appliedRelativePath, nameof(appliedRelativePath));
        string normalizedMessage = message?.Trim() ?? string.Empty;
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            CleanupLocked(_timeProvider.GetUtcNow());
            if (!_entries.TryGetValue(normalizedJobId, out Entry? entry)
                || entry.State != AgasMcpAiJobState.Succeeded
                || entry.Applied != null
                || entry.ApplyLeaseId.HasValue)
            {
                snapshot = entry == null ? null : Snapshot(entry);
                return false;
            }

            entry.Applied = new AgasMcpAiJobAppliedMetadata(
                normalizedPath,
                normalizedMessage,
                _timeProvider.GetUtcNow());
            snapshot = Snapshot(entry);
            return true;
        }
    }

    public bool ClearApplicationState(string jobId)
    {
        string normalizedJobId = NormalizeJobId(jobId);
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            CleanupLocked(_timeProvider.GetUtcNow());
            if (!_entries.TryGetValue(normalizedJobId, out Entry? entry)
                || entry.ApplicationState == null
                || entry.ApplyLeaseId.HasValue)
            {
                return false;
            }

            entry.ApplicationState = null;
            return true;
        }
    }

    internal bool TryCompleteApplyLease(
        AgasMcpAiProjectScope projectScope,
        string jobId,
        long leaseId,
        string appliedRelativePath,
        string message,
        out AgasMcpAiJobSnapshot? snapshot)
    {
        string normalizedPath = Required(appliedRelativePath, nameof(appliedRelativePath));
        string normalizedMessage = message?.Trim() ?? string.Empty;
        lock (_gate)
        {
            if (_disposed)
            {
                snapshot = null;
                return false;
            }
            if (!_entries.TryGetValue(jobId, out Entry? entry)
                || entry.ProjectScope != projectScope
                || entry.ApplyLeaseId != leaseId
                || entry.State != AgasMcpAiJobState.Succeeded
                || entry.Applied != null)
            {
                snapshot = entry == null ? null : Snapshot(entry);
                return false;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            entry.Applied = new AgasMcpAiJobAppliedMetadata(normalizedPath, normalizedMessage, now);
            entry.ApplicationState = null;
            entry.ApplyLeaseId = null;
            snapshot = Snapshot(entry);
            CleanupLocked(now);
            return true;
        }
    }

    internal void ReleaseApplyLease(
        AgasMcpAiProjectScope projectScope,
        string jobId,
        long leaseId)
    {
        lock (_gate)
        {
            if (_disposed
                || !_entries.TryGetValue(jobId, out Entry? entry)
                || entry.ProjectScope != projectScope
                || entry.ApplyLeaseId != leaseId)
            {
                return;
            }

            entry.ApplyLeaseId = null;
            CleanupLocked(_timeProvider.GetUtcNow());
        }
    }

    public int Cleanup()
    {
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            return CleanupLocked(_timeProvider.GetUtcNow());
        }
    }

    internal int StoredCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _entries.Clear();
        }

        _cleanupTimer.Dispose();
    }

    private int CleanupLocked(DateTimeOffset now)
    {
        string[] expired = _entries.Values
            .Where(entry => IsTerminal(entry.State)
                && !entry.ApplyLeaseId.HasValue
                && entry.RetentionAnchorUtc.HasValue
                && now - entry.RetentionAnchorUtc.Value >= _terminalRetention)
            .Select(entry => entry.JobId)
            .ToArray();
        foreach (string jobId in expired)
        {
            _entries.Remove(jobId);
        }

        Entry[] retainedTerminals = _entries.Values
            .Where(entry => IsTerminal(entry.State))
            .OrderByDescending(entry => entry.RetentionAnchorUtc)
            .ThenByDescending(entry => entry.TerminalSequence)
            .ToArray();
        int excess = Math.Max(0, retainedTerminals.Length - _maximumRetainedTerminalJobs);
        foreach (Entry entry in retainedTerminals.AsEnumerable().Reverse())
        {
            if (excess == 0)
            {
                break;
            }
            if (entry.ApplyLeaseId.HasValue)
            {
                continue;
            }
            _entries.Remove(entry.JobId);
            excess--;
        }

        ScheduleNextCleanupLocked(now);

        return expired.Length + Math.Max(0, retainedTerminals.Length - _maximumRetainedTerminalJobs - excess);
    }

    private void ScheduleNextCleanupLocked(DateTimeOffset now)
    {
        if (_disposed)
        {
            return;
        }

        DateTimeOffset? nextExpiration = null;
        foreach (Entry entry in _entries.Values)
        {
            if (!IsTerminal(entry.State)
                || entry.ApplyLeaseId.HasValue
                || !entry.RetentionAnchorUtc.HasValue)
            {
                continue;
            }

            DateTimeOffset expiration = entry.RetentionAnchorUtc.Value + _terminalRetention;
            if (!nextExpiration.HasValue || expiration < nextExpiration.Value)
            {
                nextExpiration = expiration;
            }
        }

        TimeSpan due = nextExpiration.HasValue
            ? Max(TimeSpan.Zero, nextExpiration.Value - now)
            : Timeout.InfiniteTimeSpan;
        _cleanupTimer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private void RunScheduledCleanup()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                CleanupLocked(_timeProvider.GetUtcNow());
            }
        }
        catch
        {
            // Timer callbacks must never tear down the host. Public operations also
            // run the same cleanup defensively.
        }
    }

    private void ThrowIfDisposedLocked()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AgasMcpAiJobStore));
        }
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;

    private static AgasMcpAiJobSnapshot Snapshot(Entry entry) => new(
        entry.JobId,
        entry.ProjectScope,
        entry.EffectiveOperation,
        entry.Execution,
        entry.State,
        entry.CreatedAtUtc,
        entry.StartedAtUtc,
        entry.CompletedAtUtc,
        entry.Result,
        entry.Failure,
        entry.Applied);

    private static bool IsTerminal(AgasMcpAiJobState state) => state is
        AgasMcpAiJobState.Succeeded
        or AgasMcpAiJobState.Failed
        or AgasMcpAiJobState.Canceled;

    private static string Required(string value, string parameterName)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Value is required.", parameterName);
        }

        return normalized;
    }

    private static string NormalizeEffectiveOperation(string value, string parameterName)
    {
        string normalized = Required(value, parameterName);
        return normalized switch
        {
            // Pre-Wave 2 fixtures and legacy internal callers used the request
            // operation name. The public AI job output contract freezes the
            // effective guided edit variant instead.
            "RegionEdit" => "GuidedRegionEdit",
            _ => normalized,
        };
    }

    private static string NormalizeJobId(string jobId) => Required(jobId, nameof(jobId));

    private static void ValidateScope(AgasMcpAiProjectScope projectScope)
    {
        if (string.IsNullOrWhiteSpace(projectScope.ProjectRoot)
            || string.IsNullOrWhiteSpace(projectScope.ProjectId))
        {
            throw new ArgumentException("A normalized project scope is required.", nameof(projectScope));
        }
    }

    private sealed class Entry
    {
        public Entry(
            string jobId,
            AgasMcpAiProjectScope projectScope,
            string effectiveOperation,
            AgasMcpAiExecutionIdentity execution,
            DateTimeOffset createdAtUtc,
            long sequence)
        {
            JobId = jobId;
            ProjectScope = projectScope;
            EffectiveOperation = effectiveOperation;
            Execution = execution;
            CreatedAtUtc = createdAtUtc;
            Sequence = sequence;
        }

        public string JobId { get; }
        public AgasMcpAiProjectScope ProjectScope { get; }
        public string EffectiveOperation { get; set; }
        public AgasMcpAiExecutionIdentity Execution { get; }
        public DateTimeOffset CreatedAtUtc { get; }
        public long Sequence { get; }
        public long TerminalSequence { get; set; }
        public AgasMcpAiJobState State { get; set; } = AgasMcpAiJobState.Queued;
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public DateTimeOffset? RetentionAnchorUtc { get; set; }
        public AgasMcpAiJobResultMetadata? Result { get; set; }
        public AgasMcpAiJobFailure? Failure { get; set; }
        public AgasMcpAiJobAppliedMetadata? Applied { get; set; }
        public object? ApplicationState { get; set; }
        public long? ApplyLeaseId { get; set; }
    }

    private static AgasMcpAiExecutionIdentity LegacyExecution() =>
        new("legacy", "legacy", "legacy", "legacy");
}

public sealed class AgasMcpAiJobApplyLease<T> : IDisposable
    where T : class
{
    private readonly AgasMcpAiJobStore _store;
    private readonly AgasMcpAiProjectScope _projectScope;
    private readonly string _jobId;
    private readonly long _leaseId;
    private int _finished;

    internal AgasMcpAiJobApplyLease(
        AgasMcpAiJobStore store,
        AgasMcpAiProjectScope projectScope,
        string jobId,
        long leaseId,
        T applicationState)
    {
        _store = store;
        _projectScope = projectScope;
        _jobId = jobId;
        _leaseId = leaseId;
        ApplicationState = applicationState;
    }

    public T ApplicationState { get; }

    public bool MarkApplied(
        string appliedRelativePath,
        string message,
        out AgasMcpAiJobSnapshot? snapshot)
    {
        if (string.IsNullOrWhiteSpace(appliedRelativePath))
        {
            throw new ArgumentException("Applied relative path is required.", nameof(appliedRelativePath));
        }
        if (Interlocked.CompareExchange(ref _finished, 1, 0) != 0)
        {
            snapshot = null;
            return false;
        }

        return _store.TryCompleteApplyLease(
            _projectScope,
            _jobId,
            _leaseId,
            appliedRelativePath,
            message,
            out snapshot);
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _finished, 1, 0) == 0)
        {
            _store.ReleaseApplyLease(_projectScope, _jobId, _leaseId);
        }
    }
}
