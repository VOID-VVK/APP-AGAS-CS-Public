#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Buffers.Binary;

namespace AppAgasCs.Core.Persistence;

public static class ScenePackageSafetyLimits
{
    public const long MaximumImageBytes = 32L * 1024 * 1024;
    public const int MaximumImageDimension = 8192;
    public const long MaximumDecodedPixels = 64_000_000;
    public const long MaximumManifestBytes = 1L * 1024 * 1024;
    public const long MaximumPackagePayloadBytes = 128L * 1024 * 1024;
    public const int MaximumManifestDepth = 64;
    public const int MaximumLayers = 256;
    public const int MaximumPromptFragments = 4096;
    public const int MaximumReferenceImages = 512;
    public static readonly TimeSpan HeavyOperationTimeout = TimeSpan.FromSeconds(120);
}

public sealed class ScenePackageImageTooLargeException : IOException
{
    public ScenePackageImageTooLargeException() : base("Image dimensions exceed the safety budget.") { }
}

public static class ScenePackageFileNamePolicy
{
    public static string SanitizeStem(string? fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName ?? string.Empty).Trim();
        var result = new StringBuilder(Math.Min(stem.Length, 80));
        bool pendingSeparator = false;
        foreach (char value in stem)
        {
            if (char.IsLetterOrDigit(value) || value is '-' or '_')
            {
                if (pendingSeparator && result.Length > 0 && result[^1] != '-') result.Append('-');
                pendingSeparator = false;
                result.Append(value);
                if (result.Length >= 80) break;
            }
            else
            {
                pendingSeparator = true;
            }
        }
        string sanitized = result.ToString().Trim('-', '_');
        return string.IsNullOrEmpty(sanitized) ? "asset" : sanitized;
    }
}

public static class ScenePackageImageInspector
{
    public static (int Width, int Height) Inspect(
        string path,
        string extension,
        IScenePackageImageDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        byte[] bytes = File.ReadAllBytes(path);
        (int Width, int Height) dimensions = extension.ToLowerInvariant() switch
        {
            ".png" => InspectPng(bytes),
            ".jpg" or ".jpeg" => InspectJpeg(bytes),
            ".webp" => InspectWebp(bytes),
            _ => throw new InvalidDataException("Image extension is not supported."),
        };
        if (dimensions.Width > ScenePackageSafetyLimits.MaximumImageDimension
            || dimensions.Height > ScenePackageSafetyLimits.MaximumImageDimension
            || checked((long)dimensions.Width * dimensions.Height) > ScenePackageSafetyLimits.MaximumDecodedPixels)
            throw new ScenePackageImageTooLargeException();
        if (!decoder.TryDecode(bytes, extension, dimensions.Width, dimensions.Height))
            throw new InvalidDataException("Image payload cannot be decoded safely.");
        return dimensions;
    }

