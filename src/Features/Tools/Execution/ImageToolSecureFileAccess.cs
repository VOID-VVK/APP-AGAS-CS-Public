#nullable enable

using System;
using System.IO;
using System.Threading;

namespace AppAgasCs.Features.Tools.Execution;

/// <summary>Project-root-bound image I/O using directory-relative handles.</summary>
internal sealed class ImageToolSecureFileAccess
{
    private readonly IImageToolPathPlatform _platform;
    private readonly Action? _beforeCommitMove;
    private readonly Action? _afterCommitMove;

    public ImageToolSecureFileAccess(
        IImageToolPathPlatform? platform = null,
        Action? beforeCommitMove = null,
        Action? afterCommitMove = null)
    {
        _platform = platform ?? new ImageToolSystemPathPlatform();
        _beforeCommitMove = beforeCommitMove;
        _afterCommitMove = afterCommitMove;
    }

    public byte[] ReadImage(string root, string inputPath, long maximumBytes, CancellationToken cancellationToken)
    {
        using IImageToolPathDirectoryLease directory = OpenParent(root, inputPath);
        string name = Path.GetFileName(inputPath);
        using ImageToolOpenedFile opened = directory.OpenExistingRead(name);
        VerifyAtPath(root, inputPath, directory, opened.Entry);
        long length = opened.Stream.Length;
        if (length < 24 || length > maximumBytes)
            throw new ImageToolRunException(ImageToolRunErrorCodes.InputInvalid, "Input image file exceeds the size limit or is incomplete.");
        byte[] bytes = new byte[checked((int)length)];
        opened.Stream.ReadExactly(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (opened.Stream.Length != length)
            throw new ImageToolRunException(ImageToolRunErrorCodes.InputInvalid, "Input image changed while reading.");
        VerifyAtPath(root, inputPath, directory, opened.Entry);
        return bytes;
    }

    public void WritePng(string root, string outputPath, ReadOnlySpan<byte> png, CancellationToken cancellationToken)
    {
        using IImageToolPathDirectoryLease directory = OpenParent(root, outputPath);
        string finalName = Path.GetFileName(outputPath);
        if (directory.InspectEntry(finalName).Exists)
            throw new ImageToolRunException(ImageToolRunErrorCodes.OutputExists, "Output path already exists.");
        string tempName = ".agas-tool-" + Guid.NewGuid().ToString("N") + ".tmp";
        using ImageToolOpenedFile temporary = directory.OpenCreateNew(tempName);
        string identity = temporary.Entry.IdentityToken;
        bool completed = false;
        try
        {
            temporary.Stream.Write(png);
            temporary.Stream.Flush(flushToDisk: true);
            cancellationToken.ThrowIfCancellationRequested();
            VerifyParent(root, outputPath, directory);
            ImageToolPathEntry tempEntry = directory.InspectEntry(tempName);
            ImageToolPathEntry heldEntry = _platform.InspectOpenFile(temporary.Stream);
            if (tempEntry.Kind != ImageToolPathEntryKind.RegularFile
                || heldEntry.Kind != ImageToolPathEntryKind.RegularFile
                || tempEntry.IdentityToken != identity
                || heldEntry.IdentityToken != identity)
                throw PathInvalid("Reserved temporary output changed identity.");
            if (directory.InspectEntry(finalName).Exists)
                throw new ImageToolRunException(ImageToolRunErrorCodes.OutputExists, "Output path already exists.");

            _beforeCommitMove?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            VerifyParent(root, outputPath, directory);
            try { directory.MoveNoOverwrite(temporary, tempName, finalName); }
            catch (IOException exception)
            {
                if (directory.InspectEntry(finalName).Exists)
                    throw new ImageToolRunException(ImageToolRunErrorCodes.OutputExists, "Output appeared before atomic commit.");
                throw PathInvalid($"Atomic output commit failed: {exception.Message}");
            }
            _afterCommitMove?.Invoke();
            ImageToolPathEntry committed = directory.InspectEntry(finalName);
            ImageToolPathEntry heldCommitted = _platform.InspectOpenFile(temporary.Stream);
            if (committed.Kind != ImageToolPathEntryKind.RegularFile
                || committed.IdentityToken != identity
                || heldCommitted.IdentityToken != identity)
                throw PathInvalid("Committed output does not match the reserved file handle.");
            VerifyAtPath(root, outputPath, directory, committed);
            completed = true;
        }
        finally
        {
            try
            {
                if (!completed && directory.InspectEntry(finalName).IdentityToken == identity)
                    directory.DeleteIfIdentityMatches(finalName, identity);
            }
            catch { /* Identity-bound cleanup is best-effort. */ }
            try { directory.DeleteIfIdentityMatches(tempName, identity); }
            catch { /* Preserve the primary failure. */ }
        }
    }

    private IImageToolPathDirectoryLease OpenParent(string root, string path)
    {
        string parent = Path.GetDirectoryName(path)
            ?? throw PathInvalid("Image parent directory is missing.");
        IImageToolPathDirectoryLease directory;
        try { directory = _platform.OpenDirectoryLease(root, parent); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            throw PathInvalid($"Image parent directory could not be opened safely: {exception.Message}");
        }
        try
        {
            VerifyParent(root, path, directory);
            return directory;
        }
        catch
        {
            directory.Dispose();
            throw;
        }
    }

    private void VerifyAtPath(
        string root,
        string path,
        IImageToolPathDirectoryLease directory,
        ImageToolPathEntry expected)
    {
        VerifyParent(root, path, directory);
        ImageToolPathEntry actual = _platform.InspectEntry(path);
        if (actual.Kind != ImageToolPathEntryKind.RegularFile
            || actual.IdentityToken != expected.IdentityToken)
            throw PathInvalid("Image path changed identity during I/O.");
    }

    private void VerifyParent(string root, string path, IImageToolPathDirectoryLease directory)
    {
        foreach (ImageToolDirectoryIdentity ancestor in directory.Ancestors)
        {
            ImageToolPathEntry actual = _platform.InspectEntry(ancestor.AbsolutePath);
            if (actual.Kind != ImageToolPathEntryKind.Directory
                || actual.IdentityToken != ancestor.IdentityToken)
                throw PathInvalid("Project image directory ancestry changed identity during I/O.");
        }
    }

    private static ImageToolRunException PathInvalid(string message) =>
        new(ImageToolRunErrorCodes.PathInvalid, message);
}
