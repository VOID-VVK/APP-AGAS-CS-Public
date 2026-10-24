using AppAgasCs.Features.Tools.Execution;
using Xunit;

namespace AppAgasCs.Tests.Core.Tools;

public sealed class ToolPostProcessAlgorithmTests
{
    [Fact]
    public void PureWhiteBoundaryResidualBecomesTransparent()
    {
        byte[] input =
        {
            0, 0, 0, 0,
            255, 255, 255, 255,
            180, 30, 20, 255,
        };

        byte[] output = Run(input, 3, 1);

        Assert.Equal(0, output[7]);
        Assert.Equal(255, output[11]);
    }

    [Fact]
    public void SemiTransparentPinkWhiteMatteRecoversRedForegroundColor()
    {
        byte[] input =
        {
            0, 0, 0, 0,
            255, 128, 128, 128,
            255, 0, 0, 255,
        };

        byte[] output = Run(input, 3, 1);

        Assert.Equal(255, output[4]);
        Assert.InRange(output[5], 0, 2);
        Assert.InRange(output[6], 0, 2);
        Assert.Equal(128, output[7]);
    }

    [Fact]
    public void LowAlphaBoundaryUsesOpaqueInteriorRgbWithoutIncreasingAlpha()
    {
        byte[] input =
        {
            0, 0, 0, 0,
            255, 255, 255, 32,
            20, 80, 180, 255,
        };

        byte[] output = Run(input, 3, 1);

        Assert.Equal(new byte[] { 20, 80, 180 }, output[4..7]);
        Assert.Equal(32, output[7]);
    }

