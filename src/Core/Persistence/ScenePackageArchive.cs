#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace AppAgasCs.Core.Persistence;

internal static class ScenePackageArchive
{
    internal const int MaximumFileEntries = 513;
    internal const int MaximumArchiveEntries = 4096;
    internal const long MaximumEntryBytes = ScenePackageSafetyLimits.MaximumImageBytes;
    internal const int MaximumEntryPathLength = 1024;

    private const int UnixFileTypeMask = 0xf000;
    private const int UnixRegularFile = 0x8000;
    private const int UnixDirectory = 0x4000;
    private const int UnixSymbolicLink = 0xa000;

    public static void Create(string sourceDirectory, string destinationArchivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationArchivePath);

        string root = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("场景包暂存目录不存在。");

        string destination = Path.GetFullPath(destinationArchivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("无法解析场景包压缩文件目录。"));

        IReadOnlyList<SourceFile> files = EnumerateSourceFiles(root);
        if (!files.Any(file => string.Equals(
                file.RelativePath,
                AgasPaths.ScenePackageManifestFileName,
                StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"场景包根目录缺少 {AgasPaths.ScenePackageManifestFileName}。");
        }

        using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);
        foreach (SourceFile file in files.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            ZipArchiveEntry entry = archive.CreateEntry(file.RelativePath, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using Stream input = new FileStream(file.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using Stream entryStream = entry.Open();
            input.CopyTo(entryStream);
        }
    }

    public static string ExtractToTemporaryDirectory(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        string fullArchivePath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullArchivePath))
            throw new FileNotFoundException("场景包压缩文件不存在。", fullArchivePath);

        string destination = Path.Combine(
            Path.GetTempPath(),
            "agas-scene-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        try
        {
            Extract(fullArchivePath, destination);
            return destination;
        }
        catch
        {
            DeleteDirectoryIfPresent(destination);
            throw;
        }
    }

    private static IReadOnlyList<SourceFile> EnumerateSourceFiles(string root)
    {
        var files = new List<SourceFile>();
        var pending = new Stack<string>();
        pending.Push(root);
        long totalBytes = 0;

        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("场景包不能包含符号链接或重解析点。");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                    continue;
                }

