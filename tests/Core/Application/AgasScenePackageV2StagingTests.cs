using System.Text;
using AppAgasCs.Application;
using AppAgasCs.Core.Persistence;
using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class AgasScenePackageV2StagingTests
{
    [Fact]
    public void ExportAndImport_V2StagesTypedFilesAndVerifiesIntegrity()
    {
        using var fixture = new Fixture();
        ScenePackageDocument document = fixture.Document(withIntegrity: false);
        var broker = new AgasImportSourceRefBroker();

        AgasScenePackageExportResult exported = fixture.Service.ExportToExchange(
            fixture.AgasHome,
            fixture.ProjectA,
            "typed-v2",
            Encoding.UTF8.GetBytes(ScenePackageCodec.Serialize(document)),
            fixture.ExportFiles(),
            broker);

        Assert.True(exported.Success, exported.Code);
        Assert.Equal(1, exported.ReferenceImageCount);
        string packageDirectory = Path.Combine(
            fixture.AgasHome, "import-exchange", "scene-packages", "typed-v2");
        ScenePackageDocument published = ScenePackageCodec.Deserialize(
            File.ReadAllText(Path.Combine(packageDirectory, "scene.json")));
        ScenePackageCodec.ValidateForTransport(published);
        Assert.Equal(2, published.Integrity!.Count);
        Assert.Equal(
            new[]
            {
                ScenePackageFileType.ReferenceImage,
                ScenePackageFileType.CanvasImage,
            }.Order(),
            published.Integrity.Select(item => item.Type).Order());
        Assert.All(published.Integrity, item =>
            ScenePackageIntegrity.Verify(
                item,
                File.ReadAllBytes(Path.Combine(packageDirectory, Path.Combine(item.Path.Split('/'))))));

        AgasScenePackagePreparedImport prepared = fixture.Service.PrepareImportBySourceRef(
            fixture.AgasHome,
            fixture.ProjectB,
            exported.SourceRef,
            "scene-imported",
            broker);

        Assert.True(prepared.Success, prepared.Code);
        Assert.Equal(2, prepared.AssetMappings.Count);
        Assert.Equal(2, prepared.PackageArtifacts.Count);
        Assert.All(prepared.PackageArtifacts, item => Assert.True(item.IsPublishedProjectAsset));
        Assert.All(prepared.PackageArtifacts, item =>
            Assert.True(File.Exists(Path.Combine(fixture.ProjectB, item.PreparedRelativePath))));

        Assert.Equal(
            new[] { "references/layer-1/ref.png", "images/layer-1/image-1.png" }.Order(),
            prepared.AssetMappings.Select(item => item.PackageRelativePath).Order());
    }

    [Fact]
    public void PrepareImport_V2RejectsSameLengthImageTamperAndRollsBack()
    {
        using var fixture = new Fixture();
        string packageDirectory = fixture.WriteInboxPackageWithIntegrity("tampered-v2");
        string image = Path.Combine(packageDirectory, "images", "layer-1", "image-1.png");
        byte[] bytes = File.ReadAllBytes(image);
        bytes[^2] ^= 1;
        File.WriteAllBytes(image, bytes);

        AgasScenePackagePreparedImport prepared = fixture.Service.PrepareImport(
            fixture.AgasHome,
            fixture.ProjectB,
            "tampered-v2",
            "scene-tampered");

        Assert.False(prepared.Success);
        Assert.Equal(AgasScenePackageStagingService.InvalidCode, prepared.Code);
        string finalAssets = Path.Combine(fixture.ProjectB, "assets", "imported", "packages");
        Assert.False(Directory.Exists(finalAssets)
            && Directory.EnumerateFiles(finalAssets, "*", SearchOption.AllDirectories).Any());
        string staging = Path.Combine(fixture.ProjectB, ".agas", "import-staging");
        Assert.False(Directory.Exists(staging) && Directory.EnumerateFileSystemEntries(staging).Any());
    }

    [Fact]
    public void ExportToExchange_V2RejectsMissingImageWithoutPublishing()
    {
        using var fixture = new Fixture();
        string image = fixture.SourcePath("images/layer-1/image-1.png");
        File.Delete(image);

        AgasScenePackageExportResult exported = fixture.Service.ExportToExchange(
            fixture.AgasHome,
            fixture.ProjectA,
            "invalid-text",
            Encoding.UTF8.GetBytes(ScenePackageCodec.Serialize(fixture.Document(withIntegrity: false))),
            fixture.ExportFiles(),
            new AgasImportSourceRefBroker());

        Assert.False(exported.Success);
        Assert.Equal(AgasScenePackageStagingService.InvalidCode, exported.Code);
        Assert.False(Directory.Exists(Path.Combine(
            fixture.AgasHome, "import-exchange", "scene-packages", "invalid-text")));
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly byte[] ReferenceBytes = AgasControlledAssetImportTests.Png1x1();
        private static readonly byte[] CanvasBytes = AgasControlledAssetImportTests.PngWithDimensions(2, 1);

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "agas-v2-staging-" + Guid.NewGuid().ToString("N"));
            AgasHome = Path.Combine(Root, "agas-home");
            ProjectA = Path.Combine(Root, "project-a");
            ProjectB = Path.Combine(Root, "project-b");
            Directory.CreateDirectory(AgasHome);
            Directory.CreateDirectory(ProjectA);
            Directory.CreateDirectory(ProjectB);
            Directory.CreateDirectory(Path.Combine(ProjectA, ".agas", "scenes"));
            Directory.CreateDirectory(Path.Combine(ProjectB, ".agas", "scenes"));
            WriteSources();
            Service = new AgasScenePackageStagingService(imageDecoder: new ManagedTestImageDecoder());
        }

        public string Root { get; }
        public string AgasHome { get; }
        public string ProjectA { get; }
        public string ProjectB { get; }
        public AgasScenePackageStagingService Service { get; }

        public ScenePackageDocument Document(bool withIntegrity)
        {
            var layer = new ScenePackageLayer(
                "layer-1",
                "Layer",
                0,
                true,
                false,
                Array.Empty<ScenePackagePromptFragment>(),
                new[]
                {
                    new ScenePackageReferenceImage("ref-1", "ref.png", 0, 1, 1, 0, 1),
                },
                new[]
                {
                    new ScenePackageImage(
                        "image-1", "images/layer-1/image-1.png", 1,
                        12.5, -7.25, 1.5, 0.75, 15, 0.8),
                });
            var document = new ScenePackageDocument(
                ScenePackageCodec.CurrentSchemaVersion,
                "scene-source",
                "Typed Fixture",
                "layer-1",
                new[] { layer },
                Array.Empty<ScenePackageFileIntegrity>());
            if (!withIntegrity) return document;
            ScenePackageFileIntegrity[] integrity = ArtifactSources()
                .Select(item => ScenePackageIntegrity.Create(item.PackagePath, item.Bytes, item.Type))
                .ToArray();
            return document with { Integrity = integrity };
        }

        public IReadOnlyList<AgasScenePackageExportFile> ExportFiles() =>
            ArtifactSources().Select(item =>
            {
                return new AgasScenePackageExportFile(
                    item.PackagePath,
                    item.PackagePath,
                    item.Type,
                    AgasScenePackageExportSourceScope.Project);
            }).ToArray();

        public string WriteInboxPackageWithIntegrity(string packageRef)
        {
            string directory = Path.Combine(AgasHome, "import-inbox", "scene-packages", packageRef);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "scene.json"), ScenePackageCodec.Serialize(Document(true)));
            foreach (var item in ArtifactSources())
            {
                string destination = Path.Combine(directory, Path.Combine(item.PackagePath.Split('/')));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, item.Bytes);
            }
            return directory;
        }

        public string SourcePath(string packagePath)
        {
            return Path.Combine(ProjectA, Path.Combine(packagePath.Split('/')));
        }

        private void WriteSources()
        {
            foreach (var item in ArtifactSources())
            {
                string destination = SourcePath(item.PackagePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, item.Bytes);
            }
        }

        private static IReadOnlyList<(string PackagePath, ScenePackageFileType Type, byte[] Bytes)>
            ArtifactSources() => new[]
            {
                ("references/layer-1/ref.png", ScenePackageFileType.ReferenceImage, ReferenceBytes),
                ("images/layer-1/image-1.png", ScenePackageFileType.CanvasImage, CanvasBytes),
            };

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