    [Fact]
    public void OpaqueWhiteInsideSubjectIsPreserved()
    {
        byte[] input = Solid(5, 5, 0, 0, 0, 0);
        for (int y = 1; y <= 3; y++)
        for (int x = 1; x <= 3; x++) Set(input, 5, x, y, 30, 60, 90, 255);
        Set(input, 5, 2, 2, 255, 255, 255, 255);

        byte[] output = Run(input, 5, 5);

        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(output, 5, 2, 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void UniformAlphaAndOneByOneInputsRemainUnchanged(int alpha)
    {
        byte[] input = Solid(4, 3, 250, 245, 240, checked((byte)alpha));
        byte[] before = (byte[])input.Clone();
        Assert.Equal(before, Run(input, 4, 3));
        Assert.Equal(before, input);

        byte[] single = { 255, 255, 255, checked((byte)alpha) };
        Assert.Equal(single, Run(single, 1, 1));
    }

    [Fact]
    public void AlphaNeverIncreasesDimensionsStayStableAndInputIsNotMutated()
    {
        byte[] input =
        {
            0, 0, 0, 0,       250, 248, 247, 80,  12, 34, 56, 255,
            0, 0, 0, 0,       255, 220, 220, 128, 90, 20, 30, 255,
        };
        byte[] before = (byte[])input.Clone();

        byte[] output = Run(input, 3, 2);

        Assert.Equal(input.Length, output.Length);
        Assert.Equal(before, input);
        for (int offset = 3; offset < input.Length; offset += 4)
        {
            Assert.True(output[offset] <= input[offset]);
        }
    }

    [Fact]
    public void TransparentSeedsAndPixelsOutsideThreePixelBandRemainBitIdentical()
    {
        byte[] input = Solid(9, 1, 20, 40, 60, 255);
        Set(input, 9, 0, 0, 111, 112, 113, 8);
        Set(input, 9, 8, 0, 255, 255, 255, 255);

        byte[] output = Run(input, 9, 1);

        Assert.Equal(Pixel(input, 9, 0, 0), Pixel(output, 9, 0, 0));
        Assert.Equal(Pixel(input, 9, 8, 0), Pixel(output, 9, 8, 0));
    }

    [Fact]
    public void SmallWhiteResidualTouchingImageBorderIsRemovedButColouredBorderSurvives()
    {
        byte[] input = Solid(3, 3, 40, 80, 120, 255);
        Set(input, 3, 2, 2, 0, 0, 0, 0);
        Set(input, 3, 0, 1, 255, 255, 255, 255);

        byte[] output = Run(input, 3, 3);

        Assert.Equal(0, Pixel(output, 3, 0, 1)[3]);
        Assert.Equal(255, Pixel(output, 3, 1, 0)[3]);
    }

    [Theory]
    [InlineData(240, 255)]
    [InlineData(246, 128)]
    [InlineData(252, 0)]
    public void NearWhiteThresholdUsesStableGraduatedAlpha(int channel, int expectedAlpha)
    {
        byte value = checked((byte)channel);
        byte[] input =
        {
            0, 0, 0, 0,
            value, value, value, 255,
            40, 80, 120, 255,
        };

        byte[] output = Run(input, 3, 1);

        Assert.Equal(expectedAlpha, output[7]);
    }

    [Fact]
    public void ConnectedWhiteComponentLargerThanLimitKeepsEveryAlpha()
    {
        const int width = 65;
        byte[] input = Solid(width, 2, 255, 255, 255, 255);
        for (int x = 0; x < width; x++) Set(input, width, x, 0, 0, 0, 0, 0);

        byte[] output = Run(input, width, 2);

        for (int x = 0; x < width; x++) Assert.Equal(255, Pixel(output, width, x, 1)[3]);
    }

    [Fact]
    public void PostProcessUsesSharedRgba8BufferValidation()
    {
        IToolPostProcessAlgorithm algorithm =
            ToolPostProcessAlgorithmRegistry.GetRequired("defringe-white");
        Assert.Throws<ArgumentNullException>(() => algorithm.Process(null!, 1, 1, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => algorithm.Process(Array.Empty<byte>(), 0, 1, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => algorithm.Process(Array.Empty<byte>(), 1, 0, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => algorithm.Process(new byte[3], 1, 1, CancellationToken.None));
        Assert.Throws<OverflowException>(() => algorithm.Process(Array.Empty<byte>(), int.MaxValue, 2, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationDuringLargeRunningTransformIsObserved()
    {
        const int width = 2048;
        const int height = 2048;
        byte[] input = Solid(width, height, 255, 255, 255, 255);
        Set(input, width, 0, 0, 0, 0, 0, 0);
        using var cancellation = new CancellationTokenSource();
        Task<byte[]> running = Task.Run(() =>
            ToolPostProcessAlgorithmRegistry.GetRequired("defringe-white")
                .Process(input, width, height, cancellation.Token));
        Assert.True(SpinWait.SpinUntil(() => running.Status == TaskStatus.Running, TimeSpan.FromSeconds(2)),
            "Large post-process transform never entered the running state.");

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running);
    }

    [Fact]
    public void CancellationIsObservedAndRegistryIsIndependentWithExactKind()
    {
        Assert.False(ToolMattingAlgorithmRegistry.TryGet("defringe-white", out _));
        Assert.NotNull(ToolPostProcessAlgorithmRegistry.GetRequired("defringe-white"));
        Assert.False(ToolPostProcessAlgorithmRegistry.TryGet("Defringe-White", out _));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ToolPostProcessAlgorithmRegistry.GetRequired("defringe-white")
                .Process(new byte[4], 1, 1, cancellation.Token));
    }

    private static byte[] Run(byte[] input, int width, int height) =>
        ToolPostProcessAlgorithmRegistry.GetRequired("defringe-white")
            .Process(input, width, height, CancellationToken.None);

    private static byte[] Solid(int width, int height, byte red, byte green, byte blue, byte alpha)
    {
        byte[] data = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++) Set(data, width, x, y, red, green, blue, alpha);
        return data;
    }

    private static void Set(byte[] data, int width, int x, int y, byte red, byte green, byte blue, byte alpha)
    {
        int offset = (y * width + x) * 4;
        data[offset] = red;
        data[offset + 1] = green;
        data[offset + 2] = blue;
        data[offset + 3] = alpha;
    }

    private static byte[] Pixel(byte[] data, int width, int x, int y)
    {
        int offset = (y * width + x) * 4;
        return data[offset..(offset + 4)];
    }
}
