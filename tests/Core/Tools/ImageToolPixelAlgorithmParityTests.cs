using AppAgasCs.Features.Tools.Execution;
using Xunit;

namespace AppAgasCs.Tests.Core.Tools;

public sealed class ImageToolPixelAlgorithmParityTests
{
    [Theory]
    [InlineData("matting-edge-connected", "74BFF6553A3E4ECAD0D62A48C04596CBFE07BA7DC1AA82C522336615EB9F07A8")]
    [InlineData("matting-checkerboard", "8F8BED3196E0A29802CD41E3ACA682D3D2061806329F4003003C6A25485A36B0")]
    [InlineData("matting-light-background", "96FB74370396DE066A24172D7F3205556380B9AAECF3D0067ED2945D47DDA77A")]
    [InlineData("defringe-white", "1655306B80E8FBF142CCE5FF42CA727B7FC7FFF9885E1D7E986FE6AED362ABE2")]
    public void MigratedToolAlgorithmPreservesFormerPixelOutput(string toolOperation, string expectedSha256)
    {
        const int width = 32;
        const int height = 24;
        byte[] input = Fixture(width, height);
        byte[] current = toolOperation == "defringe-white"
            ? ToolPostProcessAlgorithmRegistry.GetRequired(toolOperation).Process(input, width, height, CancellationToken.None)
            : ToolMattingAlgorithmRegistry.GetRequired(toolOperation).Process(input, width, height, CancellationToken.None);
        Assert.Equal(width * height * 4, current.Length);
        Assert.Equal(expectedSha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(current)));
    }

    private static byte[] Fixture(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            bool foreground = x is >= 8 and < 25 && y is >= 5 and < 19;
            bool edge = foreground && (x is 8 or 24 || y is 5 or 18);
            byte checker = (byte)(((x / 4 + y / 4) & 1) == 0 ? 243 : 224);
            pixels[offset] = foreground ? (byte)(edge ? 247 : 48 + x) : checker;
            pixels[offset + 1] = foreground ? (byte)(edge ? 240 : 91 + y) : checker;
            pixels[offset + 2] = foreground ? (byte)(edge ? 235 : 128) : checker;
            pixels[offset + 3] = (byte)(x == 0 || y == 0 ? 0 : edge ? 90 : 255);
        }
        return pixels;
    }
}