                string relativePath = Path.GetRelativePath(root, path)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                ValidateEntryPath(relativePath, isDirectory: false);
                var info = new FileInfo(path);
                long maximum = string.Equals(
                    relativePath,
                    AgasPaths.ScenePackageManifestFileName,
                    StringComparison.Ordinal)
                        ? ScenePackageSafetyLimits.MaximumManifestBytes
                        : MaximumEntryBytes;
                if (info.Length > maximum)
                    throw new InvalidDataException($"场景包文件超过大小限制：{relativePath}");
                totalBytes = checked(totalBytes + info.Length);
                if (totalBytes > ScenePackageSafetyLimits.MaximumPackagePayloadBytes)
                    throw new InvalidDataException("场景包内容超过总大小限制。");
                files.Add(new SourceFile(path, relativePath));
                if (files.Count > MaximumFileEntries)
                    throw new InvalidDataException("场景包文件数量超过限制。");
            }
        }

        return files;
    }

    private static void Extract(string archivePath, string destination)
    {
        using FileStream input = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaximumArchiveEntries)
            throw new InvalidDataException("场景包 ZIP 条目数量超过限制。");

        var paths = new Dictionary<string, RegisteredPath>(StringComparer.OrdinalIgnoreCase);
        int fileCount = 0;
        long totalBytes = 0;
        bool manifestFound = false;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string entryPath = entry.FullName;
            bool isDirectory = entryPath.EndsWith("/", StringComparison.Ordinal);
            ValidateEntryType(entry, isDirectory);
            string normalizedPath = ValidateEntryPath(entryPath, isDirectory);
            RegisterPath(paths, normalizedPath, isDirectory);
            if (isDirectory)
            {
                if (entry.Length != 0)
                    throw new InvalidDataException("场景包 ZIP 目录条目不能包含数据。");
                continue;
            }

            fileCount++;
            if (fileCount > MaximumFileEntries)
                throw new InvalidDataException("场景包 ZIP 文件数量超过限制。");
            bool isManifest = string.Equals(
                normalizedPath,
                AgasPaths.ScenePackageManifestFileName,
                StringComparison.Ordinal);
            long maximum = isManifest
                ? ScenePackageSafetyLimits.MaximumManifestBytes
                : MaximumEntryBytes;
            if (entry.Length < 0 || entry.Length > maximum)
                throw new InvalidDataException($"场景包 ZIP 条目超过大小限制：{normalizedPath}");
            if (entry.Length > ScenePackageSafetyLimits.MaximumPackagePayloadBytes - totalBytes)
                throw new InvalidDataException("场景包 ZIP 解压内容超过总大小限制。");

            string destinationPath = ResolveDestination(destination, normalizedPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using Stream entryStream = entry.Open();
            using Stream output = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            long written = CopyBounded(entryStream, output, maximum, totalBytes);
            if (written != entry.Length)
                throw new InvalidDataException($"场景包 ZIP 条目长度无效：{normalizedPath}");
            totalBytes = checked(totalBytes + written);
            manifestFound |= isManifest;
        }

        if (!manifestFound)
            throw new InvalidDataException($"场景包 ZIP 根目录缺少 {AgasPaths.ScenePackageManifestFileName}。");
    }

    private static long CopyBounded(Stream input, Stream output, long maximumEntryBytes, long totalBeforeEntry)
    {
        byte[] buffer = new byte[81920];
        long written = 0;
        while (true)
        {
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) return written;
            written = checked(written + read);
            if (written > maximumEntryBytes
                || written > ScenePackageSafetyLimits.MaximumPackagePayloadBytes - totalBeforeEntry)
            {
                throw new InvalidDataException("场景包 ZIP 解压内容超过大小限制。");
            }
            output.Write(buffer, 0, read);
        }
    }

    private static void ValidateEntryType(ZipArchiveEntry entry, bool isDirectory)
    {
        int unixFileType = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        if (unixFileType == UnixSymbolicLink)
            throw new InvalidDataException("场景包 ZIP 不能包含符号链接。");
        if (unixFileType != 0
            && unixFileType != UnixRegularFile
            && unixFileType != UnixDirectory)
        {
            throw new InvalidDataException("场景包 ZIP 包含不支持的文件类型。");
        }
        if (isDirectory && unixFileType == UnixRegularFile)
            throw new InvalidDataException("场景包 ZIP 条目类型与路径不一致。");
        if (!isDirectory && unixFileType == UnixDirectory)
            throw new InvalidDataException("场景包 ZIP 条目类型与路径不一致。");
    }

    private static string ValidateEntryPath(string path, bool isDirectory)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaximumEntryPathLength)
            throw new InvalidDataException("场景包 ZIP 条目路径无效。");
        if (path.Contains('\\'))
            throw new InvalidDataException("场景包 ZIP 条目不能使用反斜杠。");

        string normalized = isDirectory ? path[..^1] : path;
        if (string.IsNullOrEmpty(normalized)
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || Path.IsPathRooted(normalized)
            || normalized.Contains(':'))
        {
            throw new InvalidDataException("场景包 ZIP 条目不能使用绝对路径。");
        }

        string[] segments = normalized.Split('/');
        if (segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
            throw new InvalidDataException("场景包 ZIP 条目路径包含不安全的路径段。");
        return string.Join('/', segments);
    }

    private static void RegisterPath(
        IDictionary<string, RegisteredPath> registered,
        string normalizedPath,
        bool isDirectory)
    {
        string[] segments = normalizedPath.Split('/');
        for (int index = 0; index < segments.Length; index++)
        {
            string path = string.Join('/', segments.Take(index + 1));
            bool pathIsDirectory = index < segments.Length - 1 || isDirectory;
            if (registered.TryGetValue(path, out RegisteredPath? existing))
            {
                if (!string.Equals(existing.Spelling, path, StringComparison.Ordinal))
                    throw new InvalidDataException("场景包 ZIP 包含大小写冲突的路径。");
                if (existing.IsDirectory != pathIsDirectory)
                    throw new InvalidDataException("场景包 ZIP 包含文件与目录冲突的路径。");
                if (index == segments.Length - 1
                    && pathIsDirectory
                    && isDirectory
                    && !existing.IsExplicitEntry)
                {
                    registered[path] = existing with { IsExplicitEntry = true };
                    continue;
                }
                if (index == segments.Length - 1)
                    throw new InvalidDataException("场景包 ZIP 包含重复条目。");
                continue;
            }
            registered.Add(
                path,
                new RegisteredPath(
                    path,
                    pathIsDirectory,
                    IsExplicitEntry: index == segments.Length - 1));
        }
    }

    private static string ResolveDestination(string root, string relativePath)
    {
        string destination = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
            + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!destination.StartsWith(prefix, comparison))
            throw new InvalidDataException("场景包 ZIP 条目路径越界。");
        return destination;
    }

    private static void DeleteDirectoryIfPresent(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed record SourceFile(string AbsolutePath, string RelativePath);
    private sealed record RegisteredPath(
        string Spelling,
        bool IsDirectory,
        bool IsExplicitEntry);
}
