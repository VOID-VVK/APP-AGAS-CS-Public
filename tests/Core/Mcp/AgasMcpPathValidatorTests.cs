using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp;

public sealed class AgasMcpPathValidatorTests : IDisposable
{
    private readonly string _root;
    private readonly AgasMcpPathValidator _validator = new();

    public AgasMcpPathValidatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agas-mcp-path-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        Directory.CreateDirectory(Path.Combine(_root, ".agas", "scenes"));
        File.WriteAllBytes(Path.Combine(_root, "assets", "ok.png"), new byte[] { 0x89, 0x50, 0x4e, 0x47 });
        File.WriteAllBytes(Path.Combine(_root, "assets", "ok.jpg"), new byte[] { 0xff, 0xd8, 0xff });
        File.WriteAllBytes(Path.Combine(_root, "assets", "ok.jpeg"), new byte[] { 0xff, 0xd8, 0xff });
        File.WriteAllBytes(Path.Combine(_root, "assets", "ok.webp"), new byte[] { 0x52, 0x49, 0x46, 0x46 });
        File.WriteAllText(Path.Combine(_root, "assets", "bad.gif"), "gif");
        File.WriteAllText(Path.Combine(_root, ".agas", "scenes", "safe_scene-01.tres"), "scene");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("/tmp/evil.png")]
    [InlineData("../evil.png")]
    [InlineData("assets/../evil.png")]
    [InlineData("http://127.0.0.1/a.png")]
    [InlineData("https://example.test/a.png")]
    [InlineData("file:///tmp/a.png")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("AAAA;base64,BBBB")]
    [InlineData(".agas/secret.png")]
    [InlineData(".git/secret.png")]
    [InlineData("bin/secret.png")]
    [InlineData("obj/secret.png")]
    public void ValidateImageFile_RejectsUnsafePathInputs(string path)
    {
        AgasMcpPathValidationResult result = _validator.ValidateImageFile(_root, path, mustExist: false);

        Assert.False(result.Success);
        Assert.Equal(AgasMcpErrorCodes.PathRejected, result.Code);
    }

    [Fact]
    public void ValidateProjectRoot_RejectsSymlinkRootAsUnsafe()
    {
        string realRoot = Path.Combine(Path.GetTempPath(), "agas-mcp-real-root-" + Guid.NewGuid().ToString("N"));
        string linkedRoot = Path.Combine(Path.GetTempPath(), "agas-mcp-linked-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(realRoot);
        try
        {
            Directory.CreateSymbolicLink(linkedRoot, realRoot);

            AgasMcpPathValidationResult result = _validator.ValidateProjectRoot(linkedRoot);

            Assert.False(result.Success);
            Assert.Equal(AgasMcpErrorCodes.ProjectRootUnsafe, result.Code);
        }
        finally
        {
            if (Directory.Exists(linkedRoot) || File.Exists(linkedRoot)) Directory.Delete(linkedRoot);
            if (Directory.Exists(realRoot)) Directory.Delete(realRoot, recursive: true);
        }
    }

    [Fact]
    public void ValidateImageFile_RejectsSymlinkFile()
    {
        string link = Path.Combine(_root, "assets", "linked.png");
        File.CreateSymbolicLink(link, Path.Combine(_root, "assets", "ok.png"));

        AgasMcpPathValidationResult result = _validator.ValidateImageFile(_root, "assets/linked.png", mustExist: true);

        Assert.False(result.Success);
        Assert.Equal(AgasMcpErrorCodes.PathRejected, result.Code);
    }

    [Fact]
    public void ValidateImageFile_RejectsSymlinkDirectoryInPath()
    {
        string targetDir = Path.Combine(_root, "assets");
        string linkDir = Path.Combine(_root, "linked-assets");
        Directory.CreateSymbolicLink(linkDir, targetDir);

        AgasMcpPathValidationResult result = _validator.ValidateImageFile(_root, "linked-assets/ok.png", mustExist: true);

        Assert.False(result.Success);
        Assert.Equal(AgasMcpErrorCodes.PathRejected, result.Code);
    }

    [Theory]
    [InlineData("assets/ok.png", "assets/ok.png")]
    [InlineData("assets/ok.jpg", "assets/ok.jpg")]
    [InlineData("assets/ok.jpeg", "assets/ok.jpeg")]
    [InlineData("assets/ok.webp", "assets/ok.webp")]
    [InlineData("assets\\ok.png", "assets/ok.png")]
    public void ValidateImageFile_AllowsProjectRelativePngJpgJpegWebp(string input, string logicalPath)
    {
        AgasMcpPathValidationResult result = _validator.ValidateImageFile(_root, input, mustExist: true);

        Assert.True(result.Success, result.Message);
        Assert.Equal(logicalPath, result.RelativePath);
        Assert.StartsWith(AgasMcpPathValidator.NormalizeRoot(_root), result.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateImageFile_RejectsUnsupportedImageExtension()
    {
        AgasMcpPathValidationResult result = _validator.ValidateImageFile(_root, "assets/bad.gif", mustExist: true);

        Assert.False(result.Success);
        Assert.Equal(AgasMcpErrorCodes.PathRejected, result.Code);
    }

    [Theory]
    [InlineData("safe_scene-01", ".agas/scenes/safe_scene-01.tres")]
    [InlineData("AZaz09_-", ".agas/scenes/AZaz09_-.tres")]
    public void ValidateAgasSceneFile_AllowsSafeSceneIdsInsideAgasScenes(string sceneId, string relativePath)
    {
        AgasMcpPathValidationResult result = _validator.ValidateAgasSceneFile(_root, sceneId);

        Assert.True(result.Success, result.Message);
        Assert.Equal(relativePath, result.RelativePath);
        Assert.StartsWith(AgasMcpPathValidator.NormalizeRoot(_root), result.AbsolutePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("has.dot")]
    [InlineData("../scene")]
    [InlineData("scene/path")]
    [InlineData("scene path")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void ValidateAgasSceneFile_RejectsSceneIdsOutsideSafeIdentifierContract(string sceneId)
    {
        AgasMcpPathValidationResult result = _validator.ValidateAgasSceneFile(_root, sceneId);

        Assert.False(result.Success);
        Assert.Equal(AgasMcpErrorCodes.PathRejected, result.Code);
    }

    [Fact]
    public void ValidateAgasSceneFile_RejectsSymlinkSceneFile()
    {
        string link = Path.Combine(_root, ".agas", "scenes", "scene-link.tres");
        File.CreateSymbolicLink(link, Path.Combine(_root, ".agas", "scenes", "safe_scene-01.tres"));

        AgasMcpPathValidationResult result = _validator.ValidateAgasSceneFile(_root, "scene-link");

        Assert.False(result.Success);
        Assert.Equal(AgasMcpErrorCodes.PathRejected, result.Code);
    }

    [Fact]
    public void ValidateAgasSceneFile_RejectsSymlinkAgasSceneDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "agas-mcp-scene-link-root-" + Guid.NewGuid().ToString("N"));
        string realScenes = Path.Combine(Path.GetTempPath(), "agas-mcp-real-scenes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".agas"));
        Directory.CreateDirectory(realScenes);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, ".agas", "scenes"), realScenes);

            AgasMcpPathValidationResult result = _validator.ValidateAgasSceneFile(root, "scene-01");

            Assert.False(result.Success);
            Assert.Equal(AgasMcpErrorCodes.PathRejected, result.Code);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(realScenes)) Directory.Delete(realScenes, recursive: true);
        }
    }
}
