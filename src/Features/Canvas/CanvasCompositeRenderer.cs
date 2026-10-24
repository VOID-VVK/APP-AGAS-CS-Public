#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Agas.Features.Canvas;

/// <summary>
/// CPU-only deterministic canvas capture/composition used by AI region edits.
/// It deliberately does not depend on a viewport, camera, RenderingServer, or
/// mutable Godot resources, so a pending request stays reproducible.
/// </summary>
public static class CanvasCompositeRenderer
{
    public static CanvasPatchCapture Capture(CanvasPatchCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Rect2I referenceWorld = ToPixelRect(request.ReferenceRegion, request.GridSizePx);
        Rect2I replacementWorld = ToPixelRect(request.ReplacementRegion, request.GridSizePx);
        Rect2I intersectionWorld = Intersect(referenceWorld, replacementWorld);
        if (!HasArea(intersectionWorld))
        {
            throw new ArgumentException("Replacement region must overlap the reference region.", nameof(request));
        }

        Rect2I editRegion = new(
            intersectionWorld.Position - referenceWorld.Position,
            intersectionWorld.Size);
        IReadOnlyList<CanvasCompositeItem> allItems = request.Items
            .OrderBy(item => item.LayerOrder)
            .ThenBy(item => item.ImageOrder)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        IReadOnlyList<CanvasCompositeItem> renderedItems = allItems
            .Where(item => item.Visible && item.Opacity > 0.0f)
            .ToArray();

        using Image sourceCanvas = RenderRegion(referenceWorld, renderedItems);
        using Image replacementPatch = RenderRegion(replacementWorld, renderedItems);
        if (request.GenerationMode == CanvasPatchGenerationMode.RegionReplacement)
        {
            ClearRect(sourceCanvas, editRegion);
        }
        using Image mask = CreateMask(sourceCanvas.GetSize(), editRegion);

        byte[] sourceBytes = SavePng(sourceCanvas);
        byte[] maskBytes = SavePng(mask);
        byte[] replacementBytes = SavePng(replacementPatch);
        CanvasCompositeItem? target = allItems.FirstOrDefault(item =>
            string.Equals(item.Id, request.TargetImageId, StringComparison.Ordinal));
        string fingerprint = ComputeFingerprint(
            request,
            referenceWorld,
            replacementWorld,
            allItems,
            sourceBytes,
            maskBytes,
            replacementBytes);

        return new CanvasPatchCapture(
            request.ReferenceRegion,
            request.ReplacementRegion,
            request.GridSizePx,
            referenceWorld,
            replacementWorld,
            editRegion,
            sourceBytes,
            maskBytes,
            replacementBytes,
            fingerprint,
            target,
            request.GenerationMode);
    }

    /// <summary>
    /// Maps a provider response into a PNG with the exact W world-pixel size.
    /// A full-Q response is cropped to EditRegionPixels; a W-only response is
    /// scaled to W. If W extends beyond Q, uncovered W pixels stay transparent.
    /// </summary>
    public static byte[] ExtractReplacementPatch(
        CanvasPatchCapture capture,
        byte[] providerResultBytes,
        CanvasProviderResultMapping mapping = CanvasProviderResultMapping.Auto)
    {
        ArgumentNullException.ThrowIfNull(capture);
        using Image provider = LoadProviderImage(providerResultBytes, nameof(providerResultBytes));
        Vector2I referenceSize = capture.ReferenceWorldPixels.Size;
        Vector2I replacementSize = capture.ReplacementWorldPixels.Size;
        CanvasProviderResultMapping effectiveMapping = mapping == CanvasProviderResultMapping.Auto
            ? provider.GetWidth() == referenceSize.X && provider.GetHeight() == referenceSize.Y
                ? CanvasProviderResultMapping.ReferenceCanvas
                : CanvasProviderResultMapping.ReplacementPatch
            : mapping;

        if (effectiveMapping == CanvasProviderResultMapping.ReplacementPatch)
        {
            using Image resized = ResizeCopy(provider, replacementSize);
            return SavePng(resized);
        }

        using Image referenceResult = ResizeCopy(provider, referenceSize);
        using Image output = CreateTransparent(replacementSize);
        Rect2I edit = capture.EditRegionPixels;
        Vector2I destination = capture.ReferenceWorldPixels.Position + edit.Position
            - capture.ReplacementWorldPixels.Position;
        output.BlitRect(referenceResult, edit, destination);
        return SavePng(output);
    }

