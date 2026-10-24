using Agas.Features.Canvas;
using AppAgasCs.Features.Ai.Generation;
using Xunit;
using Xunit.Sdk;

namespace AppAgasCs.Tests.Core.Ai;

public sealed class CanvasPatchAssetStoreTests
{
    private static readonly byte[] Png = { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4 };

    [Fact]
    public void ScopedSave_ReadAndDelete_UseSceneLayerDirectory()
    {
        using var project = new TemporaryDirectory();
        var scope = new AiGenerationStorageScope("scene-forest", "Forest", "layer-ground", "Ground");

        bool saved = CanvasPatchAssetStore.TrySavePng(
            project.Path,
            Png,
            scope,
            out string relativePath,
            out string saveError);

        Assert.True(saved, saveError);
        Assert.Equal("agas/Forest/Ground/Result 1.png", relativePath);
        Assert.True(CanvasPatchAssetStore.TryReadGenerated(project.Path, relativePath, out byte[] read, out string readError), readError);
        Assert.Equal(Png, read);

        string absolutePath = System.IO.Path.Combine(
            project.Path,
            relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Assert.True(File.Exists(absolutePath));
        CanvasPatchAssetStore.DeleteGeneratedCandidate(project.Path, relativePath);
        Assert.False(File.Exists(absolutePath));
    }

    [Fact]
    public void ScopedSave_UsesDisplayNamesOnlyAndAllocatesSequentialResultNames()
    {
        using var project = new TemporaryDirectory();

        bool firstSaved = CanvasPatchAssetStore.TrySavePng(
            project.Path,
            Png,
            new AiGenerationStorageScope("x-y", "Shared Scene", "layer-1", "Shared Layer"),
            out string firstPath,
            out string firstError);
        bool secondSaved = CanvasPatchAssetStore.TrySavePng(
            project.Path,
            Png,
            new AiGenerationStorageScope("x--y", "Shared Scene", "layer-1", "Shared Layer"),
            out string secondPath,
            out string secondError);

        Assert.True(firstSaved, firstError);
        Assert.True(secondSaved, secondError);
        Assert.Equal("agas/Shared Scene/Shared Layer/Result 1.png", firstPath);
        Assert.Equal("agas/Shared Scene/Shared Layer/Result 2.png", secondPath);
    }

    [Fact]
    public async Task ConcurrentScopedSaves_AllocateUniqueSequentialResultNamesWithoutOverwrite()
    {
        using var project = new TemporaryDirectory();
        var scope = new AiGenerationStorageScope("scene-forest", "Forest", "layer-ground", "Ground");

        Task<(bool Saved, string Path, string Error)>[] saves = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                bool saved = CanvasPatchAssetStore.TrySavePng(
                    project.Path,
                    Png,
                    scope,
                    out string path,
                    out string error);
                return (saved, path, error);
            }))
            .ToArray();

        (bool Saved, string Path, string Error)[] results = await Task.WhenAll(saves);
        Assert.All(results, result => Assert.True(result.Saved, result.Error));
        Assert.Equal(
            Enumerable.Range(1, 8).Select(index => $"agas/Forest/Ground/Result {index}.png").Order(),
            results.Select(result => result.Path).Order());
        Assert.All(results, result => Assert.Equal(
            Png,
            File.ReadAllBytes(System.IO.Path.Combine(
                project.Path,
                result.Path.Replace('/', System.IO.Path.DirectorySeparatorChar)))));
    }

    [Fact]
    public async Task ProviderCandidate_DoesNotConsumePublicResultNumber()
    {
        using var project = new TemporaryDirectory();
        var scope = new AiGenerationStorageScope("scene-forest", "Forest", "layer-ground", "Ground");
        var providerStore = new AiGeneratedImageStore();

        AiGenerationResult candidate = await providerStore.SaveAsync(
            project.Path,
            scope,
            new GeneratedImageData(Png, "image/png"),
            CancellationToken.None);
        bool saved = CanvasPatchAssetStore.TrySavePng(
            project.Path,
            Png,
            scope,
            out string finalPath,
            out string error);

        Assert.Equal("agas/Forest/Ground/.candidates/Candidate 1.png", candidate.RelativePath);
        Assert.True(saved, error);
        Assert.Equal("agas/Forest/Ground/Result 1.png", finalPath);
        Assert.True(CanvasPatchAssetStore.TryReadGenerated(
            project.Path,
            candidate.RelativePath,
            out byte[] candidateBytes,
            out string readError), readError);
        Assert.Equal(Png, candidateBytes);
    }

    [Fact]
    public void ScopedSave_UsesExistingOnDiskDirectoryCasing()
    {
        using var project = new TemporaryDirectory();
        Assert.True(CanvasPatchAssetStore.TrySavePng(
            project.Path,
            Png,
            new AiGenerationStorageScope("scene-1", "Scene", "layer-1", "Layer"),
            out _,
            out string firstError), firstError);

        Assert.True(CanvasPatchAssetStore.TrySavePng(
            project.Path,
            Png,
            new AiGenerationStorageScope("scene-2", "scene", "layer-2", "layer"),
            out string secondPath,
            out string secondError), secondError);

        string expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? "agas/Scene/Layer/Result 2.png"
            : "agas/scene/layer/Result 1.png";
        Assert.Equal(expected, secondPath);
    }

    [Fact]
    public void ScopedSave_ContinuesAfterExistingResultWithAnotherExtension()
    {
        using var project = new TemporaryDirectory();
        string outputDirectory = System.IO.Path.Combine(project.Path, "agas", "Forest", "Ground");
        Directory.CreateDirectory(outputDirectory);
        string existingPath = System.IO.Path.Combine(outputDirectory, "Result 1.webp");
        File.WriteAllBytes(existingPath, new byte[] { 1, 2, 3 });

        Assert.True(CanvasPatchAssetStore.TrySavePng(
            project.Path,
            Png,
            new AiGenerationStorageScope("scene-forest", "Forest", "layer-ground", "Ground"),
            out string relativePath,
            out string error), error);

        Assert.Equal("agas/Forest/Ground/Result 2.png", relativePath);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(existingPath));
    }

    [Theory]
    [InlineData("CON.txt", "CON")]
    [InlineData("LPT1.foo", "LPT1")]
    public void ScopedSave_SanitizesWindowsReservedDisplayNames(string displayName, string reservedStem)
    {
        using var project = new TemporaryDirectory();

        bool saved = CanvasPatchAssetStore.TrySavePng(
            project.Path,
            Png,
            new AiGenerationStorageScope("scene-1", displayName, "layer-1", displayName),
            out string relativePath,
            out string error);

        Assert.True(saved, error);
        string[] segments = relativePath.Split('/');
        Assert.Equal(4, segments.Length);
        AssertDirectoryDisplayNameIsNotReserved(segments[1], reservedStem);
        AssertDirectoryDisplayNameIsNotReserved(segments[2], reservedStem);
        Assert.True(File.Exists(System.IO.Path.Combine(
            project.Path,
            relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar))));
    }

    [Theory]
    [InlineData("assets/generated/legacy.png")]
    [InlineData("agas/Old Scene--scene-old/Old Layer--layer-old/legacy.png")]
    public void LegacyGeneratedPath_RemainsReadableAndDeletable(string relativePath)
    {
        using var project = new TemporaryDirectory();
        string absolutePath = System.IO.Path.Combine(
            project.Path,
            relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(absolutePath)!);
        File.WriteAllBytes(absolutePath, Png);

        Assert.True(CanvasPatchAssetStore.TryReadGenerated(project.Path, relativePath, out byte[] read, out string error), error);
        Assert.Equal(Png, read);

        CanvasPatchAssetStore.DeleteGeneratedCandidate(project.Path, relativePath);
        Assert.False(File.Exists(absolutePath));
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("agas/../outside.png")]
    [InlineData("agas/Scene--scene/Layer--layer/nested/image.png")]
    [InlineData("agas/Scene--scene/Layer--layer/image.exe")]
    [InlineData("/tmp/outside.png")]
    [InlineData("C:\\outside.png")]
    public void ReadAndDelete_RejectTraversalAbsoluteAndInvalidGeneratedPaths(string relativePath)
    {
        using var project = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        string outsidePath = System.IO.Path.Combine(outside.Path, "outside.png");
        File.WriteAllBytes(outsidePath, Png);

        Assert.False(CanvasPatchAssetStore.TryReadGenerated(project.Path, relativePath, out _, out _));
        CanvasPatchAssetStore.DeleteGeneratedCandidate(project.Path, relativePath);
        Assert.True(File.Exists(outsidePath));
    }

    [Fact]
    public void ReadAndDelete_RejectGeneratedFileSymlink()
    {
        using var project = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        string outsidePath = System.IO.Path.Combine(outside.Path, "outside.png");
        File.WriteAllBytes(outsidePath, Png);
        string currentDirectory = System.IO.Path.Combine(project.Path, "agas", "Scene--scene", "Layer--layer");
        Directory.CreateDirectory(currentDirectory);
        string linkPath = System.IO.Path.Combine(currentDirectory, "candidate.png");
        CreateFileSymlinkOrSkip(linkPath, outsidePath);

        const string relativePath = "agas/Scene--scene/Layer--layer/candidate.png";
        Assert.False(CanvasPatchAssetStore.TryReadGenerated(project.Path, relativePath, out _, out _));
        CanvasPatchAssetStore.DeleteGeneratedCandidate(project.Path, relativePath);
        Assert.True(File.Exists(outsidePath));
        Assert.True(File.Exists(linkPath));
    }

    [Fact]
    public void ScopedSave_RejectsSceneDirectorySymlink()
    {
        using var project = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        string agasDirectory = System.IO.Path.Combine(project.Path, "agas");
        Directory.CreateDirectory(agasDirectory);
        string sceneLink = System.IO.Path.Combine(agasDirectory, "Forest");
        CreateDirectorySymlinkOrSkip(sceneLink, outside.Path);

        bool saved = CanvasPatchAssetStore.TrySavePng(
            project.Path,
            Png,
            new AiGenerationStorageScope("scene-forest", "Forest", "layer-ground", "Ground"),
            out string relativePath,
            out string error);

        Assert.False(saved);
        Assert.Empty(relativePath);
        Assert.NotEmpty(error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
    }

    private static void CreateDirectorySymlinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            throw SkipException.ForSkip($"Directory symlink creation is unavailable: {exception.Message}");
        }
    }

    private static void AssertDirectoryDisplayNameIsNotReserved(string directoryName, string reservedStem)
    {
        string stem = directoryName.Split('.')[0];
        Assert.False(string.Equals(reservedStem, stem, StringComparison.OrdinalIgnoreCase));
    }

    private static void CreateFileSymlinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            throw SkipException.ForSkip($"File symlink creation is unavailable: {exception.Message}");
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"agas-storage-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup must not replace an assertion failure.
            }
        }
    }
}
