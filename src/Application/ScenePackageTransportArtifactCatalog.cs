#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AppAgasCs.Core.Persistence;

namespace AppAgasCs.Application;

internal sealed record ScenePackageTransportArtifact(
    string PackagePath,
    ScenePackageFileType Type,
    ScenePackageFileIntegrity? ExpectedIntegrity)
{
    public bool IsImage => Type is ScenePackageFileType.ReferenceImage or ScenePackageFileType.CanvasImage;
}

/// <summary>
/// Projects both schema versions into the same typed transport inventory. Schema v1
/// has image-only entries without hashes; schema v2 requires complete integrity on import.
/// </summary>
internal static class ScenePackageTransportArtifactCatalog
{
    internal const int MaximumArtifacts = ScenePackageSafetyLimits.MaximumReferenceImages;

    internal static IReadOnlyList<ScenePackageTransportArtifact> ForExport(
        ScenePackageDocument document) => Build(document, requireV2Integrity: false);

    internal static IReadOnlyList<ScenePackageTransportArtifact> ForImport(
        ScenePackageDocument document) => Build(document, requireV2Integrity: true);

    internal static long MaximumBytes(ScenePackageFileType type) => type switch
    {
        ScenePackageFileType.ReferenceImage or ScenePackageFileType.CanvasImage =>
            ScenePackageSafetyLimits.MaximumImageBytes,
        _ => throw new InvalidDataException("Scene package artifact type is invalid."),
    };

    internal static ScenePackageFileIntegrity InspectAndCreateIntegrity(
        string path,
        ScenePackageTransportArtifact artifact,
        IScenePackageImageDecoder imageDecoder)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(imageDecoder);
        FileInfo info = new(path);
        info.Refresh();
        long maximum = MaximumBytes(artifact.Type);
        if (!info.Exists || info.Length <= 0)
            throw new InvalidDataException($"Scene package artifact is empty: {artifact.PackagePath}");
        if (info.Length > maximum) throw new PackageArtifactTooLargeException();

        if (artifact.IsImage)
        {
            ScenePackageImageInspector.Inspect(
                path,
                Path.GetExtension(artifact.PackagePath).ToLowerInvariant(),
                imageDecoder);
        }

        string sha256;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var actual = new ScenePackageFileIntegrity(
            artifact.PackagePath,
            info.Length,
            sha256,
            artifact.Type);
        if (artifact.ExpectedIntegrity is ScenePackageFileIntegrity expected
            && (expected.Type != actual.Type
                || expected.Size != actual.Size
                || !string.Equals(expected.Path, actual.Path, StringComparison.Ordinal)
                || !string.Equals(expected.Sha256, actual.Sha256, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"Scene package artifact integrity check failed: {artifact.PackagePath}");
        }
        return actual;
    }

    private static IReadOnlyList<ScenePackageTransportArtifact> Build(
        ScenePackageDocument document,
        bool requireV2Integrity)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (requireV2Integrity) ScenePackageCodec.ValidateForTransport(document);
        else ScenePackageCodec.Validate(document);

        var declared = new List<(string Path, ScenePackageFileType Type)>();
        foreach (ScenePackageLayer layer in document.Layers)
        {
            declared.AddRange((layer.ReferenceImages ?? Array.Empty<ScenePackageReferenceImage>())
                .Select(reference => (
                    ScenePackageCodec.ReferenceRelativePath(layer.Id, reference.FileName),
                    ScenePackageFileType.ReferenceImage)));
            if (document.SchemaVersion == ScenePackageCodec.CurrentSchemaVersion)
            {
                declared.AddRange((layer.Images ?? Array.Empty<ScenePackageImage>())
                    .Select(image => (image.FilePath, ScenePackageFileType.CanvasImage)));
            }
        }
        if (declared.Count > MaximumArtifacts) throw new PackageArtifactTooLargeException();
        Dictionary<string, ScenePackageFileIntegrity> integrity =
            (document.Integrity ?? Array.Empty<ScenePackageFileIntegrity>())
            .ToDictionary(item => item.Path, StringComparer.Ordinal);
        ScenePackageTransportArtifact[] result = declared
            .Select(item => new ScenePackageTransportArtifact(
                item.Path,
                item.Type,
                integrity.GetValueOrDefault(item.Path)))
            .ToArray();
        if (result.Select(item => item.PackagePath).Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw new InvalidDataException("Scene package artifact paths must be unique.");
        return Array.AsReadOnly(result);
    }
}

internal sealed class PackageArtifactTooLargeException : IOException { }
