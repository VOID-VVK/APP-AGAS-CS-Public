using AppAgasCs.Application;
using AppAgasCs.Core.Persistence;
using System.Buffers.Binary;
using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class AgasControlledAssetImportTests
{
    [Fact]
    public void Import_UsesFixedInboxAndAtomicallyPublishesValidatedPng()
    {
        using var fixture = new Fixture();
        fixture.WriteInbox("image.png", Png1x1());
        var service = CreateService();

        AgasAssetImportResult result = service.Import(fixture.AgasHome, fixture.ProjectRoot, "image.png");

        Assert.True(result.Success);
        Assert.StartsWith("assets/imported/", result.RelativePath, StringComparison.Ordinal);
        Assert.Equal("png", result.Extension);
        Assert.Equal(1, result.PixelWidth);
        Assert.Equal(1, result.PixelHeight);
        Assert.True(File.Exists(Path.Combine(fixture.ProjectRoot, result.RelativePath)));
        Assert.Matches(@"^assets/imported/[0-9a-f]{32}-image\.png$", result.RelativePath);
        Assert.DoesNotContain(fixture.ProjectRoot, result.RelativePath, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(fixture.ProjectRoot, "payload.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Import_UsesUuidAndSanitizedSourceNameWithoutLeakingAbsolutePath()
    {
        using var fixture = new Fixture();
        fixture.WriteInbox("my cat (final).PNG", Png1x1());

        AgasAssetImportResult result = CreateService()
            .Import(fixture.AgasHome, fixture.ProjectRoot, "my cat (final).PNG");

        Assert.True(result.Success);
        Assert.Matches(@"^assets/imported/[0-9a-f]{32}-my-cat-final\.png$", result.RelativePath);
        Assert.DoesNotContain(fixture.Root, result.RelativePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../image.png")]
    [InlineData("folder/image.png")]
    [InlineData("https://example.test/image.png")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("/tmp/image.png")]
    public void Import_RejectsNonBasenameSources(string source)
    {
        using var fixture = new Fixture();
        AgasAssetImportResult result = CreateService()
            .Import(fixture.AgasHome, fixture.ProjectRoot, source);
        Assert.False(result.Success);
        Assert.Equal(AgasControlledAssetImportService.InvalidCode, result.Code);
    }

    [Fact]
    public void Import_RejectsSymlinkSource()
    {
        using var fixture = new Fixture();
        string real = fixture.WriteInbox("real.png", Png1x1());
        File.CreateSymbolicLink(Path.Combine(fixture.AssetInbox, "link.png"), real);

        AgasAssetImportResult result = CreateService()
            .Import(fixture.AgasHome, fixture.ProjectRoot, "link.png");

        Assert.False(result.Success);
        Assert.Equal(AgasControlledAssetImportService.NotFoundCode, result.Code);
    }

    [Fact]
    public void Import_RejectsSymlinkStagingAncestorBeforeAnyExternalWrite()
    {
        using var fixture = new Fixture();
        fixture.WriteInbox("image.png", Png1x1());
        string imported = Path.Combine(fixture.ProjectRoot, "assets", "imported");
        Directory.CreateDirectory(imported);
        string outside = Path.Combine(fixture.Root, "outside-staging");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(imported, ".staging"), outside);

        AgasAssetImportResult result = CreateService()
            .Import(fixture.AgasHome, fixture.ProjectRoot, "image.png");

        Assert.False(result.Success);
        Assert.Equal(AgasControlledAssetImportService.InvalidCode, result.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public void Import_RejectsIntermediateInboxSymlink()
    {
        using var fixture = new Fixture();
        Directory.Delete(Path.Combine(fixture.AgasHome, "import-inbox"), recursive: true);
        string outside = Path.Combine(fixture.Root, "outside-inbox");
        string outsideAssets = Path.Combine(outside, "assets");
        Directory.CreateDirectory(outsideAssets);
        File.WriteAllBytes(Path.Combine(outsideAssets, "image.png"), Png1x1());
        Directory.CreateSymbolicLink(Path.Combine(fixture.AgasHome, "import-inbox"), outside);

        AgasAssetImportResult result = CreateService()
            .Import(fixture.AgasHome, fixture.ProjectRoot, "image.png");

        Assert.False(result.Success);
        Assert.Equal(AgasControlledAssetImportService.NotFoundCode, result.Code);
        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectRoot, "assets")));
    }

    [Fact]
    public void Import_CommitRejected_RemovesOnlyOperationStagingAndPublishesNothing()
    {
        using var fixture = new Fixture();
        fixture.WriteInbox("image.png", Png1x1());
        string unrelated = Path.Combine(fixture.ProjectRoot, "assets", "imported", "keep.png");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
        File.WriteAllBytes(unrelated, Png1x1());

        AgasAssetImportResult result = CreateService().Import(
            fixture.AgasHome,
            fixture.ProjectRoot,
            "image.png",
            new RejectCommit());

        Assert.False(result.Success);
        Assert.Equal(AgasControlledAssetImportService.CanceledCode, result.Code);
        Assert.True(File.Exists(unrelated));
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(fixture.ProjectRoot, "assets", "imported"),
            "payload.tmp",
            SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("symlink")]
    public void Import_CommitRejectedWithUnregisteredStagingEntry_RetainsEntireOperation(string unsafeKind)
    {
        using var fixture = new Fixture();
        string source = fixture.WriteInbox("image.png", Png1x1());
        var gate = new RejectCommitWithUnsafeStagingEntry(fixture.ProjectRoot, fixture.Root, unsafeKind);

        AgasAssetImportResult result = CreateService().Import(
            fixture.AgasHome,
            fixture.ProjectRoot,
            "image.png",
            gate);

        Assert.True(gate.WasCalled);
        Assert.False(result.Success);
        Assert.Equal(AgasControlledAssetImportService.CanceledCode, result.Code);
        Assert.NotNull(gate.StagingDirectory);
        Assert.True(Directory.Exists(gate.StagingDirectory));
        Assert.True(File.Exists(Path.Combine(gate.StagingDirectory, "payload.tmp")));
        string marker = Path.Combine(gate.StagingDirectory, "owner.v1");
        Assert.True(File.Exists(marker));
        Assert.Equal(Path.GetFileName(gate.StagingDirectory), File.ReadAllText(marker));
        AssertUnsafeEntryExists(gate.UnsafePath!, unsafeKind);
        Assert.True(File.Exists(source));
        Assert.Equal(Png1x1(), File.ReadAllBytes(source));
    }

    [Fact]
    public void Import_CommitGatePayloadReplacementRetainsMarkerReplacementAndDirectory()
    {
        using var fixture = new Fixture();
        fixture.WriteInbox("image.png", Png1x1());
        var gate = new RejectCommitWithPayloadReplacement(fixture.ProjectRoot);

        AgasAssetImportResult result = CreateService().Import(
            fixture.AgasHome, fixture.ProjectRoot, "image.png", gate);

        Assert.False(result.Success);
        Assert.Equal(AgasControlledAssetImportService.CanceledCode, result.Code);
        Assert.NotNull(gate.StagingDirectory);
        Assert.True(Directory.Exists(gate.StagingDirectory));
        Assert.Equal(gate.Replacement, File.ReadAllBytes(Path.Combine(gate.StagingDirectory, "payload.tmp")));
        Assert.True(File.Exists(Path.Combine(gate.StagingDirectory, "owner.v1")));
    }

    [Fact]
    public void Import_CommitGateStagingAncestorSymlinkRetainsOperationAndDoesNotTouchExternalTarget()
    {
        using var fixture = new Fixture();
        fixture.WriteInbox("image.png", Png1x1());
        var gate = new RejectCommitWithStagingAncestorSymlink(fixture.ProjectRoot, fixture.Root);

        AgasAssetImportResult result = CreateService().Import(
            fixture.AgasHome, fixture.ProjectRoot, "image.png", gate);

        Assert.False(result.Success);
        Assert.Equal(AgasControlledAssetImportService.CanceledCode, result.Code);
        Assert.NotNull(gate.ExternalStagingRoot);
        Assert.NotNull(new DirectoryInfo(gate.StagingLinkPath!).LinkTarget);
        Assert.Equal("external-safe", File.ReadAllText(gate.ExternalSentinel!));
        string operation = Assert.Single(Directory.EnumerateDirectories(gate.ExternalStagingRoot!));
        Assert.True(File.Exists(Path.Combine(operation, "payload.tmp")));
        Assert.True(File.Exists(Path.Combine(operation, "owner.v1")));
    }

    [Fact]
    public void Import_RejectsMoreThan32MiBBeforeStaging()
    {
        using var fixture = new Fixture();
        string path = fixture.WriteInbox("large.png", Array.Empty<byte>());
        using (FileStream stream = File.OpenWrite(path)) stream.SetLength(32L * 1024 * 1024 + 1);

        AgasAssetImportResult result = CreateService()
            .Import(fixture.AgasHome, fixture.ProjectRoot, "large.png");

        Assert.False(result.Success);
        Assert.Equal(AgasControlledAssetImportService.TooLargeCode, result.Code);
    }

    [Theory]
    [InlineData(8193, 1)]
    [InlineData(8001, 8000)]
    public void Import_RejectsOversizedDimensionsOrDecodedPixelBudget(int width, int height)
    {
        using var fixture = new Fixture();
        fixture.WriteInbox("oversized.png", PngWithDimensions(width, height));

        AgasAssetImportResult result = CreateService()
            .Import(fixture.AgasHome, fixture.ProjectRoot, "oversized.png");

        Assert.False(result.Success);
        Assert.Equal(AgasControlledAssetImportService.TooLargeCode, result.Code);
    }

    [Fact]
    public void Import_RejectsPseudoExtensionAndCorruptPayload()
    {
        using var fixture = new Fixture();
        fixture.WriteInbox("fake.jpg", Png1x1());
        byte[] corrupt = Png1x1();
        corrupt[45] ^= 0x40;
        fixture.WriteInbox("corrupt.png", corrupt);

        Assert.Equal(AgasControlledAssetImportService.InvalidCode,
            CreateService().Import(
                fixture.AgasHome, fixture.ProjectRoot, "fake.jpg").Code);
        Assert.Equal(AgasControlledAssetImportService.InvalidCode,
            CreateService().Import(
                fixture.AgasHome, fixture.ProjectRoot, "corrupt.png").Code);
    }

    [Fact]
    public void Import_RejectsStructurallyValidImageWhenFullDecoderRejectsIt()
    {
        using var fixture = new Fixture();
        fixture.WriteInbox("undecodable.png", Png1x1());

        AgasAssetImportResult result = CreateService(new ManagedTestImageDecoder(accept: false))
            .Import(fixture.AgasHome, fixture.ProjectRoot, "undecodable.png");

        Assert.False(result.Success);
        Assert.Equal(AgasControlledAssetImportService.InvalidCode, result.Code);
    }

    private static AgasControlledAssetImportService CreateService(IScenePackageImageDecoder? decoder = null) =>
        new(imageDecoder: decoder ?? new ManagedTestImageDecoder());

    private sealed class RejectCommit : IAgasDurableCommitGate
    {
        public bool TryBeginDurableCommit() => false;
    }

    private sealed class RejectCommitWithUnsafeStagingEntry : IAgasDurableCommitGate
    {
        private readonly string _projectRoot;
        private readonly string _outsideRoot;
        private readonly string _unsafeKind;

        public RejectCommitWithUnsafeStagingEntry(string projectRoot, string outsideRoot, string unsafeKind)
        {
            _projectRoot = projectRoot;
            _outsideRoot = outsideRoot;
            _unsafeKind = unsafeKind;
        }

        public bool WasCalled { get; private set; }
        public string? StagingDirectory { get; private set; }
        public string? UnsafePath { get; private set; }

        public bool TryBeginDurableCommit()
        {
            WasCalled = true;
            string stagingRoot = Path.Combine(_projectRoot, "assets", "imported", ".staging");
            StagingDirectory = Assert.Single(Directory.EnumerateDirectories(stagingRoot));
            UnsafePath = Path.Combine(StagingDirectory, $"unknown.{_unsafeKind}");
            switch (_unsafeKind)
            {
                case "file":
                    File.WriteAllText(UnsafePath, "unregistered");
                    break;
                case "directory":
                    Directory.CreateDirectory(UnsafePath);
                    break;
                case "symlink":
                    string outside = Path.Combine(_outsideRoot, "outside-unknown.txt");
                    File.WriteAllText(outside, "outside");
                    File.CreateSymbolicLink(UnsafePath, outside);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown fixture kind: {_unsafeKind}");
            }
            return false;
        }
    }

    private sealed class RejectCommitWithPayloadReplacement : IAgasDurableCommitGate
    {
        private readonly string _projectRoot;
        public RejectCommitWithPayloadReplacement(string projectRoot) => _projectRoot = projectRoot;
        public string? StagingDirectory { get; private set; }
        public byte[] Replacement { get; } = Enumerable.Repeat((byte)0x5a, Png1x1().Length).ToArray();

        public bool TryBeginDurableCommit()
        {
            StagingDirectory = Assert.Single(Directory.EnumerateDirectories(
                Path.Combine(_projectRoot, "assets", "imported", ".staging")));
            string payload = Path.Combine(StagingDirectory, "payload.tmp");
            DateTime creation = File.GetCreationTimeUtc(payload);
            DateTime mtime = File.GetLastWriteTimeUtc(payload);
            File.WriteAllBytes(payload, Replacement);
            File.SetCreationTimeUtc(payload, creation);
            File.SetLastWriteTimeUtc(payload, mtime);
            return false;
        }
    }

    private sealed class RejectCommitWithStagingAncestorSymlink : IAgasDurableCommitGate
    {
        private readonly string _projectRoot;
        private readonly string _outsideRoot;
        public RejectCommitWithStagingAncestorSymlink(string projectRoot, string outsideRoot)
        {
            _projectRoot = projectRoot;
            _outsideRoot = outsideRoot;
        }

        public string? StagingLinkPath { get; private set; }
        public string? ExternalStagingRoot { get; private set; }
        public string? ExternalSentinel { get; private set; }

        public bool TryBeginDurableCommit()
        {
            StagingLinkPath = Path.Combine(_projectRoot, "assets", "imported", ".staging");
            ExternalStagingRoot = Path.Combine(_outsideRoot, "external-retained-staging");
            Directory.Move(StagingLinkPath, ExternalStagingRoot);
            ExternalSentinel = Path.Combine(ExternalStagingRoot, "external-sentinel.txt");
            File.WriteAllText(ExternalSentinel, "external-safe");
            Directory.CreateSymbolicLink(StagingLinkPath, ExternalStagingRoot);
            return false;
        }
    }

    private static void AssertUnsafeEntryExists(string path, string unsafeKind)
    {
        if (unsafeKind == "directory")
        {
            Assert.True(Directory.Exists(path));
            return;
        }
        Assert.True(File.Exists(path));
        if (unsafeKind == "symlink") Assert.NotNull(new FileInfo(path).LinkTarget);
    }

    internal static byte[] Png1x1() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    internal static byte[] PngWithDimensions(int width, int height)
    {
        byte[] bytes = Png1x1();
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        uint crc = ComputeCrc(bytes.AsSpan(12, 17));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(29, 4), crc);
        return bytes;
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xffffffff;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xffffffff;
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "agas-asset-import", Guid.NewGuid().ToString("N"));
            AgasHome = Path.Combine(Root, "agas-home");
            ProjectRoot = Path.Combine(Root, "project");
            AssetInbox = Path.Combine(AgasHome, "import-inbox", "assets");
            Directory.CreateDirectory(AssetInbox);
            Directory.CreateDirectory(Path.Combine(ProjectRoot, ".agas", "scenes"));
        }
        public string Root { get; }
        public string AgasHome { get; }
        public string ProjectRoot { get; }
        public string AssetInbox { get; }
        public string WriteInbox(string name, byte[] bytes)
        {
            string path = Path.Combine(AssetInbox, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}

internal sealed class ManagedTestImageDecoder : IScenePackageImageDecoder
{
    private readonly bool _accept;
    public ManagedTestImageDecoder(bool accept = true) => _accept = accept;
    public bool TryDecode(ReadOnlyMemory<byte> bytes, string extension, int expectedWidth, int expectedHeight) =>
        _accept && bytes.Length > 0 && expectedWidth > 0 && expectedHeight > 0;
}
