#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AppAgasCs.Core.Persistence;

/// <summary>Line-based journal avoids a second JSON persistence owner.</summary>
public sealed class ScenePackageImportJournalStore
{
    public sealed record CleanupLease(
        string ProjectRoot,
        string OriginalDirectory,
        string QuarantineDirectory,
        ScenePackageImportJournal Journal)
    {
        internal IReadOnlyList<string> ConsumedStagingArtifactRelativePaths { get; init; } =
            Array.Empty<string>();
    }

    internal sealed record PreparedCleanupLease(
        CleanupLease Cleanup,
        string HoldingDirectory,
        IReadOnlyList<string> ContentFilesToDelete,
        IReadOnlyList<string> DirectoriesToDelete,
        IReadOnlyList<string> EvidenceFilesToDelete);

    internal interface ICleanupFaultInjector
    {
        void BeforeIrreversiblePurge(string holdingDirectory);
        void AfterIrreversibleDelete(string path, int deletedEntryCount);
    }

    public const string JournalFileName = "journal.v1";
    public const string OwnerMarkerFileName = "owner.v1";
    private readonly ScenePackagePathGuard _guard;
    private readonly ICleanupFaultInjector? _cleanupFaultInjector;

    public ScenePackageImportJournalStore(ScenePackagePathGuard? guard = null) =>
        _guard = guard ?? new ScenePackagePathGuard();

    internal ScenePackageImportJournalStore(
        ScenePackagePathGuard guard,
        ICleanupFaultInjector cleanupFaultInjector)
    {
        _guard = guard;
        _cleanupFaultInjector = cleanupFaultInjector;
    }

