using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace AppAgasCs.Features.Ai.Generation;

/// <summary>The provider-neutral operation requested by the caller.</summary>
public enum AiGenerationOperation
{
    TextToImage,
    ReferenceEdit,
    RegionEdit
}

/// <summary>Optional provider-neutral quality hint for models that expose discrete image quality tiers.</summary>
public enum AiGenerationQuality
{
    Low,
    Medium
}

public static class AiGenerationQualityPolicy
{
    public const string GrokImagineImage20ModelName = "grok-imagine-image-2.0";

    public static bool Supports(string? modelName) => string.Equals(
        modelName?.Trim(),
        GrokImagineImage20ModelName,
        StringComparison.OrdinalIgnoreCase);

    public static AiGenerationQuality? Resolve(string? modelName, AiGenerationQuality? requested) =>
        Supports(modelName) ? requested ?? AiGenerationQuality.Medium : requested;
}

[Flags]
public enum AiGenerationAdapterCapabilities
{
    None = 0,
    TextToImage = 1 << 0,
    ReferenceEdit = 1 << 1,
    RegionEdit = 1 << 2,
    NativeAlphaMask = 1 << 3,
    GuidedRegionEdit = 1 << 4
}

public static class AiGenerationCapabilityNames
{
    public static AiGenerationAdapterCapabilities Parse(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        AiGenerationAdapterCapabilities capabilities = AiGenerationAdapterCapabilities.None;
        foreach (string name in names)
        {
            capabilities |= name switch
            {
                "textToImage" => AiGenerationAdapterCapabilities.TextToImage,
                "referenceEdit" => AiGenerationAdapterCapabilities.ReferenceEdit,
                "regionEdit" => AiGenerationAdapterCapabilities.RegionEdit,
                "nativeAlphaMask" => AiGenerationAdapterCapabilities.NativeAlphaMask,
                "guidedRegionEdit" => AiGenerationAdapterCapabilities.GuidedRegionEdit,
                _ => throw new ArgumentException($"Unknown AI generation capability '{name}'.", nameof(names)),
            };
        }
        return capabilities;
    }
}

public enum AiGenerationEffectiveOperation
{
    TextToImage,
    ReferenceEdit,
    MaskedEdit,
    GuidedRegionEdit
}

/// <summary>
/// Describes what the upstream request itself guarantees. GuidedReference is
/// deliberately not called inpainting: callers must locally clip/composite the
/// returned image if pixels outside the requested region must remain unchanged.
/// </summary>
public enum AiGenerationEditGuarantee
{
    None,
    ProviderMasked,
    GuidedReference
}

/// <summary>Immutable provider parameter limits projected from image-model metadata.</summary>
public sealed record AiGenerationModelConstraints
{
    public AiGenerationModelConstraints(IEnumerable<string> ratios, IEnumerable<string> resolutions,
        string defaultRatio, string defaultResolution, int? maxImages)
    {
        Ratios = Array.AsReadOnly(ratios.ToArray());
        Resolutions = Array.AsReadOnly(resolutions.ToArray());
        DefaultRatio = defaultRatio;
        DefaultResolution = defaultResolution;
        MaxImages = maxImages;
    }
    public IReadOnlyList<string> Ratios { get; }
    public IReadOnlyList<string> Resolutions { get; }
    public string DefaultRatio { get; }
    public string DefaultResolution { get; }
    public int? MaxImages { get; }
}

public sealed class AiGenerationProviderProfile
{
    public AiGenerationProviderProfile(
        string id,
        string displayName,
        string adapterKind,
        string modelName,
        string apiKey,
        string baseUrl,
        AiGenerationAdapterCapabilities? declaredCapabilities = null)
        : this(
            id,
            displayName,
            adapterKind,
            adapterKind,
            modelName,
            apiKey,
            baseUrl,
            declaredCapabilities)
    {
    }

    /// <summary>
    /// Creates a provider profile with a stable provider identity independent
    /// from the adapter implementation kind. The legacy constructor remains
    /// available and maps ProviderId to AdapterKind for source/binary compatibility.
    /// </summary>
    public AiGenerationProviderProfile(
        string id,
        string displayName,
        string providerId,
        string adapterKind,
        string modelName,
        string apiKey,
        string baseUrl,
        AiGenerationAdapterCapabilities? declaredCapabilities = null)
        : this(id, displayName, providerId, adapterKind, modelName, apiKey, baseUrl, declaredCapabilities, null)
    {
    }

