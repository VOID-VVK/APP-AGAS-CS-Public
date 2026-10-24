#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AppAgasCs.Features.Tools.Execution;

public interface IToolRgba8Algorithm
{
    byte[] Process(byte[] rgba8, int width, int height, CancellationToken cancellationToken);
}

public interface IToolMattingAlgorithm : IToolRgba8Algorithm
{
}

/// <summary>
/// Registry for the dependency-free, first-pass matting algorithms. Implementations operate on
/// plain RGBA8 buffers only, so they are safe to run on a worker thread and are independently testable.
/// </summary>
public static class ToolMattingAlgorithmRegistry
{
    private static readonly IReadOnlyDictionary<string, IToolMattingAlgorithm> Algorithms =
        new Dictionary<string, IToolMattingAlgorithm>(StringComparer.Ordinal)
        {
            ["matting-edge-connected"] = new EdgeConnectedMattingAlgorithm(),
            ["matting-checkerboard"] = new CheckerboardMattingAlgorithm(),
            ["matting-light-background"] = new LightBackgroundMattingAlgorithm(),
        };

    public static bool TryGet(string? nodeKind, out IToolMattingAlgorithm algorithm)
    {
        if (!string.IsNullOrEmpty(nodeKind) && Algorithms.TryGetValue(nodeKind, out IToolMattingAlgorithm? found))
        {
            algorithm = found;
            return true;
        }
        algorithm = null!;
        return false;
    }

    public static IToolMattingAlgorithm GetRequired(string nodeKind) =>
        TryGet(nodeKind, out IToolMattingAlgorithm algorithm)
            ? algorithm
            : throw new KeyNotFoundException($"Unknown tool matting kind '{nodeKind}'.");
}

internal static class ToolMattingBuffer
{
    internal static int Validate(byte[] rgba8, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba8);
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        int pixels = checked(width * height);
        if (rgba8.Length != checked(pixels * 4)) throw new ArgumentException("RGBA8 buffer size does not match its dimensions.", nameof(rgba8));
        return pixels;
    }

    internal static int DistanceSquared(byte[] data, int offset, Rgb color)
    {
        int dr = data[offset] - color.R;
        int dg = data[offset + 1] - color.G;
        int db = data[offset + 2] - color.B;
        return checked(dr * dr + dg * dg + db * db);
    }

    internal static byte SoftAlpha(int distanceSquared, int transparentDistance, int opaqueDistance)
    {
        double distance = Math.Sqrt(distanceSquared);
        if (distance <= transparentDistance) return 0;
        if (distance >= opaqueDistance) return byte.MaxValue;
        return (byte)Math.Clamp(
            (int)Math.Round((distance - transparentDistance) * 255d / (opaqueDistance - transparentDistance)),
            0,
            255);
    }

    internal static void PreserveAlpha(byte[] output, int offset, byte calculatedAlpha) =>
        output[offset + 3] = Math.Min(output[offset + 3], calculatedAlpha);

    internal readonly record struct Rgb(int R, int G, int B);
}

internal sealed class EdgeConnectedMattingAlgorithm : IToolMattingAlgorithm
{
    private const int TransparentDistance = 12;
    private const int TraversalDistance = 44;
    private const int OpaqueDistance = 60;