    private static (int Width, int Height) InspectPng(byte[] bytes)
    {
        ReadOnlySpan<byte> data = bytes;
        if (data.Length < 45 || !data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("PNG signature is invalid.");
        int offset = 8;
        int width = 0;
        int height = 0;
        bool sawHeader = false;
        bool sawData = false;
        bool sawEnd = false;
        while (offset <= data.Length - 12)
        {
            uint lengthValue = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            if (lengthValue > int.MaxValue) throw new InvalidDataException("PNG chunk is invalid.");
            int length = (int)lengthValue;
            if (length > data.Length - offset - 12) throw new InvalidDataException("PNG chunk is truncated.");
            ReadOnlySpan<byte> type = data.Slice(offset + 4, 4);
            ReadOnlySpan<byte> payload = data.Slice(offset + 8, length);
            uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 8 + length, 4));
            if (ComputePngCrc(type, payload) != expectedCrc) throw new InvalidDataException("PNG chunk checksum is invalid.");
            if (type.SequenceEqual("IHDR"u8))
            {
                if (sawHeader || offset != 8 || length != 13) throw new InvalidDataException("PNG header is invalid.");
                width = BinaryPrimitives.ReadInt32BigEndian(payload[..4]);
                height = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(4, 4));
                byte bitDepth = payload[8];
                byte colorType = payload[9];
                bool validDepth = colorType switch
                {
                    0 => bitDepth is 1 or 2 or 4 or 8 or 16,
                    2 => bitDepth is 8 or 16,
                    3 => bitDepth is 1 or 2 or 4 or 8,
                    4 or 6 => bitDepth is 8 or 16,
                    _ => false,
                };
                if (width <= 0 || height <= 0 || !validDepth || payload[10] != 0 || payload[11] != 0 || payload[12] > 1)
                    throw new InvalidDataException("PNG dimensions or header fields are invalid.");
                sawHeader = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (!sawHeader || sawEnd) throw new InvalidDataException("PNG data ordering is invalid.");
                sawData = true;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (!sawData || length != 0) throw new InvalidDataException("PNG end chunk is invalid.");
                sawEnd = true;
                offset += 12;
                break;
            }
            offset += 12 + length;
        }
        if (!sawHeader || !sawData || !sawEnd || offset != data.Length)
            throw new InvalidDataException("PNG payload is incomplete.");
        return (width, height);
    }

    private static uint ComputePngCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        uint crc = 0xffffffff;
        foreach (byte value in type) crc = UpdateCrc(crc, value);
        foreach (byte value in payload) crc = UpdateCrc(crc, value);
        return crc ^ 0xffffffff;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
        return crc;
    }

    private static (int Width, int Height) InspectJpeg(byte[] bytes)
    {
        ReadOnlySpan<byte> data = bytes;
        if (data.Length < 12 || data[0] != 0xff || data[1] != 0xd8
            || data[^2] != 0xff || data[^1] != 0xd9)
            throw new InvalidDataException("JPEG signature is invalid.");
        int offset = 2;
        int width = 0;
        int height = 0;
        bool sawScan = false;
        while (offset < data.Length)
        {
            if (data[offset++] != 0xff) throw new InvalidDataException("JPEG marker is invalid.");
            while (offset < data.Length && data[offset] == 0xff) offset++;
            if (offset >= data.Length) throw new InvalidDataException("JPEG marker is truncated.");
            byte marker = data[offset++];
            if (marker == 0xd9) break;
            if (marker is 0xd8 or >= 0xd0 and <= 0xd7 or 0x01) continue;
            if (offset > data.Length - 2) throw new InvalidDataException("JPEG segment is truncated.");
            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            if (segmentLength < 2 || segmentLength > data.Length - offset)
                throw new InvalidDataException("JPEG segment length is invalid.");
            bool isSof = marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf;
            if (isSof)
            {
                if (segmentLength < 8) throw new InvalidDataException("JPEG frame header is invalid.");
                height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 5, 2));
                if (width <= 0 || height <= 0) throw new InvalidDataException("JPEG dimensions are invalid.");
            }
            offset += segmentLength;
            if (marker == 0xda) { sawScan = true; break; }
        }
        if (width <= 0 || height <= 0 || !sawScan)
            throw new InvalidDataException("JPEG payload is incomplete.");
        return (width, height);
    }

    private static (int Width, int Height) InspectWebp(byte[] bytes)
    {
        ReadOnlySpan<byte> data = bytes;
        if (data.Length < 20 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WEBP"u8))
            throw new InvalidDataException("WebP signature is invalid.");
        uint riffLength = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
        if (riffLength != data.Length - 8) throw new InvalidDataException("WebP container length is invalid.");
        int offset = 12;
        int width = 0;
        int height = 0;
        bool sawImage = false;
        while (offset <= data.Length - 8)
        {
            ReadOnlySpan<byte> type = data.Slice(offset, 4);
            uint lengthValue = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            if (lengthValue > int.MaxValue) throw new InvalidDataException("WebP chunk is invalid.");
            int length = (int)lengthValue;
            int padded = checked(length + (length & 1));
            if (padded > data.Length - offset - 8) throw new InvalidDataException("WebP chunk is truncated.");
            ReadOnlySpan<byte> payload = data.Slice(offset + 8, length);
            if (type.SequenceEqual("VP8X"u8))
            {
                if (length != 10) throw new InvalidDataException("WebP extended header is invalid.");
                width = 1 + payload[4] + (payload[5] << 8) + (payload[6] << 16);
                height = 1 + payload[7] + (payload[8] << 8) + (payload[9] << 16);
            }
            else if (type.SequenceEqual("VP8 "u8))
            {
                if (length < 10 || payload[3] != 0x9d || payload[4] != 0x01 || payload[5] != 0x2a)
                    throw new InvalidDataException("WebP VP8 header is invalid.");
                width = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2)) & 0x3fff;
                height = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2)) & 0x3fff;
                sawImage = true;
            }
            else if (type.SequenceEqual("VP8L"u8))
            {
                if (length < 5 || payload[0] != 0x2f) throw new InvalidDataException("WebP VP8L header is invalid.");
                width = 1 + payload[1] + ((payload[2] & 0x3f) << 8);
                height = 1 + (payload[2] >> 6) + (payload[3] << 2) + ((payload[4] & 0x0f) << 10);
                sawImage = true;
            }
            offset += 8 + padded;
        }
        if (offset != data.Length || width <= 0 || height <= 0 || !sawImage)
            throw new InvalidDataException("WebP payload is incomplete.");
        return (width, height);
    }
}

