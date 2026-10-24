#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AppAgasCs.Core.Persistence;

namespace AppAgasCs.Application;

public enum AgasImportKind { Asset, ScenePackage }

public sealed record AgasImportInboxItem(
    string Kind,
    string Name,
    long SizeBytes,
    string MediaType,
    string ValidationState,
    string SourceRef);

public sealed record AgasImportInboxPage
{
    public AgasImportInboxPage(
        IEnumerable<AgasImportInboxItem> items,
        int totalCount,
        bool hasMore,
        int? nextOffset)
    {
        Items = new ReadOnlyCollection<AgasImportInboxItem>(items.ToArray());
        Count = Items.Count;
        TotalCount = totalCount;
        HasMore = hasMore;
        NextOffset = nextOffset;
    }

    public IReadOnlyList<AgasImportInboxItem> Items { get; }
    public int Count { get; }
    public int TotalCount { get; }
    public bool HasMore { get; }
    public int? NextOffset { get; }
}

public sealed record AgasImportSourceGrant(
    AgasImportKind Kind,
    string AgasRoot,
    string SourcePath,
    string SafeName,
    long SizeBytes,
    DateTimeOffset ExpiresAtUtc,
    bool ImportAllowed,
    IReadOnlyDictionary<string, string> NativeIdentities);

/// <summary>
/// Issues process-local, signed, expiring capabilities for import sources. The token contains
/// only a random id and a MAC; paths and names remain server-side and never enter MCP output.
/// </summary>
public sealed class AgasImportSourceRefBroker
{
    private sealed record Entry(AgasImportSourceGrant Grant, string Signature, long Sequence);

    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);
    public const int DefaultMaximumEntries = 4_096;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _prepared = new(StringComparer.Ordinal);
    private readonly byte[] _key;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _lifetime;
    private readonly int _maximumEntries;
    private readonly ScenePackagePathGuard _guard;
    private long _nextSequence;

    public AgasImportSourceRefBroker(
        ScenePackagePathGuard? guard = null,
        TimeProvider? timeProvider = null,
        TimeSpan? lifetime = null,
        byte[]? signingKey = null,
        int maximumEntries = DefaultMaximumEntries)
    {
        _guard = guard ?? new ScenePackagePathGuard();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifetime = lifetime ?? DefaultLifetime;
        if (_lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        if (maximumEntries < 1) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        _maximumEntries = maximumEntries;
        _key = signingKey?.ToArray() ?? RandomNumberGenerator.GetBytes(32);
        if (_key.Length < 32) throw new ArgumentException("The source-ref signing key must contain at least 32 bytes.", nameof(signingKey));
    }

    public string Issue(
        AgasImportKind kind,
        string agasRoot,
        string sourcePath,
        string safeName,
        long sizeBytes,
        IReadOnlyDictionary<string, string> nativeIdentities,
        bool importAllowed = true)
    {
        string sourceRef = Prepare(
            kind, agasRoot, sourcePath, safeName, sizeBytes, nativeIdentities, importAllowed);
        if (!ActivatePrepared(sourceRef))
            throw new InvalidOperationException("The prepared import source capability could not be activated.");
        return sourceRef;
    }

    internal string Prepare(
        AgasImportKind kind,
        string agasRoot,
        string sourcePath,
        string safeName,
        long sizeBytes,
        IReadOnlyDictionary<string, string> nativeIdentities,
        bool importAllowed = true)
    {
        ArgumentNullException.ThrowIfNull(nativeIdentities);
        string root = Path.GetFullPath(agasRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string source = Path.GetFullPath(sourcePath);
        if (!_guard.IsSafeBasename(safeName) || sizeBytes < 0 || nativeIdentities.Count == 0)
            throw new InvalidDataException("Import source capability is invalid.");
        string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        string signature = Sign(id);
        var grant = new AgasImportSourceGrant(
            kind,
            root,
            source,
            safeName,
            sizeBytes,
            _timeProvider.GetUtcNow().Add(_lifetime),
            importAllowed,
            new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(nativeIdentities, StringComparer.Ordinal)));
        lock (_gate)
        {
            PruneExpiredLocked();
            _prepared[id] = new Entry(grant, signature, checked(++_nextSequence));
        }
        return $"src1-{id}-{signature}";
    }

    internal bool ActivatePrepared(string sourceRef)
    {
        if (!TryParse(sourceRef, out string id, out string signature)) return false;
        lock (_gate)
        {
            PruneExpiredLocked(retainPreparedId: id);
            if (!_prepared.TryGetValue(id, out Entry? prepared)
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(prepared.Signature), Encoding.ASCII.GetBytes(signature)))
                return false;
            prepared = prepared with
            {
                Grant = prepared.Grant with { ExpiresAtUtc = _timeProvider.GetUtcNow().Add(_lifetime) },
            };
            while (_entries.Count >= _maximumEntries)
            {
                string oldest = _entries
                    .OrderBy(item => item.Value.Sequence)
                    .ThenBy(item => item.Key, StringComparer.Ordinal)
                    .First().Key;
                _entries.Remove(oldest);
            }
            _prepared.Remove(id);
            _entries[id] = prepared;
            return true;
        }
    }

    internal int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                PruneExpiredLocked();
                return _entries.Count;
            }
        }
    }

    public bool TryResolve(string sourceRef, AgasImportKind expectedKind, string agasRoot, out AgasImportSourceGrant? grant)
    {
        grant = null;
        if (!TryParse(sourceRef, out string id, out string signature)) return false;
        string root;
        try { root = Path.GetFullPath(agasRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return false; }
        lock (_gate)
        {
            PruneExpiredLocked();
            if (!_entries.TryGetValue(id, out Entry? entry)
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(entry.Signature), Encoding.ASCII.GetBytes(signature))
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(Sign(id)), Encoding.ASCII.GetBytes(signature))
                || entry.Grant.Kind != expectedKind
                || !entry.Grant.ImportAllowed
                || !string.Equals(entry.Grant.AgasRoot, root, StringComparison.Ordinal)
                || !NativeIdentitiesMatch(entry.Grant.NativeIdentities))
                return false;
            grant = entry.Grant;
            return true;
        }
    }

    public void Revoke(string sourceRef)
    {
        if (!TryParse(sourceRef, out string id, out string signature)) return;
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out Entry? entry)
                && CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(entry.Signature), Encoding.ASCII.GetBytes(signature)))
                _entries.Remove(id);
            if (_prepared.TryGetValue(id, out Entry? prepared)
                && CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(prepared.Signature), Encoding.ASCII.GetBytes(signature)))
                _prepared.Remove(id);
        }
    }

    private bool NativeIdentitiesMatch(IReadOnlyDictionary<string, string> expected)
    {
        foreach ((string path, string token) in expected)
        {
            ProjectAccessPathIdentity identity;
            try { identity = _guard.Capture(path); }
            catch { return false; }
            if (!identity.Exists || identity.IsLinkOrReparse
                || !string.Equals(identity.IdentityToken, token, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private void PruneExpiredLocked(string? retainPreparedId = null)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        foreach (string id in _entries.Where(item => item.Value.Grant.ExpiresAtUtc <= now).Select(item => item.Key).ToArray())
            _entries.Remove(id);
        foreach (string id in _prepared
                     .Where(item => item.Key != retainPreparedId && item.Value.Grant.ExpiresAtUtc <= now)
                     .Select(item => item.Key).ToArray())
            _prepared.Remove(id);
    }

    private string Sign(string id) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes($"agas-import-source-v1|{id}")))
            .ToLowerInvariant();

    private static bool TryParse(string? sourceRef, out string id, out string signature)
    {
        id = string.Empty;
        signature = string.Empty;
        if (sourceRef == null || sourceRef.Length != 102 || !sourceRef.StartsWith("src1-", StringComparison.Ordinal))
            return false;
        id = sourceRef.Substring(5, 32);
        signature = sourceRef.Substring(38, 64);
        return id.All(IsLowerHex) && signature.All(IsLowerHex) && sourceRef[37] == '-';
    }

    private static bool IsLowerHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';
}

