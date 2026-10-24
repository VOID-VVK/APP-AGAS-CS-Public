using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AppAgasCs.Application;

public sealed record AgasSceneDetailCursorOptions(
    TimeSpan TimeToLive,
    int MaximumSnapshots,
    int MaximumSnapshotsPerProject,
    int MaximumItemsPerSnapshot,
    int MaximumItemUtf8Bytes,
    int MaximumSnapshotUtf8Bytes,
    long MaximumRetainedUtf8Bytes)
{
    public int MaximumCursors { get; init; } = 1_024;
    public int MaximumSnapshotMetadataUtf8Bytes { get; init; } = 4 * 1024 * 1024;

    public static AgasSceneDetailCursorOptions Default { get; } = new(
        TimeSpan.FromMinutes(5),
        128,
        32,
        10_000,
        256 * 1024,
        8 * 1024 * 1024,
        64L * 1024 * 1024);
}

public sealed class AgasSceneDetailPage
{
    public AgasSceneDetailPage(AgasSceneDetailSnapshot scene, IEnumerable<AgasSceneDetailItem> items, string? nextCursor)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(items);
        SceneId = scene.SceneId;
        Name = scene.Name;
        ActiveLayerId = scene.ActiveLayerId;
        ViewState = scene.ViewState;
        Items = Array.AsReadOnly(items.ToArray());
        NextCursor = nextCursor;
        Readme = scene.Readme;
    }

    public string SceneId { get; }
    public string Name { get; }
    public string? ActiveLayerId { get; }
    public AgasSceneDetailViewState ViewState { get; }
    public IReadOnlyList<AgasSceneDetailItem> Items { get; }
    public string? NextCursor { get; }
    public string Readme { get; }
}

public sealed class AgasSceneDetailPageLease : IDisposable
{
    private Action? _release;

    internal AgasSceneDetailPageLease(AgasSceneDetailPage page, Action release)
    {
        Page = page;
        _release = release;
    }

    public AgasSceneDetailPage Page { get; }

    public void Dispose() => System.Threading.Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>
/// Process-wide broker for immutable scalar scene snapshots. It owns no Godot objects.
/// </summary>
public sealed class AgasSceneDetailCursorBroker
{
    private static readonly JsonSerializerOptions SizeJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly AgasSceneDetailCursorOptions _options;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Dictionary<string, SnapshotEntry> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CursorEntry> _cursors = new(StringComparer.Ordinal);
    private long _retainedUtf8Bytes;

    public AgasSceneDetailCursorBroker(
        AgasSceneDetailCursorOptions? options = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _options = options ?? AgasSceneDetailCursorOptions.Default;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        ValidateOptions(_options);
    }

    public int RetainedSnapshotCount
    {
        get { lock (_gate) return _snapshots.Count; }
    }

    public long RetainedUtf8Bytes
    {
        get { lock (_gate) return _retainedUtf8Bytes; }
    }

    public int ActiveCursorCount
    {
        get { lock (_gate) return _cursors.Count; }
    }

    public AgasSceneDetailResult<AgasSceneDetailPage> CreateFirstPage(
        AgasSceneDetailProjectScope scope,
        AgasSceneDetailSnapshot snapshot,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateLimit(limit);
        AgasSceneDetailItem[] ordered = snapshot.Items
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Order)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length > _options.MaximumItemsPerSnapshot)
            return AgasSceneDetailResult<AgasSceneDetailPage>.Fail(AgasSceneDetailErrorCodes.SnapshotTooLarge);

        long metadataBytes = MeasureMetadata(snapshot);
        if (metadataBytes > _options.MaximumSnapshotMetadataUtf8Bytes)
            return AgasSceneDetailResult<AgasSceneDetailPage>.Fail(AgasSceneDetailErrorCodes.SnapshotTooLarge);

        long payloadBytes = MeasureSnapshot(metadataBytes, ordered, out bool itemTooLarge);
        if (itemTooLarge || payloadBytes > _options.MaximumSnapshotUtf8Bytes)
            return AgasSceneDetailResult<AgasSceneDetailPage>.Fail(AgasSceneDetailErrorCodes.SnapshotTooLarge);

        AgasSceneDetailSnapshot orderedSnapshot = new(
            snapshot.SceneId,
            snapshot.Name,
            snapshot.ActiveLayerId,
            snapshot.ViewState,
            ordered);
        if (ordered.Length <= limit)
        {
            return AgasSceneDetailResult<AgasSceneDetailPage>.Ok(
                new AgasSceneDetailPage(orderedSnapshot, ordered, nextCursor: null));
        }

