#nullable enable

using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AppAgasCs.Core.Persistence;

/// <summary>
/// Captures an OS object identity without following the final symlink/reparse point.
/// Creation timestamps are deliberately not identities: callers use this token to detect
/// delete-and-recreate races where timestamps and attributes can be preserved.
/// </summary>
public sealed class FileSystemProjectAccessIdentityProvider : IProjectAccessIdentityProvider
{
    public ProjectAccessPathIdentity Capture(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A path is required.", nameof(path));

        string fullPath = Path.GetFullPath(path);
        return OperatingSystem.IsWindows()
            ? CaptureWindows(fullPath)
            : CaptureUnix(fullPath);
    }

    private static ProjectAccessPathIdentity CaptureUnix(string fullPath)
    {
        IntPtr buffer = Marshal.AllocHGlobal(512);
        try
        {
            int result = OperatingSystem.IsMacOS()
                ? NativeMethods.MacLStat(fullPath, buffer)
                : NativeMethods.LStat(fullPath, buffer);
            if (result != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                return error is 2 or 20
                    ? Missing(fullPath)
                    : Unsafe(fullPath, $"lstat-error:{error}");
            }

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
            bool isDirectory = fileType == 0x4000u;
            bool isLink = fileType == 0xA000u;
            bool supportedType = isDirectory || isLink || fileType == 0x8000u;
            ulong device = mac
                ? unchecked((uint)Marshal.ReadInt32(buffer, 0))
                : unchecked((ulong)Marshal.ReadInt64(buffer, 0));
            ulong inode = unchecked((ulong)Marshal.ReadInt64(buffer, 8));
            string token = $"unix:{device}:{inode}:{fileType}";
            return new(fullPath, token, true, isDirectory, isLink || !supportedType);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ProjectAccessPathIdentity CaptureWindows(string fullPath)
    {
        using SafeFileHandle handle = NativeMethods.CreateFileW(
            fullPath,
            NativeMethods.FileReadAttributes,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            return error is 2 or 3
                ? Missing(fullPath)
                : Unsafe(fullPath, $"open-error:{error}");
        }

        if (!NativeMethods.GetFileInformationByHandle(handle, out NativeMethods.ByHandleFileInformation info))
            return Unsafe(fullPath, $"handle-error:{Marshal.GetLastPInvokeError()}");

        bool isDirectory = (info.FileAttributes & NativeMethods.FileAttributeDirectory) != 0;
        bool isReparse = (info.FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0;
        ulong fileId = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        string token = $"windows:{info.VolumeSerialNumber}:{fileId}:{(isDirectory ? 1 : 0)}:{(isReparse ? 1 : 0)}";
        return new(fullPath, token, true, isDirectory, isReparse);
    }

    private static ProjectAccessPathIdentity Missing(string fullPath) =>
        new(fullPath, "missing", false, false, false);

    private static ProjectAccessPathIdentity Unsafe(string fullPath, string token) =>
        new(fullPath, token, true, false, true);

    private static class NativeMethods
    {
        internal const uint FileReadAttributes = 0x80;
        internal const uint FileShareRead = 0x1;
        internal const uint FileShareWrite = 0x2;
        internal const uint FileShareDelete = 0x4;
        internal const uint OpenExisting = 3;
        internal const uint FileFlagOpenReparsePoint = 0x00200000;
        internal const uint FileFlagBackupSemantics = 0x02000000;
        internal const uint FileAttributeDirectory = 0x10;
        internal const uint FileAttributeReparsePoint = 0x400;

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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
        internal static extern int LStat(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            IntPtr buffer);

        [DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)]
        internal static extern int MacLStat(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            IntPtr buffer);
    }
}