    public byte[] Process(byte[] rgba8, int width, int height, CancellationToken cancellationToken)
    {
        int pixels = ToolMattingBuffer.Validate(rgba8, width, height);
        byte[] output = (byte[])rgba8.Clone();
        ToolMattingBuffer.Rgb background = BorderMean(rgba8, width, height, cancellationToken);
        int traversalDistanceSquared = TraversalDistance * TraversalDistance;
        var visited = new byte[pixels];
        var queue = new int[pixels];
        int head = 0;
        int tail = 0;

        void EnqueueIfBackground(int pixel)
        {
            if (visited[pixel] != 0) return;
            visited[pixel] = 1;
            int offset = pixel * 4;
            if (rgba8[offset + 3] == 0
                || ToolMattingBuffer.DistanceSquared(rgba8, offset, background) <= traversalDistanceSquared)
            {
                queue[tail++] = pixel;
            }
        }

        for (int x = 0; x < width; x++)
        {
            EnqueueIfBackground(x);
            EnqueueIfBackground((height - 1) * width + x);
        }
        for (int y = 1; y + 1 < height; y++)
        {
            EnqueueIfBackground(y * width);
            EnqueueIfBackground(y * width + width - 1);
        }

        while (head < tail)
        {
            if ((head & 0xfff) == 0) cancellationToken.ThrowIfCancellationRequested();
            int pixel = queue[head++];
            int offset = pixel * 4;
            byte alpha = ToolMattingBuffer.SoftAlpha(
                ToolMattingBuffer.DistanceSquared(rgba8, offset, background),
                TransparentDistance,
                OpaqueDistance);
            ToolMattingBuffer.PreserveAlpha(output, offset, alpha);
            int x = pixel % width;
            if (x > 0) EnqueueIfBackground(pixel - 1);
            if (x + 1 < width) EnqueueIfBackground(pixel + 1);
            if (pixel >= width) EnqueueIfBackground(pixel - width);
            if (pixel + width < pixels) EnqueueIfBackground(pixel + width);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return output;
    }

    private static ToolMattingBuffer.Rgb BorderMean(byte[] data, int width, int height, CancellationToken cancellationToken)
    {
        long r = 0;
        long g = 0;
        long b = 0;
        int count = 0;
        void Add(int pixel)
        {
            int offset = pixel * 4;
            if (data[offset + 3] == 0) return;
            r += data[offset];
            g += data[offset + 1];
            b += data[offset + 2];
            count++;
        }
        for (int x = 0; x < width; x++)
        {
            Add(x);
            if (height > 1) Add((height - 1) * width + x);
        }
        for (int y = 1; y + 1 < height; y++)
        {
            Add(y * width);
            if (width > 1) Add(y * width + width - 1);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return count == 0
            ? new ToolMattingBuffer.Rgb(0, 0, 0)
            : new ToolMattingBuffer.Rgb((int)(r / count), (int)(g / count), (int)(b / count));
    }
}

internal sealed class CheckerboardMattingAlgorithm : IToolMattingAlgorithm
{
    private const int TransparentDistance = 9;
    private const int OpaqueDistance = 48;

    public byte[] Process(byte[] rgba8, int width, int height, CancellationToken cancellationToken)
    {
        int pixels = ToolMattingBuffer.Validate(rgba8, width, height);
        byte[] output = (byte[])rgba8.Clone();
        (ToolMattingBuffer.Rgb first, ToolMattingBuffer.Rgb second) =
            FindBorderColors(rgba8, width, height, cancellationToken);
        for (int pixel = 0; pixel < pixels; pixel++)
        {
            if ((pixel & 0xfff) == 0) cancellationToken.ThrowIfCancellationRequested();
            int offset = pixel * 4;
            int distance = Math.Min(
                ToolMattingBuffer.DistanceSquared(rgba8, offset, first),
                ToolMattingBuffer.DistanceSquared(rgba8, offset, second));
            ToolMattingBuffer.PreserveAlpha(
                output,
                offset,
                ToolMattingBuffer.SoftAlpha(distance, TransparentDistance, OpaqueDistance));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return output;
    }

    private static (ToolMattingBuffer.Rgb First, ToolMattingBuffer.Rgb Second) FindBorderColors(
        byte[] data,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var samples = new List<ToolMattingBuffer.Rgb>(checked(width * 2 + Math.Max(0, height - 2) * 2));
        void Add(int pixel)
        {
            int offset = pixel * 4;
            if (data[offset + 3] != 0) samples.Add(new ToolMattingBuffer.Rgb(data[offset], data[offset + 1], data[offset + 2]));
        }
        for (int x = 0; x < width; x++) { Add(x); if (height > 1) Add((height - 1) * width + x); }
        for (int y = 1; y + 1 < height; y++) { Add(y * width); if (width > 1) Add(y * width + width - 1); }
        cancellationToken.ThrowIfCancellationRequested();
        if (samples.Count == 0) return (new(0, 0, 0), new(0, 0, 0));

        var buckets = samples
            .GroupBy(sample => (sample.R >> 4, sample.G >> 4, sample.B >> 4))
            .Select(group => new
            {
                Count = group.Count(),
                Color = new ToolMattingBuffer.Rgb(
                    (int)group.Average(sample => sample.R),
                    (int)group.Average(sample => sample.G),
                    (int)group.Average(sample => sample.B)),
            })
            .OrderByDescending(bucket => bucket.Count)
            .ThenBy(bucket => bucket.Color.R)
            .ThenBy(bucket => bucket.Color.G)
            .ThenBy(bucket => bucket.Color.B)
            .Take(2)
            .ToArray();
        ToolMattingBuffer.Rgb first = buckets[0].Color;
        int minimumSecondaryCount = Math.Max(2, (int)Math.Ceiling(samples.Count * 0.15));
        ToolMattingBuffer.Rgb second = buckets.Length > 1 && buckets[1].Count >= minimumSecondaryCount
            ? buckets[1].Color
            : first;
        cancellationToken.ThrowIfCancellationRequested();
        return (first, second);
    }
}

internal sealed class LightBackgroundMattingAlgorithm : IToolMattingAlgorithm
{
    public byte[] Process(byte[] rgba8, int width, int height, CancellationToken cancellationToken)
    {
        int pixels = ToolMattingBuffer.Validate(rgba8, width, height);
        byte[] output = (byte[])rgba8.Clone();
        for (int pixel = 0; pixel < pixels; pixel++)
        {
            if ((pixel & 0xfff) == 0) cancellationToken.ThrowIfCancellationRequested();
            int offset = pixel * 4;
            int r = rgba8[offset];
            int g = rgba8[offset + 1];
            int b = rgba8[offset + 2];
            int maximum = Math.Max(r, Math.Max(g, b));
            int minimum = Math.Min(r, Math.Min(g, b));
            int darkness = 255 - ((54 * r + 183 * g + 19 * b) >> 8);
            int colorfulness = maximum - minimum;
            int foregroundStrength = Math.Max(darkness, Math.Max(0, colorfulness - 12) * 2);
            byte alpha = foregroundStrength <= 18
                ? (byte)0
                : foregroundStrength >= 90
                    ? byte.MaxValue
                    : (byte)Math.Clamp((foregroundStrength - 18) * 255 / 72, 0, 255);
            ToolMattingBuffer.PreserveAlpha(output, offset, alpha);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return output;
    }
}
