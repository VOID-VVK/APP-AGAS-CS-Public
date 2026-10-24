#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AppAgasCs.Features.Tools.Execution;

public sealed class ImageToolSystemPathPlatform : IImageToolPathPlatform
{
    private readonly Action<string>? _directorySegmentOpenedHook;

    public ImageToolSystemPathPlatform()
    {
    }

    internal ImageToolSystemPathPlatform(Action<string> directorySegmentOpenedHook)
    {
        _directorySegmentOpenedHook = directorySegmentOpenedHook;
    }

    public StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public char DirectorySeparatorChar => Path.DirectorySeparatorChar;
    public char AltDirectorySeparatorChar => Path.AltDirectorySeparatorChar;

    public string Canonicalize(string path) => Path.GetFullPath(path);
    public string GetRelativePath(string relativeTo, string path) => Path.GetRelativePath(relativeTo, path);
    public bool IsPathRooted(string path) => Path.IsPathRooted(path);
    public bool IsPathFullyQualified(string path) => Path.IsPathFullyQualified(path);
    public bool ContainsInvalidPathChars(string path) => path.IndexOfAny(Path.GetInvalidPathChars()) >= 0;
    public IImageToolPathDirectoryLease OpenDirectoryLease(string path) =>
        OperatingSystem.IsWindows()
            ? new WindowsDirectoryLease(path, path, _directorySegmentOpenedHook)
            : new UnixDirectoryLease(path, path, _directorySegmentOpenedHook);
    public IImageToolPathDirectoryLease OpenDirectoryLease(string trustedRoot, string path) =>
        OperatingSystem.IsWindows()
            ? new WindowsDirectoryLease(trustedRoot, path, _directorySegmentOpenedHook)
            : new UnixDirectoryLease(trustedRoot, path, _directorySegmentOpenedHook);
    public IImageToolPathDirectoryLease EnsureDirectoryLease(string trustedRoot, string path, string expectedRootIdentityToken) =>
        OperatingSystem.IsWindows()
            ? new WindowsDirectoryLease(trustedRoot, path, _directorySegmentOpenedHook, expectedRootIdentityToken, createMissing: true)
            : new UnixDirectoryLease(trustedRoot, path, _directorySegmentOpenedHook, createMissing: true, expectedRootIdentityToken);
    public FileStream OpenCreateNew(string path) =>
        new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
    public void MoveNoOverwrite(string sourcePath, string destinationPath) => File.Move(sourcePath, destinationPath, overwrite: false);
    public void DeleteFile(string path) => File.Delete(path);

