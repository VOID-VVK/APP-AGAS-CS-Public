#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;

namespace AppAgasCs.Features.Tools.Execution;

public interface IToolPostProcessAlgorithm : IToolRgba8Algorithm
{
}

/// <summary>
/// Registry for post-process operations that consume an image which has already been matted.
/// It deliberately remains independent from <see cref="ToolMattingAlgorithmRegistry"/>.
/// </summary>
public static class ToolPostProcessAlgorithmRegistry
{
    private static readonly IReadOnlyDictionary<string, IToolPostProcessAlgorithm> Algorithms =
        new Dictionary<string, IToolPostProcessAlgorithm>(StringComparer.Ordinal)
        {
            ["defringe-white"] = new WhiteDefringePostProcessAlgorithm(),
        };

    public static bool TryGet(string? nodeKind, out IToolPostProcessAlgorithm algorithm)
    {
        if (!string.IsNullOrEmpty(nodeKind)
            && Algorithms.TryGetValue(nodeKind, out IToolPostProcessAlgorithm? found))
        {
            algorithm = found;
            return true;
        }
        algorithm = null!;
        return false;
    }

    public static IToolPostProcessAlgorithm GetRequired(string nodeKind) =>
        TryGet(nodeKind, out IToolPostProcessAlgorithm algorithm)
            ? algorithm
            : throw new KeyNotFoundException($"Unknown tool post-process kind '{nodeKind}'.");
}

/// <summary>
/// Removes white matte contamination from the narrow alpha boundary without changing dimensions
/// or increasing alpha. The implementation is dependency-free and operates only on RGBA8 data.
/// </summary>
internal sealed class WhiteDefringePostProcessAlgorithm : IToolPostProcessAlgorithm
{
    private const int CancellationStrideMask = 0xfff;
    private const byte TransparentSeedAlpha = 8;
    private const byte OutsideBand = byte.MaxValue;
    private const byte CandidatePixel = 254;
    private const byte NonCandidatePixel = 253;
    private const byte VisitedCandidatePixel = 252;
    private const int EdgeBandRadius = 3;
    private const int LowAlphaThreshold = 48;
    private const int InteriorAlphaThreshold = 192;
    private const int InteriorSearchRadius = 3;
    private const int NearWhiteStart = 240;
    private const int PureWhiteStart = 252;
    private const int MaximumNeutralSpread = 12;
    private const int MaximumWhiteComponentPixels = 64;

    public byte[] Process(byte[] rgba8, int width, int height, CancellationToken cancellationToken)
    {
        int pixels = ToolMattingBuffer.Validate(rgba8, width, height);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] output = (byte[])rgba8.Clone();

        var distance = new byte[pixels];
        Array.Fill(distance, OutsideBand);
        var queue = new int[pixels];
        int queueHead = 0;
        int queueTail = 0;
        for (int pixel = 0; pixel < pixels; pixel++)
        {
            CheckCancellation(pixel, cancellationToken);
            if (rgba8[pixel * 4 + 3] > TransparentSeedAlpha) continue;
            distance[pixel] = 0;
            queue[queueTail++] = pixel;
        }
        if (queueTail == 0 || queueTail == pixels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return output;
        }

        int work = 0;
        while (queueHead < queueTail)
        {
            CheckCancellation(work++, cancellationToken);
            int pixel = queue[queueHead++];
            byte nextDistance = checked((byte)(distance[pixel] + 1));
            if (nextDistance > EdgeBandRadius) continue;
            VisitNeighbours(pixel, width, height, neighbour =>
            {
                if (distance[neighbour] <= nextDistance) return;
                distance[neighbour] = nextDistance;
                queue[queueTail++] = neighbour;
            });
        }

