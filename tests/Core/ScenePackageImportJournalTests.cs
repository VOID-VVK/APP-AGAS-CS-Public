using AppAgasCs.Core.Persistence;
using Xunit;

namespace AppAgasCs.Tests.Core;

public sealed class ScenePackageImportJournalTests
{
    [Fact]
    public void Reconcile_DurableSceneCommitsJournalAndPreservesOwnedArtifact()
    {
        using var fixture = new Fixture();
        ScenePackageImportJournal journal = fixture.CreateJournalWithArtifact();

        ScenePackageReconcileResult result = fixture.Reconciler.Reconcile(
            fixture.ProjectRoot, journal.OperationId, new Probe(true));

        Assert.Equal(ScenePackageReconcileState.Committed, result.State);
        Assert.True(File.Exists(fixture.ArtifactPath));
        fixture.AssertOwnedStagingArtifactsDeleted();
        Assert.False(Directory.Exists(fixture.JournalDirectory(journal.OperationId)));
        fixture.AssertNoCleanupResidue();
    }

    [Fact]
    public void Reconcile_NotDurableDeletesOnlyIdentityMatchedJournalArtifacts()
    {
        using var fixture = new Fixture();
        string unrelated = Path.Combine(fixture.ProjectRoot, "assets", "unrelated.png");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
        File.WriteAllText(unrelated, "keep");
        ScenePackageImportJournal journal = fixture.CreateJournalWithArtifact();

        ScenePackageReconcileResult result = fixture.Reconciler.Reconcile(
            fixture.ProjectRoot, journal.OperationId, new Probe(false));

        Assert.Equal(ScenePackageReconcileState.RolledBack, result.State);
        Assert.False(File.Exists(fixture.ArtifactPath));
        Assert.True(File.Exists(unrelated));
        fixture.AssertOwnedStagingArtifactsDeleted();
        Assert.False(Directory.Exists(fixture.JournalDirectory(journal.OperationId)));
        fixture.AssertNoCleanupResidue();
    }