    public AiGenerationProviderProfile(
        string id, string displayName, string providerId, string adapterKind, string modelName,
        string apiKey, string baseUrl, AiGenerationAdapterCapabilities? declaredCapabilities,
        AiGenerationModelConstraints? modelConstraints)
    {
        Id = Required(id, nameof(id));
        DisplayName = Required(displayName, nameof(displayName));
        ProviderId = Required(providerId, nameof(providerId));
        AdapterKind = Required(adapterKind, nameof(adapterKind));
        ModelName = Required(modelName, nameof(modelName));
        ApiKey = Required(apiKey, nameof(apiKey));
        BaseUri = ParseBaseUri(baseUrl);
        DeclaredCapabilities = declaredCapabilities;
        ModelConstraints = modelConstraints;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string ProviderId { get; }
    public string AdapterKind { get; }
    public string ModelName { get; }
    public Uri BaseUri { get; }
    public AiGenerationAdapterCapabilities? DeclaredCapabilities { get; }
    public AiGenerationModelConstraints? ModelConstraints { get; }
    /// <summary>
    /// Credential for the selected profile. Adapter implementations may use it
    /// only for the current provider request and must never log or persist it.
    /// </summary>
    public string ApiKey { get; }

    public override string ToString() => $"{DisplayName} ({AdapterKind}, {ModelName})";

    private static string Required(string value, string parameterName)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Value is required.", parameterName);
        }

        return normalized;
    }

    private static Uri ParseBaseUri(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Base URL must use HTTPS, or HTTP for loopback only, without credentials, query, or fragment.", nameof(value));
        }

        return uri;
    }
}

public sealed class AiGenerationReferenceImage
{
    private const int MaximumReferenceImageBytes = 64 * 1024 * 1024;
    private readonly byte[] _bytes;

    private AiGenerationReferenceImage(byte[] bytes, string mimeType)
    {
        _bytes = bytes;
        MimeType = mimeType;
    }

    public byte[] Bytes => (byte[])_bytes.Clone();
    public long SizeBytes => _bytes.LongLength;
    public string MimeType { get; }
    public int PixelWidth => ImagePixelMetadata.Read(_bytes, MimeType).Width;
    public int PixelHeight => ImagePixelMetadata.Read(_bytes, MimeType).Height;

    public static AiGenerationReferenceImage Create(byte[] bytes, string mimeType)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
        {
            throw new ArgumentException("Reference image is empty.", nameof(bytes));
        }
        if (bytes.Length > MaximumReferenceImageBytes)
        {
            throw new ArgumentException("Reference image is too large.", nameof(bytes));
        }

        string normalizedMimeType = GeneratedImageData.NormalizeMimeType(mimeType);
        ValidateImageBytes(bytes, normalizedMimeType);
        return new AiGenerationReferenceImage((byte[])bytes.Clone(), normalizedMimeType);
    }

    private static void ValidateImageBytes(byte[] bytes, string mimeType)
    {
        bool valid = mimeType switch
        {
            "image/png" => bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
            "image/webp" => bytes.Length >= 12
                && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false
        };
        if (!valid)
        {
            throw new AiGenerationException(AiGenerationErrorCode.UnsupportedImage, "The reference image is not a valid supported image.");
        }
    }
}

public readonly record struct AiGenerationPixelRegion
{
    public AiGenerationPixelRegion(int x, int y, int width, int height)
    {
        if (x < 0) throw new ArgumentOutOfRangeException(nameof(x));
        if (y < 0) throw new ArgumentOutOfRangeException(nameof(y));
        if (width < 1) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 1) throw new ArgumentOutOfRangeException(nameof(height));
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
}

