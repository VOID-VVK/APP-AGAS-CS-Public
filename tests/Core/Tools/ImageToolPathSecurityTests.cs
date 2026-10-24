using AppAgasCs.Features.Tools.Execution;
using Xunit;

namespace AppAgasCs.Tests.Core.Tools;

public sealed class ImageToolPathSecurityTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "agas-tool-path-security-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;

    public ImageToolPathSecurityTests()
    {
        _project = Path.Combine(_base, "project");
        Directory.CreateDirectory(Path.Combine(_project, "assets"));
        File.WriteAllBytes(Path.Combine(_project, "assets", "input.png"), [1, 2, 3]);
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("assets/../outside.png")]
    [InlineData("assets//outside.png")]
    [InlineData("assets/./outside.png")]
    [InlineData("/tmp/outside.png")]
    [InlineData("C:\\outside.png")]
    [InlineData("https://example.test/a.png")]
    [InlineData("data:image/png;base64,AAAA")]
    public void InputAndOutputRejectUnsafePathSyntax(string path)
    {
        Assert.Equal(ImageToolRunErrorCodes.PathInvalid,
            Assert.Throws<ImageToolRunException>(() => ImageToolProjectPaths.ExistingImage(_project, path)).Code);
        Assert.Equal(ImageToolRunErrorCodes.PathInvalid,
            Assert.Throws<ImageToolRunException>(() => ImageToolProjectPaths.NewPng(_project, path)).Code);
    }

    [Fact]
    public void OutputCannotEscapeThroughSiblingPrefixOrParentLink()
    {
        string sibling = Path.Combine(_base, "project-other");
        Directory.CreateDirectory(sibling);
        Assert.Equal(ImageToolRunErrorCodes.PathInvalid,
            Assert.Throws<ImageToolRunException>(() => ImageToolProjectPaths.NewPng(_project, "../project-other/output.png")).Code);

        if (OperatingSystem.IsWindows()) return;
        string outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(_project, "linked"), outside);
        Assert.Equal(ImageToolRunErrorCodes.PathInvalid,
            Assert.Throws<ImageToolRunException>(() => ImageToolProjectPaths.NewPng(_project, "linked/output.png")).Code);
        Assert.False(File.Exists(Path.Combine(outside, "output.png")));
    }

    [Fact]
    public void RootAndFinalSymlinksAreRejected()
    {
        if (OperatingSystem.IsWindows()) return;
        string linkedRoot = Path.Combine(_base, "linked-root");
        Directory.CreateSymbolicLink(linkedRoot, _project);
        Assert.Equal(ImageToolRunErrorCodes.PathInvalid,
            Assert.Throws<ImageToolRunException>(() => ImageToolProjectPaths.ValidateRoot(linkedRoot)).Code);

        string linkedOutput = Path.Combine(_project, "assets", "linked-output.png");
        File.CreateSymbolicLink(linkedOutput, Path.Combine(_base, "absent.png"));
        Assert.Equal(ImageToolRunErrorCodes.OutputExists,
            Assert.Throws<ImageToolRunException>(() => ImageToolProjectPaths.NewPng(_project, "assets/linked-output.png")).Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }
}
