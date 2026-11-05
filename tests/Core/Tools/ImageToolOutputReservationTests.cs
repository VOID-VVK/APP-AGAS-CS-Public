using AppAgasCs.Features.Tools.Execution;
using Xunit;

namespace AppAgasCs.Tests.Core.Tools;

public sealed class ImageToolOutputReservationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agas-tool-number-" + Guid.NewGuid().ToString("N"));
    private string Outputs => Path.Combine(_root, "agas", "tools");

    public ImageToolOutputReservationTests() => Directory.CreateDirectory(Outputs);

    [Fact]
    public void ContinuesExistingNumbersAcrossExtensionsAndKeepsLegacyFiles()
    {
        string legacy = Path.Combine(Outputs, "tile47-standard-reverse-old.png");
        File.WriteAllText(legacy, "legacy");
        File.WriteAllText(Path.Combine(Outputs, "Tools 9.webp"), "nine");
        File.WriteAllText(Path.Combine(Outputs, "tools 10.PNG"), "ten");
        using (var reserved = ImageToolOutputReservation.Reserve(_root))
        {
            Assert.Equal("agas/tools/Tools 11.png", reserved.RelativePath);
            new ImageToolSecureFileAccess().WritePng(_root, Path.Combine(_root, reserved.RelativePath), [1, 2, 3], CancellationToken.None);
        }
        using var reopened = ImageToolOutputReservation.Reserve(_root);
        Assert.Equal("agas/tools/Tools 12.png", reopened.RelativePath);
        Assert.Equal("legacy", File.ReadAllText(legacy));
        Assert.Equal("ten", File.ReadAllText(Path.Combine(Outputs, "tools 10.PNG")));
    }

    [Fact]
    public async Task ConcurrentReservationsAreUniqueAndDoNotOverwriteOutputs()
    {
        ImageToolOutputReservation[] reservations = await Task.WhenAll(Enumerable.Range(0, 24)
            .Select(_ => Task.Run(() => ImageToolOutputReservation.Reserve(_root))));
        try
        {
            Assert.Equal(24, reservations.Select(item => item.RelativePath).Distinct().Count());
            await Task.WhenAll(reservations.Select((item, index) => Task.Run(() =>
                new ImageToolSecureFileAccess().WritePng(_root, Path.Combine(_root, item.RelativePath),
                    [(byte)index], CancellationToken.None))));
            Assert.Equal(24, Directory.GetFiles(Outputs, "*.png").Length);
            for (int index = 0; index < reservations.Length; index++)
                Assert.Equal(new[] { (byte)index }, File.ReadAllBytes(Path.Combine(_root, reservations[index].RelativePath)));
        }
        finally { foreach (var item in reservations) item.Dispose(); }
        Assert.Empty(Directory.GetFiles(Outputs, "*.reserve"));
    }

    [Fact]
    public void UnwrittenReservationsAreReleasedAndLateExistingOutputIsNotOverwritten()
    {
        using (var abandoned = ImageToolOutputReservation.Reserve(_root))
            Assert.Equal("agas/tools/Tools 1.png", abandoned.RelativePath);
        using var next = ImageToolOutputReservation.Reserve(_root);
        Assert.Equal("agas/tools/Tools 1.png", next.RelativePath);
        string final = Path.Combine(_root, next.RelativePath);
        File.WriteAllText(final, "another writer");
        Assert.Throws<ImageToolRunException>(() => new ImageToolSecureFileAccess()
            .WritePng(_root, final, [1], CancellationToken.None));
        Assert.Equal("another writer", File.ReadAllText(final));
    }

    [Fact]
    public void SkipsCrashReservationAndDirectoryNamesAndRejectsExhaustion()
    {
        File.WriteAllText(Path.Combine(Outputs, ".Tools 4.reserve"), "stale");
        Directory.CreateDirectory(Path.Combine(Outputs, "Tools 5.png"));
        using (var reserved = ImageToolOutputReservation.Reserve(_root))
            Assert.Equal("agas/tools/Tools 6.png", reserved.RelativePath);
        File.WriteAllText(Path.Combine(Outputs, "Tools 2147483647.png"), "existing");
        Assert.Throws<IOException>(() => ImageToolOutputReservation.Reserve(_root));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(Outputs, "Tools 2147483647.png")));
    }

    [Fact]
    public void RejectsLinkedOutputDirectoryBeforeCreatingReservation()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.Delete(Outputs);
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Outputs, outside);
        Assert.Throws<ImageToolRunException>(() => ImageToolOutputReservation.Reserve(_root));
        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