/// <summary>
/// Provider-neutral local edit input. SourceCanvas is the Q canvas with the W
/// area already transparent. Mask is an RGBA PNG of the same dimensions where
/// alpha zero means editable and alpha 255 means preserved.
/// </summary>
public sealed class AiGenerationEditInput
{
    public AiGenerationEditInput(
        AiGenerationReferenceImage sourceCanvas,
        AiGenerationReferenceImage mask,
        AiGenerationPixelRegion editRegionPixels)
    {
        SourceCanvas = sourceCanvas ?? throw new ArgumentNullException(nameof(sourceCanvas));
        Mask = mask ?? throw new ArgumentNullException(nameof(mask));
        if (SourceCanvas.MimeType != "image/png" || Mask.MimeType != "image/png")
        {
            throw new AiGenerationException(AiGenerationErrorCode.UnsupportedImage, "Region edit source and mask must be PNG images.");
        }

        if (!ImagePixelMetadata.PngHasAlphaChannel(Mask.Bytes))
        {
            throw new AiGenerationException(AiGenerationErrorCode.InvalidEditInput, "Region edit mask must contain an alpha channel.");
        }

        if (SourceCanvas.PixelWidth < 1 || SourceCanvas.PixelHeight < 1
            || Mask.PixelWidth != SourceCanvas.PixelWidth
            || Mask.PixelHeight != SourceCanvas.PixelHeight)
        {
            throw new AiGenerationException(AiGenerationErrorCode.InvalidEditInput, "Region edit source and mask must have identical valid pixel dimensions.");
        }

        if ((long)editRegionPixels.X + editRegionPixels.Width > SourceCanvas.PixelWidth
            || (long)editRegionPixels.Y + editRegionPixels.Height > SourceCanvas.PixelHeight)
        {
            throw new AiGenerationException(AiGenerationErrorCode.InvalidEditInput, "The edit region must be fully contained in the source canvas.");
        }
        if (!PngMaskValidator.MaskMatchesEditRegion(
                Mask.Bytes,
                SourceCanvas.PixelWidth,
                SourceCanvas.PixelHeight,
                editRegionPixels))
        {
            throw new AiGenerationException(AiGenerationErrorCode.InvalidEditInput, "Region edit mask must expose only the edit region and protect every other Q pixel.");
        }
        if (!PngMaskValidator.HasTransparentEditRegion(
                SourceCanvas.Bytes,
                SourceCanvas.PixelWidth,
                SourceCanvas.PixelHeight,
                editRegionPixels))
        {
            throw new AiGenerationException(AiGenerationErrorCode.InvalidEditInput, "Region edit source canvas must be transparent throughout the edit region.");
        }

        SourceCanvas = sourceCanvas;
        Mask = mask;
        EditRegionPixels = editRegionPixels;
    }

    public AiGenerationReferenceImage SourceCanvas { get; }
    public AiGenerationReferenceImage Mask { get; }
    public AiGenerationPixelRegion EditRegionPixels { get; }
}

