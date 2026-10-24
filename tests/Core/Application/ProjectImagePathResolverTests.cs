using AppAgasCs.Application;
using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class ProjectImagePathResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agas-image-path-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectImagePathResolver _resolver = new();

    public ProjectImagePathResolverTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        File.WriteAllBytes(Path.Combine(_root, "assets", "image.png"), new byte[] { 1 });
    }

    [Fact]
    public void Resolve_AcceptsExistingProjectRelativeImage()
    {
        ProjectImagePathResult result = _resolver.Resolve(_root, "assets/image.png");

        Assert.True(result.Success);
        Assert.Equal("assets/image.png", result.RelativePath);
        Assert.Equal(Path.Combine(_root, "assets", "image.png"), result.AbsolutePath);
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("data:image/png;base64,AA==")]
    [InlineData("C:\\outside.png")]
    [InlineData(".agas/private.png")]
    [InlineData("assets/image.gif")]
    public void Resolve_RejectsUnsafeOrUnsupportedPath(string path)
    {
        Assert.False(_resolver.Resolve(_root, path, mustExist: false).Success);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
