using AppAgasCs.Features.Tools.Execution;
using Xunit;

namespace AppAgasCs.Tests.Core.Tools;

public sealed class ToolMattingAlgorithmTests
{
    [Fact]
    public void EdgeConnectedClearsOnlyReachableBackgroundAndPreservesRgbAndExistingAlpha()
    {
        byte[] input = Solid(5, 5, 245, 245, 245, 255);
        for (int y = 1; y <= 3; y++)
        for (int x = 1; x <= 3; x++) Set(input, 5, x, y, 20, 30, 40, 255);
        Set(input, 5, 2, 2, 245, 245, 245, 120);

        byte[] output = Run("matting-edge-connected", input, 5, 5);

        Assert.Equal(0, Alpha(output, 5, 0, 0));
        Assert.Equal(120, Alpha(output, 5, 2, 2));
        Assert.Equal(input.Where((_, index) => index % 4 != 3), output.Where((_, index) => index % 4 != 3));
        AssertAlphaNeverIncreases(input, output);
    }

    [Fact]
    public void CheckerboardClearsBothBorderColorsButKeepsThirdColor()
    {
        byte[] input = Solid(4, 4, 238, 238, 226, 255);
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            if ((x + y) % 2 == 1) Set(input, 4, x, y, 218, 218, 206, 255);
        }
        Set(input, 4, 1, 1, 190, 25, 35, 91);
        Set(input, 4, 2, 2, 190, 25, 35, 255);

        byte[] output = Run("matting-checkerboard", input, 4, 4);

        Assert.Equal(0, Alpha(output, 4, 0, 0));
        Assert.Equal(0, Alpha(output, 4, 1, 0));
        Assert.Equal(91, Alpha(output, 4, 1, 1));
        Assert.Equal(255, Alpha(output, 4, 2, 2));
        AssertAlphaNeverIncreases(input, output);
    }

    [Fact]
    public void CheckerboardDoesNotLearnAThinBorderForegroundAsItsSecondBackground()
    {
        byte[] input = Solid(5, 5, 238, 238, 226, 255);
        for (int y = 0; y < 5; y++)
        for (int x = 0; x < 5; x++)
        {
            if ((x + y) % 2 == 1) Set(input, 5, x, y, 218, 218, 206, 255);
        }
        Set(input, 5, 2, 4, 190, 25, 35, 173);

        byte[] output = Run("matting-checkerboard", input, 5, 5);

        Assert.Equal(0, Alpha(output, 5, 0, 0));
        Assert.Equal(0, Alpha(output, 5, 1, 0));
        Assert.Equal(173, Alpha(output, 5, 2, 4));
    }

    [Fact]
    public void LightBackgroundClearsNearWhiteAndKeepsDarkOrSaturatedForeground()
    {
        byte[] input =
        {
            255, 255, 255, 255,
            248, 246, 245, 100,
            20, 20, 20, 255,
            250, 20, 30, 200,
        };

        byte[] output = Run("matting-light-background", input, 4, 1);

        Assert.Equal(0, output[3]);
        Assert.True(output[7] < 100);
        Assert.Equal(255, output[11]);
        Assert.Equal(200, output[15]);
        AssertAlphaNeverIncreases(input, output);
    }

    [Theory]
    [InlineData("matting-edge-connected")]
    [InlineData("matting-checkerboard")]
    [InlineData("matting-light-background")]
    public void AlgorithmsHandleOneTransparentPixelWithoutMutatingInputAndObserveCancellation(string kind)
    {
        byte[] input = { 123, 45, 67, 0 };
        byte[] before = (byte[])input.Clone();
        byte[] output = Run(kind, input, 1, 1);
        Assert.Equal(before, input);
        Assert.Equal(before, output);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ToolMattingAlgorithmRegistry.GetRequired(kind).Process(input, 1, 1, cancellation.Token));
    }

    [Fact]
    public void AlgorithmRegistryExposesTheThreeToolOperations()
    {
        foreach (string kind in new[] { "matting-edge-connected", "matting-checkerboard", "matting-light-background" })
        {
            Assert.NotNull(ToolMattingAlgorithmRegistry.GetRequired(kind));
        }
        Assert.False(ToolMattingAlgorithmRegistry.TryGet("Matting-Edge-Connected", out _));
    }

    private static byte[] Run(string kind, byte[] input, int width, int height) =>
        ToolMattingAlgorithmRegistry.GetRequired(kind).Process(input, width, height, CancellationToken.None);

    private static byte[] Solid(int width, int height, byte r, byte g, byte b, byte alpha)
    {
        byte[] data = new byte[width * height * 4];
        for (int pixel = 0; pixel < width * height; pixel++) Set(data, width, pixel % width, pixel / width, r, g, b, alpha);
        return data;
    }

    private static void Set(byte[] data, int width, int x, int y, byte r, byte g, byte b, byte alpha)
    {
        int offset = (y * width + x) * 4;
        data[offset] = r;
        data[offset + 1] = g;
        data[offset + 2] = b;
        data[offset + 3] = alpha;
    }

    private static byte Alpha(byte[] data, int width, int x, int y) => data[(y * width + x) * 4 + 3];

    private static void AssertAlphaNeverIncreases(byte[] input, byte[] output)
    {
        for (int offset = 3; offset < input.Length; offset += 4) Assert.True(output[offset] <= input[offset]);
    }
}