    public ImageToolPathEntry InspectEntry(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return InspectUnixPath(path);
        }
        using SafeFileHandle handle = NativeMethods.CreateFileW(
            path,
            NativeMethods.FileReadAttributes,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return InspectWindowsHandle(handle);
        }
        int error = Marshal.GetLastPInvokeError();
        return error is 2 or 3
            ? new ImageToolPathEntry(ImageToolPathEntryKind.Missing, string.Empty)
            : new ImageToolPathEntry(ImageToolPathEntryKind.Other, $"open-handle-error:{error}");
    }

    public ImageToolPathEntry InspectOpenFile(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!OperatingSystem.IsWindows())
        {
            return InspectUnixDescriptor(stream.SafeFileHandle.DangerousGetHandle().ToInt32());
        }
        return InspectWindowsHandle(stream.SafeFileHandle);
    }

    private static ImageToolPathEntry InspectUnixPath(string path)
    {
        IntPtr buffer = Marshal.AllocHGlobal(512);
        try
        {
            int result = OperatingSystem.IsMacOS()
                ? NativeMethods.MacLStat(path, buffer)
                : NativeMethods.LStat(path, buffer);
            if (result != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                return error is 2 or 20
                    ? new ImageToolPathEntry(ImageToolPathEntryKind.Missing, string.Empty)
                    : new ImageToolPathEntry(ImageToolPathEntryKind.Other, $"lstat-error:{error}");
            }
            return ReadUnixStat(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ImageToolPathEntry InspectUnixDescriptor(int descriptor)
    {
        IntPtr buffer = Marshal.AllocHGlobal(512);
        try
        {
            int result = OperatingSystem.IsMacOS()
                ? NativeMethods.MacFStat(descriptor, buffer)
                : NativeMethods.FStat(descriptor, buffer);
            if (result != 0)
            {
                return new ImageToolPathEntry(ImageToolPathEntryKind.Other, $"fstat-error:{Marshal.GetLastPInvokeError()}");
            }
            return ReadUnixStat(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ImageToolPathEntry InspectWindowsHandle(SafeFileHandle handle)
    {
        if (!NativeMethods.GetFileInformationByHandle(handle, out NativeMethods.ByHandleFileInformation information))
        {
            return new ImageToolPathEntry(ImageToolPathEntryKind.Other, $"handle-info-error:{Marshal.GetLastPInvokeError()}");
        }
        if (!NativeMethods.GetFileInformationByHandleEx(
                handle,
                NativeMethods.FileInfoByHandleClass.FileAttributeTagInfo,
                out NativeMethods.FileAttributeTagInformation tagInformation,
                (uint)Marshal.SizeOf<NativeMethods.FileAttributeTagInformation>()))
        {
            return new ImageToolPathEntry(ImageToolPathEntryKind.Other, $"handle-tag-error:{Marshal.GetLastPInvokeError()}");
        }

        const uint FileAttributeDirectory = 0x10;
        const uint FileAttributeReparsePoint = 0x400;
        ImageToolPathEntryKind kind = (tagInformation.FileAttributes & FileAttributeReparsePoint) != 0
            ? ImageToolPathEntryKind.SymlinkOrReparsePoint
            : (tagInformation.FileAttributes & FileAttributeDirectory) != 0
                ? ImageToolPathEntryKind.Directory
                : ImageToolPathEntryKind.RegularFile;
        ulong fileId = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        string identity = $"windows:{information.VolumeSerialNumber}:{fileId}:{tagInformation.ReparseTag}";
        return new ImageToolPathEntry(kind, identity);
    }

    private sealed class UnixDirectoryLease : IImageToolPathDirectoryLease
    {
        private readonly SafeFileHandle _handle;
        private readonly int _descriptor;

        internal UnixDirectoryLease(
            string trustedRoot,
            string path,
            Action<string>? directorySegmentOpenedHook,
            bool createMissing = false,
            string expectedRootIdentityToken = "")
        {
            int flags = OperatingSystem.IsMacOS()
                ? NativeMethods.MacOpenDirectoryFlags
                : NativeMethods.LinuxOpenDirectoryFlags;
            int descriptor = NativeMethods.Open(trustedRoot, flags);
            if (descriptor < 0)
            {
                throw CreateIOException("Unable to open trusted project root", Marshal.GetLastPInvokeError());
            }
            SafeFileHandle currentHandle = new(new IntPtr(descriptor), ownsHandle: true);
            RootEntry = InspectUnixDescriptor(descriptor);
            if (RootEntry.Kind != ImageToolPathEntryKind.Directory || string.IsNullOrEmpty(RootEntry.IdentityToken))
            {
                currentHandle.Dispose();
                throw new IOException("Trusted project root handle is not an ordinary directory.");
            }
            if (createMissing && (string.IsNullOrEmpty(expectedRootIdentityToken)
                || !string.Equals(RootEntry.IdentityToken, expectedRootIdentityToken, StringComparison.Ordinal)))
            {
                currentHandle.Dispose();
                throw new IOException("Trusted project root identity changed before directory creation.");
            }

            var ancestors = new List<ImageToolDirectoryIdentity>
            {
                new(trustedRoot, RootEntry.IdentityToken),
            };
            string currentPath = trustedRoot;

            try
            {
                foreach (string segment in GetRelativeDirectorySegments(trustedRoot, path))
                {
                    int nextDescriptor = NativeMethods.OpenAt(descriptor, segment, flags, 0);
                    if (nextDescriptor < 0 && createMissing && Marshal.GetLastPInvokeError() == 2)
                    {
                        int createResult = NativeMethods.MkdirAt(descriptor, segment, Convert.ToUInt32("700", 8));
                        if (createResult != 0 && Marshal.GetLastPInvokeError() != 17)
                        {
                            throw CreateIOException("Unable to create project directory segment safely", Marshal.GetLastPInvokeError());
                        }
                        nextDescriptor = NativeMethods.OpenAt(descriptor, segment, flags, 0);
                    }
                    if (nextDescriptor < 0)
                    {
                        throw CreateIOException("Unable to open output directory segment without following links", Marshal.GetLastPInvokeError());
                    }
                    var nextHandle = new SafeFileHandle(new IntPtr(nextDescriptor), ownsHandle: true);
                    ImageToolPathEntry nextEntry = InspectUnixDescriptor(nextDescriptor);
                    if (nextEntry.Kind != ImageToolPathEntryKind.Directory || string.IsNullOrEmpty(nextEntry.IdentityToken))
                    {
                        nextHandle.Dispose();
                        throw new IOException("An output directory segment is not an ordinary directory.");
                    }
                    currentHandle.Dispose();
                    currentHandle = nextHandle;
                    descriptor = nextDescriptor;
                    currentPath = Path.Combine(currentPath, segment);
                    ancestors.Add(new ImageToolDirectoryIdentity(currentPath, nextEntry.IdentityToken));
                    directorySegmentOpenedHook?.Invoke(segment);
                }
            }
            catch
            {
                currentHandle.Dispose();
                throw;
            }

            _descriptor = descriptor;
            _handle = currentHandle;
            Entry = InspectUnixDescriptor(descriptor);
            Ancestors = Array.AsReadOnly(ancestors.ToArray());
            if (Entry.Kind != ImageToolPathEntryKind.Directory || string.IsNullOrEmpty(Entry.IdentityToken))
            {
                _handle.Dispose();
                throw new IOException("Output parent handle is not an ordinary directory.");
            }
        }

        public ImageToolPathEntry RootEntry { get; }
        public ImageToolPathEntry Entry { get; }
        public IReadOnlyList<ImageToolDirectoryIdentity> Ancestors { get; }

        public ImageToolOpenedFile OpenCreateNew(string fileName)
        {
            EnsureSimpleName(fileName);
            int flags = OperatingSystem.IsMacOS()
                ? NativeMethods.MacCreateFileFlags
                : NativeMethods.LinuxCreateFileFlags;
            int descriptor = NativeMethods.OpenAt(_descriptor, fileName, flags, Convert.ToUInt32("600", 8));
            if (descriptor < 0)
            {
                throw CreateIOException("Unable to create reserved output", Marshal.GetLastPInvokeError());
            }
            var safeHandle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
            ImageToolPathEntry entry = new(ImageToolPathEntryKind.Other, string.Empty);
            try
            {
                entry = InspectUnixDescriptor(descriptor);
                if (entry.Kind != ImageToolPathEntryKind.RegularFile || string.IsNullOrEmpty(entry.IdentityToken))
                {
                    throw new IOException("Reserved output handle is not a regular file.");
                }
                return new ImageToolOpenedFile(new FileStream(safeHandle, FileAccess.Write, 64 * 1024, isAsync: false), entry);
            }
            catch
            {
                safeHandle.Dispose();
                if (entry.Kind == ImageToolPathEntryKind.RegularFile && !string.IsNullOrEmpty(entry.IdentityToken))
                {
                    DeleteIfIdentityMatches(fileName, entry.IdentityToken);
                }
                throw;
            }
        }

        public ImageToolOpenedFile OpenExistingRead(string fileName)
        {
            EnsureSimpleName(fileName);
            int flags = OperatingSystem.IsMacOS()
                ? NativeMethods.MacOpenReadFileFlags
                : NativeMethods.LinuxOpenReadFileFlags;
            int descriptor = NativeMethods.OpenAt(_descriptor, fileName, flags, 0);
            if (descriptor < 0)
            {
                throw CreateIOException("Unable to open input file without following links", Marshal.GetLastPInvokeError());
            }
            var safeHandle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
            try
            {
                ImageToolPathEntry entry = InspectUnixDescriptor(descriptor);
                if (entry.Kind != ImageToolPathEntryKind.RegularFile || string.IsNullOrEmpty(entry.IdentityToken))
                {
                    throw new IOException("Input handle is not a regular file.");
                }
                return new ImageToolOpenedFile(new FileStream(safeHandle, FileAccess.Read, 64 * 1024, isAsync: false), entry);
            }
            catch
            {
                safeHandle.Dispose();
                throw;
            }
        }

        public ImageToolPathEntry InspectEntry(string fileName)
        {
            EnsureSimpleName(fileName);
            IntPtr buffer = Marshal.AllocHGlobal(512);
            try
            {
                int result = OperatingSystem.IsMacOS()
                    ? NativeMethods.MacFStatAt(_descriptor, fileName, buffer, NativeMethods.MacAtSymlinkNoFollow)
                    : NativeMethods.FStatAt(_descriptor, fileName, buffer, NativeMethods.LinuxAtSymlinkNoFollow);
                if (result != 0)
                {
                    int error = Marshal.GetLastPInvokeError();
                    return error is 2 or 20
                        ? new ImageToolPathEntry(ImageToolPathEntryKind.Missing, string.Empty)
                        : new ImageToolPathEntry(ImageToolPathEntryKind.Other, $"fstatat-error:{error}");
                }
                return ReadUnixStat(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public IReadOnlyList<string> EnumerateEntryNames()
        {
            int flags = OperatingSystem.IsMacOS()
                ? NativeMethods.MacOpenDirectoryFlags
                : NativeMethods.LinuxOpenDirectoryFlags;
            int enumerationDescriptor = NativeMethods.OpenAt(_descriptor, ".", flags, 0);
            if (enumerationDescriptor < 0)
            {
                throw CreateIOException("Unable to duplicate directory for safe enumeration", Marshal.GetLastPInvokeError());
            }
            IntPtr directory = OperatingSystem.IsMacOS()
                ? NativeMethods.MacFdOpenDirectory(enumerationDescriptor)
                : NativeMethods.FdOpenDirectory(enumerationDescriptor);
            if (directory == IntPtr.Zero)
            {
                int error = Marshal.GetLastPInvokeError();
                NativeMethods.Close(enumerationDescriptor);
                throw CreateIOException("Unable to enumerate directory handle", error);
            }
            var names = new List<string>();
            try
            {
                while (true)
                {
                    Marshal.SetLastPInvokeError(0);
                    IntPtr entry = OperatingSystem.IsMacOS()
                        ? NativeMethods.MacReadDirectory(directory)
                        : NativeMethods.ReadDirectory(directory);
                    if (entry == IntPtr.Zero)
                    {
                        break;
                    }
                    int nameOffset = OperatingSystem.IsMacOS() ? 21 : 19;
                    string? name = Marshal.PtrToStringUTF8(IntPtr.Add(entry, nameOffset));
                    if (!string.IsNullOrEmpty(name) && name is not "." and not "..") names.Add(name);
                }
                return Array.AsReadOnly(names.ToArray());
            }
            finally
            {
                NativeMethods.CloseDirectory(directory);
            }
        }

        public void MoveNoOverwrite(ImageToolOpenedFile source, string sourceFileName, string destinationFileName)
        {
            ArgumentNullException.ThrowIfNull(source);
            EnsureSimpleName(sourceFileName);
            EnsureSimpleName(destinationFileName);
            ImageToolPathEntry sourceName = InspectEntry(sourceFileName);
            ImageToolPathEntry sourceHandle = InspectUnixDescriptor(source.Stream.SafeFileHandle.DangerousGetHandle().ToInt32());
            if (sourceName.Kind != ImageToolPathEntryKind.RegularFile
                || sourceHandle.Kind != ImageToolPathEntryKind.RegularFile
                || sourceName.IdentityToken != source.Entry.IdentityToken
                || sourceHandle.IdentityToken != source.Entry.IdentityToken)
            {
                throw new IOException("Reserved output changed identity before atomic commit.");
            }
            int result = OperatingSystem.IsMacOS()
                ? NativeMethods.RenameAtExclusiveMac(_descriptor, sourceFileName, _descriptor, destinationFileName,
                    NativeMethods.MacRenameExclusive | NativeMethods.MacRenameNoFollowAny)
                : NativeMethods.RenameAtNoReplaceLinux(_descriptor, sourceFileName, _descriptor, destinationFileName,
                    NativeMethods.LinuxRenameNoReplace);
            if (result != 0)
            {
                throw CreateIOException("Atomic no-overwrite output move failed", Marshal.GetLastPInvokeError());
            }
        }

        public bool TryReplaceIfIdentityMatches(
            ImageToolOpenedFile source,
            string sourceFileName,
            string destinationFileName,
            string expectedDestinationIdentityToken)
        {
            ArgumentNullException.ThrowIfNull(source);
            EnsureSimpleName(sourceFileName);
            EnsureSimpleName(destinationFileName);
            if (string.IsNullOrEmpty(expectedDestinationIdentityToken)) return false;

            int exchanged = OperatingSystem.IsMacOS()
                ? NativeMethods.RenameAtExclusiveMac(
                    _descriptor,
                    sourceFileName,
                    _descriptor,
                    destinationFileName,
                    NativeMethods.MacRenameSwap | NativeMethods.MacRenameNoFollowAny)
                : NativeMethods.RenameAtNoReplaceLinux(
                    _descriptor,
                    sourceFileName,
                    _descriptor,
                    destinationFileName,
                    NativeMethods.LinuxRenameExchange);
            if (exchanged != 0)
            {
                throw CreateIOException("Atomic identity-checked exchange failed", Marshal.GetLastPInvokeError());
            }

            ImageToolPathEntry previousDestination;
            try
            {
                previousDestination = InspectEntry(sourceFileName);
            }
            catch
            {
                int emergencyRollback = OperatingSystem.IsMacOS()
                    ? NativeMethods.RenameAtExclusiveMac(
                        _descriptor,
                        sourceFileName,
                        _descriptor,
                        destinationFileName,
                        NativeMethods.MacRenameSwap | NativeMethods.MacRenameNoFollowAny)
                    : NativeMethods.RenameAtNoReplaceLinux(
                        _descriptor,
                        sourceFileName,
                        _descriptor,
                        destinationFileName,
                        NativeMethods.LinuxRenameExchange);
                if (emergencyRollback != 0)
                {
                    throw CreateIOException("Atomic identity inspection and rollback failed", Marshal.GetLastPInvokeError());
                }
                throw;
            }
            if (previousDestination.Kind == ImageToolPathEntryKind.RegularFile
                && string.Equals(previousDestination.IdentityToken, expectedDestinationIdentityToken, StringComparison.Ordinal))
            {
                return true;
            }

            int rolledBack = OperatingSystem.IsMacOS()
                ? NativeMethods.RenameAtExclusiveMac(
                    _descriptor,
                    sourceFileName,
                    _descriptor,
                    destinationFileName,
                    NativeMethods.MacRenameSwap | NativeMethods.MacRenameNoFollowAny)
                : NativeMethods.RenameAtNoReplaceLinux(
                    _descriptor,
                    sourceFileName,
                    _descriptor,
                    destinationFileName,
                    NativeMethods.LinuxRenameExchange);
            if (rolledBack != 0)
            {
                throw CreateIOException("Atomic identity mismatch rollback failed", Marshal.GetLastPInvokeError());
            }
            return false;
        }

        public bool ReconcileAtomicReplace(string fileName, out string error)
        {
            EnsureSimpleName(fileName);
            error = string.Empty;
            return true;
        }

        public bool FinalizeAtomicReplace(
            string sourceFileName,
            string destinationFileName,
            string newIdentityToken,
            string oldIdentityToken,
            out string error)
        {
            error = string.Empty;
            ImageToolPathEntry source = InspectEntry(sourceFileName);
            ImageToolPathEntry destination = InspectEntry(destinationFileName);
            if (source.Kind == ImageToolPathEntryKind.RegularFile
                && destination.Kind == ImageToolPathEntryKind.RegularFile
                && string.Equals(source.IdentityToken, oldIdentityToken, StringComparison.Ordinal)
                && string.Equals(destination.IdentityToken, newIdentityToken, StringComparison.Ordinal)) return true;
            error = "Atomic replace identities changed before finalize.";
            return false;
        }

        public bool RollbackAtomicReplace(
            string sourceFileName,
            string destinationFileName,
            string newIdentityToken,
            string oldIdentityToken,
            out string error)
        {
            error = string.Empty;
            ImageToolPathEntry source = InspectEntry(sourceFileName);
            ImageToolPathEntry destination = InspectEntry(destinationFileName);
            if (source.Kind != ImageToolPathEntryKind.RegularFile
                || destination.Kind != ImageToolPathEntryKind.RegularFile
                || !string.Equals(source.IdentityToken, oldIdentityToken, StringComparison.Ordinal)
                || !string.Equals(destination.IdentityToken, newIdentityToken, StringComparison.Ordinal))
            {
                error = "Atomic replace identities changed before rollback.";
                return false;
            }

            int rolledBack = OperatingSystem.IsMacOS()
                ? NativeMethods.RenameAtExclusiveMac(
                    _descriptor, sourceFileName, _descriptor, destinationFileName,
                    NativeMethods.MacRenameSwap | NativeMethods.MacRenameNoFollowAny)
                : NativeMethods.RenameAtNoReplaceLinux(
                    _descriptor, sourceFileName, _descriptor, destinationFileName,
                    NativeMethods.LinuxRenameExchange);
            if (rolledBack != 0)
            {
                error = $"Atomic replace rollback failed with native error {Marshal.GetLastPInvokeError()}.";
                return false;
            }
            ImageToolPathEntry restored = InspectEntry(destinationFileName);
            if (restored.Kind == ImageToolPathEntryKind.RegularFile
                && string.Equals(restored.IdentityToken, oldIdentityToken, StringComparison.Ordinal)) return true;
            error = "Atomic replace rollback restored an unexpected identity.";
            return false;
        }

        public void DeleteIfIdentityMatches(string fileName, string identityToken)
        {
            EnsureSimpleName(fileName);
            if (string.IsNullOrEmpty(identityToken)) return;

            ImageToolPathEntry existing = InspectEntry(fileName);
            if (existing.Kind != ImageToolPathEntryKind.RegularFile
                || existing.IdentityToken != identityToken) return;

            string quarantine = $".{fileName}.{Guid.NewGuid():N}.cleanup";
            try
            {
                MoveNameNoOverwrite(fileName, quarantine);
                ImageToolPathEntry moved = InspectEntry(quarantine);
                if (moved.Kind == ImageToolPathEntryKind.RegularFile
                    && string.Equals(moved.IdentityToken, identityToken, StringComparison.Ordinal))
                {
                    if (NativeMethods.UnlinkAt(_descriptor, quarantine, 0) != 0)
                    {
                        throw CreateIOException("Unable to remove reserved output", Marshal.GetLastPInvokeError());
                    }
                    return;
                }

                try { MoveNameNoOverwrite(quarantine, fileName); } catch { }
            }
            catch
            {
                // Cleanup is best-effort. Never unlink a name whose moved identity did not match.
            }
        }

        private void MoveNameNoOverwrite(string sourceFileName, string destinationFileName)
        {
            int result = OperatingSystem.IsMacOS()
                ? NativeMethods.RenameAtExclusiveMac(_descriptor, sourceFileName, _descriptor, destinationFileName,
                    NativeMethods.MacRenameExclusive | NativeMethods.MacRenameNoFollowAny)
                : NativeMethods.RenameAtNoReplaceLinux(_descriptor, sourceFileName, _descriptor, destinationFileName,
                    NativeMethods.LinuxRenameNoReplace);
            if (result != 0) throw CreateIOException("Directory-relative move failed", Marshal.GetLastPInvokeError());
        }

        public void Dispose() => _handle.Dispose();
    }

    private sealed class WindowsDirectoryLease : IImageToolPathDirectoryLease
    {
        private readonly SafeFileHandle _handle;
        private PendingWindowsReplace? _pendingReplace;

        internal WindowsDirectoryLease(
            string trustedRoot,
            string path,
            Action<string>? directorySegmentOpenedHook,
            string expectedRootIdentityToken = "",
            bool createMissing = false)
        {
            SafeFileHandle currentHandle = NativeMethods.CreateFileW(
                trustedRoot,
                NativeMethods.FileReadAttributes,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (currentHandle.IsInvalid) throw CreateIOException("Unable to open trusted project root", Marshal.GetLastPInvokeError());
            RootEntry = InspectWindowsHandle(currentHandle);
            if (RootEntry.Kind != ImageToolPathEntryKind.Directory || string.IsNullOrEmpty(RootEntry.IdentityToken))
            {
                currentHandle.Dispose();
                throw new IOException("Trusted project root handle is not an ordinary directory.");
            }
            if (!string.IsNullOrEmpty(expectedRootIdentityToken)
                && !string.Equals(RootEntry.IdentityToken, expectedRootIdentityToken, StringComparison.Ordinal))
            {
                currentHandle.Dispose();
                throw new IOException("Trusted project root identity changed before directory access.");
            }

            var ancestors = new List<ImageToolDirectoryIdentity>
            {
                new(trustedRoot, RootEntry.IdentityToken),
            };
            string currentPath = trustedRoot;

            try
            {
                foreach (string segment in GetRelativeDirectorySegments(trustedRoot, path))
                {
                    SafeFileHandle nextHandle;
                    try
                    {
                        nextHandle = OpenRelativeHandle(currentHandle, segment, create: false, requestDelete: false, requireDirectory: true, requestReadData: false);
                    }
                    catch (Win32Exception exception) when (createMissing && exception.NativeErrorCode is 2 or 3)
                    {
                        try
                        {
                            nextHandle = OpenRelativeHandle(currentHandle, segment, create: true, requestDelete: false, requireDirectory: true, requestReadData: false);
                        }
                        catch (Win32Exception createException) when (createException.NativeErrorCode is 80 or 183)
                        {
                            nextHandle = OpenRelativeHandle(currentHandle, segment, create: false, requestDelete: false, requireDirectory: true, requestReadData: false);
                        }
                    }
                    ImageToolPathEntry nextEntry = InspectWindowsHandle(nextHandle);
                    if (nextEntry.Kind != ImageToolPathEntryKind.Directory || string.IsNullOrEmpty(nextEntry.IdentityToken))
                    {
                        nextHandle.Dispose();
                        throw new IOException("An output directory segment is not an ordinary directory.");
                    }
                    currentHandle.Dispose();
                    currentHandle = nextHandle;
                    currentPath = Path.Combine(currentPath, segment);
                    ancestors.Add(new ImageToolDirectoryIdentity(currentPath, nextEntry.IdentityToken));
                    directorySegmentOpenedHook?.Invoke(segment);
                }
            }
            catch
            {
                currentHandle.Dispose();
                throw;
            }

            _handle = currentHandle;
            Entry = InspectWindowsHandle(_handle);
            Ancestors = Array.AsReadOnly(ancestors.ToArray());
            if (Entry.Kind != ImageToolPathEntryKind.Directory || string.IsNullOrEmpty(Entry.IdentityToken))
            {
                _handle.Dispose();
                throw new IOException("Output parent handle is not an ordinary directory.");
            }
        }

        public ImageToolPathEntry RootEntry { get; }
        public ImageToolPathEntry Entry { get; }
        public IReadOnlyList<ImageToolDirectoryIdentity> Ancestors { get; }

        public ImageToolOpenedFile OpenCreateNew(string fileName)
        {
            EnsureSimpleName(fileName);
            SafeFileHandle handle = OpenRelativeHandle(_handle, fileName, create: true, requestDelete: false, requireDirectory: false, requestReadData: false);
            ImageToolPathEntry entry = new(ImageToolPathEntryKind.Other, string.Empty);
            try
            {
                entry = InspectWindowsHandle(handle);
                if (entry.Kind != ImageToolPathEntryKind.RegularFile || string.IsNullOrEmpty(entry.IdentityToken))
                {
                    throw new IOException("Reserved output handle is not a regular file.");
                }
                return new ImageToolOpenedFile(new FileStream(handle, FileAccess.Write, 64 * 1024, isAsync: false), entry);
            }
            catch
            {
                handle.Dispose();
                if (entry.Kind == ImageToolPathEntryKind.RegularFile && !string.IsNullOrEmpty(entry.IdentityToken))
                {
                    DeleteIfIdentityMatches(fileName, entry.IdentityToken);
                }
                throw;
            }
        }

        public ImageToolOpenedFile OpenExistingRead(string fileName)
        {
            EnsureSimpleName(fileName);
            SafeFileHandle handle = OpenRelativeHandle(
                _handle,
                fileName,
                create: false,
                requestDelete: false,
                requireDirectory: false,
                requestReadData: true);
            try
            {
                ImageToolPathEntry entry = InspectWindowsHandle(handle);
                if (entry.Kind != ImageToolPathEntryKind.RegularFile || string.IsNullOrEmpty(entry.IdentityToken))
                {
                    throw new IOException("Input handle is not a regular file.");
                }
                return new ImageToolOpenedFile(new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: false), entry);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public ImageToolPathEntry InspectEntry(string fileName)
        {
            EnsureSimpleName(fileName);
            SafeFileHandle? handle = null;
            try
            {
                handle = OpenRelativeHandle(_handle, fileName, create: false, requestDelete: false, requireDirectory: false, requestReadData: false);
                return InspectWindowsHandle(handle);
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
            {
                return new ImageToolPathEntry(ImageToolPathEntryKind.Missing, string.Empty);
            }
            finally
            {
                handle?.Dispose();
            }
        }

        public IReadOnlyList<string> EnumerateEntryNames()
        {
            const int bufferSize = 64 * 1024;
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            var names = new List<string>();
            bool restartScan = true;
            try
            {
                while (true)
                {
                    int status = NativeMethods.NtQueryDirectoryFile(
                        _handle,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        out NativeMethods.IoStatusBlock ioStatus,
                        buffer,
                        (uint)bufferSize,
                        NativeMethods.FileInformationClass.FileIdBothDirectoryInformation,
                        returnSingleEntry: false,
                        IntPtr.Zero,
                        restartScan);
                    restartScan = false;
                    if (status == NativeMethods.StatusNoMoreFiles) break;
                    if (status < 0)
                    {
                        throw new Win32Exception(unchecked((int)NativeMethods.RtlNtStatusToDosError(status)));
                    }

                    int available = checked((int)ioStatus.Information.ToUInt64());
                    int offset = 0;
                    while (offset < available)
                    {
                        if (available - offset < NativeMethods.FileIdBothDirectoryNameOffset)
                        {
                            throw new IOException("Directory enumeration returned a truncated entry.");
                        }
                        IntPtr entry = IntPtr.Add(buffer, offset);
                        uint nextOffset = unchecked((uint)Marshal.ReadInt32(entry, 0));
                        uint nameBytes = unchecked((uint)Marshal.ReadInt32(entry, 60));
                        if ((nameBytes & 1) != 0
                            || nameBytes > available - offset - NativeMethods.FileIdBothDirectoryNameOffset)
                        {
                            throw new IOException("Directory enumeration returned an invalid file name.");
                        }
                        string name = Marshal.PtrToStringUni(
                            IntPtr.Add(entry, NativeMethods.FileIdBothDirectoryNameOffset),
                            checked((int)nameBytes / 2)) ?? string.Empty;
                        if (name is not "" and not "." and not "..") names.Add(name);
                        if (nextOffset == 0) break;
                        if (nextOffset > available - offset) throw new IOException("Directory enumeration returned an invalid next-entry offset.");
                        offset = checked(offset + (int)nextOffset);
                    }
                }
                return Array.AsReadOnly(names.ToArray());
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void MoveNoOverwrite(ImageToolOpenedFile source, string sourceFileName, string destinationFileName)
        {
            ArgumentNullException.ThrowIfNull(source);
            EnsureSimpleName(sourceFileName);
            EnsureSimpleName(destinationFileName);
            RenameHandle(source.Stream.SafeFileHandle, destinationFileName);
        }

        public bool TryReplaceIfIdentityMatches(
            ImageToolOpenedFile source,
            string sourceFileName,
            string destinationFileName,
            string expectedDestinationIdentityToken)
        {
            ArgumentNullException.ThrowIfNull(source);
            EnsureSimpleName(sourceFileName);
            EnsureSimpleName(destinationFileName);
            if (string.IsNullOrEmpty(expectedDestinationIdentityToken)
                || !ReconcileAtomicReplace(destinationFileName, out _)) return false;

            ImageToolPathEntry sourceEntry = InspectWindowsHandle(source.Stream.SafeFileHandle);
            if (sourceEntry.Kind != ImageToolPathEntryKind.RegularFile || string.IsNullOrEmpty(sourceEntry.IdentityToken)) return false;
            string backupName = BuildAtomicBackupName(destinationFileName, expectedDestinationIdentityToken, sourceEntry.IdentityToken);
            SafeFileHandle? previousHandle = null;
            bool backupMoved = false;
            bool newFinalMoved = false;
            try
            {
                previousHandle = OpenRelativeHandle(
                    _handle, destinationFileName, create: false, requestDelete: true,
                    requireDirectory: false, requestReadData: false, exclusiveShare: true);
                ImageToolPathEntry previousEntry = InspectWindowsHandle(previousHandle);
                if (previousEntry.Kind != ImageToolPathEntryKind.RegularFile
                    || !string.Equals(previousEntry.IdentityToken, expectedDestinationIdentityToken, StringComparison.Ordinal)) return false;

                RenameHandle(previousHandle, backupName);
                backupMoved = true;
                RenameHandle(source.Stream.SafeFileHandle, destinationFileName);
                newFinalMoved = true;
                ImageToolPathEntry committed = InspectEntry(destinationFileName);
                if (committed.Kind != ImageToolPathEntryKind.RegularFile
                    || !string.Equals(committed.IdentityToken, sourceEntry.IdentityToken, StringComparison.Ordinal))
                {
                    bool rolledBack = RollbackWindowsReplace(
                        previousHandle,
                        destinationFileName,
                        sourceEntry.IdentityToken,
                        expectedDestinationIdentityToken);
                    if (!rolledBack && IsWindowsReplaceCommitted(
                        previousHandle,
                        destinationFileName,
                        sourceEntry.IdentityToken,
                        expectedDestinationIdentityToken))
                    {
                        _pendingReplace = new PendingWindowsReplace(
                            previousHandle!, backupName, sourceFileName, destinationFileName,
                            expectedDestinationIdentityToken, sourceEntry.IdentityToken);
                        previousHandle = null;
                        return true;
                    }
                    return false;
                }

                _pendingReplace = new PendingWindowsReplace(
                    previousHandle, backupName, sourceFileName, destinationFileName,
                    expectedDestinationIdentityToken, sourceEntry.IdentityToken);
                previousHandle = null;
                return true;
            }
            catch
            {
                if (backupMoved)
                {
                    bool rolledBack = RollbackWindowsReplace(
                        previousHandle,
                        destinationFileName,
                        newFinalMoved ? sourceEntry.IdentityToken : string.Empty,
                        expectedDestinationIdentityToken);
                    if (!rolledBack && newFinalMoved && IsWindowsReplaceCommitted(
                        previousHandle,
                        destinationFileName,
                        sourceEntry.IdentityToken,
                        expectedDestinationIdentityToken))
                    {
                        _pendingReplace = new PendingWindowsReplace(
                            previousHandle!, backupName, sourceFileName, destinationFileName,
                            expectedDestinationIdentityToken, sourceEntry.IdentityToken);
                        previousHandle = null;
                        return true;
                    }
                }
                return false;
            }
            finally
            {
                previousHandle?.Dispose();
            }
        }

        public bool ReconcileAtomicReplace(string fileName, out string error)
        {
            EnsureSimpleName(fileName);
            error = string.Empty;
            string prefix = $".{fileName}.";
            const string suffix = ".agas-backup";
            string[] backups = EnumerateEntryNames()
                .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (backups.Length == 0) return true;
            if (backups.Length != 1
                || !TryParseAtomicBackupName(fileName, backups[0], out string oldHash, out string newHash))
            {
                error = "Atomic replace recovery is ambiguous.";
                return false;
            }

            string backupName = backups[0];
            ImageToolPathEntry backupEntry = InspectEntry(backupName);
            if (backupEntry.Kind != ImageToolPathEntryKind.RegularFile
                || !string.Equals(HashIdentity(backupEntry.IdentityToken), oldHash, StringComparison.Ordinal))
            {
                error = "Atomic replace backup identity is invalid.";
                return false;
            }
            ImageToolPathEntry finalEntry = InspectEntry(fileName);
            if (finalEntry.Kind == ImageToolPathEntryKind.RegularFile
                && string.Equals(HashIdentity(finalEntry.IdentityToken), newHash, StringComparison.Ordinal))
            {
                DeleteIfIdentityMatches(backupName, backupEntry.IdentityToken);
                if (InspectEntry(backupName).Kind == ImageToolPathEntryKind.Missing) return true;
                error = "Committed atomic replace backup could not be cleaned up.";
                return false;
            }
            if (finalEntry.Kind != ImageToolPathEntryKind.Missing)
            {
                error = "Atomic replace final path is occupied by another identity.";
                return false;
            }

            SafeFileHandle? backupHandle = null;
            try
            {
                backupHandle = OpenRelativeHandle(
                    _handle, backupName, create: false, requestDelete: true,
                    requireDirectory: false, requestReadData: false, exclusiveShare: true);
                ImageToolPathEntry heldBackup = InspectWindowsHandle(backupHandle);
                if (!string.Equals(heldBackup.IdentityToken, backupEntry.IdentityToken, StringComparison.Ordinal))
                {
                    error = "Atomic replace backup changed before recovery.";
                    return false;
                }
                RenameHandle(backupHandle, fileName);
                ImageToolPathEntry restored = InspectWindowsHandle(backupHandle);
                if (restored.Kind == ImageToolPathEntryKind.RegularFile
                    && string.Equals(restored.IdentityToken, backupEntry.IdentityToken, StringComparison.Ordinal)) return true;
                error = "Atomic replace backup restoration identity mismatch.";
                return false;
            }
            catch (Exception exception)
            {
                error = $"Atomic replace recovery failed: {exception.Message}";
                return false;
            }
            finally
            {
                backupHandle?.Dispose();
            }
        }

        public bool FinalizeAtomicReplace(
            string sourceFileName,
            string destinationFileName,
            string newIdentityToken,
            string oldIdentityToken,
            out string error)
        {
            error = string.Empty;
            if (!TryGetMatchingPending(
                    sourceFileName, destinationFileName, newIdentityToken, oldIdentityToken,
                    out PendingWindowsReplace pending, out error)) return false;
            ImageToolPathEntry finalEntry = InspectEntry(destinationFileName);
            if (finalEntry.Kind != ImageToolPathEntryKind.RegularFile
                || !string.Equals(finalEntry.IdentityToken, newIdentityToken, StringComparison.Ordinal))
            {
                error = "Atomic replace final identity changed before finalize.";
                return false;
            }
            if (!DeleteHeldFile(pending.PreviousHandle))
            {
                error = "Atomic replace backup could not be finalized.";
                return false;
            }
            pending.PreviousHandle.Dispose();
            _pendingReplace = null;
            return true;
        }

        public bool RollbackAtomicReplace(
            string sourceFileName,
            string destinationFileName,
            string newIdentityToken,
            string oldIdentityToken,
            out string error)
        {
            error = string.Empty;
            if (!TryGetMatchingPending(
                    sourceFileName, destinationFileName, newIdentityToken, oldIdentityToken,
                    out PendingWindowsReplace pending, out error)) return false;
            if (!RollbackWindowsReplace(
                    pending.PreviousHandle,
                    destinationFileName,
                    newIdentityToken,
                    oldIdentityToken))
            {
                error = "Atomic replace rollback could not safely restore the previous file.";
                return false;
            }
            pending.PreviousHandle.Dispose();
            _pendingReplace = null;
            return true;
        }

        private bool TryGetMatchingPending(
            string sourceFileName,
            string destinationFileName,
            string newIdentityToken,
            string oldIdentityToken,
            out PendingWindowsReplace pending,
            out string error)
        {
            PendingWindowsReplace? candidate = _pendingReplace;
            pending = candidate!;
            error = string.Empty;
            if (candidate == null
                || !string.Equals(candidate.SourceFileName, sourceFileName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(candidate.DestinationFileName, destinationFileName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(candidate.NewIdentityToken, newIdentityToken, StringComparison.Ordinal)
                || !string.Equals(candidate.OldIdentityToken, oldIdentityToken, StringComparison.Ordinal))
            {
                error = "Atomic replace transaction does not match the pending operation.";
                return false;
            }
            return true;
        }

        private static bool DeleteHeldFile(SafeFileHandle handle)
        {
            var disposition = new NativeMethods.FileDispositionInformationEx
            {
                Flags = NativeMethods.FileDispositionFlagDelete
                    | NativeMethods.FileDispositionFlagPosixSemantics
                    | NativeMethods.FileDispositionFlagIgnoreReadonlyAttribute,
            };
            return NativeMethods.SetFileInformationByHandle(
                handle,
                NativeMethods.FileInfoByHandleClass.FileDispositionInfoEx,
                ref disposition,
                (uint)Marshal.SizeOf<NativeMethods.FileDispositionInformationEx>());
        }

        private bool RollbackWindowsReplace(
            SafeFileHandle? previousHandle,
            string destinationFileName,
            string newFinalIdentityToken,
            string expectedDestinationIdentityToken)
        {
            if (previousHandle == null || previousHandle.IsInvalid) return false;
            ImageToolPathEntry finalEntry = InspectEntry(destinationFileName);
            if (!string.IsNullOrEmpty(newFinalIdentityToken)
                && finalEntry.Kind == ImageToolPathEntryKind.RegularFile
                && string.Equals(finalEntry.IdentityToken, newFinalIdentityToken, StringComparison.Ordinal))
            {
                DeleteIfIdentityMatches(destinationFileName, newFinalIdentityToken);
                finalEntry = InspectEntry(destinationFileName);
            }
            if (finalEntry.Kind != ImageToolPathEntryKind.Missing) return false;
            try
            {
                RenameHandle(previousHandle, destinationFileName);
                ImageToolPathEntry restored = InspectWindowsHandle(previousHandle);
                return restored.Kind == ImageToolPathEntryKind.RegularFile
                    && string.Equals(restored.IdentityToken, expectedDestinationIdentityToken, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private bool IsWindowsReplaceCommitted(
            SafeFileHandle? previousHandle,
            string destinationFileName,
            string newFinalIdentityToken,
            string expectedDestinationIdentityToken)
        {
            if (previousHandle == null || previousHandle.IsInvalid) return false;
            try
            {
                ImageToolPathEntry heldPrevious = InspectWindowsHandle(previousHandle);
                ImageToolPathEntry finalEntry = InspectEntry(destinationFileName);
                return heldPrevious.Kind == ImageToolPathEntryKind.RegularFile
                    && string.Equals(heldPrevious.IdentityToken, expectedDestinationIdentityToken, StringComparison.Ordinal)
                    && finalEntry.Kind == ImageToolPathEntryKind.RegularFile
                    && string.Equals(finalEntry.IdentityToken, newFinalIdentityToken, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static string BuildAtomicBackupName(string fileName, string oldIdentity, string newIdentity) =>
            $".{fileName}.{HashIdentity(oldIdentity)}.{HashIdentity(newIdentity)}.agas-backup";

        private static bool TryParseAtomicBackupName(
            string fileName, string backupName, out string oldHash, out string newHash)
        {
            oldHash = string.Empty;
            newHash = string.Empty;
            string prefix = $".{fileName}.";
            const string suffix = ".agas-backup";
            if (!backupName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !backupName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
            string[] parts = backupName[prefix.Length..^suffix.Length].Split('.', StringSplitOptions.None);
            if (parts.Length != 2 || parts.Any(part => part.Length != 16 || !part.All(Uri.IsHexDigit))) return false;
            oldHash = parts[0];
            newHash = parts[1];
            return true;
        }

        private static string HashIdentity(string identityToken) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityToken)))[..16];

        public void DeleteIfIdentityMatches(string fileName, string identityToken)
        {
            EnsureSimpleName(fileName);
            if (string.IsNullOrEmpty(identityToken)) return;
            SafeFileHandle? handle = null;
            try
            {
                handle = OpenRelativeHandle(_handle, fileName, create: false, requestDelete: true, requireDirectory: false, requestReadData: false);
                ImageToolPathEntry entry = InspectWindowsHandle(handle);
                if (entry.Kind != ImageToolPathEntryKind.RegularFile
                    || !string.Equals(entry.IdentityToken, identityToken, StringComparison.Ordinal)) return;
                var disposition = new NativeMethods.FileDispositionInformationEx
                {
                    Flags = NativeMethods.FileDispositionFlagDelete
                        | NativeMethods.FileDispositionFlagPosixSemantics
                        | NativeMethods.FileDispositionFlagIgnoreReadonlyAttribute,
                };
                if (!NativeMethods.SetFileInformationByHandle(
                        handle,
                        NativeMethods.FileInfoByHandleClass.FileDispositionInfoEx,
                        ref disposition,
                        (uint)Marshal.SizeOf<NativeMethods.FileDispositionInformationEx>()))
                {
                    throw CreateIOException("Unable to remove reserved output", Marshal.GetLastPInvokeError());
                }
            }
            catch
            {
                // Cleanup is best-effort and is bound to the verified file handle.
            }
            finally
            {
                handle?.Dispose();
            }
        }

        private static SafeFileHandle OpenRelativeHandle(
            SafeFileHandle rootHandle,
            string fileName,
            bool create,
            bool requestDelete,
            bool requireDirectory,
            bool requestReadData,
            bool exclusiveShare = false)
        {
            IntPtr nameBuffer = Marshal.StringToHGlobalUni(fileName);
            IntPtr unicodeBuffer = IntPtr.Zero;
            try
            {
                var unicode = new NativeMethods.UnicodeString
                {
                    Length = checked((ushort)(fileName.Length * 2)),
                    MaximumLength = checked((ushort)((fileName.Length + 1) * 2)),
                    Buffer = nameBuffer,
                };
                unicodeBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.UnicodeString>());
                Marshal.StructureToPtr(unicode, unicodeBuffer, fDeleteOld: false);
                var attributes = new NativeMethods.ObjectAttributes
                {
                    Length = Marshal.SizeOf<NativeMethods.ObjectAttributes>(),
                    RootDirectory = rootHandle.DangerousGetHandle(),
                    ObjectName = unicodeBuffer,
                    Attributes = NativeMethods.ObjCaseInsensitive,
                };
                uint desiredAccess = NativeMethods.FileReadAttributes | NativeMethods.Synchronize;
                if (requireDirectory) desiredAccess |= NativeMethods.FileListDirectory;
                if (requestReadData) desiredAccess |= NativeMethods.FileReadData;
                if (create) desiredAccess |= NativeMethods.FileWriteData | NativeMethods.FileWriteAttributes | NativeMethods.Delete;
                if (requestDelete) desiredAccess |= NativeMethods.Delete;
                int status = NativeMethods.NtCreateFile(
                    out SafeFileHandle result,
                    desiredAccess,
                    ref attributes,
                    out _,
                    IntPtr.Zero,
                    NativeMethods.FileAttributeNormal,
                    exclusiveShare
                        ? 0
                        : NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
                    create ? NativeMethods.FileCreate : NativeMethods.FileOpen,
                    NativeMethods.FileSynchronousIoNonAlert
                        | NativeMethods.FileOpenReparsePoint
                        | NativeMethods.FileOpenForBackupIntent
                        | (requireDirectory ? NativeMethods.FileDirectoryFile : NativeMethods.FileNonDirectoryFile),
                    IntPtr.Zero,
                    0);
                if (status < 0)
                {
                    int error = unchecked((int)NativeMethods.RtlNtStatusToDosError(status));
                    result?.Dispose();
                    throw new Win32Exception(error);
                }
                return result;
            }
            finally
            {
                if (unicodeBuffer != IntPtr.Zero) Marshal.FreeHGlobal(unicodeBuffer);
                Marshal.FreeHGlobal(nameBuffer);
            }
        }

        private void RenameHandle(SafeFileHandle sourceHandle, string destinationFileName)
        {
            int nameBytes = checked(destinationFileName.Length * 2);
            int headerSize = Marshal.OffsetOf<NativeMethods.FileRenameInformation>(nameof(NativeMethods.FileRenameInformation.FileName)).ToInt32();
            IntPtr buffer = Marshal.AllocHGlobal(headerSize + nameBytes);
            try
            {
                var information = new NativeMethods.FileRenameInformation
                {
                    ReplaceIfExists = 0,
                    RootDirectory = _handle.DangerousGetHandle(),
                    FileNameLength = (uint)nameBytes,
                    FileName = '\0',
                };
                Marshal.StructureToPtr(information, buffer, fDeleteOld: false);
                Marshal.Copy(destinationFileName.ToCharArray(), 0, IntPtr.Add(buffer, headerSize), destinationFileName.Length);
                int status = NativeMethods.NtSetInformationFile(
                    sourceHandle,
                    out _,
                    buffer,
                    (uint)(headerSize + nameBytes),
                    NativeMethods.FileInformationClass.FileRenameInformation);
                if (status < 0)
                {
                    int error = unchecked((int)NativeMethods.RtlNtStatusToDosError(status));
                    throw CreateIOException("Atomic no-overwrite output move failed", error);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Dispose()
        {
            PendingWindowsReplace? pending = _pendingReplace;
            if (pending != null)
            {
                if (RollbackWindowsReplace(
                        pending.PreviousHandle,
                        pending.DestinationFileName,
                        pending.NewIdentityToken,
                        pending.OldIdentityToken))
                {
                    _pendingReplace = null;
                }
                pending.PreviousHandle.Dispose();
            }
            _handle.Dispose();
        }

        private sealed record PendingWindowsReplace(
            SafeFileHandle PreviousHandle,
            string BackupFileName,
            string SourceFileName,
            string DestinationFileName,
            string OldIdentityToken,
            string NewIdentityToken);
    }

    private static void EnsureSimpleName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName is "." or ".."
            || fileName.IndexOf('/') >= 0
            || fileName.IndexOf('\\') >= 0)
        {
            throw new ArgumentException("A single directory-relative file name is required.", nameof(fileName));
        }
    }

    private static IEnumerable<string> GetRelativeDirectorySegments(string trustedRoot, string path)
    {
        string relative = Path.GetRelativePath(trustedRoot, path);
        if (relative == ".") return Array.Empty<string>();
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new IOException("Output directory is outside the trusted project root.");
        }
        string[] segments = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments) EnsureSimpleName(segment);
        return segments;
    }

    private static IOException CreateIOException(string operation, int error) =>
        new($"{operation}: {new Win32Exception(error).Message} ({error}).");

    private static ImageToolPathEntry ReadUnixStat(IntPtr buffer)
    {
        bool mac = OperatingSystem.IsMacOS();
        int modeOffset = mac
            ? 4
            : RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm
                ? 16
                : 24;
        uint mode = mac
            ? unchecked((ushort)Marshal.ReadInt16(buffer, modeOffset))
            : unchecked((uint)Marshal.ReadInt32(buffer, modeOffset));
        uint fileType = mode & 0xF000u;
        ImageToolPathEntryKind kind = fileType switch
        {
            0x8000u => ImageToolPathEntryKind.RegularFile,
            0x4000u => ImageToolPathEntryKind.Directory,
            0xA000u => ImageToolPathEntryKind.SymlinkOrReparsePoint,
            _ => ImageToolPathEntryKind.Other,
        };
        ulong device = mac
            ? unchecked((uint)Marshal.ReadInt32(buffer, 0))
            : unchecked((ulong)Marshal.ReadInt64(buffer, 0));
        ulong inode = unchecked((ulong)Marshal.ReadInt64(buffer, 8));
        return new ImageToolPathEntry(kind, $"unix:{device}:{inode}");
    }

    private static class NativeMethods
    {
        internal const int LinuxOpenDirectoryFlags = 0x10000 | 0x20000 | 0x80000;
        internal const int LinuxCreateFileFlags = 0x1 | 0x40 | 0x80 | 0x20000 | 0x80000;
        internal const int LinuxOpenReadFileFlags = 0x20000 | 0x80000;
        internal const int LinuxAtSymlinkNoFollow = 0x100;
        internal const uint LinuxRenameNoReplace = 0x1;
        internal const uint LinuxRenameExchange = 0x2;
        internal const int MacOpenDirectoryFlags = 0x100000 | 0x100 | 0x1000000;
        internal const int MacCreateFileFlags = 0x1 | 0x200 | 0x800 | 0x100 | 0x1000000;
        internal const int MacOpenReadFileFlags = 0x100 | 0x1000000;
        internal const int MacAtSymlinkNoFollow = 0x20;
        internal const uint MacRenameExclusive = 0x4;
        internal const uint MacRenameSwap = 0x2;
        internal const uint MacRenameNoFollowAny = 0x10;

        internal const uint FileReadAttributes = 0x80;
        internal const uint FileReadData = 0x1;
        internal const uint FileListDirectory = 0x1;
        internal const uint FileWriteData = 0x2;
        internal const uint FileWriteAttributes = 0x100;
        internal const uint Delete = 0x10000;
        internal const uint Synchronize = 0x100000;
        internal const uint FileShareRead = 0x1;
        internal const uint FileShareWrite = 0x2;
        internal const uint FileShareDelete = 0x4;
        internal const uint OpenExisting = 3;
        internal const uint FileAttributeNormal = 0x80;
        internal const uint FileFlagOpenReparsePoint = 0x00200000;
        internal const uint FileFlagBackupSemantics = 0x02000000;
        internal const uint FileCreate = 2;
        internal const uint FileOpen = 1;
        internal const uint FileOpenForBackupIntent = 0x4000;
        internal const uint FileDirectoryFile = 0x1;
        internal const uint FileNonDirectoryFile = 0x40;
        internal const uint FileOpenReparsePoint = 0x00200000;
        internal const uint FileSynchronousIoNonAlert = 0x20;
        internal const uint ObjCaseInsensitive = 0x40;
        internal const uint FileDispositionFlagDelete = 0x1;
        internal const uint FileDispositionFlagPosixSemantics = 0x2;
        internal const uint FileDispositionFlagIgnoreReadonlyAttribute = 0x10;
        internal const int StatusNoMoreFiles = unchecked((int)0x80000006);
        internal const int FileIdBothDirectoryNameOffset = 104;

        internal enum FileInformationClass
        {
            FileRenameInformation = 10,
            FileIdBothDirectoryInformation = 37,
        }

        internal enum FileInfoByHandleClass
        {
            FileRenameInfo = 3,
            FileAttributeTagInfo = 9,
            FileDispositionInfoEx = 21,
            FileRenameInfoEx = 22,
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ByHandleFileInformation
        {
            internal uint FileAttributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            internal uint VolumeSerialNumber;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint NumberOfLinks;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileAttributeTagInformation
        {
            internal uint FileAttributes;
            internal uint ReparseTag;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct UnicodeString
        {
            internal ushort Length;
            internal ushort MaximumLength;
            internal IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ObjectAttributes
        {
            internal int Length;
            internal IntPtr RootDirectory;
            internal IntPtr ObjectName;
            internal uint Attributes;
            internal IntPtr SecurityDescriptor;
            internal IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IoStatusBlock
        {
            internal IntPtr Status;
            internal UIntPtr Information;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct FileRenameInformation
        {
            internal int ReplaceIfExists;
            internal IntPtr RootDirectory;
            internal uint FileNameLength;
            internal char FileName;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileDispositionInformationEx
        {
            internal uint Flags;
        }

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
        internal static extern int OpenAt(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);

        [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
        internal static extern int MkdirAt(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

        [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
        internal static extern IntPtr FdOpenDirectory(int descriptor);

        [DllImport("libc", EntryPoint = "fdopendir$INODE64", SetLastError = true)]
        internal static extern IntPtr MacFdOpenDirectory(int descriptor);

        [DllImport("libc", EntryPoint = "readdir", SetLastError = true)]
        internal static extern IntPtr ReadDirectory(IntPtr directory);

        [DllImport("libc", EntryPoint = "readdir$INODE64", SetLastError = true)]
        internal static extern IntPtr MacReadDirectory(IntPtr directory);

        [DllImport("libc", EntryPoint = "closedir", SetLastError = true)]
        internal static extern int CloseDirectory(IntPtr directory);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        internal static extern int Close(int descriptor);

        [DllImport("libc", EntryPoint = "fstatat", SetLastError = true)]
        internal static extern int FStatAt(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer, int flags);

        [DllImport("libc", EntryPoint = "fstatat$INODE64", SetLastError = true)]
        internal static extern int MacFStatAt(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer, int flags);

        [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
        internal static extern int RenameAtNoReplaceLinux(int oldDirectoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, int newDirectoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);

        [DllImport("libc", EntryPoint = "renameatx_np", SetLastError = true)]
        internal static extern int RenameAtExclusiveMac(int oldDirectoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, int newDirectoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);

        [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
        internal static extern int UnlinkAt(int directoryDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandleEx(SafeFileHandle file, FileInfoByHandleClass informationClass, out FileAttributeTagInformation information, uint bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(SafeFileHandle file, FileInfoByHandleClass informationClass, IntPtr information, uint bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(SafeFileHandle file, FileInfoByHandleClass informationClass, ref FileDispositionInformationEx information, uint bufferSize);

        [DllImport("ntdll.dll")]
        internal static extern int NtCreateFile(out SafeFileHandle fileHandle, uint desiredAccess, ref ObjectAttributes objectAttributes, out IoStatusBlock ioStatusBlock, IntPtr allocationSize, uint fileAttributes, uint shareAccess, uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength);

        [DllImport("ntdll.dll")]
        internal static extern int NtSetInformationFile(
            SafeFileHandle fileHandle,
            out IoStatusBlock ioStatusBlock,
            IntPtr fileInformation,
            uint length,
            FileInformationClass fileInformationClass);

        [DllImport("ntdll.dll")]
        internal static extern int NtQueryDirectoryFile(
            SafeFileHandle fileHandle,
            IntPtr eventHandle,
            IntPtr apcRoutine,
            IntPtr apcContext,
            out IoStatusBlock ioStatusBlock,
            IntPtr fileInformation,
            uint length,
            FileInformationClass fileInformationClass,
            [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry,
            IntPtr fileName,
            [MarshalAs(UnmanagedType.U1)] bool restartScan);

        [DllImport("ntdll.dll")]
        internal static extern uint RtlNtStatusToDosError(int status);

        [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
        internal static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);

        [DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)]
        internal static extern int MacLStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);

        [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
        internal static extern int FStat(int descriptor, IntPtr buffer);

        [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
        internal static extern int MacFStat(int descriptor, IntPtr buffer);
    }
}