public interface IScenePackageImageDecoder
{
    bool TryDecode(ReadOnlyMemory<byte> bytes, string extension, int expectedWidth, int expectedHeight);
}

public sealed class GodotScenePackageImageDecoder : IScenePackageImageDecoder
{
    public bool TryDecode(ReadOnlyMemory<byte> bytes, string extension, int expectedWidth, int expectedHeight)
    {
        using var decoded = new Godot.Image();
        Godot.Error result = extension.ToLowerInvariant() switch
        {
            ".png" => decoded.LoadPngFromBuffer(bytes.ToArray()),
            ".jpg" or ".jpeg" => decoded.LoadJpgFromBuffer(bytes.ToArray()),
            ".webp" => decoded.LoadWebpFromBuffer(bytes.ToArray()),
            _ => Godot.Error.FileUnrecognized,
        };
        return result == Godot.Error.Ok
            && !decoded.IsEmpty()
            && decoded.GetWidth() == expectedWidth
            && decoded.GetHeight() == expectedHeight;
    }
}

public sealed class ScenePackagePathGuard
{
    private static readonly Regex SafeReference = new("^[A-Za-z0-9_-]{1,120}$", RegexOptions.CultureInvariant);
    private readonly IProjectAccessIdentityProvider _identityProvider;

    public ScenePackagePathGuard(IProjectAccessIdentityProvider? identityProvider = null) =>
        _identityProvider = identityProvider ?? new FileSystemProjectAccessIdentityProvider();

    public bool IsSafeReference(string? value) =>
        !string.IsNullOrWhiteSpace(value) && SafeReference.IsMatch(value);