        // First remove the known white matte contribution from semi-transparent boundary pixels.
        for (int pixel = 0; pixel < pixels; pixel++)
        {
            CheckCancellation(pixel, cancellationToken);
            int offset = pixel * 4;
            byte alpha = rgba8[offset + 3];
            if (alpha <= TransparentSeedAlpha || alpha == byte.MaxValue || !IsEdgeBand(distance[pixel]))
            {
                continue;
            }
            output[offset] = RemoveWhiteMatte(rgba8[offset], alpha);
            output[offset + 1] = RemoveWhiteMatte(rgba8[offset + 1], alpha);
            output[offset + 2] = RemoveWhiteMatte(rgba8[offset + 2], alpha);
        }

        // Very low alpha makes unmatting numerically unstable. Refill only RGB from more opaque
        // inward neighbours; alpha is intentionally kept unchanged.
        for (int pixel = 0; pixel < pixels; pixel++)
        {
            CheckCancellation(pixel, cancellationToken);
            int offset = pixel * 4;
            byte alpha = rgba8[offset + 3];
            if (alpha <= TransparentSeedAlpha || alpha > LowAlphaThreshold || !IsEdgeBand(distance[pixel]))
            {
                continue;
            }
            if (TrySampleInteriorColor(output, rgba8, distance, width, height, pixel, alpha, out byte r, out byte g, out byte b))
            {
                output[offset] = r;
                output[offset + 1] = g;
                output[offset + 2] = b;
            }
        }

        // Finally suppress small connected near-white remnants inside the narrow edge band.
        // Reuse the distance buffer for candidate/visited state so peak auxiliary memory stays bounded.
        for (int pixel = 0; pixel < pixels; pixel++)
        {
            CheckCancellation(pixel, cancellationToken);
            int offset = pixel * 4;
            byte originalAlpha = rgba8[offset + 3];
            int minimum = Math.Min(output[offset], Math.Min(output[offset + 1], output[offset + 2]));
            int maximum = Math.Max(output[offset], Math.Max(output[offset + 1], output[offset + 2]));
            distance[pixel] = originalAlpha > TransparentSeedAlpha
                && IsEdgeBand(distance[pixel])
                && HasBoundaryExposure(rgba8, width, height, pixel, originalAlpha)
                && minimum >= NearWhiteStart
                && maximum - minimum <= MaximumNeutralSpread
                    ? CandidatePixel
                    : NonCandidatePixel;
        }

