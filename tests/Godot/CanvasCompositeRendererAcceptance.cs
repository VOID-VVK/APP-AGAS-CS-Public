using Agas.Features.Canvas;
using Godot;
using System;
using System.Collections.Generic;

public partial class CanvasCompositeRendererAcceptance : Node
{
    private const string Prefix = "[AGAS][CANVAS-COMPOSITE]";

    public override void _Ready()
    {
        int exitCode = 0;
        try
        {
            CaptureCompositesOpacityAndBuildsMask();
            MaskProtectsTransparentPixelsOutsideW();
            CaptureSupportsNegativeCoordinatesScaleAndRotation();
            ProviderResultsMapExactlyToW();
            FullQProviderResponseWritesBackCompleteQ();
            LegacyWProviderResponseComposesIntoCompleteQ();
            PngJpegAndWebpProviderResultsCropAndComposeToW();
            ReplacementPreservesSourceOutsideWAndOriginalSize();
            ReplacementUsesTargetRotationAndScale();
            FingerprintIsContentSensitiveAndCaptureIsImmutable();
            GD.Print($"{Prefix}[PASS]");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {exception}");
        }
        GetTree().Quit(exitCode);
    }

    private static void CaptureCompositesOpacityAndBuildsMask()
    {
        var request = new CanvasPatchCaptureRequest(
            new CanvasGridRegion(-1, -1, 3, 2),
            new CanvasGridRegion(0, -1, 1, 1),
            2,
            new[]
            {
                Item("bottom", 0, 0, true, new Vector2(-2, -2), Vector2.One, 0, 1, SolidPng(6, 4, Colors.Red)),
                Item("top", 1, 0, true, new Vector2(-2, -2), Vector2.One, 0, 0.5f, SolidPng(2, 2, Colors.Blue)),
                Item("hidden", 2, 0, false, new Vector2(-2, -2), Vector2.One, 0, 1, SolidPng(6, 4, Colors.Green))
            },
            "bottom");
        CanvasPatchCapture capture = CanvasCompositeRenderer.Capture(request);
        Equal(new Rect2I(-2, -2, 6, 4), capture.ReferenceWorldPixels, "Q pixel rect");
        Equal(new Rect2I(0, -2, 2, 2), capture.ReplacementWorldPixels, "W pixel rect");
        Equal(new Rect2I(2, 0, 2, 2), capture.EditRegionPixels, "relative edit rect");
        Equal("bottom", capture.TargetItem?.Id ?? "", "target snapshot");
        using Image source = Load(capture.SourceCanvasPng);
        using Image mask = Load(capture.MaskPng);
        using Image replacement = Load(capture.ReplacementPatchPng);
        Equal(new Vector2I(6, 4), new Vector2I(source.GetWidth(), source.GetHeight()), "source PNG must retain complete Q IHDR size");
        Equal(new Vector2I(6, 4), new Vector2I(mask.GetWidth(), mask.GetHeight()), "mask PNG must retain complete Q IHDR size");
        for (int y = 0; y < source.GetHeight(); y++)
        {
            for (int x = 0; x < source.GetWidth(); x++)
            {
                bool insideW = x is >= 2 and < 4 && y is >= 0 and < 2;
                True(insideW ? source.GetPixel(x, y).A < 0.01f : source.GetPixel(x, y).A > 0.99f,
                    $"source alpha must punch only W at ({x},{y})");
                True(insideW ? mask.GetPixel(x, y).A < 0.01f : mask.GetPixel(x, y).A > 0.99f,
                    $"mask alpha must identify only W at ({x},{y})");
            }
        }
        ColorNear(new Color(0.5f, 0, 0.5f, 1), source.GetPixel(0, 0), "ordered alpha composite");
        ColorNear(Colors.Transparent, source.GetPixel(2, 0), "W transparent hole");
        ColorNear(Colors.White, mask.GetPixel(0, 0), "mask outside W");
        ColorNear(new Color(1, 1, 1, 0), mask.GetPixel(2, 0), "mask inside W");
        ColorNear(Colors.Red, replacement.GetPixel(0, 0), "unpunched W reference");
    }

