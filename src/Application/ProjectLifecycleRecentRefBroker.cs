#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace AppAgasCs.Application;

public sealed class ProjectLifecycleRecentRefBroker
{
    public const int MaximumActiveReferences = 20;
    public static readonly TimeSpan ReferenceTtl = TimeSpan.FromMinutes(10);
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly object _gate = new();
    private readonly IProjectLifecycleClock _clock;
    private readonly IProjectLifecycleRefFactory _refFactory;
    private readonly Dictionary<string, IssuedReference> _byRef = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _refByRoot = new(PathComparer);

    public ProjectLifecycleRecentRefBroker(
        IProjectLifecycleClock? clock = null,
        IProjectLifecycleRefFactory? refFactory = null)
    {
        _clock = clock ?? new SystemProjectLifecycleClock();
        _refFactory = refFactory ?? new RandomProjectLifecycleRefFactory();
    }

    public IReadOnlyList<ProjectLifecycleRecentProject> Rotate(
        IEnumerable<ProjectLifecycleRecentEntry> currentEntries,
        string? openProjectRoot,
        int limit = MaximumActiveReferences)
    {
        ArgumentNullException.ThrowIfNull(currentEntries);
        if (limit is < 1 or > MaximumActiveReferences)
            throw new ArgumentOutOfRangeException(nameof(limit));

        ProjectLifecycleRecentEntry[] entries = currentEntries
            .Where(entry => entry.Available)
            .GroupBy(entry => entry.ProjectRoot, PathComparer)
            .Select(group => group
                .OrderByDescending(entry => entry.LastOpenedUnixTime)
                .First())
            .OrderByDescending(entry => entry.LastOpenedUnixTime)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.ProjectRoot, StringComparer.Ordinal)
            .ToArray();

        lock (_gate)
        {
            DateTimeOffset now = _clock.UtcNow;
            PruneExpired(now);
            // A recent-list call is one issuance generation. References omitted by a smaller
            // limit are revoked along with replaced roots, so an old client cannot retain a
            // capability that is no longer present in the latest list response.
            _byRef.Clear();
            _refByRoot.Clear();

            var result = new List<ProjectLifecycleRecentProject>(Math.Min(limit, entries.Length));
            foreach (ProjectLifecycleRecentEntry entry in entries.Take(limit))
            {
                string projectRef = CreateUniqueRef();
                var issued = new IssuedReference(
                    projectRef,
                    entry.ProjectRoot,
                    entry.ProjectIdentity,
                    entry.Name,
                    now,
                    now + ReferenceTtl);
                _byRef.Add(projectRef, issued);
                _refByRoot.Add(entry.ProjectRoot, projectRef);
                result.Add(new(
                    projectRef,
                    entry.Name,
                    entry.LastOpenedUnixTime,
                    true,
                    string.Equals(entry.ProjectRoot, openProjectRoot, PathComparison)));
            }

            EnforceCapacity();
            return result.AsReadOnly();
        }
    }

    public bool TryResolve(
        string projectRef,
        IEnumerable<ProjectLifecycleRecentEntry> currentEntries,
        out ProjectLifecycleResolvedRecentProject? resolved)
    {
        ArgumentNullException.ThrowIfNull(currentEntries);
        resolved = null;
        if (string.IsNullOrWhiteSpace(projectRef))
            return false;

        lock (_gate)
        {
            PruneExpired(_clock.UtcNow);
            if (!_byRef.TryGetValue(projectRef, out IssuedReference? issued))
                return false;

            ProjectLifecycleRecentEntry? current = currentEntries.FirstOrDefault(entry =>
                entry.Available
                && string.Equals(entry.ProjectRoot, issued.ProjectRoot, PathComparison));
            if (current is null
                || !string.Equals(current.ProjectIdentity, issued.ProjectIdentity, StringComparison.Ordinal))
            {
                RevokeRoot(issued.ProjectRoot);
                return false;
            }

            resolved = new(issued.ProjectRoot, issued.ProjectIdentity, current.Name);
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _byRef.Clear();
            _refByRoot.Clear();
        }
    }

    private string CreateUniqueRef()
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            string candidate = _refFactory.CreateProjectRef();
            if (candidate.Length == 43
                && candidate.StartsWith("projectref-", StringComparison.Ordinal)
                && candidate[11..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
                && !_byRef.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Unable to issue a unique opaque project reference.");
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (IssuedReference expired in _byRef.Values
                     .Where(item => item.ExpiresAt <= now)
                     .OrderBy(item => item.ExpiresAt)
                     .ThenBy(item => item.IssuedAt)
                     .ToArray())
        {
            RevokeRoot(expired.ProjectRoot);
        }
    }

    private void EnforceCapacity()
    {
        foreach (IssuedReference evicted in _byRef.Values
                     .OrderBy(item => item.ExpiresAt)
                     .ThenBy(item => item.IssuedAt)
                     .ThenBy(item => item.ProjectRef, StringComparer.Ordinal)
                     .Take(Math.Max(0, _byRef.Count - MaximumActiveReferences))
                     .ToArray())
        {
            RevokeRoot(evicted.ProjectRoot);
        }
    }

    private void RevokeRoot(string projectRoot)
    {
        if (_refByRoot.Remove(projectRoot, out string? oldRef))
            _byRef.Remove(oldRef);
    }

    private sealed record IssuedReference(
        string ProjectRef,
        string ProjectRoot,
        string ProjectIdentity,
        string Name,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt);
}