    public bool IsSafeBasename(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim()) return false;
        if (value.Contains('/') || value.Contains('\\') || value.Contains('\0')) return false;
        if (value is "." or ".." || !string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal)) return false;
        return !ProjectPathRules.ContainsUrlOrBase64(value)
            && !Path.IsPathRooted(value)
            && !ProjectPathRules.LooksLikeWindowsDrivePrefix(value);
    }

    public bool TryResolveExistingFile(string root, string relativePath, out string absolutePath)
    {
        absolutePath = string.Empty;
        if (!TryResolve(root, relativePath, out string candidate) || !File.Exists(candidate)) return false;
        if (!SafeChain(root, candidate)) return false;
        ProjectAccessPathIdentity identity = _identityProvider.Capture(candidate);
        if (!identity.Exists || identity.IsDirectory || identity.IsLinkOrReparse) return false;
        absolutePath = candidate;
        return true;
    }

    public bool TryResolveOwnedTarget(string root, string relativePath, out string absolutePath)
    {
        absolutePath = string.Empty;
        if (!TryResolve(root, relativePath, out string candidate) || !SafeChain(root, candidate)) return false;
        absolutePath = candidate;
        return true;
    }

    public bool TryResolveDirectory(string root, string relativePath, bool mustExist, out string absolutePath)
    {
        absolutePath = string.Empty;
        if (!TryResolve(root, relativePath, out string candidate)) return false;
        if (mustExist && !Directory.Exists(candidate)) return false;
        if (!SafeChain(root, candidate)) return false;
        if (Directory.Exists(candidate))
        {
            ProjectAccessPathIdentity identity = _identityProvider.Capture(candidate);
            if (!identity.Exists || !identity.IsDirectory || identity.IsLinkOrReparse) return false;
        }
        absolutePath = candidate;
        return true;
    }

    public ProjectAccessPathIdentity Capture(string path) => _identityProvider.Capture(path);

    public string CaptureOwnedFileIdentityToken(string path) =>
        CaptureOwnedFileIdentityToken(path, path, string.Empty, string.Empty);

    public string CaptureOwnedFileIdentityToken(
        string path,
        string canonicalDestinationPath,
        string operationId,
        string ownerToken)
    {
        ProjectAccessPathIdentity identity = _identityProvider.Capture(path);
        if (!identity.Exists || identity.IsDirectory || identity.IsLinkOrReparse)
            throw new InvalidDataException("Owned artifact identity is unavailable.");
        FileInfo info = new(path);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string contentHash = Convert.ToHexString(SHA256.HashData(stream));
        string canonicalPath = Path.GetFullPath(canonicalDestinationPath);
        string value = string.Join("|",
            canonicalPath,
            info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            info.CreationTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            contentHash,
            operationId,
            ownerToken);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    public string CaptureOwnedDirectoryIdentityToken(
        string path,
        string canonicalDestinationPath,
        string operationId,
        string ownerToken)
    {
        ProjectAccessPathIdentity identity = _identityProvider.Capture(path);
        if (!identity.Exists || !identity.IsDirectory || identity.IsLinkOrReparse)
            throw new InvalidDataException("Owned directory identity is unavailable.");
        DirectoryInfo info = new(path);
        info.Refresh();
        string value = string.Join("|",
            Path.GetFullPath(canonicalDestinationPath),
            "directory",
            ((int)info.Attributes).ToString(System.Globalization.CultureInfo.InvariantCulture),
            info.CreationTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            operationId,
            ownerToken);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    public bool IsOperationOwnedArtifactRelativePath(string operationId, string relativePath)
    {
        if (!IsSafeReference(operationId) || string.IsNullOrWhiteSpace(relativePath)) return false;
        const string prefix = "assets/imported/packages/";
        if (!relativePath.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string fileName = relativePath[prefix.Length..];
        if (!IsSafeBasename(fileName) || !fileName.StartsWith(operationId + "-", StringComparison.Ordinal)) return false;
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        return ProjectPathRules.AllowedImageExtensionsWithDot.Contains(extension);
    }

    public bool IsOperationOwnedStagingArtifactRelativePath(string operationId, string relativePath)
    {
        if (!IsSafeReference(operationId) || string.IsNullOrWhiteSpace(relativePath)) return false;
        string prefix = $".agas/import-staging/{operationId}/";
        if (!relativePath.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string descendant = relativePath[prefix.Length..];
        if (descendant is ScenePackageImportJournalStore.JournalFileName
            or ScenePackageImportJournalStore.OwnerMarkerFileName) return false;
        string[] segments = descendant.Split('/', StringSplitOptions.None);
        return segments.All(segment => IsSafeBasename(segment));
    }

    private static bool TryResolve(string root, string relativePath, out string absolutePath)
    {
        absolutePath = string.Empty;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relativePath)) return false;
        if (Path.IsPathRooted(relativePath)
            || ProjectPathRules.LooksLikeWindowsDrivePrefix(relativePath)
            || ProjectPathRules.ContainsUrlOrBase64(relativePath)
            || relativePath.Contains('\\')
            || relativePath.Contains('\0')) return false;
        string[] segments = relativePath.Split('/', StringSplitOptions.None);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or "..")) return false;
        try
        {
            string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string candidate = Path.GetFullPath(Path.Combine(normalizedRoot, Path.Combine(segments)));
            string prefix = normalizedRoot + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(prefix, StringComparison.Ordinal)) return false;
            absolutePath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool SafeChain(string root, string target)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        ProjectAccessPathIdentity rootIdentity = _identityProvider.Capture(normalizedRoot);
        if (!rootIdentity.Exists || !rootIdentity.IsDirectory || rootIdentity.IsLinkOrReparse) return false;
        string? current = Directory.Exists(target) ? target : Path.GetDirectoryName(target);
        while (!string.IsNullOrEmpty(current)
               && current.StartsWith(normalizedRoot, StringComparison.Ordinal))
        {
            if (Directory.Exists(current))
            {
                ProjectAccessPathIdentity identity = _identityProvider.Capture(current);
                if (!identity.Exists || !identity.IsDirectory || identity.IsLinkOrReparse) return false;
            }
            if (string.Equals(current, normalizedRoot, StringComparison.Ordinal)) break;
            current = Path.GetDirectoryName(current);
        }
        return string.Equals(current, normalizedRoot, StringComparison.Ordinal);
    }
}

