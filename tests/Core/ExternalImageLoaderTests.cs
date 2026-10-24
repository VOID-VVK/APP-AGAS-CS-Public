using Xunit;
using AppAgasCs.Core.Persistence;

namespace AppAgasCs.Tests.Core;

public sealed class ExternalImageLoaderTests
{
    [Fact]
    public void SupportedExtensions_AreExposedAsReadOnlyData()
    {
        Assert.Equal(new[] { ".png", ".jpg", ".jpeg", ".webp" }, ExternalImageLoader.SupportedExtensions);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)ExternalImageLoader.SupportedExtensions)[0] = ".unsafe");
        Assert.Equal(".png", ExternalImageLoader.SupportedExtensions[0]);
    }

    [Fact]
    public void ExternalImageLoaderSource_UsesImageLoadFromFileAndRejectsUnsafePaths()
    {
        string source = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/Core/Persistence/ExternalImageLoader.cs"));

        Assert.Contains("Image.LoadFromFile(absolutePath)", source);
        Assert.Contains(".png", source);
        Assert.Contains(".jpg", source);
        Assert.Contains(".jpeg", source);
        Assert.Contains(".webp", source);
        Assert.Contains("Path.IsPathRooted(imagePath)", source);
        Assert.Contains("data:", source);
        Assert.Contains("://", source);
    }

    [Fact]
    public void ProductSources_DoNotUseGdscriptJavascriptOrJsonPersistence()
    {
        string[] forbiddenExtensions = Directory.GetFiles(ProjectRoot.Path, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => path.EndsWith(".gd", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(forbiddenExtensions);

        string coreRoot = Path.Combine(ProjectRoot.Path, "src/Core");
        string scenePackageCodecPath = Path.GetFullPath(
            Path.Combine(coreRoot, "Persistence/ScenePackageCodec.cs"));
        string scenePackageStorePath = Path.GetFullPath(
            Path.Combine(coreRoot, "Persistence/ScenePackageStore.cs"));
        string scenePackageStagingPath = Path.GetFullPath(
            Path.Combine(ProjectRoot.Path, "src/Application/AgasScenePackageStagingService.cs"));
        string scenePackageArtifactCatalogPath = Path.GetFullPath(
            Path.Combine(ProjectRoot.Path, "src/Application/ScenePackageTransportArtifactCatalog.cs"));
        string[] productSources = Directory.GetFiles(coreRoot, "*.cs", SearchOption.AllDirectories);
        string[] jsonSerializerSources = productSources
            .Where(sourcePath => File.ReadAllText(sourcePath).Contains("JsonSerializer", StringComparison.Ordinal))
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { scenePackageCodecPath }, jsonSerializerSources);

        string[] scenePackageCodecConsumers = Directory
            .GetFiles(Path.Combine(ProjectRoot.Path, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(sourcePath => !string.Equals(
                Path.GetFullPath(sourcePath),
                scenePackageCodecPath,
                StringComparison.Ordinal))
            .Where(sourcePath => File.ReadAllText(sourcePath)
                .Contains("ScenePackageCodec.", StringComparison.Ordinal))
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                scenePackageStagingPath,
                scenePackageArtifactCatalogPath,
                scenePackageStorePath,
            }
                .OrderBy(path => path, StringComparer.Ordinal),
            scenePackageCodecConsumers);

        string scenePackageStoreSource = File.ReadAllText(scenePackageStorePath);
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"File\.WriteAllText\(\s*AgasPaths\.ScenePackageManifestPath\(stagingDirectory\),\s*ScenePackageCodec\.Serialize\(document\)\s*\)",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            scenePackageStoreSource);
        Assert.Matches(
            new System.Text.RegularExpressions.Regex(
                @"string\s+manifestPath\s*=\s*AgasPaths\.ScenePackageManifestPath\(packageDirectory\);.*?ScenePackageCodec\.Deserialize\(ReadManifest\(manifestPath\)\)",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant
                    | System.Text.RegularExpressions.RegexOptions.Singleline),
            scenePackageStoreSource);

        foreach (string sourcePath in productSources)
        {
            string source = File.ReadAllText(sourcePath);
            Assert.DoesNotContain("Newtonsoft", source);
        }
    }
}