    private static void CaptureSupportsNegativeCoordinatesScaleAndRotation()
    {
        var request = new CanvasPatchCaptureRequest(
            new CanvasGridRegion(-1, -1, 5, 3),
            new CanvasGridRegion(3, 1, 1, 1),
            1,
            new[]
            {
                Item("scaled", 0, 0, true, new Vector2(-1, -1), new Vector2(2, 2), 0, 1,
                    Png(2, 1, (x, _) => x == 0 ? Colors.Red : Colors.Green)),
                Item("rotated", 1, 0, true, new Vector2(1, -1), new Vector2(2, 1), 90, 1,
                    SolidPng(1, 1, new Color(1, 0, 1, 1)))
            });
        using Image image = Load(CanvasCompositeRenderer.Capture(request).SourceCanvasPng);
        ColorNear(Colors.Red, image.GetPixel(0, 0), "negative coordinate + scale");
        ColorNear(Colors.Green, image.GetPixel(2, 1), "scaled second pixel");
        ColorNear(new Color(1, 0, 1, 1), image.GetPixel(1, 0), "rotation");
    }

    private static void MaskProtectsTransparentPixelsOutsideW()
    {
        CanvasPatchCapture capture = CanvasCompositeRenderer.Capture(new CanvasPatchCaptureRequest(
            new CanvasGridRegion(0, 0, 4, 4),
            new CanvasGridRegion(1, 1, 2, 2),
            1,
            Array.Empty<CanvasCompositeItem>()));
        using Image source = Load(capture.SourceCanvasPng);
        using Image mask = Load(capture.MaskPng);

        True(source.GetPixel(0, 0).A < 0.01f, "transparent Q fixture must remain transparent in source");
        True(mask.GetPixel(0, 0).A > 0.99f, "transparent Q pixels outside W must remain protected");
        True(mask.GetPixel(1, 1).A < 0.01f, "W must remain the only transparent mask region");
    }

    private static void ProviderResultsMapExactlyToW()
    {
        CanvasPatchCapture capture = EmptyCapture(4, 4, new CanvasGridRegion(1, 1, 2, 2));
        byte[] fullQ = Png(4, 4, (x, y) => x is >= 1 and < 3 && y is >= 1 and < 3 ? Colors.Green : Colors.Red);
        using Image cropped = Load(CanvasCompositeRenderer.ExtractReplacementPatch(capture, fullQ, CanvasProviderResultMapping.ReferenceCanvas));
        using Image scaled = Load(CanvasCompositeRenderer.ExtractReplacementPatch(capture, SolidPng(1, 1, Colors.Blue), CanvasProviderResultMapping.ReplacementPatch));
        Equal(new Vector2I(2, 2), new Vector2I(cropped.GetWidth(), cropped.GetHeight()), "cropped W size");
        ColorNear(Colors.Green, cropped.GetPixel(0, 0), "full-Q result crop");
        Equal(new Vector2I(2, 2), new Vector2I(scaled.GetWidth(), scaled.GetHeight()), "scaled W size");
        ColorNear(Colors.Blue, scaled.GetPixel(1, 1), "W result scaling");
    }

