#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace AppAgasCs.Core.Persistence;

/// <summary>
/// Ordinary canvas image included by a schema-v2 scene package. FilePath is a
/// package-relative POSIX path; it is never an external URL or inline payload.
/// </summary>
public sealed record ScenePackageImage(
    string Id,
    string FilePath,
    int Order,
    double X,
    double Y,
    double ScaleX,
    double ScaleY,
    double RotationDegrees,
    double Opacity);

public enum ScenePackageFileType
{
    Unspecified = 0,
    ReferenceImage = 1,
    CanvasImage = 2,
}

/// <summary>
/// Integrity metadata for one package file. Sha256 is exactly 64 lowercase
/// hexadecimal characters and does not include a scheme prefix.
/// </summary>
public sealed record ScenePackageFileIntegrity(
    string Path,
    long Size,
    string Sha256,
    ScenePackageFileType Type);

public static class ScenePackageIntegrity
{
    public static ScenePackageFileIntegrity Create(
        string path,
        ReadOnlySpan<byte> contents,
        ScenePackageFileType type)
    {
        if (contents.IsEmpty) throw new InvalidDataException("Package files cannot be empty.");
        if (type == ScenePackageFileType.Unspecified || !Enum.IsDefined(type))
            throw new InvalidDataException("Package file type is invalid.");
        return new ScenePackageFileIntegrity(path, contents.Length, ComputeSha256(contents), type);
    }

    public static string ComputeSha256(ReadOnlySpan<byte> contents) =>
        Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant();

    public static void Verify(ScenePackageFileIntegrity expected, ReadOnlySpan<byte> contents)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Size <= 0
            || contents.Length != expected.Size
            || !string.Equals(expected.Sha256, ComputeSha256(contents), StringComparison.Ordinal))
            throw new InvalidDataException($"Package file integrity check failed: {expected.Path}");
    }
}
