using AppAgasCs.Features.Tools.Execution;
using Xunit;

namespace AppAgasCs.Tests.Core.Tools;

public sealed class ImageToolPathAndHeaderTests
{
    [Fact]
    public void DirectoryHandleCommitRejectsParentSymlinkSwapWithoutRedirectingOutput()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "agas-tool-race-" + Guid.NewGuid().ToString("N"));
        string generated = Path.Combine(root, "assets", "generated");
        string held = Path.Combine(root, "assets", "generated-held");
        string attacker = Path.Combine(root, "attacker");
        Directory.CreateDirectory(generated);
        Directory.CreateDirectory(attacker);
        try
        {
            string validatedRoot = ImageToolProjectPaths.ValidateRoot(root);
            string final = ImageToolProjectPaths.NewPng(validatedRoot, "assets/generated/result.png");
            var files = new ImageToolSecureFileAccess(beforeCommitMove: () =>
            {
                Directory.Move(generated, held);
                Directory.CreateSymbolicLink(generated, attacker);
                string tempName = Assert.Single(Directory.GetFiles(held, ".agas-tool-*.tmp"));
                File.WriteAllText(Path.Combine(attacker, Path.GetFileName(tempName)), "attacker");
            });
            Assert.Equal(ImageToolRunErrorCodes.PathInvalid,
                Assert.Throws<ImageToolRunException>(() => files.WritePng(validatedRoot, final, [1, 2, 3], CancellationToken.None)).Code);
            Assert.False(File.Exists(Path.Combine(attacker, "result.png")));
            Assert.False(File.Exists(Path.Combine(held, "result.png")));
            Assert.Empty(Directory.GetFiles(held, ".agas-tool-*.tmp"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DirectoryHandleCommitNeverOverwritesLateWinner()
    {
        string root = Path.Combine(Path.GetTempPath(), "agas-tool-commit-" + Guid.NewGuid().ToString("N"));
        string generated = Path.Combine(root, "assets", "generated");
        Directory.CreateDirectory(generated);
        try
        {
            string final = ImageToolProjectPaths.NewPng(ImageToolProjectPaths.ValidateRoot(root), "assets/generated/result.png");
            var files = new ImageToolSecureFileAccess(beforeCommitMove: () => File.WriteAllText(final, "winner"));
            Assert.Equal(ImageToolRunErrorCodes.OutputExists,
                Assert.Throws<ImageToolRunException>(() => files.WritePng(root, final, [1, 2, 3], CancellationToken.None)).Code);
            Assert.Equal("winner", File.ReadAllText(final));
            Assert.Empty(Directory.GetFiles(generated, ".agas-tool-*.tmp"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DirectoryHandleCommitCleansOwnedOutputIfParentChangesAfterMove()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "agas-tool-post-move-" + Guid.NewGuid().ToString("N"));
        string generated = Path.Combine(root, "assets", "generated");
        string held = Path.Combine(root, "assets", "generated-held");
        string attacker = Path.Combine(root, "attacker");
        Directory.CreateDirectory(generated);
        Directory.CreateDirectory(attacker);
        try
        {
            string final = ImageToolProjectPaths.NewPng(ImageToolProjectPaths.ValidateRoot(root), "assets/generated/result.png");
            var files = new ImageToolSecureFileAccess(afterCommitMove: () =>
            {
                Directory.Move(generated, held);
                Directory.CreateSymbolicLink(generated, attacker);
            });
            Assert.Equal(ImageToolRunErrorCodes.PathInvalid,
                Assert.Throws<ImageToolRunException>(() => files.WritePng(root, final, [1, 2, 3], CancellationToken.None)).Code);
            Assert.False(File.Exists(Path.Combine(held, "result.png")));
            Assert.False(File.Exists(Path.Combine(attacker, "result.png")));
            Assert.Empty(Directory.GetFiles(held, ".agas-tool-*.tmp"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DirectoryHandleCommitRejectsTemporaryFileReplacementWithoutDeletingAttackerFile()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "agas-tool-temp-swap-" + Guid.NewGuid().ToString("N"));
        string generated = Path.Combine(root, "assets", "generated");
        Directory.CreateDirectory(generated);
        try
        {
            string final = ImageToolProjectPaths.NewPng(ImageToolProjectPaths.ValidateRoot(root), "assets/generated/result.png");
            var files = new ImageToolSecureFileAccess(beforeCommitMove: () =>
            {
                string temp = Assert.Single(Directory.GetFiles(generated, ".agas-tool-*.tmp"));
                File.Delete(temp);
                File.WriteAllText(temp, "attacker");
            });
            Assert.Equal(ImageToolRunErrorCodes.PathInvalid,
                Assert.Throws<ImageToolRunException>(() => files.WritePng(root, final, [1, 2, 3], CancellationToken.None)).Code);
            Assert.False(File.Exists(final));
            Assert.Equal("attacker", File.ReadAllText(Assert.Single(Directory.GetFiles(generated, ".agas-tool-*.tmp"))));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CancelledDirectoryHandleWriteLeavesNoTemporaryFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "agas-tool-cancel-" + Guid.NewGuid().ToString("N"));
        string generated = Path.Combine(root, "assets", "generated");
        Directory.CreateDirectory(generated);
        try
        {
            string final = ImageToolProjectPaths.NewPng(ImageToolProjectPaths.ValidateRoot(root), "assets/generated/result.png");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                new ImageToolSecureFileAccess().WritePng(root, final, [1, 2, 3], cancelled.Token));
            Assert.False(File.Exists(final));
            Assert.Empty(Directory.GetFiles(generated, ".agas-tool-*.tmp"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DirectoryLeaseRejectsIntermediateAncestorSwapDuringWalk()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "agas-tool-ancestor-" + Guid.NewGuid().ToString("N"));
        string assets = Path.Combine(root, "assets");
        string held = Path.Combine(root, "assets-held");
        string attacker = Path.Combine(root, "attacker");
        Directory.CreateDirectory(Path.Combine(assets, "generated"));
        Directory.CreateDirectory(Path.Combine(attacker, "generated"));
        try
        {
            string final = ImageToolProjectPaths.NewPng(ImageToolProjectPaths.ValidateRoot(root), "assets/generated/result.png");
            bool swapped = false;
            var platform = new ImageToolSystemPathPlatform(segment =>
            {
                if (swapped || segment != "assets") return;
                swapped = true;
                Directory.Move(assets, held);
                Directory.CreateSymbolicLink(assets, attacker);
            });
            var files = new ImageToolSecureFileAccess(platform);
            Assert.Equal(ImageToolRunErrorCodes.PathInvalid,
                Assert.Throws<ImageToolRunException>(() => files.WritePng(root, final, [1, 2, 3], CancellationToken.None)).Code);
            Assert.True(swapped);
            Assert.Empty(Directory.GetFiles(Path.Combine(held, "generated"), ".agas-tool-*.tmp"));
            Assert.Empty(Directory.GetFiles(Path.Combine(attacker, "generated"), ".agas-tool-*.tmp"));
            Assert.False(File.Exists(Path.Combine(attacker, "generated", "result.png")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DirectoryHandleCommitRejectsAncestorMovedOutsideProject()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "agas-tool-external-" + Guid.NewGuid().ToString("N"));
        string assets = Path.Combine(root, "assets");
        string outside = root + "-outside";
        Directory.CreateDirectory(Path.Combine(assets, "generated"));
        try
        {
            string final = ImageToolProjectPaths.NewPng(ImageToolProjectPaths.ValidateRoot(root), "assets/generated/result.png");
            var files = new ImageToolSecureFileAccess(beforeCommitMove: () =>
            {
                Directory.Move(assets, outside);
                Directory.CreateSymbolicLink(assets, outside);
            });
            Assert.Equal(ImageToolRunErrorCodes.PathInvalid,
                Assert.Throws<ImageToolRunException>(() => files.WritePng(root, final, [1, 2, 3], CancellationToken.None)).Code);
            Assert.False(File.Exists(Path.Combine(outside, "generated", "result.png")));
            Assert.Empty(Directory.GetFiles(Path.Combine(outside, "generated"), ".agas-tool-*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void CancellationAtCommitBoundaryLeavesNoOutput()
    {
        string root = Path.Combine(Path.GetTempPath(), "agas-tool-cancel-boundary-" + Guid.NewGuid().ToString("N"));
        string generated = Path.Combine(root, "assets", "generated");
        Directory.CreateDirectory(generated);
        try
        {
            string final = ImageToolProjectPaths.NewPng(ImageToolProjectPaths.ValidateRoot(root), "assets/generated/result.png");
            using var cancellation = new CancellationTokenSource();
            var files = new ImageToolSecureFileAccess(beforeCommitMove: cancellation.Cancel);
            Assert.Throws<OperationCanceledException>(() => files.WritePng(root, final, [1, 2, 3], cancellation.Token));
            Assert.False(File.Exists(final));
            Assert.Empty(Directory.GetFiles(generated, ".agas-tool-*.tmp"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ProjectPathsRejectTraversalLinksAndExistingOutput()
    {
        string root = Path.Combine(Path.GetTempPath(), "agas-tool-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        try
        {
            File.WriteAllBytes(Path.Combine(root, "assets", "input.png"), [1, 2, 3]);
            string project = ImageToolProjectPaths.ValidateRoot(root);
            Assert.Equal(Path.Combine(root, "assets", "input.png"), ImageToolProjectPaths.ExistingImage(project, "assets/input.png"));
            Assert.Equal(Path.Combine(root, "assets", "new.png"), ImageToolProjectPaths.NewPng(project, "assets/new.png"));
            Assert.Equal(ImageToolRunErrorCodes.PathInvalid,
                Assert.Throws<ImageToolRunException>(() => ImageToolProjectPaths.ExistingImage(project, "../outside.png")).Code);
            Assert.Equal(ImageToolRunErrorCodes.OutputExists,
                Assert.Throws<ImageToolRunException>(() => ImageToolProjectPaths.NewPng(project, "assets/input.png")).Code);
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(Path.Combine(root, "assets", "link.png"), Path.Combine(root, "assets", "input.png"));
                Assert.Equal(ImageToolRunErrorCodes.PathInvalid,
                    Assert.Throws<ImageToolRunException>(() => ImageToolProjectPaths.ExistingImage(project, "assets/link.png")).Code);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PngHeaderDimensionsAreCheckedBeforeDecode()
    {
        byte[] png =
        [
            137, 80, 78, 71, 13, 10, 26, 10,
            0, 0, 0, 13, 73, 72, 68, 82,
            0, 0, 4, 0, 0, 0, 2, 0,
        ];
        Assert.True(ImageToolImageHeader.TryRead("image.png", png, out int width, out int height));
        Assert.Equal((1024, 512), (width, height));
        Assert.False(ImageToolImageHeader.TryRead("image.png", png.AsSpan(1), out _, out _));
        Assert.Equal(ImageToolRunErrorCodes.ImageLimitExceeded,
            Assert.Throws<ImageToolRunException>(() => new ImageToolExecutionLimits(maxPixels: 100).ValidateImage(width, height)).Code);
    }
}