        lock (_gate)
        {
            DateTimeOffset now = _utcNow();
            CleanupExpired(now);
            while (WouldExceedCapacity(scope, payloadBytes, cursorsToAdd: 1))
            {
                SnapshotEntry? candidate = FindEvictionCandidate(scope);
                if (candidate == null)
                    return AgasSceneDetailResult<AgasSceneDetailPage>.Fail(AgasSceneDetailErrorCodes.CursorCapacity);
                RemoveSnapshot(candidate);
            }

            string snapshotToken = NewToken("scnsnap-", _snapshots.ContainsKey);
            var entry = new SnapshotEntry(snapshotToken, scope, orderedSnapshot, payloadBytes, now, now + _options.TimeToLive);
            _snapshots.Add(snapshotToken, entry);
            _retainedUtf8Bytes += payloadBytes;
            string nextCursor = IssueCursor(entry, limit);
            return AgasSceneDetailResult<AgasSceneDetailPage>.Ok(
                new AgasSceneDetailPage(orderedSnapshot, ordered.Take(limit), nextCursor));
        }
    }

    public AgasSceneDetailResult<AgasSceneDetailPageLease> AcquirePage(
        AgasSceneDetailProjectScope scope,
        string sceneId,
        string cursor,
        int limit)
    {
        ValidateLimit(limit);
        if (string.IsNullOrWhiteSpace(sceneId) || string.IsNullOrWhiteSpace(cursor))
            return AgasSceneDetailResult<AgasSceneDetailPageLease>.Fail(AgasSceneDetailErrorCodes.CursorInvalid);

        lock (_gate)
        {
            DateTimeOffset now = _utcNow();
            CleanupExpired(now);
            if (!_cursors.TryGetValue(cursor, out CursorEntry? cursorEntry)
                || !_snapshots.TryGetValue(cursorEntry.SnapshotToken, out SnapshotEntry? snapshot)
                || snapshot.Revoked
                || snapshot.ExpiresAt <= now
                || snapshot.Scope != scope
                || !string.Equals(snapshot.Scene.SceneId, sceneId, StringComparison.Ordinal))
            {
                return AgasSceneDetailResult<AgasSceneDetailPageLease>.Fail(AgasSceneDetailErrorCodes.CursorInvalid);
            }

            string? next = null;
            int nextOffset = cursorEntry.Offset + limit;
            if (nextOffset < snapshot.Scene.Items.Count)
            {
                if (!cursorEntry.TryGetNext(limit, out next))
                {
                    while (_cursors.Count + 1 > _options.MaximumCursors)
                    {
                        SnapshotEntry? candidate = FindCursorEvictionCandidate(snapshot.SnapshotToken);
                        if (candidate == null)
                        {
                            return AgasSceneDetailResult<AgasSceneDetailPageLease>.Fail(
                                AgasSceneDetailErrorCodes.CursorCapacity);
                        }
                        RemoveSnapshot(candidate);
                    }

                    next = IssueCursor(snapshot, nextOffset);
                    cursorEntry.AddNext(limit, next);
                }
            }
            snapshot.PinCount++;
            var page = new AgasSceneDetailPage(
                snapshot.Scene,
                snapshot.Scene.Items.Skip(cursorEntry.Offset).Take(limit),
                next);
            return AgasSceneDetailResult<AgasSceneDetailPageLease>.Ok(
                new AgasSceneDetailPageLease(page, () => Release(snapshot.SnapshotToken)));
        }
    }

    public void RevokeScope(AgasSceneDetailProjectScope scope)
    {
        lock (_gate)
        {
            foreach (SnapshotEntry snapshot in _snapshots.Values.Where(item => item.Scope == scope).ToArray())
            {
                snapshot.Revoked = true;
                if (snapshot.PinCount == 0) RemoveSnapshot(snapshot);
            }
        }
    }

    private void Release(string snapshotToken)
    {
        lock (_gate)
        {
            if (!_snapshots.TryGetValue(snapshotToken, out SnapshotEntry? snapshot)) return;
            if (snapshot.PinCount <= 0) throw new InvalidOperationException("Scene cursor pin count underflow.");
            snapshot.PinCount--;
            if (snapshot.PinCount == 0 && (snapshot.Revoked || snapshot.ExpiresAt <= _utcNow()))
                RemoveSnapshot(snapshot);
        }
    }

    private bool WouldExceedCapacity(
        AgasSceneDetailProjectScope scope,
        long payloadBytes,
        int cursorsToAdd) =>
        _snapshots.Count + 1 > _options.MaximumSnapshots
        || _snapshots.Values.Count(item => item.Scope == scope) + 1 > _options.MaximumSnapshotsPerProject
        || _retainedUtf8Bytes + payloadBytes > _options.MaximumRetainedUtf8Bytes
        || _cursors.Count + cursorsToAdd > _options.MaximumCursors;

    private SnapshotEntry? FindEvictionCandidate(AgasSceneDetailProjectScope scope)
    {
        bool projectLimit = _snapshots.Values.Count(item => item.Scope == scope) + 1 > _options.MaximumSnapshotsPerProject;
        IEnumerable<SnapshotEntry> candidates = _snapshots.Values.Where(item => item.PinCount == 0);
        if (projectLimit) candidates = candidates.Where(item => item.Scope == scope);
        return candidates
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.SnapshotToken, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private SnapshotEntry? FindCursorEvictionCandidate(string excludedSnapshotToken) =>
        _snapshots.Values
            .Where(item => item.PinCount == 0
                           && !string.Equals(item.SnapshotToken, excludedSnapshotToken, StringComparison.Ordinal))
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.SnapshotToken, StringComparer.Ordinal)
            .FirstOrDefault();

    private void CleanupExpired(DateTimeOffset now)
    {
        foreach (SnapshotEntry snapshot in _snapshots.Values
                     .Where(item => item.PinCount == 0 && item.ExpiresAt <= now)
                     .ToArray())
        {
            RemoveSnapshot(snapshot);
        }
    }

    private string IssueCursor(SnapshotEntry snapshot, int offset)
    {
        string token = NewToken("scncur-", _cursors.ContainsKey);
        _cursors.Add(token, new CursorEntry(snapshot.SnapshotToken, offset));
        snapshot.CursorTokens.Add(token);
        return token;
    }

    private void RemoveSnapshot(SnapshotEntry snapshot)
    {
        if (!_snapshots.Remove(snapshot.SnapshotToken)) return;
        foreach (string token in snapshot.CursorTokens) _cursors.Remove(token);
        _retainedUtf8Bytes -= snapshot.PayloadBytes;
        if (_retainedUtf8Bytes < 0) throw new InvalidOperationException("Scene cursor payload accounting underflow.");
    }

    private static long MeasureMetadata(AgasSceneDetailSnapshot snapshot) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            snapshot.SceneId,
            snapshot.Name,
            snapshot.ActiveLayerId,
            snapshot.ViewState,
        }, SizeJsonOptions).LongLength;

    private long MeasureSnapshot(
        long metadataBytes,
        IReadOnlyList<AgasSceneDetailItem> items,
        out bool itemTooLarge)
    {
        itemTooLarge = false;
        long total = metadataBytes;
        foreach (AgasSceneDetailItem item in items)
        {
            int length = JsonSerializer.SerializeToUtf8Bytes(item, item.GetType(), SizeJsonOptions).Length;
            if (length > _options.MaximumItemUtf8Bytes)
            {
                itemTooLarge = true;
                return total;
            }
            total += length;
            if (total > _options.MaximumSnapshotUtf8Bytes) return total;
        }
        return total;
    }

    private static string NewToken(string prefix, Func<string, bool> exists)
    {
        string token;
        do token = prefix + Guid.NewGuid().ToString("N"); while (exists(token));
        return token;
    }

    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
    }

    private static void ValidateOptions(AgasSceneDetailCursorOptions options)
    {
        if (options.TimeToLive <= TimeSpan.Zero
            || options.MaximumSnapshots <= 0
            || options.MaximumSnapshotsPerProject <= 0
            || options.MaximumSnapshotsPerProject > options.MaximumSnapshots
            || options.MaximumCursors <= 0
            || options.MaximumItemsPerSnapshot <= 0
            || options.MaximumItemUtf8Bytes <= 0
            || options.MaximumSnapshotMetadataUtf8Bytes <= 0
            || options.MaximumSnapshotMetadataUtf8Bytes > options.MaximumSnapshotUtf8Bytes
            || options.MaximumSnapshotUtf8Bytes < options.MaximumItemUtf8Bytes
            || options.MaximumRetainedUtf8Bytes < options.MaximumSnapshotUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private sealed class SnapshotEntry
    {
        public SnapshotEntry(
            string snapshotToken,
            AgasSceneDetailProjectScope scope,
            AgasSceneDetailSnapshot scene,
            long payloadBytes,
            DateTimeOffset createdAt,
            DateTimeOffset expiresAt)
        {
            SnapshotToken = snapshotToken;
            Scope = scope;
            Scene = scene;
            PayloadBytes = payloadBytes;
            CreatedAt = createdAt;
            ExpiresAt = expiresAt;
        }

        public string SnapshotToken { get; }
        public AgasSceneDetailProjectScope Scope { get; }
        public AgasSceneDetailSnapshot Scene { get; }
        public long PayloadBytes { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset ExpiresAt { get; }
        public HashSet<string> CursorTokens { get; } = new(StringComparer.Ordinal);
        public int PinCount { get; set; }
        public bool Revoked { get; set; }
    }

    private sealed class CursorEntry
    {
        private readonly Dictionary<int, string> _nextByLimit = new();

        public CursorEntry(string snapshotToken, int offset)
        {
            SnapshotToken = snapshotToken;
            Offset = offset;
        }

        public string SnapshotToken { get; }
        public int Offset { get; }

        public bool TryGetNext(int limit, out string? token) =>
            _nextByLimit.TryGetValue(limit, out token);

        public void AddNext(int limit, string token)
        {
            _nextByLimit.Add(limit, token);
        }
    }
}