internal static class PngMaskValidator
{
    public static bool MaskMatchesEditRegion(
        byte[] mask,
        int width,
        int height,
        AiGenerationPixelRegion editRegion)
    {
        if (!TryReadAlpha(mask, width, height, out byte[] maskAlpha))
        {
            return false;
        }
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool editable = x >= editRegion.X
                    && x < editRegion.X + editRegion.Width
                    && y >= editRegion.Y
                    && y < editRegion.Y + editRegion.Height;
                byte expected = editable ? (byte)0 : (byte)255;
                if (maskAlpha[y * width + x] != expected)
                {
                    return false;
                }
            }
        }
        return true;
    }

    public static bool HasTransparentEditRegion(byte[] png, int width, int height, AiGenerationPixelRegion editRegion)
    {
        if (!TryReadAlpha(png, width, height, out byte[] alpha))
        {
            return false;
        }
        for (int y = editRegion.Y; y < editRegion.Y + editRegion.Height; y++)
        {
            for (int x = editRegion.X; x < editRegion.X + editRegion.Width; x++)
            {
                if (alpha[y * width + x] != 0)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool TryReadAlpha(
        byte[] png,
        int width,
        int height,
        out byte[] alpha)
    {
        alpha = Array.Empty<byte>();
        try
        {
            if (png.Length < 33 || png[24] != 8 || png[26] != 0 || png[27] != 0 || png[28] != 0) return false;
            int bytesPerPixel = png[25] switch { 4 => 2, 6 => 4, _ => 0 };
            if (bytesPerPixel == 0) return false;

            using var compressed = new MemoryStream();
            int offset = 8;
            bool sawEnd = false;
            while (offset + 12 <= png.Length)
            {
                int length = ReadBigEndian32(png, offset);
                if (length < 0 || offset + 12L + length > png.Length) return false;
                ReadOnlySpan<byte> type = png.AsSpan(offset + 4, 4);
                if (type.SequenceEqual("IDAT"u8)) compressed.Write(png, offset + 8, length);
                if (type.SequenceEqual("IEND"u8)) { sawEnd = true; break; }
                offset += 12 + length;
            }
            if (!sawEnd || compressed.Length == 0) return false;

            compressed.Position = 0;
            using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
            int stride = checked(width * bytesPerPixel);
            byte[] encodedRow = new byte[stride + 1];
            byte[] previous = new byte[stride];
            byte[] current = new byte[stride];
            alpha = new byte[checked(width * height)];
            for (int y = 0; y < height; y++)
            {
                inflater.ReadExactly(encodedRow);
                byte filter = encodedRow[0];
                if (filter > 4) return false;
                for (int index = 0; index < stride; index++)
                {
                    byte raw = encodedRow[index + 1];
                    byte left = index >= bytesPerPixel ? current[index - bytesPerPixel] : (byte)0;
                    byte up = previous[index];
                    byte upLeft = index >= bytesPerPixel ? previous[index - bytesPerPixel] : (byte)0;
                    current[index] = filter switch
                    {
                        0 => raw,
                        1 => unchecked((byte)(raw + left)),
                        2 => unchecked((byte)(raw + up)),
                        3 => unchecked((byte)(raw + ((left + up) / 2))),
                        4 => unchecked((byte)(raw + Paeth(left, up, upLeft))),
                        _ => raw
                    };
                }

                int alphaOffset = bytesPerPixel - 1;
                for (int x = 0; x < width; x++)
                {
                    alpha[y * width + x] = current[x * bytesPerPixel + alphaOffset];
                }
                (previous, current) = (current, previous);
            }
            return inflater.ReadByte() == -1;
        }
        catch
        {
            return false;
        }
    }

    private static byte Paeth(byte left, byte up, byte upLeft)
    {
        int estimate = left + up - upLeft;
        int leftDistance = Math.Abs(estimate - left);
        int upDistance = Math.Abs(estimate - up);
        int diagonalDistance = Math.Abs(estimate - upLeft);
        return leftDistance <= upDistance && leftDistance <= diagonalDistance ? left
            : upDistance <= diagonalDistance ? up
            : upLeft;
    }

    private static int ReadBigEndian32(byte[] bytes, int offset) =>
        (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
}

/// <summary>
/// Provider-neutral grid region metadata. Coordinates are expressed in project
/// grid cells. Provider adapters use the pixel-space region in
/// <see cref="AiGenerationEditInput"/> for local edits; this metadata remains
/// useful for canvas placement and diagnostics.
/// </summary>
public readonly record struct AiGenerationGridRegion
{
    public AiGenerationGridRegion(int column, int row, int width, int height)
    {
        if (width < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        Column = column;
        Row = row;
        Width = width;
        Height = height;
    }

    public int Column { get; }
    public int Row { get; }
    public int Width { get; }
    public int Height { get; }
}

/// <summary>
/// Optional Q/W canvas selection metadata carried with a generation request.
/// Q maps to <see cref="SelectionRegion"/> and W maps to
/// <see cref="ReplacementRegion"/>. It does not replace the explicit raster
/// source and mask required by a <see cref="AiGenerationOperation.RegionEdit"/>.
/// </summary>
public sealed record AiGenerationRegionMetadata(
    AiGenerationGridRegion? SelectionRegion,
    AiGenerationGridRegion? ReplacementRegion);

/// <summary>Frozen scene/layer identity used to place all artifacts from one request.</summary>
public sealed record AiGenerationStorageScope
{
    public AiGenerationStorageScope(string? sceneId, string? sceneName, string? layerId, string? layerName)
    {
        SceneId = sceneId?.Trim() ?? string.Empty;
        SceneName = sceneName?.Trim() ?? string.Empty;
        LayerId = layerId?.Trim() ?? string.Empty;
        LayerName = layerName?.Trim() ?? string.Empty;
    }

    public string SceneId { get; }
    public string SceneName { get; }
    public string LayerId { get; }
    public string LayerName { get; }
}

public sealed class AiGenerationRequest
{
    private const long MaximumTotalReferenceImageBytes = 64L * 1024 * 1024;

    public AiGenerationRequest(
        string projectRoot,
        string prompt,
        int width = 1024,
        int height = 1024,
        IReadOnlyList<AiGenerationReferenceImage>? referenceImages = null,
        AiGenerationRegionMetadata? regionMetadata = null,
        AiGenerationOperation? operation = null,
        AiGenerationEditInput? editInput = null,
        string sceneId = "",
        string sceneName = "",
        string layerId = "",
        string layerName = "",
        AiGenerationQuality? quality = null)
    {
        ProjectRoot = string.IsNullOrWhiteSpace(projectRoot)
            ? throw new ArgumentException("Project root is required.", nameof(projectRoot))
            : projectRoot;
        Prompt = string.IsNullOrWhiteSpace(prompt)
            ? throw new ArgumentException("Prompt is required.", nameof(prompt))
            : prompt.Trim();
        if (width is < 1 or > 8192)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height is < 1 or > 8192)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        Width = width;
        Height = height;
        List<AiGenerationReferenceImage> references = referenceImages is null
            ? new List<AiGenerationReferenceImage>()
            : new List<AiGenerationReferenceImage>(referenceImages);
        if (references.Count > 8 || references.Any(image => image is null))
        {
            throw new ArgumentException("At most eight non-null reference images are allowed.", nameof(referenceImages));
        }
        long totalReferenceImageBytes = 0;
        foreach (AiGenerationReferenceImage image in references)
        {
            if (image.SizeBytes > MaximumTotalReferenceImageBytes - totalReferenceImageBytes)
            {
                throw new ArgumentException("Reference images exceed the total size limit.", nameof(referenceImages));
            }
            totalReferenceImageBytes += image.SizeBytes;
        }
        ReferenceImages = references.AsReadOnly();
        RegionMetadata = regionMetadata;
        StorageScope = new AiGenerationStorageScope(sceneId, sceneName, layerId, layerName);
        Operation = operation ?? (references.Count > 0 ? AiGenerationOperation.ReferenceEdit : AiGenerationOperation.TextToImage);
        EditInput = editInput;
        Quality = quality;
        if (Operation == AiGenerationOperation.RegionEdit && EditInput is null)
        {
            throw new AiGenerationException(AiGenerationErrorCode.InvalidEditInput, "Region edit requires a source canvas and mask.");
        }
        if (Operation == AiGenerationOperation.RegionEdit
            && (EditInput!.SourceCanvas.PixelWidth != Width || EditInput.SourceCanvas.PixelHeight != Height))
        {
            throw new AiGenerationException(AiGenerationErrorCode.InvalidEditInput, "Region edit request dimensions must match the source canvas.");
        }
        if (Operation != AiGenerationOperation.RegionEdit && EditInput is not null)
        {
            throw new AiGenerationException(AiGenerationErrorCode.InvalidEditInput, "Edit input is only valid for a region edit request.");
        }
        if (Operation == AiGenerationOperation.ReferenceEdit && ReferenceImages.Count == 0)
        {
            throw new AiGenerationException(AiGenerationErrorCode.InvalidEditInput, "Reference edit requires at least one reference image.");
        }
    }

    public string ProjectRoot { get; }
    public string Prompt { get; }
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<AiGenerationReferenceImage> ReferenceImages { get; }
    public AiGenerationRegionMetadata? RegionMetadata { get; }
    public AiGenerationStorageScope StorageScope { get; }
    public string SceneId => StorageScope.SceneId;
    public string SceneName => StorageScope.SceneName;
    public string LayerId => StorageScope.LayerId;
    public string LayerName => StorageScope.LayerName;
    public AiGenerationOperation Operation { get; }
    public AiGenerationEditInput? EditInput { get; }
    public AiGenerationQuality? Quality { get; }
}

public sealed record AiGenerationResult(
    string RelativePath,
    string MimeType,
    long SizeBytes,
    AiGenerationEffectiveOperation EffectiveOperation = AiGenerationEffectiveOperation.TextToImage,
    AiGenerationEditGuarantee EditGuarantee = AiGenerationEditGuarantee.None,
    int PixelWidth = 0,
    int PixelHeight = 0);

public sealed record GeneratedImageData
{
    private readonly byte[] _bytes;

    public GeneratedImageData(byte[] bytes, string mimeType)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        _bytes = bytes.ToArray();
        MimeType = mimeType;
    }

    public byte[] Bytes => _bytes.ToArray();
    public string MimeType { get; }
    public AiGenerationEffectiveOperation EffectiveOperation { get; init; } = AiGenerationEffectiveOperation.TextToImage;
    public AiGenerationEditGuarantee EditGuarantee { get; init; } = AiGenerationEditGuarantee.None;
    public int PixelWidth => ImagePixelMetadata.Read(Bytes, MimeType).Width;
    public int PixelHeight => ImagePixelMetadata.Read(Bytes, MimeType).Height;

    public static string NormalizeMimeType(string? mimeType)
    {
        string normalized = (mimeType ?? string.Empty).Split(';', 2)[0].Trim().ToLowerInvariant();
        return normalized switch
        {
            "image/png" => "image/png",
            "image/jpeg" or "image/jpg" => "image/jpeg",
            "image/webp" => "image/webp",
            _ => throw new AiGenerationException(AiGenerationErrorCode.UnsupportedImage, "The provider returned an unsupported image type.")
        };
    }
}

internal static class ImagePixelMetadata
{
    public static bool PngHasAlphaChannel(byte[] bytes) =>
        bytes is { Length: >= 26 }
        && bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)
        && bytes[25] is 4 or 6;

    public static (int Width, int Height) Read(byte[] bytes, string mimeType)
    {
        if (bytes is null) return (0, 0);
        try
        {
            return GeneratedImageData.NormalizeMimeType(mimeType) switch
            {
                "image/png" => ReadPng(bytes),
                "image/jpeg" => ReadJpeg(bytes),
                "image/webp" => ReadWebp(bytes),
                _ => (0, 0)
            };
        }
        catch
        {
            return (0, 0);
        }
    }

    private static (int Width, int Height) ReadPng(byte[] bytes)
    {
        if (bytes.Length < 24 || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return (0, 0);
        int width = ReadBigEndian32(bytes, 16);
        int height = ReadBigEndian32(bytes, 20);
        return width > 0 && height > 0 ? (width, height) : (0, 0);
    }

    private static (int Width, int Height) ReadJpeg(byte[] bytes)
    {
        int offset = 2;
        while (offset + 8 < bytes.Length)
        {
            if (bytes[offset++] != 0xff) continue;
            byte marker = bytes[offset++];
            if (marker is 0xd8 or 0xd9 || marker is >= 0xd0 and <= 0xd7) continue;
            if (offset + 2 > bytes.Length) break;
            int length = (bytes[offset] << 8) | bytes[offset + 1];
            if (length < 2 || offset + length > bytes.Length) break;
            if (marker is >= 0xc0 and <= 0xc3 || marker is >= 0xc5 and <= 0xc7 || marker is >= 0xc9 and <= 0xcb || marker is >= 0xcd and <= 0xcf)
            {
                int height = (bytes[offset + 3] << 8) | bytes[offset + 4];
                int width = (bytes[offset + 5] << 8) | bytes[offset + 6];
                return width > 0 && height > 0 ? (width, height) : (0, 0);
            }
            offset += length;
        }
        return (0, 0);
    }

    private static (int Width, int Height) ReadWebp(byte[] bytes)
    {
        if (bytes.Length < 25) return (0, 0);
        if (bytes.AsSpan(12, 4).SequenceEqual("VP8X"u8) && bytes.Length >= 30)
        {
            int width = 1 + bytes[24] + (bytes[25] << 8) + (bytes[26] << 16);
            int height = 1 + bytes[27] + (bytes[28] << 8) + (bytes[29] << 16);
            return (width, height);
        }
        if (bytes.AsSpan(12, 4).SequenceEqual("VP8 "u8)
            && bytes.Length >= 30
            && bytes.AsSpan(23, 3).SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a }))
        {
            int width = (bytes[26] | (bytes[27] << 8)) & 0x3fff;
            int height = (bytes[28] | (bytes[29] << 8)) & 0x3fff;
            return (width, height);
        }
        if (bytes.AsSpan(12, 4).SequenceEqual("VP8L"u8) && bytes.Length >= 25 && bytes[20] == 0x2f)
        {
            int width = 1 + bytes[21] + ((bytes[22] & 0x3f) << 8);
            int height = 1 + (bytes[22] >> 6) + (bytes[23] << 2) + ((bytes[24] & 0x0f) << 10);
            return (width, height);
        }
        return (0, 0);
    }

    private static int ReadBigEndian32(byte[] bytes, int offset) =>
        (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
}
