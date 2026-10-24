#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using AppAgasCs.Core.Persistence;
using Xunit;

namespace AppAgasCs.Tests.Core;

public sealed class ScenePackageArchiveTests
{
    [Fact]
    public void CreateAndExtract_PreservesRootManifestAndNestedFiles()
    {
        using var fixture = new Fixture();
        string source = fixture.Directory("source");
        Directory.CreateDirectory(Path.Combine(source, "images", "layer-1"));
        File.WriteAllText(Path.Combine(source, "scene.json"), "{}");
        File.WriteAllText(Path.Combine(source, "images", "layer-1", "image.txt"), "payload");
        string archivePath = fixture.Path("package.zip");

        ScenePackageArchive.Create(source, archivePath);
        using (ZipArchive archive = ZipFile.OpenRead(archivePath))
        {
            Assert.Equal(
                new[] { "images/layer-1/image.txt", "scene.json" },
                archive.Entries.Select(entry => entry.FullName).OrderBy(path => path, StringComparer.Ordinal));
        }

        string extracted = ScenePackageArchive.ExtractToTemporaryDirectory(archivePath);
        try
        {
            Assert.Equal("{}", File.ReadAllText(Path.Combine(extracted, "scene.json")));
            Assert.Equal("payload", File.ReadAllText(Path.Combine(extracted, "images", "layer-1", "image.txt")));
        }
        finally
        {
            Directory.Delete(extracted, recursive: true);
        }
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/drive.txt")]
    [InlineData("nested\\backslash.txt")]
    [InlineData("nested//empty.txt")]
    [InlineData("nested/./dot.txt")]
    public void Extract_RejectsUnsafeEntryPaths(string unsafePath)
    {
        using var fixture = new Fixture();
        string archivePath = fixture.CreateArchive(
            ("scene.json", "{}"u8.ToArray(), null),
            (unsafePath, "bad"u8.ToArray(), null));

        AssertExtractionRejectedWithoutTemporaryResidue(archivePath);
    }

    [Fact]
    public void Extract_RejectsDuplicateAndCaseConflictingPaths()
    {
        using var fixture = new Fixture();
        string duplicate = fixture.CreateArchive(
            ("scene.json", "{}"u8.ToArray(), null),
            ("assets/a.txt", "one"u8.ToArray(), null),
            ("assets/a.txt", "two"u8.ToArray(), null));
        string caseConflict = fixture.CreateArchive(
            ("scene.json", "{}"u8.ToArray(), null),
            ("Assets/a.txt", "one"u8.ToArray(), null),
            ("assets/b.txt", "two"u8.ToArray(), null));

        AssertExtractionRejectedWithoutTemporaryResidue(duplicate);
        AssertExtractionRejectedWithoutTemporaryResidue(caseConflict);
    }

    [Fact]
    public void Extract_AcceptsExplicitDirectoryAfterItsChild()
    {
        using var fixture = new Fixture();
        string archivePath = fixture.CreateArchive(
            ("scene.json", "{}"u8.ToArray(), null),
            ("assets/a.txt", "one"u8.ToArray(), null),
            ("assets/", Array.Empty<byte>(), null));

        string extracted = ScenePackageArchive.ExtractToTemporaryDirectory(archivePath);
        try
        {
            Assert.Equal("one", File.ReadAllText(Path.Combine(extracted, "assets", "a.txt")));
        }
        finally
        {
            Directory.Delete(extracted, recursive: true);
        }
    }

    [Fact]
    public void Extract_RejectsUnixSymbolicLinkEntries()
    {
        using var fixture = new Fixture();
        int symbolicLinkAttributes = unchecked((0xa000 | 0x1ff) << 16);
        string archivePath = fixture.CreateArchive(
            ("scene.json", "{}"u8.ToArray(), null),
            ("link", "outside"u8.ToArray(), symbolicLinkAttributes));

        AssertExtractionRejectedWithoutTemporaryResidue(archivePath);
    }

    [Fact]
    public void Extract_RejectsMoreThanManifestPlus512PayloadFiles()
    {
        using var fixture = new Fixture();
        var entries = new List<(string Name, byte[] Bytes, int? ExternalAttributes)>
        {
            ("scene.json", "{}"u8.ToArray(), null),
        };
        entries.AddRange(Enumerable.Range(0, 513)
            .Select(index => ($"files/{index:D3}.bin", Array.Empty<byte>(), (int?)null)));
        string archivePath = fixture.CreateArchive(entries.ToArray());

        AssertExtractionRejectedWithoutTemporaryResidue(archivePath);
    }

    [Fact]
    public void Extract_RejectsManifestOverOneMiB()
    {
        using var fixture = new Fixture();
        string archivePath = fixture.CreateArchive(
            ("scene.json", new byte[ScenePackageSafetyLimits.MaximumManifestBytes + 1], null));

        AssertExtractionRejectedWithoutTemporaryResidue(archivePath);
    }

    private static void AssertExtractionRejectedWithoutTemporaryResidue(string archivePath)
    {
        var before = Directory.GetDirectories(Path.GetTempPath(), "agas-scene-package-*")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Throws<InvalidDataException>(() =>
            ScenePackageArchive.ExtractToTemporaryDirectory(archivePath));

        string[] residue = Directory.GetDirectories(Path.GetTempPath(), "agas-scene-package-*")
            .Where(path => !before.Contains(path))
            .ToArray();
        Assert.Empty(residue);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "agas-scene-archive-tests-" + Guid.NewGuid().ToString("N"));
        private int _archiveIndex;

        public Fixture() => System.IO.Directory.CreateDirectory(_root);

        public string Directory(string name)
        {
            string path = Path(name);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public string Path(string name) => System.IO.Path.Combine(_root, name);

        public string CreateArchive(
            params (string Name, byte[] Bytes, int? ExternalAttributes)[] entries)
        {
            string path = Path($"crafted-{_archiveIndex++}.zip");
            using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);
            foreach ((string name, byte[] bytes, int? attributes) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                if (attributes.HasValue) entry.ExternalAttributes = attributes.Value;
                using Stream stream = entry.Open();
                stream.Write(bytes);
            }
            return path;
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(_root))
                System.IO.Directory.Delete(_root, recursive: true);
        }
    }
}
