#nullable enable

using System;
using System.Collections.Generic;
using System.IO;

namespace AppAgasCs.Features.Tools.Execution;

public enum ImageToolPathEntryKind
{
    Missing,
    RegularFile,
    Directory,
    SymlinkOrReparsePoint,
    Other,
}

public readonly record struct ImageToolPathEntry(ImageToolPathEntryKind Kind, string IdentityToken)
{
    public bool Exists => Kind != ImageToolPathEntryKind.Missing;
}

public readonly record struct ImageToolDirectoryIdentity(string AbsolutePath, string IdentityToken);

public interface IImageToolPathPlatform
{
    StringComparison PathComparison { get; }
    char DirectorySeparatorChar { get; }
    char AltDirectorySeparatorChar { get; }
    string Canonicalize(string path);
    string GetRelativePath(string relativeTo, string path);
    bool IsPathRooted(string path);
    bool IsPathFullyQualified(string path);
    bool ContainsInvalidPathChars(string path);
    ImageToolPathEntry InspectEntry(string path);
    ImageToolPathEntry InspectOpenFile(FileStream stream);
    FileStream OpenCreateNew(string path);
    void MoveNoOverwrite(string sourcePath, string destinationPath);
    void DeleteFile(string path);
    IImageToolPathDirectoryLease OpenDirectoryLease(string path);
    IImageToolPathDirectoryLease OpenDirectoryLease(string trustedRoot, string path);
    IImageToolPathDirectoryLease EnsureDirectoryLease(string trustedRoot, string path, string expectedRootIdentityToken);
}

public interface IImageToolPathDirectoryLease : IDisposable
{
    ImageToolPathEntry RootEntry { get; }
    ImageToolPathEntry Entry { get; }
    IReadOnlyList<ImageToolDirectoryIdentity> Ancestors { get; }
    ImageToolOpenedFile OpenCreateNew(string fileName);
    ImageToolOpenedFile OpenExistingRead(string fileName);
    ImageToolPathEntry InspectEntry(string fileName);
    IReadOnlyList<string> EnumerateEntryNames();
    void MoveNoOverwrite(ImageToolOpenedFile source, string sourceFileName, string destinationFileName);
    bool TryReplaceIfIdentityMatches(
        ImageToolOpenedFile source,
        string sourceFileName,
        string destinationFileName,
        string expectedDestinationIdentityToken);
    bool ReconcileAtomicReplace(string fileName, out string error);
    bool FinalizeAtomicReplace(
        string sourceFileName,
        string destinationFileName,
        string newIdentityToken,
        string oldIdentityToken,
        out string error);
    bool RollbackAtomicReplace(
        string sourceFileName,
        string destinationFileName,
        string newIdentityToken,
        string oldIdentityToken,
        out string error);
    void DeleteIfIdentityMatches(string fileName, string identityToken);
}

public sealed class ImageToolOpenedFile : IDisposable
{
    internal ImageToolOpenedFile(FileStream stream, ImageToolPathEntry entry)
    {
        Stream = stream;
        Entry = entry;
    }

    public FileStream Stream { get; }
    public ImageToolPathEntry Entry { get; }
    public void Dispose() => Stream.Dispose();
}