    private static void FullQProviderResponseWritesBackCompleteQ()
    {
        CanvasCompositeItem target = Item(
            "full-q-target",
            0,
            0,
            true,
            Vector2.Zero,
            Vector2.One,
            0,
            1,
            SolidPng(4, 4, Colors.White));
        CanvasPatchCapture capture = CanvasCompositeRenderer.Capture(new CanvasPatchCaptureRequest(
            new CanvasGridRegion(0, 0, 4, 4),
            new CanvasGridRegion(1, 1, 2, 2),
            1,
            new[] { target },
            target.Id));
        byte[] fullQ = Png(4, 4, (x, y) =>
            x is >= 1 and < 3 && y is >= 1 and < 3 ? Colors.Green : Colors.Red);

        using Image applied = Load(CanvasCompositeRenderer.ExtractGeneratedReferenceCanvas(
            capture,
            fullQ,
            CanvasProviderResultMapping.ReferenceCanvas));
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                ColorNear(
                    x is >= 1 and < 3 && y is >= 1 and < 3 ? Colors.Green : Colors.Red,
                    applied.GetPixel(x, y),
                    $"full-Q provider response must write the complete Q at ({x},{y})");
            }
        }
    }

    private static void LegacyWProviderResponseComposesIntoCompleteQ()
    {
        CanvasPatchCapture capture = CanvasCompositeRenderer.Capture(new CanvasPatchCaptureRequest(
            new CanvasGridRegion(0, 0, 4, 4),
            new CanvasGridRegion(1, 1, 2, 2),
            1,
            new[] { Item("legacy-w", 0, 0, true, Vector2.Zero, Vector2.One, 0, 1, SolidPng(4, 4, Colors.White)) },
            "legacy-w"));

        using Image applied = Load(CanvasCompositeRenderer.ExtractGeneratedReferenceCanvas(
            capture,
            SolidPng(2, 2, Colors.Blue),
            CanvasProviderResultMapping.ReferenceCanvas));
        Equal(new Vector2I(4, 4), new Vector2I(applied.GetWidth(), applied.GetHeight()),
            "legacy W response must still produce complete Q dimensions");
        ColorNear(Colors.White, applied.GetPixel(0, 0), "legacy W response must preserve frozen Q outside W");
        ColorNear(Colors.Blue, applied.GetPixel(1, 1), "legacy W response must fill W");
    }

    private static void PngJpegAndWebpProviderResultsCropAndComposeToW()
    {
        CanvasCompositeItem target = Item("format-target", 0, 0, true, Vector2.Zero, Vector2.One, 0, 1, SolidPng(4, 4, Colors.White));
        CanvasPatchCapture capture = CanvasCompositeRenderer.Capture(new CanvasPatchCaptureRequest(
            new CanvasGridRegion(0, 0, 4, 4),
            new CanvasGridRegion(1, 1, 2, 2),
            1,
            new[] { target },
            target.Id));
        using Image provider = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        provider.Fill(Colors.Green);
        (string Name, byte[] Bytes)[] formats =
        {
            ("PNG", provider.SavePngToBuffer()),
            ("JPEG", provider.SaveJpgToBuffer(1.0f)),
            ("WebP", provider.SaveWebpToBuffer(lossy: false, quality: 1.0f))
        };

        foreach ((string name, byte[] bytes) in formats)
        {
            using Image cropped = Load(CanvasCompositeRenderer.ExtractReplacementPatch(
                capture,
                bytes,
                CanvasProviderResultMapping.ReferenceCanvas));
            Equal(new Vector2I(2, 2), new Vector2I(cropped.GetWidth(), cropped.GetHeight()), $"{name} cropped W size");
            ColorNear(Colors.Green, cropped.GetPixel(1, 1), $"{name} cropped W content", tolerance: 0.08f);

            using Image applied = Load(CanvasCompositeRenderer.ApplyReplacementToSource(
                capture,
                target,
                bytes,
                CanvasProviderResultMapping.ReferenceCanvas));
            Equal(new Vector2I(4, 4), new Vector2I(applied.GetWidth(), applied.GetHeight()), $"{name} applied source size");
            ColorNear(Colors.White, applied.GetPixel(0, 0), $"{name} outside W preserved");
            ColorNear(Colors.Green, applied.GetPixel(1, 1), $"{name} composed W content", tolerance: 0.08f);
        }
    }

    private static void ReplacementPreservesSourceOutsideWAndOriginalSize()
    {
        CanvasCompositeItem target = Item("target", 0, 0, true, Vector2.Zero, Vector2.One, 0, 1, SolidPng(4, 4, Colors.White));
        CanvasPatchCapture capture = CanvasCompositeRenderer.Capture(new CanvasPatchCaptureRequest(
            new CanvasGridRegion(0, 0, 4, 4), new CanvasGridRegion(1, 1, 2, 2), 1, new[] { target }, target.Id));
        using Image image = Load(CanvasCompositeRenderer.ApplyReplacementToSource(
            capture, target, SolidPng(2, 2, Colors.Blue), CanvasProviderResultMapping.ReplacementPatch));
        Equal(new Vector2I(4, 4), new Vector2I(image.GetWidth(), image.GetHeight()), "source dimensions preserved");
        ColorNear(Colors.White, image.GetPixel(0, 0), "outside W unchanged");
        ColorNear(Colors.White, image.GetPixel(3, 3), "outside W unchanged 2");
        ColorNear(Colors.Blue, image.GetPixel(1, 1), "inside W replaced");
        ColorNear(Colors.Blue, image.GetPixel(2, 2), "inside W replaced 2");
    }

    private static void ReplacementUsesTargetRotationAndScale()
    {
        CanvasCompositeItem target = Item("rotated", 0, 0, true, Vector2.Zero, new Vector2(2, 2), 90, 1, SolidPng(2, 2, Colors.White));
        CanvasPatchCapture capture = CanvasCompositeRenderer.Capture(new CanvasPatchCaptureRequest(
            new CanvasGridRegion(-4, 0, 4, 4), new CanvasGridRegion(-4, 0, 2, 4), 1, new[] { target }, target.Id));
        using Image image = Load(CanvasCompositeRenderer.ApplyReplacementToSource(
            capture, target, SolidPng(2, 4, Colors.Blue), CanvasProviderResultMapping.ReplacementPatch));
        ColorNear(Colors.White, image.GetPixel(0, 0), "rotated target outside W");
        ColorNear(Colors.White, image.GetPixel(1, 0), "rotated target outside W 2");
        ColorNear(Colors.Blue, image.GetPixel(0, 1), "rotated target inside W");
        ColorNear(Colors.Blue, image.GetPixel(1, 1), "rotated target inside W 2");
    }

    private static void FingerprintIsContentSensitiveAndCaptureIsImmutable()
    {
        CanvasPatchCapture Capture(CanvasCompositeItem item) => CanvasCompositeRenderer.Capture(new CanvasPatchCaptureRequest(
            new CanvasGridRegion(0, 0, 2, 2), new CanvasGridRegion(0, 0, 1, 1), 1, new[] { item }, item.Id));
        CanvasPatchCapture first = Capture(Item("a", 0, 0, true, Vector2.Zero, Vector2.One, 0, 1, SolidPng(1, 1, Colors.Red)));
        CanvasPatchCapture moved = Capture(Item("a", 0, 0, true, Vector2.One, Vector2.One, 0, 1, SolidPng(1, 1, Colors.Red)));
        CanvasPatchCapture recolored = Capture(Item("a", 0, 0, true, Vector2.Zero, Vector2.One, 0, 1, SolidPng(1, 1, Colors.Blue)));
        True(first.ContentFingerprint != moved.ContentFingerprint, "transform must affect fingerprint");
        True(first.ContentFingerprint != recolored.ContentFingerprint, "content must affect fingerprint");
        byte[] mutable = first.SourceCanvasPng;
        mutable[0] ^= 0xff;
        True(mutable[0] != first.SourceCanvasPng[0], "capture byte getters must clone");
    }

    private static CanvasPatchCapture EmptyCapture(int width, int height, CanvasGridRegion replacement) =>
        CanvasCompositeRenderer.Capture(new CanvasPatchCaptureRequest(
            new CanvasGridRegion(0, 0, width, height), replacement, 1, Array.Empty<CanvasCompositeItem>()));

    private static CanvasCompositeItem Item(string id, int layer, int order, bool visible, Vector2 position, Vector2 scale, float rotation, float opacity, byte[] png) =>
        new(id, layer, order, visible, position, scale, rotation, opacity, png);

    private static byte[] SolidPng(int width, int height, Color color) => Png(width, height, (_, _) => color);

    private static byte[] Png(int width, int height, Func<int, int, Color> pixel)
    {
        using Image image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                image.SetPixel(x, y, pixel(x, y));
        return image.SavePngToBuffer();
    }

    private static Image Load(byte[] png)
    {
        Image image = new();
        Equal(Error.Ok, image.LoadPngFromBuffer(png), "PNG decode");
        return image;
    }

    private static void ColorNear(Color expected, Color actual, string message, float tolerance = 0.012f)
    {
        True(Math.Abs(expected.R - actual.R) <= tolerance
            && Math.Abs(expected.G - actual.G) <= tolerance
            && Math.Abs(expected.B - actual.B) <= tolerance
            && Math.Abs(expected.A - actual.A) <= tolerance,
            $"{message}: expected {expected}, got {actual}");
    }

    private static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }
}