    [Fact]
    public void Reconcile_FinalArtifactReplacementRaceRestoresEveryFinalAndDoesNotTouchOutsideTarget()
    {
        var holdFaultInjector = new HoldFaultInjector();
        using var fixture = new Fixture(holdFaultInjector: holdFaultInjector);
        ScenePackageImportJournal journal = fixture.CreateJournalWithArtifacts(2);
        string firstArtifact = fixture.ArtifactPaths[0];
        string secondArtifact = fixture.ArtifactPaths[1];
        string outsideTarget = Path.Combine(fixture.Root, "outside-target.bin");
        File.WriteAllText(outsideTarget, "outside-safe");
        holdFaultInjector.Mutation = originalPaths =>
        {
            Assert.Equal(2, originalPaths.Count);
            Assert.All(originalPaths, path => Assert.False(File.Exists(path)));
            File.CreateSymbolicLink(originalPaths[1], outsideTarget);
        };

        ScenePackageReconcileResult result = fixture.Reconciler.Reconcile(
            fixture.ProjectRoot, journal.OperationId, new Probe(false));

        Assert.Equal(ScenePackageReconcileState.RetainedUnsafe, result.State);
        Assert.Equal(1, holdFaultInjector.HeldCallbackCount);
        Assert.Equal("owned", File.ReadAllText(firstArtifact));
        Assert.Equal("owned-1", File.ReadAllText(secondArtifact));
        Assert.Equal("outside-safe", File.ReadAllText(outsideTarget));
        fixture.AssertAllOwnedEvidencePreserved(journal.OperationId);
        Assert.Empty(Directory.EnumerateDirectories(
            Path.Combine(fixture.ProjectRoot, ".agas", "import-staging"), ".rollback-*"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reconcile_PurgeInjectionBeforeFirstDeleteRetainsAllEvidenceAndNeverReportsSuccess(bool durable)
    {
        var faultInjector = new CleanupFaultInjector { ThrowBeforePurge = true };
        using var fixture = new Fixture(cleanupFaultInjector: faultInjector);
        ScenePackageImportJournal journal = fixture.CreateJournalWithArtifact();

        ScenePackageReconcileResult result = fixture.Reconciler.Reconcile(
            fixture.ProjectRoot, journal.OperationId, new Probe(durable));

        Assert.Equal(ScenePackageReconcileState.RetainedUnsafe, result.State);
        Assert.Equal(1, faultInjector.BeforePurgeCount);
        Assert.Equal("owned", File.ReadAllText(fixture.ArtifactPath));
        fixture.AssertAllOwnedEvidencePreserved(journal.OperationId);
        Assert.Empty(Directory.EnumerateDirectories(
            Path.Combine(fixture.ProjectRoot, ".agas", "import-staging"), ".cleanup-*"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reconcile_PurgeFailureAfterAuthorizationReturnsLogicalStateAndLeavesIsolatedResidue(bool durable)
    {
        var faultInjector = new CleanupFaultInjector { ThrowAfterDeleteCount = 1 };
        using var fixture = new Fixture(cleanupFaultInjector: faultInjector);
        ScenePackageImportJournal journal = fixture.CreateJournalWithArtifact();
        string outside = Path.Combine(fixture.Root, "outside-safe.txt");
        File.WriteAllText(outside, "outside-safe");

        ScenePackageReconcileResult result = fixture.Reconciler.Reconcile(
            fixture.ProjectRoot, journal.OperationId, new Probe(durable));

        Assert.Equal(
            durable ? ScenePackageReconcileState.Committed : ScenePackageReconcileState.RolledBack,
            result.State);
        Assert.Equal(1, faultInjector.AfterDeleteCount);
        Assert.Equal(durable, File.Exists(fixture.ArtifactPath));
        Assert.False(File.Exists(fixture.StagedPayloadPath));
        Assert.False(Directory.Exists(fixture.JournalDirectory(journal.OperationId)));
        string residue = Assert.Single(Directory.EnumerateDirectories(
            Path.Combine(fixture.ProjectRoot, ".agas", "import-staging"), ".cleanup-*"));
        Assert.Equal("{\"scene\":true}", File.ReadAllText(Path.Combine(residue, "payload", "scene.json")));
        Assert.Equal("outside-safe", File.ReadAllText(outside));
    }

    [Fact]
    public void Reconcile_MissingTmpRequiresItsExactMappedFinalArtifact()
    {
        using var fixture = new Fixture();
        ScenePackageImportJournal journal = fixture.CreateJournalWithArtifacts(2);
        File.Delete(fixture.ArtifactPaths[1]);
        File.Delete(fixture.StagedPayloadPaths[1]);

        ScenePackageReconcileResult result = fixture.Reconciler.Reconcile(
            fixture.ProjectRoot, journal.OperationId, new Probe(false));

        Assert.Equal(ScenePackageReconcileState.RetainedUnsafe, result.State);
        Assert.Equal("owned", File.ReadAllText(fixture.ArtifactPaths[0]));
        Assert.Equal("staged-payload", File.ReadAllText(fixture.StagedPayloadPaths[0]));
        Assert.Equal("{\"scene\":true}", File.ReadAllText(fixture.StagedManifestPath));
        fixture.AssertJournalEvidencePreserved(journal.OperationId);
        fixture.AssertNoCleanupResidue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reconcile_UnregisteredStagingFileRetainsEveryArtifactAndJournal(bool durable)
    {
        using var fixture = new Fixture();
        ScenePackageImportJournal journal = fixture.CreateJournalWithArtifact();
        string unregistered = Path.Combine(
            fixture.JournalDirectory(journal.OperationId),
            $"{ScenePackageImportJournalStore.JournalFileName}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(unregistered, "do-not-delete");

        ScenePackageReconcileResult result = fixture.Reconciler.Reconcile(
            fixture.ProjectRoot, journal.OperationId, new Probe(durable));

        Assert.Equal(ScenePackageReconcileState.RetainedUnsafe, result.State);
        Assert.True(File.Exists(fixture.ArtifactPath));
        Assert.Equal("do-not-delete", File.ReadAllText(unregistered));
        fixture.AssertAllOwnedEvidencePreserved(journal.OperationId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reconcile_UnregisteredStagingDirectoryRetainsEveryArtifactAndJournal(bool durable)
    {
        using var fixture = new Fixture();
        ScenePackageImportJournal journal = fixture.CreateJournalWithArtifact();
        string unregisteredDirectory = Path.Combine(
            fixture.JournalDirectory(journal.OperationId), "unregistered-directory");
        Directory.CreateDirectory(unregisteredDirectory);
        Assert.Empty(Directory.EnumerateFileSystemEntries(unregisteredDirectory));

        ScenePackageReconcileResult result = fixture.Reconciler.Reconcile(
            fixture.ProjectRoot, journal.OperationId, new Probe(durable));

        Assert.Equal(ScenePackageReconcileState.RetainedUnsafe, result.State);
        Assert.True(File.Exists(fixture.ArtifactPath));
        Assert.True(Directory.Exists(unregisteredDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(unregisteredDirectory));
        fixture.AssertAllOwnedEvidencePreserved(journal.OperationId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reconcile_UnregisteredStagingSymlinkRetainsEveryArtifactAndJournal(bool durable)
    {
        using var fixture = new Fixture();
        ScenePackageImportJournal journal = fixture.CreateJournalWithArtifact();
        string externalTarget = Path.Combine(fixture.Root, "external-evidence.bin");
        string unregisteredLink = Path.Combine(fixture.JournalDirectory(journal.OperationId), "unregistered-link");
        File.WriteAllText(externalTarget, "outside-operation");
        File.CreateSymbolicLink(unregisteredLink, externalTarget);

        ScenePackageReconcileResult result = fixture.Reconciler.Reconcile(
            fixture.ProjectRoot, journal.OperationId, new Probe(durable));

        Assert.Equal(ScenePackageReconcileState.RetainedUnsafe, result.State);
        Assert.True(File.Exists(fixture.ArtifactPath));
        Assert.Equal(externalTarget, new FileInfo(unregisteredLink).LinkTarget);
        Assert.Equal("outside-operation", File.ReadAllText(externalTarget));
        fixture.AssertAllOwnedEvidencePreserved(journal.OperationId);
    }

    [Fact]
    public void Reconcile_ChangedOwnedArtifactFailsClosedAndRetainsEvidence()
    {
        using var fixture = new Fixture();
        ScenePackageImportJournal journal = fixture.CreateJournalWithArtifact();
        File.AppendAllText(fixture.ArtifactPath, "changed");

        ScenePackageReconcileResult result = fixture.Reconciler.Reconcile(
            fixture.ProjectRoot, journal.OperationId, new Probe(false));

        Assert.Equal(ScenePackageReconcileState.RetainedUnsafe, result.State);
        Assert.True(File.Exists(fixture.ArtifactPath));
        fixture.AssertAllOwnedEvidencePreserved(journal.OperationId);
    }

    [Fact]
    public void Reconcile_MtimeTamperFailsClosedWithoutDeletingAnyOwnedFile()
    {
        using var fixture = new Fixture();
        ScenePackageImportJournal journal = fixture.CreateJournalWithArtifact();
        File.SetLastWriteTimeUtc(fixture.ArtifactPath, File.GetLastWriteTimeUtc(fixture.ArtifactPath).AddMinutes(1));

        ScenePackageReconcileResult result = fixture.Reconciler.Reconcile(
            fixture.ProjectRoot, journal.OperationId, new Probe(false));

        Assert.Equal(ScenePackageReconcileState.RetainedUnsafe, result.State);
        Assert.True(File.Exists(fixture.ArtifactPath));
        fixture.AssertAllOwnedEvidencePreserved(journal.OperationId);
    }

    [Fact]
    public void Reconcile_SameLengthContentReplacementWithRestoredMtimeFailsSha256AndRetainsEvidence()
    {
        using var fixture = new Fixture();
        ScenePackageImportJournal journal = fixture.CreateJournalWithArtifact();
        byte[] originalBytes = File.ReadAllBytes(fixture.ArtifactPath);
        long originalLength = new FileInfo(fixture.ArtifactPath).Length;
        DateTime originalCreationTime = File.GetCreationTimeUtc(fixture.ArtifactPath);
        DateTime originalMtime = File.GetLastWriteTimeUtc(fixture.ArtifactPath);

        byte[] replacementBytes = "other"u8.ToArray();
        Assert.Equal(originalBytes.Length, replacementBytes.Length);
        Assert.False(originalBytes.SequenceEqual(replacementBytes));
        File.WriteAllBytes(fixture.ArtifactPath, replacementBytes);
        File.SetCreationTimeUtc(fixture.ArtifactPath, originalCreationTime);
        File.SetLastWriteTimeUtc(fixture.ArtifactPath, originalMtime);

        Assert.Equal(originalLength, new FileInfo(fixture.ArtifactPath).Length);
        Assert.Equal(originalCreationTime, File.GetCreationTimeUtc(fixture.ArtifactPath));
        Assert.Equal(originalMtime, File.GetLastWriteTimeUtc(fixture.ArtifactPath));
        Assert.False(originalBytes.SequenceEqual(File.ReadAllBytes(fixture.ArtifactPath)));

        ScenePackageReconcileResult result = fixture.Reconciler.Reconcile(
            fixture.ProjectRoot, journal.OperationId, new Probe(false));

        Assert.Equal(ScenePackageReconcileState.RetainedUnsafe, result.State);
        Assert.Equal(replacementBytes, File.ReadAllBytes(fixture.ArtifactPath));
        fixture.AssertAllOwnedEvidencePreserved(journal.OperationId);
    }

    [Fact]
    public void Reconcile_RegisteredStagingSameLengthReplacementWithRestoredMtimeFailsSha256AndRetainsAllEvidence()
    {
        using var fixture = new Fixture();
        ScenePackageImportJournal journal = fixture.CreateJournalWithArtifact();
        byte[] originalBytes = File.ReadAllBytes(fixture.StagedPayloadPath);
        long originalLength = new FileInfo(fixture.StagedPayloadPath).Length;
        DateTime originalCreationTime = File.GetCreationTimeUtc(fixture.StagedPayloadPath);
        DateTime originalMtime = File.GetLastWriteTimeUtc(fixture.StagedPayloadPath);

        byte[] replacementBytes = Enumerable.Repeat((byte)'x', originalBytes.Length).ToArray();
        Assert.Equal(originalBytes.Length, replacementBytes.Length);
        Assert.False(originalBytes.SequenceEqual(replacementBytes));
        File.WriteAllBytes(fixture.StagedPayloadPath, replacementBytes);
        File.SetCreationTimeUtc(fixture.StagedPayloadPath, originalCreationTime);
        File.SetLastWriteTimeUtc(fixture.StagedPayloadPath, originalMtime);

        Assert.Equal(originalLength, new FileInfo(fixture.StagedPayloadPath).Length);
        Assert.Equal(originalCreationTime, File.GetCreationTimeUtc(fixture.StagedPayloadPath));
        Assert.Equal(originalMtime, File.GetLastWriteTimeUtc(fixture.StagedPayloadPath));
        Assert.False(originalBytes.SequenceEqual(File.ReadAllBytes(fixture.StagedPayloadPath)));

        ScenePackageReconcileResult result = fixture.Reconciler.Reconcile(
            fixture.ProjectRoot, journal.OperationId, new Probe(false));

        Assert.Equal(ScenePackageReconcileState.RetainedUnsafe, result.State);
        Assert.Equal("owned", File.ReadAllText(fixture.ArtifactPath));
        Assert.True(Directory.Exists(fixture.StagingPayloadDirectoryPath));
        Assert.Equal(replacementBytes, File.ReadAllBytes(fixture.StagedPayloadPath));
        Assert.Equal("{\"scene\":true}", File.ReadAllText(fixture.StagedManifestPath));
        fixture.AssertJournalEvidencePreserved(journal.OperationId);
    }

    [Fact]
    public void Reconcile_TamperedJournalPathFailsClosedAndPreservesUnrelatedFile()
    {
        using var fixture = new Fixture();
        ScenePackageImportJournal journal = fixture.CreateJournalWithArtifact();
        string unrelated = Path.Combine(fixture.ProjectRoot, "assets", "unrelated.png");
        File.WriteAllText(unrelated, "keep");
        string journalPath = Path.Combine(fixture.JournalDirectory(journal.OperationId), ScenePackageImportJournalStore.JournalFileName);
        string[] lines = File.ReadAllLines(journalPath);
        string[] artifact = lines[8].Split('|');
        artifact[0] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("assets/unrelated.png"));
        lines[8] = string.Join('|', artifact);
        File.WriteAllLines(journalPath, lines);

        ScenePackageReconcileResult result = fixture.Reconciler.Reconcile(
            fixture.ProjectRoot, journal.OperationId, new Probe(false));

        Assert.Equal(ScenePackageReconcileState.RetainedUnsafe, result.State);
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(fixture.ArtifactPath));
        fixture.AssertAllOwnedEvidencePreserved(journal.OperationId);
    }

    [Fact]
    public void Reconcile_OwnedFileReplacedByDirectoryRetainsArtifactAndJournalEvidence()
    {
        using var fixture = new Fixture();
        ScenePackageImportJournal journal = fixture.CreateJournalWithArtifact();
        File.Delete(fixture.ArtifactPath);
        Directory.CreateDirectory(fixture.ArtifactPath);

        ScenePackageReconcileResult result = fixture.Reconciler.Reconcile(
            fixture.ProjectRoot, journal.OperationId, new Probe(false));

        Assert.Equal(ScenePackageReconcileState.RetainedUnsafe, result.State);
        Assert.True(Directory.Exists(fixture.ArtifactPath));
        fixture.AssertAllOwnedEvidencePreserved(journal.OperationId);
    }

    [Fact]
    public void Reconcile_OwnedFileReplacedByDanglingSymlinkRetainsArtifactAndJournalEvidence()
    {
        using var fixture = new Fixture();
        ScenePackageImportJournal journal = fixture.CreateJournalWithArtifact();
        File.Delete(fixture.ArtifactPath);
        File.CreateSymbolicLink(fixture.ArtifactPath, Path.Combine(fixture.Root, "missing-target.png"));

        ScenePackageReconcileResult result = fixture.Reconciler.Reconcile(
            fixture.ProjectRoot, journal.OperationId, new Probe(false));

        Assert.Equal(ScenePackageReconcileState.RetainedUnsafe, result.State);
        Assert.NotNull(new FileInfo(fixture.ArtifactPath).LinkTarget);
        fixture.AssertAllOwnedEvidencePreserved(journal.OperationId);
    }

    [Fact]
    public void Reconcile_UnknownInjectedAfterPreflightRollsBackQuarantineWithoutPartialDeletion()
    {
        var identityProvider = new MutatingIdentityProvider(path =>
            path.Contains(".reconcile-", StringComparison.Ordinal)
            && Path.GetFileName(path) == ScenePackageImportJournalStore.OwnerMarkerFileName);
        using var fixture = new Fixture(identityProvider);
        ScenePackageImportJournal journal = fixture.CreateJournalWithArtifact();
        identityProvider.Mutation = markerPath =>
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(markerPath)!, "late-unknown.tmp"), "retain");

        ScenePackageReconcileResult result = fixture.Reconciler.Reconcile(
            fixture.ProjectRoot, journal.OperationId, new Probe(false));

        Assert.Equal(ScenePackageReconcileState.RetainedUnsafe, result.State);
        Assert.Equal("owned", File.ReadAllText(fixture.ArtifactPath));
        Assert.Equal("retain", File.ReadAllText(Path.Combine(
            fixture.JournalDirectory(journal.OperationId), "late-unknown.tmp")));
        fixture.AssertAllOwnedEvidencePreserved(journal.OperationId);
    }

    [Fact]
    public void Reconcile_OperationPathRecreatedAfterQuarantineRetainsFinalAndMergesIntrusionBackWithJournal()
    {
        var identityProvider = new MutatingIdentityProvider(path =>
            path.Contains(".reconcile-", StringComparison.Ordinal)
            && Path.GetFileName(path) == ScenePackageImportJournalStore.OwnerMarkerFileName);
        using var fixture = new Fixture(identityProvider);
        ScenePackageImportJournal journal = fixture.CreateJournalWithArtifact();
        identityProvider.Mutation = _ =>
        {
            string recreated = fixture.JournalDirectory(journal.OperationId);
            Directory.CreateDirectory(recreated);
            File.WriteAllText(Path.Combine(recreated, "late-unknown.tmp"), "retain");
        };

        ScenePackageReconcileResult result = fixture.Reconciler.Reconcile(
            fixture.ProjectRoot, journal.OperationId, new Probe(false));

        Assert.Equal(ScenePackageReconcileState.RetainedUnsafe, result.State);
        Assert.Equal("owned", File.ReadAllText(fixture.ArtifactPath));
        fixture.AssertAllOwnedEvidencePreserved(journal.OperationId);
        string intrusion = Assert.Single(Directory.EnumerateDirectories(
            fixture.JournalDirectory(journal.OperationId), "unregistered-after-quarantine-*"));
        Assert.Equal("retain", File.ReadAllText(Path.Combine(intrusion, "late-unknown.tmp")));
    }

    [Fact]
    public void Reconcile_StagingAncestorReplacedBySymlinkAfterPreflightRetainsAllEvidence()
    {
        var identityProvider = new MutatingIdentityProvider(path =>
            path.Contains(".reconcile-", StringComparison.Ordinal)
            && Path.GetFileName(path) == ScenePackageImportJournalStore.OwnerMarkerFileName);
        using var fixture = new Fixture(identityProvider);
        ScenePackageImportJournal journal = fixture.CreateJournalWithArtifact();
        identityProvider.Mutation = markerPath =>
        {
            string operationDirectory = Path.GetDirectoryName(markerPath)!;
            string payload = Path.Combine(operationDirectory, "payload");
            string retainedPayload = Path.Combine(operationDirectory, "payload-retained");
            Directory.Move(payload, retainedPayload);
            Directory.CreateSymbolicLink(payload, retainedPayload);
        };

        ScenePackageReconcileResult result = fixture.Reconciler.Reconcile(
            fixture.ProjectRoot, journal.OperationId, new Probe(false));

        Assert.Equal(ScenePackageReconcileState.RetainedUnsafe, result.State);
        Assert.Equal("owned", File.ReadAllText(fixture.ArtifactPath));
        string operation = fixture.JournalDirectory(journal.OperationId);
        Assert.NotNull(new DirectoryInfo(Path.Combine(operation, "payload")).LinkTarget);
        Assert.Equal("staged-payload", File.ReadAllText(Path.Combine(operation, "payload-retained", "000000.tmp")));
        fixture.AssertJournalEvidencePreserved(journal.OperationId);
    }

    [Fact]
    public void Reconcile_OwnerMarkerReplacedBySymlinkAfterPreflightRetainsJournalAndFinalArtifact()
    {
        var identityProvider = new MutatingIdentityProvider(path =>
            path.Contains(".reconcile-", StringComparison.Ordinal)
            && Path.GetFileName(path) == ScenePackageImportJournalStore.OwnerMarkerFileName);
        using var fixture = new Fixture(identityProvider);
        ScenePackageImportJournal journal = fixture.CreateJournalWithArtifact();
        identityProvider.Mutation = markerPath =>
        {
            string retainedMarker = Path.Combine(Path.GetDirectoryName(markerPath)!, "owner-retained.v1");
            File.Move(markerPath, retainedMarker);
            File.CreateSymbolicLink(markerPath, retainedMarker);
        };

        ScenePackageReconcileResult result = fixture.Reconciler.Reconcile(
            fixture.ProjectRoot, journal.OperationId, new Probe(false));

        Assert.Equal(ScenePackageReconcileState.RetainedUnsafe, result.State);
        Assert.Equal("owned", File.ReadAllText(fixture.ArtifactPath));
        string operation = fixture.JournalDirectory(journal.OperationId);
        Assert.NotNull(new FileInfo(Path.Combine(
            operation, ScenePackageImportJournalStore.OwnerMarkerFileName)).LinkTarget);
        Assert.True(File.Exists(Path.Combine(operation, ScenePackageImportJournalStore.JournalFileName)));
        Assert.True(File.Exists(Path.Combine(operation, "owner-retained.v1")));
    }

    [Fact]
    public void Reconcile_UnsafeStagingAncestorReturnsRetainedUnsafeInsteadOfNotFound()
    {
        using var fixture = new Fixture();
        ScenePackageImportJournal journal = fixture.CreateJournalWithArtifact();
        string stagingRoot = Path.Combine(fixture.ProjectRoot, ".agas", "import-staging");
        string retainedRoot = Path.Combine(fixture.Root, "retained-import-staging");
        Directory.Move(stagingRoot, retainedRoot);
        Directory.CreateSymbolicLink(stagingRoot, retainedRoot);

        ScenePackageReconcileResult result = fixture.Reconciler.Reconcile(
            fixture.ProjectRoot, journal.OperationId, new Probe(false));

        Assert.Equal(ScenePackageReconcileState.RetainedUnsafe, result.State);
        Assert.Equal("owned", File.ReadAllText(fixture.ArtifactPath));
        Assert.True(File.Exists(Path.Combine(
            retainedRoot, journal.OperationId, ScenePackageImportJournalStore.JournalFileName)));
        Assert.Equal(retainedRoot, new DirectoryInfo(stagingRoot).LinkTarget);
    }

    private sealed class Probe : IScenePackageDurableStateProbe
    {
        private readonly bool _durable;
        public Probe(bool durable) => _durable = durable;
        public bool IsSceneDurable(string projectRoot, string sceneId, string expectedSceneRelativePath) => _durable;
    }

    private sealed class MutatingIdentityProvider : IProjectAccessIdentityProvider
    {
        private readonly FileSystemProjectAccessIdentityProvider _inner = new();
        private readonly Func<string, bool> _trigger;
        private int _mutated;

        public MutatingIdentityProvider(Func<string, bool> trigger) => _trigger = trigger;
        public Action<string>? Mutation { get; set; }

        public ProjectAccessPathIdentity Capture(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (Mutation != null && _trigger(fullPath)
                && Interlocked.Exchange(ref _mutated, 1) == 0)
                Mutation(fullPath);
            return _inner.Capture(fullPath);
        }
    }

    private sealed class CleanupFaultInjector : ScenePackageImportJournalStore.ICleanupFaultInjector
    {
        public bool ThrowBeforePurge { get; init; }
        public int? ThrowAfterDeleteCount { get; init; }
        public int BeforePurgeCount { get; private set; }
        public int AfterDeleteCount { get; private set; }

        public void BeforeIrreversiblePurge(string holdingDirectory)
        {
            BeforePurgeCount++;
            if (ThrowBeforePurge) throw new IOException("controlled purge failure");
        }

        public void AfterIrreversibleDelete(string path, int deletedEntryCount)
        {
            AfterDeleteCount = deletedEntryCount;
            if (ThrowAfterDeleteCount == deletedEntryCount)
                throw new IOException("controlled failure after irreversible delete");
        }
    }

    private sealed class HoldFaultInjector : ScenePackageImportJournalReconciler.IHoldFaultInjector
    {
        public Action<IReadOnlyList<string>>? Mutation { get; set; }
        public int HeldCallbackCount { get; private set; }

        public void AfterAllFinalArtifactsHeld(IReadOnlyList<string> originalPaths)
        {
            HeldCallbackCount++;
            Mutation?.Invoke(originalPaths);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ScenePackagePathGuard _guard;
        private readonly ScenePackageImportJournalStore _store;
        public Fixture(
            IProjectAccessIdentityProvider? identityProvider = null,
            ScenePackageImportJournalStore.ICleanupFaultInjector? cleanupFaultInjector = null,
            ScenePackageImportJournalReconciler.IHoldFaultInjector? holdFaultInjector = null)
        {
            _guard = new ScenePackagePathGuard(identityProvider);
            Root = Path.Combine(Path.GetTempPath(), "agas-package-journal", Guid.NewGuid().ToString("N"));
            ProjectRoot = Path.Combine(Root, "project");
            Directory.CreateDirectory(Path.Combine(ProjectRoot, ".agas", "scenes"));
            ArtifactPath = string.Empty;
            StagingPayloadDirectoryPath = string.Empty;
            StagedPayloadPath = string.Empty;
            StagedManifestPath = string.Empty;
            _store = cleanupFaultInjector == null
                ? new ScenePackageImportJournalStore(_guard)
                : new ScenePackageImportJournalStore(_guard, cleanupFaultInjector);
            Reconciler = holdFaultInjector == null
                ? new ScenePackageImportJournalReconciler(_store, _guard)
                : new ScenePackageImportJournalReconciler(_store, _guard, holdFaultInjector);
        }
        public string Root { get; }
        public string ProjectRoot { get; }
        public string ArtifactPath { get; private set; }
        public string StagingPayloadDirectoryPath { get; private set; }
        public string StagedPayloadPath { get; private set; }
        public string StagedManifestPath { get; private set; }
        public IReadOnlyList<string> ArtifactPaths { get; private set; } = Array.Empty<string>();
        public IReadOnlyList<string> StagedPayloadPaths { get; private set; } = Array.Empty<string>();
        public ScenePackageImportJournalReconciler Reconciler { get; }
        public string JournalDirectory(string operationId) => Path.Combine(ProjectRoot, ".agas", "import-staging", operationId);
        public void AssertJournalEvidencePreserved(string operationId)
        {
            string journalDirectory = JournalDirectory(operationId);
            Assert.True(Directory.Exists(journalDirectory));
            Assert.True(File.Exists(Path.Combine(journalDirectory, ScenePackageImportJournalStore.OwnerMarkerFileName)));
            Assert.True(File.Exists(Path.Combine(journalDirectory, ScenePackageImportJournalStore.JournalFileName)));
        }
        public void AssertAllOwnedEvidencePreserved(string operationId)
        {
            Assert.True(Directory.Exists(StagingPayloadDirectoryPath));
            Assert.Equal("staged-payload", File.ReadAllText(StagedPayloadPath));
            Assert.Equal("{\"scene\":true}", File.ReadAllText(StagedManifestPath));
            AssertJournalEvidencePreserved(operationId);
        }
        public void AssertOwnedStagingArtifactsDeleted()
        {
            Assert.False(File.Exists(StagedPayloadPath));
            Assert.False(File.Exists(StagedManifestPath));
            Assert.False(Directory.Exists(StagingPayloadDirectoryPath));
        }
        public void AssertNoCleanupResidue()
        {
            string stagingRoot = Path.Combine(ProjectRoot, ".agas", "import-staging");
            Assert.Empty(Directory.EnumerateDirectories(stagingRoot, ".cleanup-*"));
            Assert.Empty(Directory.EnumerateDirectories(stagingRoot, ".rollback-*"));
            Assert.Empty(Directory.EnumerateDirectories(stagingRoot, ".reconcile-*"));
        }
        public ScenePackageImportJournal CreateJournalWithArtifact() =>
            CreateJournalWithArtifacts(1);

        public ScenePackageImportJournal CreateJournalWithArtifacts(int count)
        {
            Assert.InRange(count, 1, 4);
            string operationId = Guid.NewGuid().ToString("N");
            string artifactDirectory = Path.Combine(ProjectRoot, "assets", "imported", "packages");
            Directory.CreateDirectory(artifactDirectory);
            ArtifactPaths = Enumerable.Range(0, count)
                .Select(index => Path.Combine(
                    artifactDirectory,
                    index == 0 ? $"{operationId}-owned.png" : $"{operationId}-owned-{index}.png"))
                .ToArray();
            for (int index = 0; index < ArtifactPaths.Count; index++)
                File.WriteAllText(ArtifactPaths[index], index == 0 ? "owned" : $"owned-{index}");
            ArtifactPath = ArtifactPaths[0];
            ScenePackageImportJournal journal = _store.CreatePending(
                ProjectRoot, operationId, "scene-new", ".agas/scenes/scene-new.tres");
            StagingPayloadDirectoryPath = Path.Combine(JournalDirectory(operationId), "payload");
            StagedPayloadPaths = Enumerable.Range(0, count)
                .Select(index => Path.Combine(StagingPayloadDirectoryPath, $"{index:D6}.tmp"))
                .ToArray();
            StagedPayloadPath = StagedPayloadPaths[0];
            StagedManifestPath = Path.Combine(StagingPayloadDirectoryPath, "scene.json");
            Directory.CreateDirectory(StagingPayloadDirectoryPath);
            for (int index = 0; index < StagedPayloadPaths.Count; index++)
                File.WriteAllText(
                    StagedPayloadPaths[index], index == 0 ? "staged-payload" : $"staged-payload-{index}");
            File.WriteAllText(StagedManifestPath, "{\"scene\":true}");
            ScenePackageOwnedArtifact[] artifacts = ArtifactPaths.Select(path =>
                new ScenePackageOwnedArtifact(
                    Path.GetRelativePath(ProjectRoot, path).Replace('\\', '/'),
                    _guard.CaptureOwnedFileIdentityToken(
                        path, path, operationId, journal.OwnerToken))).ToArray();
            var stagingArtifacts = new List<ScenePackageOwnedStagingArtifact>
            {
                new ScenePackageOwnedStagingArtifact(
                    $".agas/import-staging/{operationId}/payload",
                    ScenePackageStagingArtifactKind.Directory,
                    _guard.CaptureOwnedDirectoryIdentityToken(
                        StagingPayloadDirectoryPath, StagingPayloadDirectoryPath, operationId, journal.OwnerToken)),
                new ScenePackageOwnedStagingArtifact(
                    $".agas/import-staging/{operationId}/payload/scene.json",
                    ScenePackageStagingArtifactKind.File,
                    _guard.CaptureOwnedFileIdentityToken(
                        StagedManifestPath, StagedManifestPath, operationId, journal.OwnerToken)),
            };
            for (int index = 0; index < StagedPayloadPaths.Count; index++)
            {
                string stagedPayloadPath = StagedPayloadPaths[index];
                stagingArtifacts.Add(new ScenePackageOwnedStagingArtifact(
                    $".agas/import-staging/{operationId}/payload/{index:D6}.tmp",
                    ScenePackageStagingArtifactKind.File,
                    _guard.CaptureOwnedFileIdentityToken(
                        stagedPayloadPath, stagedPayloadPath, operationId, journal.OwnerToken)));
            }
            ScenePackageStagingArtifactPublication[] publications = Enumerable.Range(0, count)
                .Select(index => new ScenePackageStagingArtifactPublication(
                    $".agas/import-staging/{operationId}/payload/{index:D6}.tmp",
                    artifacts[index].RelativePath))
                .ToArray();
            return _store.UpdateArtifacts(
                ProjectRoot, journal, artifacts, stagingArtifacts, publications);
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