        work = 0;
        for (int start = 0; start < pixels; start++)
        {
            CheckCancellation(work++, cancellationToken);
            if (distance[start] != CandidatePixel) continue;
            queueHead = 0;
            queueTail = 0;
            distance[start] = VisitedCandidatePixel;
            queue[queueTail++] = start;
            while (queueHead < queueTail)
            {
                CheckCancellation(work++, cancellationToken);
                int pixel = queue[queueHead++];
                VisitNeighbours(pixel, width, height, neighbour =>
                {
                    if (distance[neighbour] != CandidatePixel) return;
                    distance[neighbour] = VisitedCandidatePixel;
                    queue[queueTail++] = neighbour;
                });
            }
            if (queueTail > MaximumWhiteComponentPixels) continue;
            for (int index = 0; index < queueTail; index++)
            {
                int pixel = queue[index];
                int offset = pixel * 4;
                int minimum = Math.Min(output[offset], Math.Min(output[offset + 1], output[offset + 2]));
                int removal = minimum >= PureWhiteStart
                    ? byte.MaxValue
                    : checked((minimum - NearWhiteStart) * byte.MaxValue / (PureWhiteStart - NearWhiteStart));
                byte reducedAlpha = checked((byte)(rgba8[offset + 3] * (byte.MaxValue - removal) / byte.MaxValue));
                output[offset + 3] = Math.Min(output[offset + 3], reducedAlpha);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return output;
    }

    private static byte RemoveWhiteMatte(byte observed, byte alpha)
    {
        // F = (C - 255 * (1-a)) / a, evaluated in integer space with rounding.
        int numerator = checked(observed * byte.MaxValue - byte.MaxValue * (byte.MaxValue - alpha));
        if (numerator <= 0) return 0;
        return checked((byte)Math.Clamp((numerator + alpha / 2) / alpha, 0, byte.MaxValue));
    }

    private static bool TrySampleInteriorColor(
        byte[] colors,
        byte[] alphaSource,
        byte[] distance,
        int width,
        int height,
        int pixel,
        byte currentAlpha,
        out byte red,
        out byte green,
        out byte blue)
    {
        int centerX = pixel % width;
        int centerY = pixel / width;
        byte currentDistance = distance[pixel];
        long redSum = 0;
        long greenSum = 0;
        long blueSum = 0;
        long weightSum = 0;
        for (int dy = -InteriorSearchRadius; dy <= InteriorSearchRadius; dy++)
        {
            int y = centerY + dy;
            if (y < 0 || y >= height) continue;
            for (int dx = -InteriorSearchRadius; dx <= InteriorSearchRadius; dx++)
            {
                int x = centerX + dx;
                if (x < 0 || x >= width || (dx == 0 && dy == 0)) continue;
                int candidate = y * width + x;
                int candidateOffset = candidate * 4;
                byte candidateAlpha = alphaSource[candidateOffset + 3];
                byte candidateDistance = distance[candidate];
                if (candidateAlpha < InteriorAlphaThreshold
                    || candidateAlpha <= currentAlpha
                    || (candidateDistance != OutsideBand && candidateDistance <= currentDistance)) continue;
                int distanceSquared = dx * dx + dy * dy;
                int inwardBonus = candidateDistance == OutsideBand
                    ? EdgeBandRadius + 1
                    : candidateDistance - currentDistance;
                int weight = checked(candidateAlpha
                    * (InteriorSearchRadius * InteriorSearchRadius + 1 - distanceSquared)
                    * inwardBonus);
                if (weight <= 0) continue;
                redSum += colors[candidateOffset] * (long)weight;
                greenSum += colors[candidateOffset + 1] * (long)weight;
                blueSum += colors[candidateOffset + 2] * (long)weight;
                weightSum += weight;
            }
        }
        if (weightSum == 0)
        {
            red = green = blue = 0;
            return false;
        }
        red = checked((byte)Math.Clamp((redSum + weightSum / 2) / weightSum, 0, byte.MaxValue));
        green = checked((byte)Math.Clamp((greenSum + weightSum / 2) / weightSum, 0, byte.MaxValue));
        blue = checked((byte)Math.Clamp((blueSum + weightSum / 2) / weightSum, 0, byte.MaxValue));
        return true;
    }

    private static bool IsEdgeBand(byte distance) => distance is >= 1 and <= EdgeBandRadius;

    private static bool HasBoundaryExposure(byte[] data, int width, int height, int pixel, byte alpha)
    {
        int x = pixel % width;
        int y = pixel / width;
        if (x == 0 || x + 1 == width || y == 0 || y + 1 == height) return true;
        bool exposed = false;
        VisitNeighbours(pixel, width, height, neighbour =>
        {
            if (data[neighbour * 4 + 3] < alpha) exposed = true;
        });
        return exposed;
    }

    private static void VisitNeighbours(int pixel, int width, int height, Action<int> visit)
    {
        int x = pixel % width;
        int y = pixel / width;
        for (int dy = -1; dy <= 1; dy++)
        {
            int neighbourY = y + dy;
            if (neighbourY < 0 || neighbourY >= height) continue;
            for (int dx = -1; dx <= 1; dx++)
            {
                int neighbourX = x + dx;
                if ((dx == 0 && dy == 0) || neighbourX < 0 || neighbourX >= width) continue;
                visit(neighbourY * width + neighbourX);
            }
        }
    }

    private static void CheckCancellation(int pixel, CancellationToken cancellationToken)
    {
        if ((pixel & CancellationStrideMask) == 0) cancellationToken.ThrowIfCancellationRequested();
    }
}
