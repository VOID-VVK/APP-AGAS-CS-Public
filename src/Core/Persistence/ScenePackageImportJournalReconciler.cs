#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AppAgasCs.Core.Persistence;

public sealed class ScenePackageImportJournalReconciler
{
    internal interface IHoldFaultInjector
    {
        void AfterAllFinalArtifactsHeld(IReadOnlyList<string> originalPaths);
    }

    private sealed record HeldOwnedFile(
        string OriginalPath,
        string HoldingPath,
        ScenePackageOwnedArtifact Artifact);

    private readonly ScenePackageImportJournalStore _store;
    private readonly ScenePackagePathGuard _guard;
    private readonly IHoldFaultInjector? _holdFaultInjector;

    public ScenePackageImportJournalReconciler(
        ScenePackageImportJournalStore? store = null,
        ScenePackagePathGuard? guard = null)
    {
        _guard = guard ?? new ScenePackagePathGuard();
        _store = store ?? new ScenePackageImportJournalStore(_guard);
    }

    internal ScenePackageImportJournalReconciler(
        ScenePackageImportJournalStore store,
        ScenePackagePathGuard guard,
        IHoldFaultInjector holdFaultInjector)
    {
        _store = store;
        _guard = guard;
        _holdFaultInjector = holdFaultInjector;
    }

    public ScenePackageReconcileResult Reconcile(
        string projectRoot,
        string operationId,
        IScenePackageDurableStateProbe durableState)
    {
        ArgumentNullException.ThrowIfNull(durableState);
        if (!_store.TryRead(projectRoot, operationId, out ScenePackageImportJournal? journal) || journal == null)
            return new(_store.HasOperationEntry(projectRoot, operationId)
                ? ScenePackageReconcileState.RetainedUnsafe
                : ScenePackageReconcileState.NotFound, operationId);

        ProjectAccessPathIdentity projectIdentity = _guard.Capture(projectRoot);
        if (!projectIdentity.Exists
            || projectIdentity.IsLinkOrReparse
            || projectIdentity.IdentityToken != journal.ProjectIdentityToken)
            return new(ScenePackageReconcileState.RetainedUnsafe, operationId);

        if (!_store.TryValidateReconcileStagingTree(projectRoot, journal, out _, out _))
            return new(ScenePackageReconcileState.RetainedUnsafe, operationId);

        if (durableState.IsSceneDurable(projectRoot, journal.NewSceneId, journal.ExpectedSceneRelativePath))
        {
            return _store.TryBeginCleanup(projectRoot, journal, out ScenePackageImportJournalStore.CleanupLease? durableLease)
                   && durableLease != null
                   && _store.CompleteCleanup(durableLease)
                ? new(ScenePackageReconcileState.Committed, operationId)
                : new(ScenePackageReconcileState.RetainedUnsafe, operationId);
        }

        var ownedFiles = new List<(string Path, ScenePackageOwnedArtifact Artifact)>();
        foreach (ScenePackageOwnedArtifact artifact in journal.OwnedArtifacts)
        {
            if (!TryValidateOwnedFile(projectRoot, journal, artifact, allowMissing: true, out string path))
                return new(ScenePackageReconcileState.RetainedUnsafe, operationId);
            if (path.Length > 0) ownedFiles.Add((path, artifact));
        }

        if (!_store.TryBeginCleanup(
                projectRoot, journal, out ScenePackageImportJournalStore.CleanupLease? rollbackLease)
            || rollbackLease == null)
            return new(ScenePackageReconcileState.RetainedUnsafe, operationId);

        if (!TryHoldOwnedFiles(
                projectRoot,
                journal,
                rollbackLease,
                ownedFiles,
                out string finalHolding,
                out List<HeldOwnedFile> heldFiles))
        {
            _store.CancelCleanup(rollbackLease);
            return new(ScenePackageReconcileState.RetainedUnsafe, operationId);
        }

        if (!_store.TryPrepareCleanup(
                rollbackLease,
                out ScenePackageImportJournalStore.PreparedCleanupLease? prepared)
            || prepared == null)
        {
            RollBackHeldOwnedFiles(projectRoot, journal, rollbackLease, finalHolding, heldFiles);
            return new(ScenePackageReconcileState.RetainedUnsafe, operationId);
        }

        if (!_store.TryAuthorizePreparedCleanup(prepared))
        {
            RollBackHeldOwnedFiles(projectRoot, journal, rollbackLease, finalHolding, heldFiles);
            return new(ScenePackageReconcileState.RetainedUnsafe, operationId);
        }

        PurgeHeldOwnedFiles(finalHolding, heldFiles);
        _store.CompletePreparedCleanup(prepared, purgeAuthorized: true);
        return new(ScenePackageReconcileState.RolledBack, operationId);
    }