    public ScenePackageImportJournal CreatePending(
        string projectRoot,
        string operationId,
        string newSceneId,
        string expectedSceneRelativePath)
    {
        if (!_guard.IsSafeReference(operationId) || !_guard.IsSafeReference(newSceneId))
            throw new InvalidDataException("Package journal identifiers are invalid.");
        string expectedScene = $".agas/scenes/{newSceneId}.tres";
        if (!string.Equals(expectedSceneRelativePath, expectedScene, StringComparison.Ordinal)
            || !_guard.TryResolveOwnedTarget(projectRoot, expectedSceneRelativePath, out _))
            throw new InvalidDataException("Package journal scene target is invalid.");
        string relativeDirectory = $".agas/import-staging/{operationId}";
        if (!_guard.TryResolveDirectory(projectRoot, ".agas", mustExist: true, out _))
            throw new InvalidDataException("Project persistence root is unsafe.");
        if (!_guard.TryResolveDirectory(projectRoot, ".agas/import-staging", mustExist: false, out _))
            throw new InvalidDataException("Package staging root is unsafe.");
        string directory = Path.Combine(Path.GetFullPath(projectRoot), ".agas", "import-staging", operationId);
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new InvalidDataException("Package staging operation already exists.");
        Directory.CreateDirectory(directory);
        if (!_guard.TryResolveDirectory(projectRoot, relativeDirectory, mustExist: true, out directory))
            throw new InvalidDataException("Package staging directory is unsafe.");

        string ownerToken = Guid.NewGuid().ToString("N");
        string markerPath = Path.Combine(directory, OwnerMarkerFileName);
        try
        {
            File.WriteAllLines(markerPath, new[] { "version=1", operationId, ownerToken }, new UTF8Encoding(false));
            ProjectAccessPathIdentity projectIdentity = _guard.Capture(projectRoot);
            if (!projectIdentity.Exists || !projectIdentity.IsDirectory || projectIdentity.IsLinkOrReparse)
                throw new InvalidDataException("Project identity is unsafe.");
            var journal = new ScenePackageImportJournal(
                operationId,
                ownerToken,
                projectIdentity.IdentityToken,
                newSceneId,
                expectedSceneRelativePath,
                _guard.CaptureOwnedFileIdentityToken(
                    markerPath, markerPath, operationId, ownerToken),
                Array.Empty<ScenePackageOwnedArtifact>(),
                Array.Empty<ScenePackageOwnedStagingArtifact>());
            Write(projectRoot, journal);
            return journal;
        }
        catch
        {
            try
            {
                if (File.Exists(markerPath)
                    && File.ReadAllLines(markerPath).SequenceEqual(new[] { "version=1", operationId, ownerToken }))
                    File.Delete(markerPath);
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory, recursive: false);
            }
            catch { }
            throw;
        }
    }

    public ScenePackageImportJournal UpdateArtifacts(
        string projectRoot,
        ScenePackageImportJournal journal,
        IEnumerable<ScenePackageOwnedArtifact> artifacts,
        IEnumerable<ScenePackageOwnedStagingArtifact>? stagingArtifacts = null,
        IEnumerable<ScenePackageStagingArtifactPublication>? stagingArtifactPublications = null)
    {
        if (!TryRead(projectRoot, journal.OperationId, out ScenePackageImportJournal? current)
            || current == null
            || current.OwnerToken != journal.OwnerToken
            || current.MarkerIdentityToken != journal.MarkerIdentityToken)
            throw new InvalidDataException("Package journal ownership changed.");
        ScenePackageOwnedArtifact[] validatedArtifacts = artifacts
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();
        if (!ValidateArtifacts(journal.OperationId, validatedArtifacts))
            throw new InvalidDataException("Package journal artifacts are invalid.");
        ScenePackageOwnedStagingArtifact[] validatedStagingArtifacts = (stagingArtifacts ?? current.OwnedStagingArtifacts)
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();
        if (!ValidateStagingArtifacts(journal.OperationId, validatedStagingArtifacts))
            throw new InvalidDataException("Package journal staging artifacts are invalid.");
        ScenePackageStagingArtifactPublication[] validatedPublications =
            (stagingArtifactPublications ?? current.StagingArtifactPublications)
            .OrderBy(item => item.StagingRelativePath, StringComparer.Ordinal)
            .ToArray();
        if (!ValidatePublications(
                journal.OperationId,
                validatedArtifacts,
                validatedStagingArtifacts,
                validatedPublications))
            throw new InvalidDataException("Package journal staging publications are invalid.");
        ScenePackageImportJournal updated = new(
            journal.OperationId,
            journal.OwnerToken,
            journal.ProjectIdentityToken,
            journal.NewSceneId,
            journal.ExpectedSceneRelativePath,
            journal.MarkerIdentityToken,
            validatedArtifacts,
            validatedStagingArtifacts,
            validatedPublications);
        Write(projectRoot, updated);
        return updated;
    }

    public bool TryRead(string projectRoot, string operationId, out ScenePackageImportJournal? journal)
    {
        journal = null;
        if (!_guard.IsSafeReference(operationId)) return false;
        string relativeDirectory = $".agas/import-staging/{operationId}";
        if (!_guard.TryResolveDirectory(projectRoot, relativeDirectory, mustExist: true, out string directory)) return false;
        return TryReadAtDirectory(projectRoot, operationId, directory, directory, out journal);
    }

    private bool TryReadAtDirectory(
        string projectRoot,
        string operationId,
        string directory,
        string canonicalDirectory,
        out ScenePackageImportJournal? journal)
    {
        journal = null;
        string markerPath = Path.Combine(directory, OwnerMarkerFileName);
        string journalPath = Path.Combine(directory, JournalFileName);
        string project = Path.GetFullPath(projectRoot);
        string markerRelative = Path.GetRelativePath(project, markerPath).Replace('\\', '/');
        string journalRelative = Path.GetRelativePath(project, journalPath).Replace('\\', '/');
        if (!_guard.TryResolveExistingFile(projectRoot, markerRelative, out markerPath)
            || !_guard.TryResolveExistingFile(projectRoot, journalRelative, out journalPath)) return false;
        try
        {
            string[] marker = File.ReadAllLines(markerPath, Encoding.UTF8);
            string[] lines = File.ReadAllLines(journalPath, Encoding.UTF8);
            if (marker.Length != 3 || marker[0] != "version=1" || lines.Length < 8
                || lines[0] is not ("version=1" or "version=2" or "version=3")) return false;
            string op = Decode(lines[1]);
            string owner = Decode(lines[2]);
            if (marker[1] != op || marker[2] != owner || op != operationId) return false;
            ProjectAccessPathIdentity markerIdentity = _guard.Capture(markerPath);
            if (markerIdentity.IsLinkOrReparse
                || _guard.CaptureOwnedFileIdentityToken(
                    markerPath, Path.Combine(canonicalDirectory, OwnerMarkerFileName), op, owner) != Decode(lines[6])) return false;
            int count = int.Parse(lines[7], System.Globalization.CultureInfo.InvariantCulture);
            if (count < 0 || lines.Length < 8 + count) return false;
            var artifacts = new List<ScenePackageOwnedArtifact>();
            for (int index = 0; index < count; index++)
            {
                string[] fields = lines[8 + index].Split('|');
                if (fields.Length != 2) return false;
                artifacts.Add(new ScenePackageOwnedArtifact(Decode(fields[0]), Decode(fields[1])));
            }
            var stagingArtifacts = new List<ScenePackageOwnedStagingArtifact>();
            var publications = new List<ScenePackageStagingArtifactPublication>();
            if (lines[0] == "version=1")
            {
                if (lines.Length != 8 + count) return false;
            }
            else
            {
                int stagingCountLine = 8 + count;
                if (lines.Length <= stagingCountLine) return false;
                int stagingCount = int.Parse(lines[stagingCountLine], System.Globalization.CultureInfo.InvariantCulture);
                int publicationCountLine = stagingCountLine + 1 + stagingCount;
                if (stagingCount < 0
                    || (lines[0] == "version=2" && lines.Length != publicationCountLine)
                    || (lines[0] == "version=3" && lines.Length <= publicationCountLine)) return false;
                for (int index = 0; index < stagingCount; index++)
                {
                    string[] fields = lines[stagingCountLine + 1 + index].Split('|');
                    if (fields.Length != 3
                        || !Enum.TryParse(Decode(fields[0]), ignoreCase: false, out ScenePackageStagingArtifactKind kind))
                        return false;
                    stagingArtifacts.Add(new ScenePackageOwnedStagingArtifact(
                        Decode(fields[1]), kind, Decode(fields[2])));
                }
                if (lines[0] == "version=3")
                {
                    int publicationCount = int.Parse(
                        lines[publicationCountLine], System.Globalization.CultureInfo.InvariantCulture);
                    if (publicationCount < 0 || lines.Length != publicationCountLine + 1 + publicationCount)
                        return false;
                    for (int index = 0; index < publicationCount; index++)
                    {
                        string[] fields = lines[publicationCountLine + 1 + index].Split('|');
                        if (fields.Length != 2) return false;
                        publications.Add(new ScenePackageStagingArtifactPublication(
                            Decode(fields[0]), Decode(fields[1])));
                    }
                }
            }
            string projectIdentity = Decode(lines[3]);
            string sceneId = Decode(lines[4]);
            string expectedScene = Decode(lines[5]);
            if (!_guard.IsSafeReference(sceneId)
                || expectedScene != $".agas/scenes/{sceneId}.tres"
                || !_guard.TryResolveOwnedTarget(projectRoot, expectedScene, out _)
                || !ValidateArtifacts(op, artifacts)
                || !ValidateStagingArtifacts(op, stagingArtifacts)
                || !ValidatePublications(op, artifacts, stagingArtifacts, publications)) return false;
            journal = new ScenePackageImportJournal(
                op, owner, projectIdentity, sceneId, expectedScene, Decode(lines[6]), artifacts, stagingArtifacts,
                publications);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool HasOperationEntry(string projectRoot, string operationId)
    {
        if (!_guard.IsSafeReference(operationId)) return false;
        try
        {
            string project = Path.GetFullPath(projectRoot);
            ProjectAccessPathIdentity projectIdentity = _guard.Capture(project);
            if (!projectIdentity.Exists || !projectIdentity.IsDirectory || projectIdentity.IsLinkOrReparse)
                return true;
            foreach (string ancestor in new[]
                     {
                         Path.Combine(project, ".agas"),
                         Path.Combine(project, ".agas", "import-staging"),
                     })
            {
                ProjectAccessPathIdentity ancestorIdentity = _guard.Capture(ancestor);
                if (ancestorIdentity.Exists
                    && (!ancestorIdentity.IsDirectory || ancestorIdentity.IsLinkOrReparse)) return true;
            }
            string operationPath = Path.Combine(project, ".agas", "import-staging", operationId);
            return _guard.Capture(operationPath).Exists;
        }
        catch
        {
            return true;
        }
    }

    public bool HasOperationDirectory(string projectRoot, string operationId) =>
        HasOperationEntry(projectRoot, operationId);

    public bool DeleteJournalDirectoryIfOwned(string projectRoot, ScenePackageImportJournal journal)
    {
        return TryBeginCleanup(projectRoot, journal, out CleanupLease? lease)
            && lease != null
            && CompleteCleanup(lease);
    }

    public bool TryBeginCleanup(
        string projectRoot,
        ScenePackageImportJournal journal,
        out CleanupLease? lease)
    {
        lease = null;
        if (!TryValidateReconcileStagingTree(
                projectRoot,
                journal,
                out string directory,
                out IReadOnlyList<string> consumedStagingArtifacts)) return false;
        string stagingRoot = Path.GetDirectoryName(directory)!;
        string quarantine = Path.Combine(
            stagingRoot, $".reconcile-{journal.OperationId}-{Guid.NewGuid():N}");
        if (File.Exists(quarantine) || Directory.Exists(quarantine)) return false;
        try
        {
            Directory.Move(directory, quarantine);
            if (!TryReadAtDirectory(projectRoot, journal.OperationId, quarantine, directory, out ScenePackageImportJournal? moved)
                || moved == null
                || !SameJournal(moved, journal)
                || !TryValidateStagingTreeAt(
                    projectRoot,
                    journal,
                    quarantine,
                    directory,
                    consumedStagingArtifacts)
                || _guard.Capture(directory).Exists)
            {
                TryRestoreQuarantine(directory, quarantine);
                return false;
            }
            lease = new CleanupLease(Path.GetFullPath(projectRoot), directory, quarantine, journal)
            {
                ConsumedStagingArtifactRelativePaths = consumedStagingArtifacts,
            };
            return true;
        }
        catch
        {
            TryRestoreQuarantine(directory, quarantine);
            return false;
        }
    }

    public bool CompleteCleanup(CleanupLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return TryPrepareCleanup(lease, out PreparedCleanupLease? prepared)
            && prepared != null
            && TryAuthorizePreparedCleanup(prepared)
            && CompletePreparedCleanup(prepared, purgeAuthorized: true);
    }

    internal bool TryPrepareCleanup(
        CleanupLease lease,
        out PreparedCleanupLease? prepared)
    {
        ArgumentNullException.ThrowIfNull(lease);
        prepared = null;
        ScenePackageImportJournal journal = lease.Journal;
        string directory = lease.QuarantineDirectory;
        if (_guard.Capture(lease.OriginalDirectory).Exists
            || !TryReadAtDirectory(
                lease.ProjectRoot, journal.OperationId, directory, lease.OriginalDirectory, out ScenePackageImportJournal? current)
            || current == null
            || !SameJournal(current, journal)
            || !TryValidateStagingTreeAt(
                lease.ProjectRoot,
                journal,
                directory,
                lease.OriginalDirectory,
                lease.ConsumedStagingArtifactRelativePaths))
        {
            TryRestoreQuarantine(lease.OriginalDirectory, lease.QuarantineDirectory);
            return false;
        }
        string holding = Path.Combine(
            Path.GetDirectoryName(directory)!, $".cleanup-{journal.OperationId}-{Guid.NewGuid():N}");
        var movedNames = new List<string>();
        try
        {
            Directory.CreateDirectory(holding);
            foreach (string source in Directory.EnumerateFileSystemEntries(directory)
                         .Where(path => Path.GetFileName(path) is not (JournalFileName or OwnerMarkerFileName))
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                string name = Path.GetFileName(source);
                MoveEntry(source, Path.Combine(holding, name));
                movedNames.Add(name);
            }
            File.Move(Path.Combine(directory, JournalFileName), Path.Combine(holding, JournalFileName));
            movedNames.Add(JournalFileName);
            File.Move(Path.Combine(directory, OwnerMarkerFileName), Path.Combine(holding, OwnerMarkerFileName));
            movedNames.Add(OwnerMarkerFileName);

            if (Directory.EnumerateFileSystemEntries(directory).Any()
                || !TryReadAtDirectory(
                    lease.ProjectRoot, journal.OperationId, holding, lease.OriginalDirectory, out current)
                || current == null
                || !SameJournal(current, journal)
                || !TryValidateStagingTreeAt(
                    lease.ProjectRoot,
                    journal,
                    holding,
                    lease.OriginalDirectory,
                    lease.ConsumedStagingArtifactRelativePaths))
                return RollBackCleanupLease(lease, holding, movedNames);
        }
        catch
        {
            return RollBackCleanupLease(lease, holding, movedNames);
        }

        prepared = new PreparedCleanupLease(
            lease,
            holding,
            journal.OwnedStagingArtifacts
                .Where(item => item.Kind == ScenePackageStagingArtifactKind.File)
                .OrderByDescending(item => item.RelativePath.Length)
                .Select(item => RebaseStagingArtifact(journal.OperationId, item.RelativePath, holding))
                .ToArray(),
            journal.OwnedStagingArtifacts
                .Where(item => item.Kind == ScenePackageStagingArtifactKind.Directory)
                .OrderByDescending(item => item.RelativePath.Count(value => value == '/'))
                .Select(item => RebaseStagingArtifact(journal.OperationId, item.RelativePath, holding))
                .ToArray(),
            new[]
            {
                Path.Combine(holding, JournalFileName),
                Path.Combine(holding, OwnerMarkerFileName),
            }
                .ToArray());
        return true;
    }

    internal bool TryAuthorizePreparedCleanup(PreparedCleanupLease prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        try
        {
            _cleanupFaultInjector?.BeforeIrreversiblePurge(prepared.HoldingDirectory);
            return true;
        }
        catch
        {
            RollBackPreparedCleanup(prepared);
            return false;
        }
    }

    internal bool CompletePreparedCleanup(
        PreparedCleanupLease prepared,
        bool purgeAuthorized = false)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!purgeAuthorized && !TryAuthorizePreparedCleanup(prepared)) return false;
        PurgeHoldingDirectory(prepared);
        try
        {
            Directory.Delete(prepared.Cleanup.QuarantineDirectory, recursive: false);
        }
        catch { }
        return true;
    }

    internal void CancelPreparedCleanup(PreparedCleanupLease prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        RollBackPreparedCleanup(prepared);
    }

    public void CancelCleanup(CleanupLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        TryRestoreQuarantine(lease.OriginalDirectory, lease.QuarantineDirectory);
    }

    public bool TryValidateStagingTree(
        string projectRoot,
        ScenePackageImportJournal journal,
        out string operationDirectory)
    {
        operationDirectory = string.Empty;
        if (!TryRead(projectRoot, journal.OperationId, out ScenePackageImportJournal? current)
            || current == null
            || !SameJournal(current, journal)
            || !_guard.TryResolveDirectory(projectRoot,
                $".agas/import-staging/{journal.OperationId}", mustExist: true, out operationDirectory))
            return false;

        return TryValidateStagingTreeAt(projectRoot, journal, operationDirectory, operationDirectory);
    }

    internal bool TryValidateReconcileStagingTree(
        string projectRoot,
        ScenePackageImportJournal journal,
        out string operationDirectory,
        out IReadOnlyList<string> consumedStagingArtifacts)
    {
        operationDirectory = string.Empty;
        consumedStagingArtifacts = Array.Empty<string>();
        if (!TryRead(projectRoot, journal.OperationId, out ScenePackageImportJournal? current)
            || current == null
            || !SameJournal(current, journal)
            || !_guard.TryResolveDirectory(projectRoot,
                $".agas/import-staging/{journal.OperationId}", mustExist: true, out operationDirectory))
            return false;

        var consumed = new List<string>();
        if (!TryValidateStagingTreeAt(
                projectRoot,
                journal,
                operationDirectory,
                operationDirectory,
                allowedMissingStagingArtifacts: null,
                consumedStagingArtifacts: consumed))
            return false;
        consumedStagingArtifacts = consumed;
        return true;
    }

    private bool TryValidateStagingTreeAt(
        string projectRoot,
        ScenePackageImportJournal journal,
        string operationDirectory,
        string canonicalOperationDirectory,
        IReadOnlyCollection<string>? allowedMissingStagingArtifacts = null,
        List<string>? consumedStagingArtifacts = null)
    {
        string project = Path.GetFullPath(projectRoot);
        string markerPath = Path.Combine(operationDirectory, OwnerMarkerFileName);
        var expected = new Dictionary<string, (
            ScenePackageStagingArtifactKind Kind,
            string? Token,
            string CanonicalPath)>(StringComparer.Ordinal)
        {
            [markerPath] = (
                ScenePackageStagingArtifactKind.File,
                journal.MarkerIdentityToken,
                Path.Combine(canonicalOperationDirectory, OwnerMarkerFileName)),
            [Path.Combine(operationDirectory, JournalFileName)] = (
                ScenePackageStagingArtifactKind.File,
                null,
                Path.Combine(canonicalOperationDirectory, JournalFileName)),
        };
        foreach (ScenePackageOwnedStagingArtifact artifact in journal.OwnedStagingArtifacts)
        {
            string canonical = Path.Combine(project, Path.Combine(artifact.RelativePath.Split('/')));
            string path = RebaseStagingArtifact(journal.OperationId, artifact.RelativePath, operationDirectory);
            if (!expected.TryAdd(path, (artifact.Kind, artifact.IdentityToken, canonical))) return false;
        }

        var pending = new Stack<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        pending.Push(operationDirectory);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory).ToArray(); }
            catch { return false; }
            foreach (string rawEntry in entries)
            {
                string entry;
                try { entry = Path.GetFullPath(rawEntry); }
                catch { return false; }
                if (!expected.TryGetValue(entry, out var expectedEntry) || !seen.Add(entry)) return false;
                ProjectAccessPathIdentity identity;
                try { identity = _guard.Capture(entry); }
                catch { return false; }
                if (!identity.Exists || identity.IsLinkOrReparse
                    || identity.IsDirectory != (expectedEntry.Kind == ScenePackageStagingArtifactKind.Directory))
                    return false;
                if (expectedEntry.Token != null)
                {
                    if (!IdentityMatches(
                            entry,
                            expectedEntry.CanonicalPath,
                            expectedEntry.Kind,
                            expectedEntry.Token,
                            journal)) return false;
                }
                if (identity.IsDirectory) pending.Push(entry);
            }
        }
        if (seen.Count == expected.Count) return true;

        var allowedMissing = allowedMissingStagingArtifacts == null
            ? null
            : new HashSet<string>(allowedMissingStagingArtifacts, StringComparer.Ordinal);
        int missingCount = 0;
        foreach (ScenePackageOwnedStagingArtifact artifact in journal.OwnedStagingArtifacts)
        {
            string path = RebaseStagingArtifact(journal.OperationId, artifact.RelativePath, operationDirectory);
            if (seen.Contains(path)) continue;
            missingCount++;
            if (allowedMissing != null)
            {
                if (!allowedMissing.Contains(artifact.RelativePath)) return false;
                continue;
            }
            if (!TryValidateExactPublishedArtifact(projectRoot, journal, artifact)) return false;
            consumedStagingArtifacts?.Add(artifact.RelativePath);
        }
        return seen.Count + missingCount == expected.Count;
    }

    private bool TryValidateExactPublishedArtifact(
        string projectRoot,
        ScenePackageImportJournal journal,
        ScenePackageOwnedStagingArtifact missingStagingArtifact)
    {
        try
        {
            if (!IsPublishedPayloadSlot(journal.OperationId, missingStagingArtifact)) return false;
            ScenePackageStagingArtifactPublication? publication = journal.StagingArtifactPublications
                .SingleOrDefault(item => string.Equals(
                    item.StagingRelativePath, missingStagingArtifact.RelativePath, StringComparison.Ordinal));
            if (publication == null) return false;
            ScenePackageOwnedArtifact? artifact = journal.OwnedArtifacts.SingleOrDefault(item =>
                string.Equals(item.RelativePath, publication.FinalRelativePath, StringComparison.Ordinal));
            if (artifact == null
                || !_guard.TryResolveOwnedTarget(projectRoot, artifact.RelativePath, out string canonical))
                return false;
            ProjectAccessPathIdentity identity = _guard.Capture(canonical);
            return identity.Exists
                && !identity.IsDirectory
                && !identity.IsLinkOrReparse
                && _guard.TryResolveExistingFile(projectRoot, artifact.RelativePath, out string path)
                && _guard.CaptureOwnedFileIdentityToken(
                    path,
                    canonical,
                    journal.OperationId,
                    journal.OwnerToken) == artifact.IdentityToken;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPublishedPayloadSlot(
        string operationId,
        ScenePackageOwnedStagingArtifact artifact)
    {
        if (artifact.Kind != ScenePackageStagingArtifactKind.File) return false;
        string prefix = $".agas/import-staging/{operationId}/payload/";
        if (!artifact.RelativePath.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string name = artifact.RelativePath[prefix.Length..];
        return name.Length == 10
            && name.EndsWith(".tmp", StringComparison.Ordinal)
            && name.AsSpan(0, 6).ToString().All(char.IsAsciiDigit);
    }

    private static string RebaseStagingArtifact(
        string operationId,
        string relativePath,
        string operationDirectory)
    {
        string prefix = $".agas/import-staging/{operationId}/";
        string descendant = relativePath[prefix.Length..];
        return Path.Combine(operationDirectory, Path.Combine(descendant.Split('/')));
    }

    private void TryRestoreQuarantine(string originalDirectory, string quarantineDirectory)
    {
        try
        {
            if (!Directory.Exists(quarantineDirectory)) return;
            ProjectAccessPathIdentity originalIdentity = _guard.Capture(originalDirectory);
            if (originalIdentity.Exists)
            {
                string intrusion = Path.Combine(
                    quarantineDirectory, $"unregistered-after-quarantine-{Guid.NewGuid():N}");
                MoveEntry(originalDirectory, intrusion);
            }
            if (!_guard.Capture(originalDirectory).Exists)
                Directory.Move(quarantineDirectory, originalDirectory);
        }
        catch { }
    }

    private bool RollBackCleanupLease(
        CleanupLease lease,
        string holding,
        IReadOnlyList<string> movedNames)
    {
        try
        {
            for (int index = movedNames.Count - 1; index >= 0; index--)
            {
                string name = movedNames[index];
                MoveEntry(
                    Path.Combine(holding, name),
                    Path.Combine(lease.QuarantineDirectory, name));
            }
            if (Directory.Exists(holding) && !Directory.EnumerateFileSystemEntries(holding).Any())
                Directory.Delete(holding, recursive: false);
        }
        catch { }
        TryRestoreQuarantine(lease.OriginalDirectory, lease.QuarantineDirectory);
        return false;
    }

    private bool PurgeHoldingDirectory(PreparedCleanupLease prepared)
    {
        int deletedEntryCount = 0;
        try
        {
            foreach (string path in prepared.ContentFilesToDelete)
            {
                File.Delete(path);
                deletedEntryCount++;
                _cleanupFaultInjector?.AfterIrreversibleDelete(path, deletedEntryCount);
            }
            foreach (string path in prepared.EvidenceFilesToDelete)
            {
                File.Delete(path);
                deletedEntryCount++;
                _cleanupFaultInjector?.AfterIrreversibleDelete(path, deletedEntryCount);
            }
            foreach (string path in prepared.DirectoriesToDelete)
            {
                Directory.Delete(path, recursive: false);
                deletedEntryCount++;
                _cleanupFaultInjector?.AfterIrreversibleDelete(path, deletedEntryCount);
            }
            Directory.Delete(prepared.HoldingDirectory, recursive: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RollBackPreparedCleanup(PreparedCleanupLease prepared)
    {
        CleanupLease lease = prepared.Cleanup;
        try
        {
            if (Directory.Exists(prepared.HoldingDirectory))
            {
                foreach (string source in Directory.EnumerateFileSystemEntries(prepared.HoldingDirectory)
                             .OrderBy(path => path, StringComparer.Ordinal))
                {
                    string destination = Path.Combine(
                        lease.QuarantineDirectory,
                        Path.GetFileName(source));
                    MoveEntry(source, destination);
                }
                Directory.Delete(prepared.HoldingDirectory, recursive: false);
            }
        }
        catch { }
        TryRestoreQuarantine(lease.OriginalDirectory, lease.QuarantineDirectory);
    }

    private static void MoveEntry(string source, string destination)
    {
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination);
    }

    private void Write(string projectRoot, ScenePackageImportJournal journal)
    {
        string directory = Path.Combine(projectRoot, ".agas", "import-staging", journal.OperationId);
        string journalPath = Path.Combine(directory, JournalFileName);
        string tempPath = journalPath + $".{Guid.NewGuid():N}.tmp";
        var lines = new List<string>
        {
            "version=3",
            Encode(journal.OperationId),
            Encode(journal.OwnerToken),
            Encode(journal.ProjectIdentityToken),
            Encode(journal.NewSceneId),
            Encode(journal.ExpectedSceneRelativePath),
            Encode(journal.MarkerIdentityToken),
            journal.OwnedArtifacts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        lines.AddRange(journal.OwnedArtifacts.Select(item => $"{Encode(item.RelativePath)}|{Encode(item.IdentityToken)}"));
        lines.Add(journal.OwnedStagingArtifacts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        lines.AddRange(journal.OwnedStagingArtifacts.Select(item =>
            $"{Encode(item.Kind.ToString())}|{Encode(item.RelativePath)}|{Encode(item.IdentityToken)}"));
        lines.Add(journal.StagingArtifactPublications.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        lines.AddRange(journal.StagingArtifactPublications.Select(item =>
            $"{Encode(item.StagingRelativePath)}|{Encode(item.FinalRelativePath)}"));
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                foreach (string line in lines) writer.WriteLine(line);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, journalPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));

    private bool ValidateArtifacts(string operationId, IReadOnlyList<ScenePackageOwnedArtifact> artifacts)
    {
        if (artifacts.Count > ScenePackageSafetyLimits.MaximumReferenceImages) return false;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScenePackageOwnedArtifact artifact in artifacts)
        {
            if (!_guard.IsOperationOwnedArtifactRelativePath(operationId, artifact.RelativePath)
                || !paths.Add(artifact.RelativePath)
                || artifact.IdentityToken.Length != 64
                || artifact.IdentityToken.Any(value => !Uri.IsHexDigit(value))) return false;
        }
        return true;
    }

    private bool ValidateStagingArtifacts(
        string operationId,
        IReadOnlyList<ScenePackageOwnedStagingArtifact> artifacts)
    {
        if (artifacts.Count > ScenePackageSafetyLimits.MaximumReferenceImages + 16) return false;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScenePackageOwnedStagingArtifact artifact in artifacts)
        {
            if (!_guard.IsOperationOwnedStagingArtifactRelativePath(operationId, artifact.RelativePath)
                || !Enum.IsDefined(artifact.Kind)
                || !paths.Add(artifact.RelativePath)
                || artifact.IdentityToken.Length != 64
                || artifact.IdentityToken.Any(value => !Uri.IsHexDigit(value))) return false;
        }
        return true;
    }

    private bool ValidatePublications(
        string operationId,
        IReadOnlyList<ScenePackageOwnedArtifact> artifacts,
        IReadOnlyList<ScenePackageOwnedStagingArtifact> stagingArtifacts,
        IReadOnlyList<ScenePackageStagingArtifactPublication> publications)
    {
        if (publications.Count > ScenePackageSafetyLimits.MaximumReferenceImages) return false;
        var stagingByPath = stagingArtifacts.ToDictionary(item => item.RelativePath, StringComparer.Ordinal);
        var finalByPath = artifacts.ToDictionary(item => item.RelativePath, StringComparer.Ordinal);
        var stagingPaths = new HashSet<string>(StringComparer.Ordinal);
        var finalPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScenePackageStagingArtifactPublication publication in publications)
        {
            if (!stagingPaths.Add(publication.StagingRelativePath)
                || !finalPaths.Add(publication.FinalRelativePath)
                || !stagingByPath.TryGetValue(
                    publication.StagingRelativePath, out ScenePackageOwnedStagingArtifact? stagingArtifact)
                || !IsPublishedPayloadSlot(operationId, stagingArtifact)
                || !finalByPath.ContainsKey(publication.FinalRelativePath))
                return false;
        }
        return true;
    }

    private bool IdentityMatches(
        string path,
        string canonicalPath,
        ScenePackageStagingArtifactKind kind,
        string token,
        ScenePackageImportJournal journal)
    {
        try
        {
            string current = kind == ScenePackageStagingArtifactKind.File
                ? _guard.CaptureOwnedFileIdentityToken(path, canonicalPath, journal.OperationId, journal.OwnerToken)
                : _guard.CaptureOwnedDirectoryIdentityToken(path, canonicalPath, journal.OperationId, journal.OwnerToken);
            return string.Equals(current, token, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool SameJournal(ScenePackageImportJournal left, ScenePackageImportJournal right) =>
        left.OperationId == right.OperationId
        && left.OwnerToken == right.OwnerToken
        && left.ProjectIdentityToken == right.ProjectIdentityToken
        && left.NewSceneId == right.NewSceneId
        && left.ExpectedSceneRelativePath == right.ExpectedSceneRelativePath
        && left.MarkerIdentityToken == right.MarkerIdentityToken
        && left.OwnedArtifacts.SequenceEqual(right.OwnedArtifacts)
        && left.OwnedStagingArtifacts.SequenceEqual(right.OwnedStagingArtifacts)
        && left.StagingArtifactPublications.SequenceEqual(right.StagingArtifactPublications);
}
