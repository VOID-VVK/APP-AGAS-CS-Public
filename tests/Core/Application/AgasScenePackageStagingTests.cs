using System.Text;
using AppAgasCs.Application;
using AppAgasCs.Core.Persistence;
using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class AgasScenePackageStagingTests
{
    [Fact]
    public void PrepareImport_UsesPackageRefInboxAndCreatesOwnerJournalForPublishedAssets()
    {
        using var fixture = new Fixture();
        fixture.WritePackage("package-one", ValidManifest("ref.png"), ("references/layer-1/ref.png", AgasControlledAssetImportTests.Png1x1()));

        AgasScenePackagePreparedImport result = fixture.Service.PrepareImport(
            fixture.AgasHome, fixture.ProjectRoot, "package-one", "scene-new");

        Assert.True(result.Success);
        string asset = Assert.Single(result.CopiedAssetRelativePaths);
        AgasScenePackageAssetMapping mapping = Assert.Single(result.AssetMappings);
        Assert.Equal("references/layer-1/ref.png", mapping.PackageRelativePath);
        Assert.Equal(asset, mapping.ProjectRelativePath);
        Assert.Matches($@"^assets/imported/packages/{result.OperationId}-ref\.png$", asset);
        Assert.True(File.Exists(Path.Combine(fixture.ProjectRoot, asset)));
        var guard = new ScenePackagePathGuard();
        var store = new ScenePackageImportJournalStore(guard);
        Assert.True(store.TryRead(fixture.ProjectRoot, result.OperationId, out ScenePackageImportJournal? journal));
        Assert.NotNull(journal);
        ScenePackageOwnedArtifact owned = Assert.Single(journal.OwnedArtifacts);
        string finalPath = Path.Combine(fixture.ProjectRoot, asset);
        Assert.Equal(
            guard.CaptureOwnedFileIdentityToken(finalPath, finalPath, result.OperationId, journal.OwnerToken),
            owned.IdentityToken);
        Assert.DoesNotContain(fixture.ProjectRoot, result.ManifestText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../package")]
    [InlineData("folder/package")]
    [InlineData("https://example.test/package")]
    public void PrepareImport_RejectsNonOpaquePackageRefs(string packageRef)
    {
        using var fixture = new Fixture();
        AgasScenePackagePreparedImport result = fixture.Service.PrepareImport(
            fixture.AgasHome, fixture.ProjectRoot, packageRef, "scene-new");
        Assert.False(result.Success);
        Assert.Equal(AgasScenePackageStagingService.InvalidCode, result.Code);
    }

    [Fact]
    public void PrepareImport_RejectsManifestOverOneMiB()
    {
        using var fixture = new Fixture();
        string directory = fixture.PackageDirectory("large");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "scene.json"), new byte[1024 * 1024 + 1]);

        AgasScenePackagePreparedImport result = fixture.Service.PrepareImport(
            fixture.AgasHome, fixture.ProjectRoot, "large", "scene-new");

        Assert.False(result.Success);
        Assert.Equal(AgasScenePackageStagingService.TooLargeCode, result.Code);
    }

    [Fact]
    public void PrepareImport_RejectsCombinedPayloadOver128MiBWithoutCopying()
    {
        using var fixture = new Fixture();
        string[] names = { "a.png", "b.png", "c.png", "d.png" };
        string directory = fixture.PackageDirectory("too-large");
        Directory.CreateDirectory(Path.Combine(directory, "references", "layer-1"));
        File.WriteAllText(Path.Combine(directory, "scene.json"), ManifestWithReferences(names));
        foreach (string name in names)
        {
            using FileStream stream = File.Create(Path.Combine(directory, "references", "layer-1", name));
            stream.SetLength(ScenePackageSafetyLimits.MaximumImageBytes);
        }

        AgasScenePackagePreparedImport result = fixture.Service.PrepareImport(
            fixture.AgasHome, fixture.ProjectRoot, "too-large", "scene-new");

        Assert.False(result.Success);
        Assert.Equal(AgasScenePackageStagingService.TooLargeCode, result.Code);
        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectRoot, "assets")));
    }

    [Theory]
    [InlineData("layers")]
    [InlineData("prompts")]
    [InlineData("references")]
    public void PrepareImport_EnforcesManifestCollectionBudgets(string kind)
    {
        using var fixture = new Fixture();
        fixture.WritePackage("too-many", OversizedManifest(kind));

        AgasScenePackagePreparedImport result = fixture.Service.PrepareImport(
            fixture.AgasHome, fixture.ProjectRoot, "too-many", "scene-new");

        Assert.False(result.Success);
        Assert.Equal(AgasScenePackageStagingService.TooLargeCode, result.Code);
    }

    [Fact]
    public void FrozenSafetyBudgetsAndTimeoutMatchWaveOneContract()
    {
        Assert.Equal(32L * 1024 * 1024, ScenePackageSafetyLimits.MaximumImageBytes);
        Assert.Equal(8192, ScenePackageSafetyLimits.MaximumImageDimension);
        Assert.Equal(64_000_000, ScenePackageSafetyLimits.MaximumDecodedPixels);
        Assert.Equal(1L * 1024 * 1024, ScenePackageSafetyLimits.MaximumManifestBytes);
        Assert.Equal(128L * 1024 * 1024, ScenePackageSafetyLimits.MaximumPackagePayloadBytes);
        Assert.Equal(64, ScenePackageSafetyLimits.MaximumManifestDepth);
        Assert.Equal(256, ScenePackageSafetyLimits.MaximumLayers);
        Assert.Equal(4096, ScenePackageSafetyLimits.MaximumPromptFragments);
        Assert.Equal(512, ScenePackageSafetyLimits.MaximumReferenceImages);
        Assert.Equal(TimeSpan.FromSeconds(120), ScenePackageSafetyLimits.HeavyOperationTimeout);
    }

    [Theory]
    [InlineData(8193, 1)]
    [InlineData(8001, 8000)]
    public void PrepareImport_RejectsReferenceOverDimensionOrDecodedPixelBudget(int width, int height)
    {
        using var fixture = new Fixture();
        fixture.WritePackage("oversized", ValidManifest("ref.png"),
            ("references/layer-1/ref.png", AgasControlledAssetImportTests.PngWithDimensions(width, height)));

        AgasScenePackagePreparedImport result = fixture.Service.PrepareImport(
            fixture.AgasHome, fixture.ProjectRoot, "oversized", "scene-new");

        Assert.False(result.Success);
        Assert.Equal(AgasScenePackageStagingService.TooLargeCode, result.Code);
        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectRoot, "assets", "imported", "packages"))
            && Directory.EnumerateFiles(Path.Combine(fixture.ProjectRoot, "assets", "imported", "packages")).Any());
    }

    [Fact]
    public void PrepareImport_RejectsCorruptOrPseudoExtensionReference()
    {
        using var fixture = new Fixture();
        byte[] corrupt = AgasControlledAssetImportTests.Png1x1();
        corrupt[45] ^= 0x40;
        fixture.WritePackage("corrupt", ValidManifest("ref.png"), ("references/layer-1/ref.png", corrupt));
        fixture.WritePackage("pseudo", ValidManifest("ref.jpg"),
            ("references/layer-1/ref.jpg", AgasControlledAssetImportTests.Png1x1()));

        Assert.Equal(AgasScenePackageStagingService.InvalidCode,
            fixture.Service.PrepareImport(fixture.AgasHome, fixture.ProjectRoot, "corrupt", "scene-corrupt").Code);
        Assert.Equal(AgasScenePackageStagingService.InvalidCode,
            fixture.Service.PrepareImport(fixture.AgasHome, fixture.ProjectRoot, "pseudo", "scene-pseudo").Code);
    }

    [Fact]
    public void PrepareImport_RejectsManifestBeyondDepth64()
    {
        using var fixture = new Fixture();
        string manifest = ValidManifest("ref.png");
        int objectStart = manifest.IndexOf('{');
        string deep = string.Concat(Enumerable.Repeat("[", 65)) + "0" + string.Concat(Enumerable.Repeat("]", 65));
        manifest = manifest.Insert(objectStart + 1, $"\"deep\":{deep},");
        fixture.WritePackage("too-deep", manifest,
            ("references/layer-1/ref.png", AgasControlledAssetImportTests.Png1x1()));

        AgasScenePackagePreparedImport result = fixture.Service.PrepareImport(
            fixture.AgasHome, fixture.ProjectRoot, "too-deep", "scene-new");

        Assert.False(result.Success);
        Assert.Equal(AgasScenePackageStagingService.InvalidCode, result.Code);
    }

    [Fact]
    public void PrepareImport_RejectsReferenceWhenFullDecoderRejectsIt()
    {
        using var fixture = new Fixture();
        fixture.WritePackage("undecodable", ValidManifest("ref.png"),
            ("references/layer-1/ref.png", AgasControlledAssetImportTests.Png1x1()));
        var service = new AgasScenePackageStagingService(
            imageDecoder: new ManagedTestImageDecoder(accept: false));

        AgasScenePackagePreparedImport result = service.PrepareImport(
            fixture.AgasHome, fixture.ProjectRoot, "undecodable", "scene-new");

        Assert.False(result.Success);
        Assert.Equal(AgasScenePackageStagingService.InvalidCode, result.Code);
    }

    [Fact]
    public void PrepareImport_NonDurableReconcileRollsBackPublishedAssetsEndToEnd()
    {
        using var fixture = new Fixture();
        fixture.WritePackage("pending", ValidManifest("ref.png"),
            ("references/layer-1/ref.png", AgasControlledAssetImportTests.Png1x1()));
        AgasScenePackagePreparedImport prepared = fixture.Service.PrepareImport(
            fixture.AgasHome, fixture.ProjectRoot, "pending", "scene-pending");
        Assert.True(prepared.Success);

        ScenePackageReconcileResult result = new ScenePackageImportJournalReconciler().Reconcile(
            fixture.ProjectRoot, prepared.OperationId, new Probe(false));

        Assert.Equal(ScenePackageReconcileState.RolledBack, result.State);
        Assert.All(prepared.CopiedAssetRelativePaths,
            relative => Assert.False(File.Exists(Path.Combine(fixture.ProjectRoot, relative))));
    }

    [Fact]
    public void PrepareImport_DurableReconcilePreservesPublishedAssetsEndToEnd()
    {
        using var fixture = new Fixture();
        fixture.WritePackage("durable", ValidManifest("ref.png"),
            ("references/layer-1/ref.png", AgasControlledAssetImportTests.Png1x1()));
        AgasScenePackagePreparedImport prepared = fixture.Service.PrepareImport(
            fixture.AgasHome, fixture.ProjectRoot, "durable", "scene-durable");
        Assert.True(prepared.Success);

        ScenePackageReconcileResult result = new ScenePackageImportJournalReconciler().Reconcile(
            fixture.ProjectRoot, prepared.OperationId, new Probe(true));

        Assert.Equal(ScenePackageReconcileState.Committed, result.State);
        Assert.All(prepared.CopiedAssetRelativePaths,
            relative => Assert.True(File.Exists(Path.Combine(fixture.ProjectRoot, relative))));
    }

    [Fact]
    public void PrepareImport_RejectsSymlinkReferenceWithoutPublishing()
    {
        using var fixture = new Fixture();
        fixture.WritePackage("linked", ValidManifest("ref.png"));
        string outside = Path.Combine(fixture.Root, "outside.png");
        File.WriteAllBytes(outside, AgasControlledAssetImportTests.Png1x1());
        string referenceDirectory = Path.Combine(fixture.PackageDirectory("linked"), "references", "layer-1");
        Directory.CreateDirectory(referenceDirectory);
        File.CreateSymbolicLink(Path.Combine(referenceDirectory, "ref.png"), outside);

        AgasScenePackagePreparedImport result = fixture.Service.PrepareImport(
            fixture.AgasHome, fixture.ProjectRoot, "linked", "scene-new");

        Assert.False(result.Success);
        string assets = Path.Combine(fixture.ProjectRoot, "assets");
        Assert.False(Directory.Exists(assets) && Directory.EnumerateFiles(assets, "*.png", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void PrepareImport_RejectsIntermediateInboxSymlink()
    {
        using var fixture = new Fixture();
        Directory.Delete(Path.Combine(fixture.AgasHome, "import-inbox"), recursive: true);
        string outside = Path.Combine(fixture.Root, "outside-inbox");
        string package = Path.Combine(outside, "scene-packages", "linked-root");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "scene.json"), ValidManifest("ref.png"));
        Directory.CreateSymbolicLink(Path.Combine(fixture.AgasHome, "import-inbox"), outside);

        AgasScenePackagePreparedImport result = fixture.Service.PrepareImport(
            fixture.AgasHome, fixture.ProjectRoot, "linked-root", "scene-new");

        Assert.False(result.Success);
        Assert.Equal(AgasScenePackageStagingService.NotFoundCode, result.Code);
        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectRoot, "assets")));
    }

    [Fact]
    public void Export_CommitFailureDoesNotDamageExistingPackageOrLeaveStagingPayload()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.ProjectRoot, "assets", "source.png");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, AgasControlledAssetImportTests.Png1x1());
        string existing = Path.Combine(fixture.ProjectRoot, ".agas", "scene-exports", "existing");
        Directory.CreateDirectory(existing);
        string existingManifest = Path.Combine(existing, "scene.json");
        File.WriteAllText(existingManifest, "old");

        AgasScenePackageExportResult result = fixture.Service.Export(
            fixture.ProjectRoot,
            "existing",
            Encoding.UTF8.GetBytes(ValidManifest("ref.png")),
            new[] { new AgasScenePackageExportFile("assets/source.png", "references/layer-1/ref.png") });

        Assert.False(result.Success);
        Assert.Equal("old", File.ReadAllText(existingManifest));
        string staging = Path.Combine(fixture.ProjectRoot, ".agas", "scene-exports", ".staging");
        Assert.False(Directory.Exists(staging) && Directory.EnumerateFileSystemEntries(staging).Any());
    }

    [Fact]
    public void Export_DurableCommitRejected_LeavesSourceAndPublishesNoPackageOrStagingArtifacts()
    {
        using var fixture = new Fixture();
        string source = Path.Combine(fixture.ProjectRoot, "assets", "source.png");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, AgasControlledAssetImportTests.Png1x1());
        var gate = new RejectDurableCommitGate();

        AgasScenePackageExportResult result = fixture.Service.Export(
            fixture.ProjectRoot,
            "rejected",
            Encoding.UTF8.GetBytes(ValidManifest("ref.png")),
            new[] { new AgasScenePackageExportFile("assets/source.png", "references/layer-1/ref.png") },
            gate);

        Assert.True(gate.WasCalled);
        Assert.False(result.Success);
        Assert.Equal(AgasScenePackageStagingService.CanceledCode, result.Code);
        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectRoot, ".agas", "scene-exports", "rejected")));
        string staging = Path.Combine(fixture.ProjectRoot, ".agas", "scene-exports", ".staging");
        Assert.False(Directory.Exists(staging) && Directory.EnumerateFileSystemEntries(staging).Any());
        Assert.True(File.Exists(source));
        Assert.Equal(AgasControlledAssetImportTests.Png1x1(), File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("symlink")]
    public void Export_DurableCommitRejected_RetainsEntireOwnedStagingWhenUnknownEntryIsInjected(string unknownKind)
    {
        using var fixture = new Fixture();
        byte[] sourceBytes = AgasControlledAssetImportTests.Png1x1();
        string source = Path.Combine(fixture.ProjectRoot, "assets", "source.png");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, sourceBytes);
        byte[] manifestBytes = Encoding.UTF8.GetBytes(ValidManifest("ref.png"));
        var gate = new InjectUnknownRejectDurableCommitGate(fixture.ProjectRoot, source, unknownKind);

        AgasScenePackageExportResult result = fixture.Service.Export(
            fixture.ProjectRoot,
            $"rejected-{unknownKind}",
            manifestBytes,
            new[] { new AgasScenePackageExportFile("assets/source.png", "references/layer-1/ref.png") },
            gate);

        Assert.True(gate.WasCalled);
        Assert.False(result.Success);
        Assert.Equal(AgasScenePackageStagingService.CanceledCode, result.Code);
        Assert.False(Directory.Exists(Path.Combine(
            fixture.ProjectRoot, ".agas", "scene-exports", $"rejected-{unknownKind}")));
        Assert.True(Directory.Exists(gate.StagingDirectory));
        string marker = Path.Combine(gate.StagingDirectory, "owner.v1");
        Assert.True(File.Exists(marker));
        Assert.Equal(Path.GetFileName(gate.StagingDirectory), File.ReadAllText(marker));
        Assert.Equal(manifestBytes, File.ReadAllBytes(Path.Combine(gate.StagingDirectory, "scene.json")));
        Assert.True(Directory.Exists(Path.Combine(gate.StagingDirectory, "references")));
        Assert.True(Directory.Exists(Path.Combine(gate.StagingDirectory, "references", "layer-1")));
        Assert.Equal(sourceBytes, File.ReadAllBytes(Path.Combine(
            gate.StagingDirectory, "references", "layer-1", "ref.png")));
        Assert.True(File.Exists(source));
        Assert.Equal(sourceBytes, File.ReadAllBytes(source));
        if (unknownKind == "directory")
            Assert.True(Directory.Exists(gate.UnknownPath));
        else if (unknownKind == "symlink")
            Assert.NotNull(new FileInfo(gate.UnknownPath).LinkTarget);
        else
            Assert.Equal(InjectUnknownRejectDurableCommitGate.UnknownFileBytes, File.ReadAllBytes(gate.UnknownPath));
    }

    private static string ValidManifest(string fileName) => $$"""
    {
      "schemaVersion": 1,
      "sourceSceneId": "scene-source",
      "name": "Fixture",
      "activeLayerId": "layer-1",
      "layers": [{
        "id": "layer-1",
        "name": "Layer",
        "order": 0,
        "visible": true,
        "locked": false,
        "promptFragments": [{"id":"prompt-1","text":"x","enabled":true,"order":0,"role":"Environment"}],
        "referenceImages": [{"id":"ref-1","fileName":"{{fileName}}","order":0,"scaleX":1,"scaleY":1,"rotationDegrees":0,"opacity":1}]
      }]
    }
    """;

    private static string ManifestWithReferences(IEnumerable<string> names)
    {
        string references = string.Join(",", names.Select((name, index) =>
            $$"""{"id":"ref-{{index}}","fileName":"{{name}}","order":{{index}},"scaleX":1,"scaleY":1,"rotationDegrees":0,"opacity":1}"""));
        return $$"""
        {"schemaVersion":1,"sourceSceneId":"scene-source","name":"Fixture","activeLayerId":"layer-1","layers":[{"id":"layer-1","name":"Layer","order":0,"visible":true,"locked":false,"promptFragments":[],"referenceImages":[{{references}}]}]}
        """;
    }

    private static string OversizedManifest(string kind)
    {
        int layerCount = kind == "layers" ? ScenePackageSafetyLimits.MaximumLayers + 1 : 1;
        var layers = new List<ScenePackageLayer>(layerCount);
        for (int layerIndex = 0; layerIndex < layerCount; layerIndex++)
        {
            ScenePackagePromptFragment[] prompts = kind == "prompts"
                ? Enumerable.Range(0, ScenePackageSafetyLimits.MaximumPromptFragments + 1)
                    .Select(index => new ScenePackagePromptFragment($"prompt-{index}", "x", true, index, "General"))
                    .ToArray()
                : Array.Empty<ScenePackagePromptFragment>();
            ScenePackageReferenceImage[] references = kind == "references"
                ? Enumerable.Range(0, ScenePackageSafetyLimits.MaximumReferenceImages + 1)
                    .Select(index => new ScenePackageReferenceImage($"ref-{index}", $"ref-{index}.png", index, 1, 1, 0, 1))
                    .ToArray()
                : Array.Empty<ScenePackageReferenceImage>();
            layers.Add(new ScenePackageLayer(
                $"layer-{layerIndex}", "Layer", layerIndex, true, false, prompts, references));
        }
        return ScenePackageCodec.Serialize(new ScenePackageDocument(
            1, "source", "Fixture", "layer-0", layers));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "agas-package-staging", Guid.NewGuid().ToString("N"));
            AgasHome = Path.Combine(Root, "agas-home");
            ProjectRoot = Path.Combine(Root, "project");
            Directory.CreateDirectory(Path.Combine(AgasHome, "import-inbox", "scene-packages"));
            Directory.CreateDirectory(Path.Combine(ProjectRoot, ".agas", "scenes"));
            Service = new AgasScenePackageStagingService(imageDecoder: new ManagedTestImageDecoder());
        }
        public string Root { get; }
        public string AgasHome { get; }
        public string ProjectRoot { get; }
        public AgasScenePackageStagingService Service { get; }
        public string PackageDirectory(string packageRef) => Path.Combine(AgasHome, "import-inbox", "scene-packages", packageRef);
        public void WritePackage(string packageRef, string manifest, params (string Relative, byte[] Bytes)[] files)
        {
            string directory = PackageDirectory(packageRef);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "scene.json"), manifest);
            foreach ((string relative, byte[] bytes) in files)
            {
                string path = Path.Combine(directory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
            }
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    private sealed class Probe : IScenePackageDurableStateProbe
    {
        private readonly bool _durable;
        public Probe(bool durable) => _durable = durable;
        public bool IsSceneDurable(string projectRoot, string sceneId, string expectedSceneRelativePath) => _durable;
    }

    private sealed class RejectDurableCommitGate : IAgasDurableCommitGate
    {
        public bool WasCalled { get; private set; }

        public bool TryBeginDurableCommit()
        {
            WasCalled = true;
            return false;
        }
    }

    private sealed class InjectUnknownRejectDurableCommitGate : IAgasDurableCommitGate
    {
        public static readonly byte[] UnknownFileBytes = Encoding.UTF8.GetBytes("unregistered");

        private readonly string _projectRoot;
        private readonly string _source;
        private readonly string _unknownKind;

        public InjectUnknownRejectDurableCommitGate(string projectRoot, string source, string unknownKind)
        {
            _projectRoot = projectRoot;
            _source = source;
            _unknownKind = unknownKind;
        }

        public bool WasCalled { get; private set; }
        public string StagingDirectory { get; private set; } = string.Empty;
        public string UnknownPath { get; private set; } = string.Empty;

        public bool TryBeginDurableCommit()
        {
            WasCalled = true;
            string stagingRoot = Path.Combine(_projectRoot, ".agas", "scene-exports", ".staging");
            StagingDirectory = Assert.Single(Directory.EnumerateDirectories(stagingRoot));
            UnknownPath = Path.Combine(StagingDirectory, "unknown-entry");
            if (_unknownKind == "directory")
                Directory.CreateDirectory(UnknownPath);
            else if (_unknownKind == "symlink")
                File.CreateSymbolicLink(UnknownPath, _source);
            else
                File.WriteAllBytes(UnknownPath, UnknownFileBytes);
            return false;
        }
    }
}
