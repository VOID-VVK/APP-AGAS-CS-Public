using AppAgasCs.Core.Persistence;
using AppAgasCs.Tests.Core;
using Xunit;

namespace AppAgasCs.Tests.Core.Application;

/// <summary>
/// Guards the path-security single source of truth: MCP / Tools / Application
/// must not reintroduce private extension or internal-directory allow-lists.
/// </summary>
public sealed class ProjectPathRulesArchitectureTests
{
    [Fact]
    public void SharedRules_OwnCanonicalExtensionAndInternalDirectorySets()
    {
        Assert.Equal(
            new[] { ".jpeg", ".jpg", ".png", ".webp" },
            ProjectPathRules.AllowedImageExtensionsWithDot.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.Equal(
            new[] { ".agas", ".git", ".godot", "bin", "obj" },
            ProjectPathRules.SkippedDirectoryNames.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [Fact]
    public void Consumers_DoNotRedefineSharedAllowLists()
    {
        string rules = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/Core/Persistence/ProjectPathRules.cs"));
        string mcp = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/Mcp/AgasMcpPathValidator.cs"));
        string imageResolver = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/Application/ProjectImagePathResolver.cs"));
        string toolPaths = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/Features/Tools/Execution/ImageToolProjectPaths.cs"));
        string scenePackage = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/Core/Persistence/ScenePackageStore.cs"));

        Assert.Contains("static class ProjectPathRules", rules, StringComparison.Ordinal);
        Assert.Contains("AllowedImageExtensionsWithDot", rules, StringComparison.Ordinal);
        Assert.Contains("SkippedDirectoryNames", rules, StringComparison.Ordinal);

        foreach (string consumer in new[] { mcp, imageResolver, toolPaths, scenePackage })
        {
            Assert.DoesNotContain(
                "\".agas\", \".godot\", \".git\", \"bin\", \"obj\"",
                consumer,
                StringComparison.Ordinal);
        }

        Assert.DoesNotContain(
            "new[] { \".png\", \".jpg\", \".jpeg\", \".webp\" }",
            mcp,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "private static readonly HashSet<string> AllowedExtensions",
            imageResolver,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "private static readonly HashSet<string> AllowedImageExtensions",
            scenePackage,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("assets/a.png", true)]
    [InlineData("../escape.png", false)]
    [InlineData(".agas/secret.png", false)]
    [InlineData("data:image/png;base64,AA==", false)]
    [InlineData("folder/.git/x.png", true)] // FirstSegmentOnly allows nested names for MCP/UI assets
    public void TryNormalizeRelativePath_FirstSegmentPolicy_MatchesAssetRules(
        string path,
        bool expectedSuccess)
    {
        ProjectRelativePathSyntaxResult result = ProjectPathRules.TryNormalizeRelativePath(
            path,
            new ProjectRelativePathSyntaxOptions(
                AllowInternalDirectory: false,
                InternalDirectoryPolicy: ProjectInternalDirectoryPolicy.FirstSegmentOnly,
                RequireImageExtension: true));

        Assert.Equal(expectedSuccess, result.Success);
    }

    [Theory]
    [InlineData("assets/a.png", true)]
    [InlineData("folder/.git/x.png", false)] // AnySegment rejects nested internal dirs
    [InlineData(".agas/blueprints/a.tres", false)]
    public void TryNormalizeRelativePath_AnySegmentPolicy_IsStricter(
        string path,
        bool expectedSuccess)
    {
        ProjectRelativePathSyntaxResult result = ProjectPathRules.TryNormalizeRelativePath(
            path,
            new ProjectRelativePathSyntaxOptions(
                AllowInternalDirectory: false,
                InternalDirectoryPolicy: ProjectInternalDirectoryPolicy.AnySegment,
                RequireImageExtension: false,
                RejectEmptyOrDotSegments: true,
                RejectUntrimmedWhitespace: true));

        Assert.Equal(expectedSuccess, result.Success);
    }
}
