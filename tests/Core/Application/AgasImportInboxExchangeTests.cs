using System.Text;
using System.Text.Json;
using AppAgasCs.Application;
using AppAgasCs.Core.Persistence;
using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class AgasImportInboxExchangeTests
{
    [Fact]
    public void List_IsPaginatedLightweightAndIssuesOpaqueRefsWithoutPathsOrPayloads()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(Path.Combine(fixture.AssetInbox, "cat.png"), AgasControlledAssetImportTests.Png1x1());
        fixture.WritePackage("package-one");
        var broker = new AgasImportSourceRefBroker();
        var service = new AgasImportInboxService(broker);

        AgasImportInboxPage first = service.List(fixture.AgasHome, limit: 1);
        AgasImportInboxPage second = service.List(fixture.AgasHome, limit: 1, offset: 1);

        Assert.Equal(2, first.TotalCount);
        Assert.Single(first.Items);
        Assert.True(first.HasMore);
        Assert.Equal(1, first.NextOffset);
        Assert.Single(second.Items);
        Assert.False(second.HasMore);
        Assert.Null(second.NextOffset);
        Assert.All(first.Items.Concat(second.Items), item =>
        {
            Assert.Matches("^src1-[a-f0-9]{32}-[a-f0-9]{64}$", item.SourceRef);
            Assert.Contains(item.Kind, new[] { "asset", "scenePackage" });
            Assert.Contains(item.ValidationState, new[] { "unverified", "invalid" });
        });
        string json = JsonSerializer.Serialize(new[] { first, second });
        Assert.DoesNotContain(fixture.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain("base64", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("manifest", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("metadata", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssetImport_SourceRefRevalidatesNativeIdentity()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.AssetInbox, "cat.png");
        File.WriteAllBytes(source, AgasControlledAssetImportTests.Png1x1());
        var broker = new AgasImportSourceRefBroker();
        string sourceRef = Assert.Single(new AgasImportInboxService(broker)
            .List(fixture.AgasHome, AgasImportKind.Asset).Items).SourceRef;
        File.Delete(source);
        File.WriteAllBytes(source, AgasControlledAssetImportTests.PngWithDimensions(2, 1));

        AgasAssetImportResult result = new AgasControlledAssetImportService(
                imageDecoder: new ManagedTestImageDecoder())
            .ImportBySourceRef(fixture.AgasHome, fixture.ProjectA, sourceRef, broker);

        Assert.False(result.Success);
        Assert.Equal(AgasControlledAssetImportService.NotFoundCode, result.Code);
        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectA, "assets"))
            && Directory.EnumerateFiles(Path.Combine(fixture.ProjectA, "assets"), "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void SourceRefBroker_BoundsCapacityEvictsOldestAndRejectsExpiredRefs()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.AssetInbox, "cat.png");
        File.WriteAllBytes(source, AgasControlledAssetImportTests.Png1x1());
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero));
        var broker = new AgasImportSourceRefBroker(
            timeProvider: clock,
            lifetime: TimeSpan.FromMinutes(1),
            signingKey: Enumerable.Repeat((byte)0x5a, 32).ToArray(),
            maximumEntries: 2);
        ProjectAccessPathIdentity identity = new ScenePackagePathGuard().Capture(source);
        var identities = new Dictionary<string, string> { [source] = identity.IdentityToken };

        string oldest = broker.Issue(AgasImportKind.Asset, fixture.AgasHome, source, "cat.png", 1, identities);
        string middle = broker.Issue(AgasImportKind.Asset, fixture.AgasHome, source, "cat.png", 1, identities);
        string newest = broker.Issue(AgasImportKind.Asset, fixture.AgasHome, source, "cat.png", 1, identities);

        Assert.Equal(2, broker.ActiveCount);
        Assert.False(broker.TryResolve(oldest, AgasImportKind.Asset, fixture.AgasHome, out _));
        Assert.True(broker.TryResolve(middle, AgasImportKind.Asset, fixture.AgasHome, out _));
        Assert.True(broker.TryResolve(newest, AgasImportKind.Asset, fixture.AgasHome, out _));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(broker.TryResolve(newest, AgasImportKind.Asset, fixture.AgasHome, out _));
        Assert.Equal(0, broker.ActiveCount);
    }

    [Fact]
    public void ExportToGlobalExchange_ListIssuedRefImportsIntoAnotherOpenProjectWithoutManualMove()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.ProjectA, "assets", "source.png");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, AgasControlledAssetImportTests.Png1x1());
        var broker = new AgasImportSourceRefBroker();
        var service = new AgasScenePackageStagingService(imageDecoder: new ManagedTestImageDecoder());

        AgasScenePackageExportResult exported = service.ExportToExchange(
            fixture.AgasHome,
            fixture.ProjectA,
            "shared-package",
            Encoding.UTF8.GetBytes(ValidManifest()),
            new[] { new AgasScenePackageExportFile("assets/source.png", "references/layer-1/ref.png") },
            broker);
        Assert.True(exported.Success);
        AgasImportInboxItem listed = new AgasImportInboxService(broker)
            .List(fixture.AgasHome, AgasImportKind.ScenePackage)
            .Items
            .Single(item => item.Kind == "scenePackage" && item.Name == exported.PackageRef);
        AgasScenePackagePreparedImport imported = service.PrepareImportBySourceRef(
            fixture.AgasHome,
            fixture.ProjectB,
            listed.SourceRef,
            "scene-imported",
            broker);

        Assert.Matches("^src1-[a-f0-9]{32}-[a-f0-9]{64}$", exported.SourceRef);
        Assert.Matches("^src1-[a-f0-9]{32}-[a-f0-9]{64}$", listed.SourceRef);
        Assert.Equal(exported.PackageRef, listed.Name);
        Assert.Equal(1, exported.ReferenceImageCount);
        Assert.True(Directory.Exists(Path.Combine(
            fixture.AgasHome, "import-exchange", "scene-packages", "shared-package")));
        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectA, ".agas", "scene-exports")));
        Assert.True(imported.Success);
        Assert.All(imported.CopiedAssetRelativePaths,
            relative => Assert.True(File.Exists(Path.Combine(fixture.ProjectB, relative))));
    }

    [Fact]
    public void ExportToGlobalExchange_InitializesFreshAgasHomeBeforeStaging()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.ProjectA, "assets", "source.png");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, AgasControlledAssetImportTests.Png1x1());
        string freshAgasHome = Path.Combine(fixture.Root, "fresh-agas-home");
        var broker = new AgasImportSourceRefBroker();
        var service = new AgasScenePackageStagingService(imageDecoder: new ManagedTestImageDecoder());

        Assert.False(Directory.Exists(freshAgasHome));
        AgasScenePackageExportResult exported = service.ExportToExchange(
            freshAgasHome,
            fixture.ProjectA,
            "fresh-package",
            Encoding.UTF8.GetBytes(ValidManifest()),
            new[] { new AgasScenePackageExportFile("assets/source.png", "references/layer-1/ref.png") },
            broker);

        Assert.True(exported.Success);
        Assert.NotEmpty(exported.SourceRef);
        Assert.True(Directory.Exists(Path.Combine(
            freshAgasHome, "import-exchange", "scene-packages", "fresh-package")));
    }

    [Fact]
    public void AssetCopyFaultAfterOwnedSlotRegistration_FinallyRemovesPartialTemporary()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(Path.Combine(fixture.AssetInbox, "cat.png"), AgasControlledAssetImportTests.Png1x1());
        var service = new AgasControlledAssetImportService(
            guard: null,
            timeout: null,
            imageDecoder: new ManagedTestImageDecoder(),
            copyFaultInjector: new ThrowOnFileName("payload.tmp"));

        AgasAssetImportResult result = service.Import(fixture.AgasHome, fixture.ProjectA, "cat.png");

        Assert.False(result.Success);
        Assert.Empty(Directory.EnumerateFiles(fixture.ProjectA, "payload.tmp", SearchOption.AllDirectories));
        string staging = Path.Combine(fixture.ProjectA, "assets", "imported", ".staging");
        Assert.False(Directory.Exists(staging) && Directory.EnumerateFileSystemEntries(staging).Any());
    }

    [Fact]
    public void PackageCopyFaultAfterJournalSlotRegistration_FinallyReconcilesTemporary()
    {
        using var fixture = new Fixture();
        fixture.WritePackage("fault-package");
        var service = new AgasScenePackageStagingService(
            guard: null,
            journals: null,
            reconciler: null,
            timeout: null,
            imageDecoder: new ManagedTestImageDecoder(),
            copyFaultInjector: new ThrowOnFileName("000000.tmp"));

        AgasScenePackagePreparedImport result = service.PrepareImport(
            fixture.AgasHome, fixture.ProjectA, "fault-package", "scene-fault");

        Assert.False(result.Success);
        string staging = Path.Combine(fixture.ProjectA, ".agas", "import-staging");
        Assert.False(Directory.Exists(staging) && Directory.EnumerateFileSystemEntries(staging).Any());
        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectA, "assets", "imported", "packages"))
            && Directory.EnumerateFiles(
                Path.Combine(fixture.ProjectA, "assets", "imported", "packages"), "*", SearchOption.AllDirectories).Any());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PackageSourceRef_RejectsReferencedFileReplacementForInboxAndExchange(bool exchange)
    {
        using var fixture = new Fixture();
        var broker = new AgasImportSourceRefBroker();
        string sourceRef;
        string packageDirectory;
        if (exchange)
        {
            string source = Path.Combine(fixture.ProjectA, "assets", "source.png");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllBytes(source, AgasControlledAssetImportTests.Png1x1());
            AgasScenePackageExportResult exported = new AgasScenePackageStagingService(
                    imageDecoder: new ManagedTestImageDecoder())
                .ExportToExchange(
                    fixture.AgasHome,
                    fixture.ProjectA,
                    "replace-exchange",
                    Encoding.UTF8.GetBytes(ValidManifest()),
                    new[] { new AgasScenePackageExportFile("assets/source.png", "references/layer-1/ref.png") },
                    broker);
            Assert.True(exported.Success);
            sourceRef = exported.SourceRef;
            packageDirectory = Path.Combine(
                fixture.AgasHome, "import-exchange", "scene-packages", "replace-exchange");
        }
        else
        {
            fixture.WritePackage("replace-inbox");
            sourceRef = Assert.Single(new AgasImportInboxService(broker)
                .List(fixture.AgasHome, AgasImportKind.ScenePackage).Items).SourceRef;
            packageDirectory = Path.Combine(fixture.PackageInbox, "replace-inbox");
        }
        string reference = Path.Combine(packageDirectory, "references", "layer-1", "ref.png");
        File.Delete(reference);
        File.WriteAllBytes(reference, AgasControlledAssetImportTests.PngWithDimensions(2, 1));

        AgasScenePackagePreparedImport imported = new AgasScenePackageStagingService(
                imageDecoder: new ManagedTestImageDecoder())
            .PrepareImportBySourceRef(
                fixture.AgasHome, fixture.ProjectB, sourceRef, "scene-replaced", broker);

        Assert.False(imported.Success);
        Assert.Equal(AgasScenePackageStagingService.NotFoundCode, imported.Code);
    }

    [Fact]
    public void List_LimitOneDoesNotTraverseUnpagedPackageAndNeverFollowsPackageSymlink()
    {
        using var fixture = new Fixture();
        fixture.WritePackage("a-first");
        string unpaged = Path.Combine(fixture.PackageInbox, "z-unpaged");
        Directory.CreateDirectory(unpaged);
        for (int index = 0; index < AgasImportInboxService.MaximumPackageTreeEntries + 1; index++)
            File.WriteAllText(Path.Combine(unpaged, $"{index:D4}.txt"), "x");
        string outside = Path.Combine(fixture.Root, "outside-package");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        Directory.CreateSymbolicLink(Path.Combine(fixture.PackageInbox, "linked"), outside);

        var broker = new AgasImportSourceRefBroker();
        var service = new AgasImportInboxService(broker);
        AgasImportInboxPage page = service
            .List(fixture.AgasHome, AgasImportKind.ScenePackage, limit: 1);

        AgasImportInboxItem item = Assert.Single(page.Items);
        Assert.Equal("a-first", item.Name);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(page), StringComparison.OrdinalIgnoreCase);

        AgasImportInboxItem invalid = Assert.Single(service.List(
            fixture.AgasHome, AgasImportKind.ScenePackage, limit: 1, offset: 1).Items);
        Assert.Equal("z-unpaged", invalid.Name);
        Assert.Equal("invalid", invalid.ValidationState);
        AgasScenePackagePreparedImport rejected = new AgasScenePackageStagingService(
                imageDecoder: new ManagedTestImageDecoder())
            .PrepareImportBySourceRef(
                fixture.AgasHome, fixture.ProjectB, invalid.SourceRef, "scene-invalid-tree", broker);
        Assert.False(rejected.Success);
        Assert.Equal(AgasScenePackageStagingService.NotFoundCode, rejected.Code);
    }

    [Fact]
    public void ExchangeExportCopyFaultAfterOwnedSlotRegistration_FinallyRemovesPartialTemporary()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.ProjectA, "assets", "source.png");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, AgasControlledAssetImportTests.Png1x1());
        var service = new AgasScenePackageStagingService(
            guard: null,
            journals: null,
            reconciler: null,
            timeout: null,
            imageDecoder: new ManagedTestImageDecoder(),
            copyFaultInjector: new ThrowOnFileName("scene.json"));

        AgasScenePackageExportResult result = service.ExportToExchange(
            fixture.AgasHome,
            fixture.ProjectA,
            "fault-export",
            Encoding.UTF8.GetBytes(ValidManifest()),
            new[] { new AgasScenePackageExportFile("assets/source.png", "references/layer-1/ref.png") },
            new AgasImportSourceRefBroker());

        Assert.False(result.Success);
        string staging = Path.Combine(fixture.AgasHome, "import-exchange", "scene-packages", ".staging");
        Assert.False(Directory.Exists(staging) && Directory.EnumerateFileSystemEntries(staging).Any());
        Assert.False(Directory.Exists(Path.Combine(
            fixture.AgasHome, "import-exchange", "scene-packages", "fault-export")));
    }

    [Fact]
    public void ExchangeExport_PostCommitFaultCannotRewritePublishedSuccessOrRevokePreparedRef()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.ProjectA, "assets", "source.png");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, AgasControlledAssetImportTests.Png1x1());
        var broker = new AgasImportSourceRefBroker();
        var service = new AgasScenePackageStagingService(
            guard: null,
            journals: null,
            reconciler: null,
            timeout: null,
            imageDecoder: new ManagedTestImageDecoder(),
            copyFaultInjector: new ThrowAfterExchangePublish());

        AgasScenePackageExportResult exported = service.ExportToExchange(
            fixture.AgasHome,
            fixture.ProjectA,
            "postcommit-receipt",
            Encoding.UTF8.GetBytes(ValidManifest()),
            new[] { new AgasScenePackageExportFile("assets/source.png", "references/layer-1/ref.png") },
            broker);
        AgasScenePackagePreparedImport imported = service.PrepareImportBySourceRef(
            fixture.AgasHome,
            fixture.ProjectB,
            exported.SourceRef,
            "scene-postcommit",
            broker);

        Assert.True(exported.Success);
        Assert.NotEmpty(exported.SourceRef);
        Assert.Equal(1, exported.ReferenceImageCount);
        Assert.True(Directory.Exists(Path.Combine(
            fixture.AgasHome, "import-exchange", "scene-packages", "postcommit-receipt")));
        Assert.True(imported.Success);
    }

    [Fact]
    public void ExchangeExport_RejectedPreparedRefDoesNotEvictExistingCapabilityAtCapacity()
    {
        using var fixture = new Fixture();
        string inboxSource = Path.Combine(fixture.AssetInbox, "cat.png");
        File.WriteAllBytes(inboxSource, AgasControlledAssetImportTests.Png1x1());
        var broker = new AgasImportSourceRefBroker(maximumEntries: 1);
        string existingRef = Assert.Single(new AgasImportInboxService(broker)
            .List(fixture.AgasHome, AgasImportKind.Asset).Items).SourceRef;
        string projectSource = Path.Combine(fixture.ProjectA, "assets", "source.png");
        Directory.CreateDirectory(Path.GetDirectoryName(projectSource)!);
        File.WriteAllBytes(projectSource, AgasControlledAssetImportTests.Png1x1());
        var service = new AgasScenePackageStagingService(imageDecoder: new ManagedTestImageDecoder());

        AgasScenePackageExportResult rejected = service.ExportToExchange(
            fixture.AgasHome,
            fixture.ProjectA,
            "rejected-capacity",
            Encoding.UTF8.GetBytes(ValidManifest()),
            new[] { new AgasScenePackageExportFile("assets/source.png", "references/layer-1/ref.png") },
            broker,
            new RejectCommitGate());

        Assert.False(rejected.Success);
        Assert.True(broker.TryResolve(existingRef, AgasImportKind.Asset, fixture.AgasHome, out _));
        Assert.Equal(1, broker.ActiveCount);
        Assert.False(Directory.Exists(Path.Combine(
            fixture.AgasHome, "import-exchange", "scene-packages", "rejected-capacity")));

        AgasScenePackageExportResult published = service.ExportToExchange(
            fixture.AgasHome,
            fixture.ProjectA,
            "published-capacity",
            Encoding.UTF8.GetBytes(ValidManifest()),
            new[] { new AgasScenePackageExportFile("assets/source.png", "references/layer-1/ref.png") },
            broker);
        Assert.True(published.Success);
        Assert.False(broker.TryResolve(existingRef, AgasImportKind.Asset, fixture.AgasHome, out _));
        Assert.True(broker.TryResolve(
            published.SourceRef, AgasImportKind.ScenePackage, fixture.AgasHome, out _));
    }

    private static string ValidManifest() => """
    {
      "schemaVersion":1,
      "sourceSceneId":"scene-source",
      "name":"Fixture",
      "activeLayerId":"layer-1",
      "layers":[{
        "id":"layer-1","name":"Layer","order":0,"visible":true,"locked":false,
        "promptFragments":[],
        "referenceImages":[{"id":"ref-1","fileName":"ref.png","order":0,"scaleX":1,"scaleY":1,"rotationDegrees":0,"opacity":1}]
      }]
    }
    """;

    private sealed class ThrowOnFileName : IAgasImportCopyFaultInjector
    {
        private readonly string _name;
        public ThrowOnFileName(string name) => _name = name;
        public void AfterTemporarySlotRegistered(string path)
        {
            if (Path.GetFileName(path) == _name) throw new IOException("injected copy fault");
        }
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;
        public TestTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }

    private sealed class ThrowAfterExchangePublish : IAgasImportCopyFaultInjector
    {
        public void AfterTemporarySlotRegistered(string path) { }
        public void AfterExchangePublished() => throw new IOException("injected post-commit fault");
    }

    private sealed class RejectCommitGate : IAgasDurableCommitGate
    {
        public bool TryBeginDurableCommit() => false;
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "agas-import-exchange", Guid.NewGuid().ToString("N"));
            AgasHome = Path.Combine(Root, "agas-home");
            AssetInbox = Path.Combine(AgasHome, "import-inbox", "assets");
            PackageInbox = Path.Combine(AgasHome, "import-inbox", "scene-packages");
            ProjectA = Path.Combine(Root, "project-a");
            ProjectB = Path.Combine(Root, "project-b");
            Directory.CreateDirectory(AssetInbox);
            Directory.CreateDirectory(PackageInbox);
            Directory.CreateDirectory(Path.Combine(ProjectA, ".agas", "scenes"));
            Directory.CreateDirectory(Path.Combine(ProjectB, ".agas", "scenes"));
        }

        public string Root { get; }
        public string AgasHome { get; }
        public string AssetInbox { get; }
        public string PackageInbox { get; }
        public string ProjectA { get; }
        public string ProjectB { get; }

        public void WritePackage(string packageRef)
        {
            string directory = Path.Combine(PackageInbox, packageRef);
            Directory.CreateDirectory(Path.Combine(directory, "references", "layer-1"));
            File.WriteAllText(Path.Combine(directory, "scene.json"), ValidManifest());
            File.WriteAllBytes(
                Path.Combine(directory, "references", "layer-1", "ref.png"),
                AgasControlledAssetImportTests.Png1x1());
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