    private bool TryValidateOwnedFile(
        string projectRoot,
        ScenePackageImportJournal journal,
        ScenePackageOwnedArtifact artifact,
        bool allowMissing,
        out string path)
    {
        path = string.Empty;
        try
        {
            if (!_guard.TryResolveOwnedTarget(projectRoot, artifact.RelativePath, out string target))
                return false;
            ProjectAccessPathIdentity targetIdentity = _guard.Capture(target);
            if (!targetIdentity.Exists) return allowMissing;
            if (targetIdentity.IsDirectory || targetIdentity.IsLinkOrReparse || !File.Exists(target))
                return false;
            if (!_guard.TryResolveExistingFile(projectRoot, artifact.RelativePath, out path))
                return false;
            return _guard.CaptureOwnedFileIdentityToken(
                       path, target, journal.OperationId, journal.OwnerToken)
                   == artifact.IdentityToken;
        }
        catch
        {
            path = string.Empty;
            return false;
        }
    }

    private bool TryHoldOwnedFiles(
        string projectRoot,
        ScenePackageImportJournal journal,
        ScenePackageImportJournalStore.CleanupLease cleanupLease,
        IReadOnlyList<(string Path, ScenePackageOwnedArtifact Artifact)> ownedFiles,
        out string holding,
        out List<HeldOwnedFile> heldFiles)
    {
        holding = string.Empty;
        heldFiles = new List<HeldOwnedFile>();
        try
        {
            if (!_guard.TryResolveDirectory(
                    projectRoot, ".agas/import-staging", mustExist: true, out string stagingRoot)
                || !string.Equals(
                    stagingRoot,
                    Path.GetDirectoryName(cleanupLease.QuarantineDirectory),
                    StringComparison.Ordinal))
                return false;
            holding = Path.Combine(
                stagingRoot, $".rollback-{journal.OperationId}-{Guid.NewGuid():N}");
            if (_guard.Capture(holding).Exists) return false;
            Directory.CreateDirectory(holding);
            ProjectAccessPathIdentity holdingIdentity = _guard.Capture(holding);
            if (!holdingIdentity.Exists || !holdingIdentity.IsDirectory || holdingIdentity.IsLinkOrReparse)
                return false;

            for (int index = 0; index < ownedFiles.Count; index++)
            {
                (string originalPath, ScenePackageOwnedArtifact artifact) = ownedFiles[index];
                if (!TryValidateOwnedFile(
                        projectRoot, journal, artifact, allowMissing: false, out string current)
                    || !string.Equals(current, originalPath, StringComparison.Ordinal))
                {
                    RollBackHeldOwnedFiles(projectRoot, journal, cleanupLease, holding, heldFiles);
                    return false;
                }
                string heldPath = Path.Combine(holding, $"{index:D6}.held");
                File.Move(current, heldPath);
                heldFiles.Add(new HeldOwnedFile(originalPath, heldPath, artifact));
            }

            _holdFaultInjector?.AfterAllFinalArtifactsHeld(
                heldFiles.Select(item => item.OriginalPath).ToArray());

            if (!_guard.TryResolveDirectory(
                    projectRoot, ".agas/import-staging", mustExist: true, out string currentStagingRoot)
                || !string.Equals(currentStagingRoot, stagingRoot, StringComparison.Ordinal))
            {
                RollBackHeldOwnedFiles(projectRoot, journal, cleanupLease, holding, heldFiles);
                return false;
            }
            holdingIdentity = _guard.Capture(holding);
            if (!holdingIdentity.Exists || !holdingIdentity.IsDirectory || holdingIdentity.IsLinkOrReparse
                || Directory.EnumerateFileSystemEntries(holding).Count() != heldFiles.Count)
            {
                RollBackHeldOwnedFiles(projectRoot, journal, cleanupLease, holding, heldFiles);
                return false;
            }
            foreach (HeldOwnedFile held in heldFiles)
            {
                if (!_guard.TryResolveOwnedTarget(
                        projectRoot, held.Artifact.RelativePath, out string canonical)
                    || !string.Equals(canonical, held.OriginalPath, StringComparison.Ordinal)
                    || _guard.Capture(canonical).Exists)
                {
                    RollBackHeldOwnedFiles(projectRoot, journal, cleanupLease, holding, heldFiles);
                    return false;
                }
                ProjectAccessPathIdentity identity = _guard.Capture(held.HoldingPath);
                if (!identity.Exists || identity.IsDirectory || identity.IsLinkOrReparse
                    || !File.Exists(held.HoldingPath)
                    || _guard.CaptureOwnedFileIdentityToken(
                        held.HoldingPath,
                        held.OriginalPath,
                        journal.OperationId,
                        journal.OwnerToken) != held.Artifact.IdentityToken)
                {
                    RollBackHeldOwnedFiles(projectRoot, journal, cleanupLease, holding, heldFiles);
                    return false;
                }
            }
            return true;
        }
        catch
        {
            RollBackHeldOwnedFiles(projectRoot, journal, cleanupLease, holding, heldFiles);
            return false;
        }
    }