    /// <summary>
    /// Maps a provider response into a PNG with the exact frozen Q dimensions.
    /// Reference-canvas responses are normalized directly to Q. The legacy
    /// W-only response shape is composed into the frozen Q source so callers
    /// still receive one complete Q image instead of accidentally writing W.
    /// </summary>
    public static byte[] ExtractGeneratedReferenceCanvas(
        CanvasPatchCapture capture,
        byte[] providerResultBytes,
        CanvasProviderResultMapping mapping = CanvasProviderResultMapping.Auto)
    {
        ArgumentNullException.ThrowIfNull(capture);
        using Image provider = LoadProviderImage(providerResultBytes, nameof(providerResultBytes));
        Vector2I referenceSize = capture.ReferenceWorldPixels.Size;
        Vector2I replacementSize = capture.ReplacementWorldPixels.Size;
        bool providerIsDistinctReplacementPatch = replacementSize != referenceSize
            && provider.GetWidth() == replacementSize.X
            && provider.GetHeight() == replacementSize.Y;

        if (!providerIsDistinctReplacementPatch)
        {
            using Image referenceResult = ResizeCopy(provider, referenceSize);
            return SavePng(referenceResult);
        }

        using Image output = LoadPng(capture.SourceCanvasPng, nameof(capture));
        using Image replacementPatch = ResizeCopy(provider, replacementSize);
        Rect2I edit = capture.EditRegionPixels;
        Vector2I patchSourcePosition = capture.ReferenceWorldPixels.Position + edit.Position
            - capture.ReplacementWorldPixels.Position;
        output.BlitRect(
            replacementPatch,
            new Rect2I(patchSourcePosition, edit.Size),
            edit.Position);
        return SavePng(output);
    }

    /// <summary>
    /// Applies the W patch to pixels of the target's original bitmap only. The
    /// original dimensions and every source pixel mapped outside W are retained.
    /// Target scale/rotation/negative world coordinates are handled by mapping
    /// each source-pixel center through the placement transform.
    /// </summary>
    public static byte[] ApplyReplacementToSource(
        CanvasPatchCapture capture,
        CanvasCompositeItem target,
        byte[] providerResultBytes,
        CanvasProviderResultMapping mapping = CanvasProviderResultMapping.Auto)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(target);
        if (Mathf.IsZeroApprox(target.Scale.X) || Mathf.IsZeroApprox(target.Scale.Y))
        {
            throw new ArgumentException("Target image scale cannot be zero.", nameof(target));
        }

        byte[] patchPng = ExtractReplacementPatch(capture, providerResultBytes, mapping);
        using Image patch = LoadPng(patchPng, nameof(providerResultBytes));
        using Image source = LoadPng(target.SourcePng, nameof(target));
        Rect2I replacement = capture.ReplacementWorldPixels;
        float rotation = Mathf.DegToRad(target.RotationDegrees);

        for (int y = 0; y < source.GetHeight(); y++)
        {
            for (int x = 0; x < source.GetWidth(); x++)
            {
                Vector2 localCenter = new(x + 0.5f, y + 0.5f);
                Vector2 world = target.Position + new Vector2(
                    localCenter.X * target.Scale.X,
                    localCenter.Y * target.Scale.Y).Rotated(rotation);
                if (!Contains(replacement, world))
                {
                    continue;
                }

                int patchX = Math.Clamp(Mathf.FloorToInt(world.X - replacement.Position.X), 0, patch.GetWidth() - 1);
                int patchY = Math.Clamp(Mathf.FloorToInt(world.Y - replacement.Position.Y), 0, patch.GetHeight() - 1);
                source.SetPixel(x, y, patch.GetPixel(patchX, patchY));
            }
        }

