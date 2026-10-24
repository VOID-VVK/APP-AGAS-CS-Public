using Agas.Features.Canvas;
using AppAgasCs.Application;
using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class CanvasImageImportPathServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "agas-canvas-import-" + Guid.NewGuid().ToString("N"));
    private readonly CanvasImageImportPathService _service = new();

    public CanvasImageImportPathServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        Directory.CreateDirectory(Path.Combine(_root, ".agas"));
        File.WriteAllBytes(Path.Combine(_root, "assets", "image.png"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_root, "assets", "unsupported.gif"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_root, ".agas", "private.png"), new byte[] { 1 });
    }

    [Fact]
    public void ResolveImportPath_NormalizesRelativeAndAbsoluteInputsToTheSameProjectPath()
    {
        ProjectImagePathResult relative = _service.ResolveImportPath(_root, "assets/image.png");
        ProjectImagePathResult absolute = _service.ResolveImportPath(
            _root,
            Path.Combine(_root, "assets", "image.png"));

        Assert.True(relative.Success);
        Assert.True(absolute.Success);
        Assert.Equal("assets/image.png", relative.RelativePath);
        Assert.Equal(relative, absolute);
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("data:image/png;base64,AA==")]
    [InlineData("assets/unsupported.gif")]
    [InlineData("assets/missing.png")]
    [InlineData(".agas/private.png")]
    public void ResolveImportPath_RejectsUnsafeUnsupportedOrMissingInputs(string input)
    {
        Assert.False(_service.ResolveImportPath(_root, input).Success);
    }

    [Fact]
    public void ResolveImportPath_RejectsAbsolutePathOutsideProject()
    {
        string outside = Path.Combine(Path.GetTempPath(), "agas-outside-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            File.WriteAllBytes(outside, new byte[] { 1 });
            Assert.False(_service.ResolveImportPath(_root, outside).Success);
        }
        finally
        {
            try { File.Delete(outside); } catch { }
        }
    }

    [Fact]
    public void ResolveStoredProjectPath_RejectsAbsoluteInput()
    {
        string absolute = Path.Combine(_root, "assets", "image.png");

        Assert.False(_service.ResolveStoredProjectPath(_root, absolute).Success);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