    private bool RollBackHeldOwnedFiles(
        string projectRoot,
        ScenePackageImportJournal journal,
        ScenePackageImportJournalStore.CleanupLease cleanupLease,
        string holding,
        IReadOnlyList<HeldOwnedFile> heldFiles)
    {
        bool restored = true;
        for (int index = heldFiles.Count - 1; index >= 0; index--)
        {
            HeldOwnedFile held = heldFiles[index];
            try
            {
                ProjectAccessPathIdentity heldIdentity = _guard.Capture(held.HoldingPath);
                if (!heldIdentity.Exists || heldIdentity.IsDirectory || heldIdentity.IsLinkOrReparse
                    || _guard.CaptureOwnedFileIdentityToken(
                        held.HoldingPath,
                        held.OriginalPath,
                        journal.OperationId,
                        journal.OwnerToken) != held.Artifact.IdentityToken
                    || !_guard.TryResolveOwnedTarget(
                        projectRoot, held.Artifact.RelativePath, out string original)
                    || !string.Equals(original, held.OriginalPath, StringComparison.Ordinal))
                {
                    restored = false;
                    continue;
                }

                ProjectAccessPathIdentity originalIdentity = _guard.Capture(original);
                if (originalIdentity.Exists)
                {
                    string evidenceDirectory = Directory.Exists(cleanupLease.QuarantineDirectory)
                        ? cleanupLease.QuarantineDirectory
                        : cleanupLease.OriginalDirectory;
                    if (!Directory.Exists(evidenceDirectory))
                    {
                        restored = false;
                        continue;
                    }
                    string intrusion = Path.Combine(
                        evidenceDirectory,
                        $"unregistered-final-after-holding-{index:D6}-{Guid.NewGuid():N}");
                    MoveEntry(original, intrusion, originalIdentity.IsDirectory);
                }
                if (_guard.Capture(original).Exists)
                {
                    restored = false;
                    continue;
                }
                File.Move(held.HoldingPath, original);
            }
            catch
            {
                restored = false;
            }
        }

        try
        {
            if (!string.IsNullOrEmpty(holding) && Directory.Exists(holding))
                Directory.Delete(holding, recursive: false);
        }
        catch
        {
            restored = false;
        }
        return restored;
    }

    private static void PurgeHeldOwnedFiles(
        string holding,
        IReadOnlyList<HeldOwnedFile> heldFiles)
    {
        try
        {
            foreach (HeldOwnedFile held in heldFiles) File.Delete(held.HoldingPath);
            Directory.Delete(holding, recursive: false);
        }
        catch
        {
        }
    }

    private static void MoveEntry(string source, string destination, bool isDirectory)
    {
        if (isDirectory) Directory.Move(source, destination);
        else File.Move(source, destination);
    }
}