        return SavePng(source);
    }

    private static Image RenderRegion(Rect2I worldRect, IReadOnlyList<CanvasCompositeItem> items)
    {
        Image canvas = CreateTransparent(worldRect.Size);
        foreach (CanvasCompositeItem item in items)
        {
            if (Mathf.IsZeroApprox(item.Scale.X) || Mathf.IsZeroApprox(item.Scale.Y))
            {
                continue;
            }

            using Image source = LoadPng(item.SourcePng, $"item {item.Id}");
            DrawItem(canvas, worldRect, source, item);
        }

        return canvas;
    }

    private static void DrawItem(Image canvas, Rect2I worldRect, Image source, CanvasCompositeItem item)
    {
        float rotation = Mathf.DegToRad(item.RotationDegrees);
        Vector2 scaledRight = new(source.GetWidth() * item.Scale.X, 0);
        Vector2 scaledDown = new(0, source.GetHeight() * item.Scale.Y);
        Vector2[] corners =
        {
            item.Position,
            item.Position + scaledRight.Rotated(rotation),
            item.Position + (scaledRight + scaledDown).Rotated(rotation),
            item.Position + scaledDown.Rotated(rotation)
        };
        float minWorldX = corners.Min(point => point.X);
        float minWorldY = corners.Min(point => point.Y);
        float maxWorldX = corners.Max(point => point.X);
        float maxWorldY = corners.Max(point => point.Y);
        int startX = Math.Max(0, Mathf.FloorToInt(minWorldX - worldRect.Position.X));
        int startY = Math.Max(0, Mathf.FloorToInt(minWorldY - worldRect.Position.Y));
        int endX = Math.Min(canvas.GetWidth(), Mathf.CeilToInt(maxWorldX - worldRect.Position.X));
        int endY = Math.Min(canvas.GetHeight(), Mathf.CeilToInt(maxWorldY - worldRect.Position.Y));

        for (int y = startY; y < endY; y++)
        {
            for (int x = startX; x < endX; x++)
            {
                Vector2 worldCenter = new(
                    worldRect.Position.X + x + 0.5f,
                    worldRect.Position.Y + y + 0.5f);
                Vector2 unrotated = (worldCenter - item.Position).Rotated(-rotation);
                float localX = unrotated.X / item.Scale.X;
                float localY = unrotated.Y / item.Scale.Y;
                if (localX < 0 || localY < 0 || localX >= source.GetWidth() || localY >= source.GetHeight())
                {
                    continue;
                }

                Color sourceColor = source.GetPixel(
                    Math.Clamp(Mathf.FloorToInt(localX), 0, source.GetWidth() - 1),
                    Math.Clamp(Mathf.FloorToInt(localY), 0, source.GetHeight() - 1));
                sourceColor.A *= item.Opacity;
                canvas.SetPixel(x, y, AlphaOver(sourceColor, canvas.GetPixel(x, y)));
            }
        }
    }

    private static Color AlphaOver(Color source, Color destination)
    {
        float outAlpha = source.A + destination.A * (1.0f - source.A);
        if (outAlpha <= 0.0f)
        {
            return Colors.Transparent;
        }

        return new Color(
            (source.R * source.A + destination.R * destination.A * (1.0f - source.A)) / outAlpha,
            (source.G * source.A + destination.G * destination.A * (1.0f - source.A)) / outAlpha,
            (source.B * source.A + destination.B * destination.A * (1.0f - source.A)) / outAlpha,
            outAlpha);
    }

    private static Image CreateMask(Vector2I size, Rect2I editRegion)
    {
        Image mask = Image.CreateEmpty(size.X, size.Y, false, Image.Format.Rgba8);
        mask.Fill(Colors.White);
        ClearRect(mask, editRegion);
        return mask;
    }

    private static Image CreateTransparent(Vector2I size)
    {
        if (size.X < 1 || size.Y < 1)
        {
            throw new ArgumentException("Image dimensions must be positive.", nameof(size));
        }

        Image image = Image.CreateEmpty(size.X, size.Y, false, Image.Format.Rgba8);
        image.Fill(Colors.Transparent);
        return image;
    }

    private static Image ResizeCopy(Image source, Vector2I size)
    {
        Image copy = (Image)source.Duplicate();
        if (copy.GetFormat() != Image.Format.Rgba8)
        {
            copy.Convert(Image.Format.Rgba8);
        }

        if (copy.GetWidth() != size.X || copy.GetHeight() != size.Y)
        {
            copy.Resize(size.X, size.Y, Image.Interpolation.Nearest);
        }

        return copy;
    }

    private static Image LoadPng(byte[] png, string parameterName)
    {
        if (png == null || png.Length == 0)
        {
            throw new ArgumentException("PNG must not be empty.", parameterName);
        }

        Image image = new();
        Error error = image.LoadPngFromBuffer(png);
        if (error != Error.Ok || image.IsEmpty())
        {
            image.Dispose();
            throw new ArgumentException($"Invalid PNG ({error}).", parameterName);
        }

        if (image.GetFormat() != Image.Format.Rgba8)
        {
            image.Convert(Image.Format.Rgba8);
        }

        return image;
    }

    private static Image LoadProviderImage(byte[] bytes, string parameterName)
    {
        if (bytes == null || bytes.Length == 0)
        {
            throw new ArgumentException("Provider image must not be empty.", parameterName);
        }

        Image image = new();
        Error error;
        if (HasPngSignature(bytes))
        {
            error = image.LoadPngFromBuffer(bytes);
        }
        else if (HasJpegSignature(bytes))
        {
            error = image.LoadJpgFromBuffer(bytes);
        }
        else if (HasWebpSignature(bytes))
        {
            error = image.LoadWebpFromBuffer(bytes);
        }
        else
        {
            image.Dispose();
            throw new ArgumentException("Provider image must be PNG, JPEG, or WebP.", parameterName);
        }

        if (error != Error.Ok || image.IsEmpty())
        {
            image.Dispose();
            throw new ArgumentException($"Provider image could not be decoded ({error}).", parameterName);
        }

        if (image.GetFormat() != Image.Format.Rgba8)
        {
            image.Convert(Image.Format.Rgba8);
        }

        return image;
    }

    private static bool HasPngSignature(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8
        && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71
        && bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10;

    private static bool HasJpegSignature(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff;

    private static bool HasWebpSignature(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12
        && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
        && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P';

    private static byte[] SavePng(Image image)
    {
        byte[] bytes = image.SavePngToBuffer();
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException("Godot failed to encode PNG data.");
        }

        return bytes;
    }

    private static void ClearRect(Image image, Rect2I rect)
    {
        for (int y = rect.Position.Y; y < rect.End.Y; y++)
        {
            for (int x = rect.Position.X; x < rect.End.X; x++)
            {
                image.SetPixel(x, y, Colors.Transparent);
            }
        }
    }

    private static Rect2I ToPixelRect(CanvasGridRegion region, int gridSizePx)
    {
        checked
        {
            return new Rect2I(
                region.Column * gridSizePx,
                region.Row * gridSizePx,
                region.Width * gridSizePx,
                region.Height * gridSizePx);
        }
    }

    private static Rect2I Intersect(Rect2I a, Rect2I b)
    {
        int left = Math.Max(a.Position.X, b.Position.X);
        int top = Math.Max(a.Position.Y, b.Position.Y);
        int right = Math.Min(a.End.X, b.End.X);
        int bottom = Math.Min(a.End.Y, b.End.Y);
        return new Rect2I(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static bool HasArea(Rect2I rect) => rect.Size.X > 0 && rect.Size.Y > 0;

    private static bool Contains(Rect2I rect, Vector2 point) =>
        point.X >= rect.Position.X && point.Y >= rect.Position.Y && point.X < rect.End.X && point.Y < rect.End.Y;

    private static string ComputeFingerprint(
        CanvasPatchCaptureRequest request,
        Rect2I reference,
        Rect2I replacement,
        IReadOnlyList<CanvasCompositeItem> items,
        byte[] source,
        byte[] mask,
        byte[] replacementPatch)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, $"v2|grid={request.GridSizePx}|q={RectText(reference)}|w={RectText(replacement)}|mode={request.GenerationMode}|target={request.TargetImageId}\n");
        foreach (CanvasCompositeItem item in items)
        {
            Append(hash, string.Create(CultureInfo.InvariantCulture,
                $"{item.LayerOrder}|{item.ImageOrder}|{item.Id}|{item.Position.X:R}|{item.Position.Y:R}|{item.Scale.X:R}|{item.Scale.Y:R}|{item.RotationDegrees:R}|{item.Opacity:R}|"));
            hash.AppendData(SHA256.HashData(item.SourcePngSpan));
            Append(hash, "\n");
        }

        hash.AppendData(SHA256.HashData(source));
        hash.AppendData(SHA256.HashData(mask));
        hash.AppendData(SHA256.HashData(replacementPatch));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value) => hash.AppendData(Encoding.UTF8.GetBytes(value));

    private static string RectText(Rect2I rect) =>
        string.Create(CultureInfo.InvariantCulture, $"{rect.Position.X},{rect.Position.Y},{rect.Size.X},{rect.Size.Y}");
}