public sealed record ScenePackageOwnedArtifact(string RelativePath, string IdentityToken);

public enum ScenePackageStagingArtifactKind { File, Directory }

public sealed record ScenePackageOwnedStagingArtifact(
    string RelativePath,
    ScenePackageStagingArtifactKind Kind,
    string IdentityToken);

public sealed record ScenePackageStagingArtifactPublication(
    string StagingRelativePath,
    string FinalRelativePath);

public sealed record ScenePackageImportJournal
{
    public ScenePackageImportJournal(
        string operationId,
        string ownerToken,
        string projectIdentityToken,
        string newSceneId,
        string expectedSceneRelativePath,
        string markerIdentityToken,
        IEnumerable<ScenePackageOwnedArtifact> ownedArtifacts,
        IEnumerable<ScenePackageOwnedStagingArtifact> ownedStagingArtifacts,
        IEnumerable<ScenePackageStagingArtifactPublication>? stagingArtifactPublications = null)
    {
        OperationId = operationId;
        OwnerToken = ownerToken;
        ProjectIdentityToken = projectIdentityToken;
        NewSceneId = newSceneId;
        ExpectedSceneRelativePath = expectedSceneRelativePath;
        MarkerIdentityToken = markerIdentityToken;
        OwnedArtifacts = new ReadOnlyCollection<ScenePackageOwnedArtifact>(ownedArtifacts.ToArray());
        OwnedStagingArtifacts = new ReadOnlyCollection<ScenePackageOwnedStagingArtifact>(ownedStagingArtifacts.ToArray());
        StagingArtifactPublications = new ReadOnlyCollection<ScenePackageStagingArtifactPublication>(
            (stagingArtifactPublications ?? Array.Empty<ScenePackageStagingArtifactPublication>()).ToArray());
    }

    public string OperationId { get; }
    public string OwnerToken { get; }
    public string ProjectIdentityToken { get; }
    public string NewSceneId { get; }
    public string ExpectedSceneRelativePath { get; }
    public string MarkerIdentityToken { get; }
    public IReadOnlyList<ScenePackageOwnedArtifact> OwnedArtifacts { get; }
    public IReadOnlyList<ScenePackageOwnedStagingArtifact> OwnedStagingArtifacts { get; }
    public IReadOnlyList<ScenePackageStagingArtifactPublication> StagingArtifactPublications { get; }
}

public interface IScenePackageDurableStateProbe
{
    bool IsSceneDurable(string projectRoot, string sceneId, string expectedSceneRelativePath);
}

public enum ScenePackageReconcileState { Committed, RolledBack, RetainedUnsafe, NotFound }

public sealed record ScenePackageReconcileResult(ScenePackageReconcileState State, string OperationId);