/// <summary>Lightweight discovery for controlled asset/package inboxes and global package exchange.</summary>
public sealed class AgasImportInboxService
{
    internal const int MaximumPackageTreeEntries = 2_048;
    internal const int MaximumPackageTreeDepth = 8;
    private readonly ScenePackagePathGuard _guard;
    private readonly AgasImportSourceRefBroker _broker;

    public AgasImportInboxService(
        AgasImportSourceRefBroker broker,
        ScenePackagePathGuard? guard = null)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _guard = guard ?? new ScenePackagePathGuard();
    }

    public AgasImportInboxPage List(
        string agasHome,
        AgasImportKind? kind = null,
        int limit = 50,
        int offset = 0)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        string root = Path.GetFullPath(agasHome);
        var candidates = new List<(AgasImportKind Kind, string Name, string Path, string Origin)>();
        if (kind is null or AgasImportKind.Asset)
            AddAssetCandidates(root, candidates);
        if (kind is null or AgasImportKind.ScenePackage)
        {
            AddPackageCandidates(root, "import-inbox/scene-packages", "inbox", candidates);
            AddPackageCandidates(root, "import-exchange/scene-packages", "exchange", candidates);
        }

        var ordered = candidates
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Origin, StringComparer.Ordinal)
            .ToArray();
        AgasImportInboxItem[] page = ordered
            .Skip(offset)
            .Take(limit)
            .Select(item => BuildItem(root, item.Kind, item.Name, item.Path))
            .Where(item => item != null)
            .Cast<AgasImportInboxItem>()
            .ToArray();
        bool hasMore = offset + Math.Min(limit, Math.Max(0, ordered.Length - offset)) < ordered.Length;
        return new AgasImportInboxPage(
            page,
            ordered.Length,
            hasMore,
            hasMore ? offset + Math.Min(limit, ordered.Length - offset) : null);
    }

    private void AddAssetCandidates(string root, List<(AgasImportKind, string, string, string)> candidates)
    {
        if (!_guard.TryResolveDirectory(root, "import-inbox/assets", mustExist: true, out string directory)) return;
        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(path);
            if (_guard.IsSafeBasename(name)) candidates.Add((AgasImportKind.Asset, name, path, "inbox"));
        }
    }

    private void AddPackageCandidates(
        string root,
        string relativeRoot,
        string origin,
        List<(AgasImportKind, string, string, string)> candidates)
    {
        if (!_guard.TryResolveDirectory(root, relativeRoot, mustExist: true, out string directory)) return;
        foreach (string path in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(path);
            if (name == ".staging" || !_guard.IsSafeBasename(name)) continue;
            ProjectAccessPathIdentity identity = _guard.Capture(path);
            if (identity.Exists && identity.IsDirectory && !identity.IsLinkOrReparse)
                candidates.Add((AgasImportKind.ScenePackage, name, path, origin));
        }
    }

    private AgasImportInboxItem? BuildItem(string root, AgasImportKind kind, string name, string path)
    {
        try
        {
            ProjectAccessPathIdentity sourceIdentity = _guard.Capture(path);
            if (!sourceIdentity.Exists || sourceIdentity.IsLinkOrReparse
                || sourceIdentity.IsDirectory != (kind == AgasImportKind.ScenePackage)) return null;
            long size;
            string mediaType;
            string validation;
            var identities = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Path.GetFullPath(path)] = sourceIdentity.IdentityToken,
            };
            if (kind == AgasImportKind.Asset)
            {
                if (!_guard.TryResolveExistingFile(Path.GetDirectoryName(path)!, name, out string safePath)
                    || !string.Equals(safePath, Path.GetFullPath(path), StringComparison.Ordinal)) return null;
                string extension = Path.GetExtension(name).ToLowerInvariant();
                size = new FileInfo(path).Length;
                mediaType = extension switch
                {
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".webp" => "image/webp",
                    _ => "application/octet-stream",
                };
                validation = ProjectPathRules.AllowedImageExtensionsWithDot.Contains(extension)
                             && size <= ScenePackageSafetyLimits.MaximumImageBytes
                    ? "unverified"
                    : "invalid";
            }
            else
            {
                if (!TryCollectPackageTreeIdentities(path, _guard, out size, out Dictionary<string, string> packageIdentities))
                {
                    size = 0;
                    mediaType = "application/vnd.agas.scene-package";
                    validation = "invalid";
                }
                else
                {
                    foreach ((string packagePath, string token) in packageIdentities)
                        identities[packagePath] = token;
                    string manifest = Path.Combine(path, "scene.json");
                    mediaType = "application/vnd.agas.scene-package";
                    validation = File.Exists(manifest)
                                 && new FileInfo(manifest).Length <= ScenePackageSafetyLimits.MaximumManifestBytes
                                 && size <= ScenePackageSafetyLimits.MaximumPackagePayloadBytes
                        ? "unverified"
                        : "invalid";
                }
            }
            string sourceRef = _broker.Issue(
                kind, root, path, name, size, identities,
                importAllowed: !string.Equals(validation, "invalid", StringComparison.Ordinal));
            return new AgasImportInboxItem(
                kind == AgasImportKind.Asset ? "asset" : "scenePackage",
                name,
                size,
                mediaType,
                validation,
                sourceRef);
        }
        catch
        {
            return null;
        }
    }

    internal static bool TryCollectPackageTreeIdentities(
        string packageDirectory,
        ScenePackagePathGuard guard,
        out long sizeBytes,
        out Dictionary<string, string> identities)
    {
        sizeBytes = 0;
        identities = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            string root = Path.GetFullPath(packageDirectory);
            ProjectAccessPathIdentity rootIdentity = guard.Capture(root);
            if (!rootIdentity.Exists || !rootIdentity.IsDirectory || rootIdentity.IsLinkOrReparse) return false;
            identities[root] = rootIdentity.IdentityToken;
            int entryCount = 0;
            var pending = new Stack<(string Directory, int Depth)>();
            pending.Push((root, 0));
            while (pending.Count > 0)
            {
                (string directory, int depth) = pending.Pop();
                if (depth >= MaximumPackageTreeDepth) return false;
                foreach (string rawEntry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (++entryCount > MaximumPackageTreeEntries) return false;
                    string entry = Path.GetFullPath(rawEntry);
                    if (!string.Equals(Path.GetDirectoryName(entry), directory, StringComparison.Ordinal)) return false;
                    ProjectAccessPathIdentity identity = guard.Capture(entry);
                    if (!identity.Exists || identity.IsLinkOrReparse) return false;
                    identities[entry] = identity.IdentityToken;
                    if (identity.IsDirectory)
                    {
                        pending.Push((entry, depth + 1));
                        continue;
                    }
                    long length = new FileInfo(entry).Length;
                    sizeBytes = checked(sizeBytes + length);
                    if (sizeBytes > ScenePackageSafetyLimits.MaximumPackagePayloadBytes) return false;
                }
            }
            return true;
        }
        catch
        {
            sizeBytes = 0;
            identities.Clear();
            return false;
        }
    }
}
