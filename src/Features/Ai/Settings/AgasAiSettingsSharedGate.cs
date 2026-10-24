using System;
using System.Collections.Concurrent;
using System.IO;

namespace AppAgasCs.Features.Ai.Settings;

internal static class AgasAiSettingsSharedGate
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static readonly ConcurrentDictionary<string, object> Gates = new(PathComparer);

    public static object ForProfilesPath(string profilesPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilesPath);
        string canonical = CanonicalizePath(profilesPath);
        if (Path.GetDirectoryName(canonical) == null)
        {
            throw new ArgumentException("Settings path must have a parent directory.", nameof(profilesPath));
        }
        return Gates.GetOrAdd(canonical, static _ => new object());
    }

    internal static string CanonicalizePath(string path) =>
        CanonicalizePath(Path.GetFullPath(path), symlinkDepth: 0);

    private static string CanonicalizePath(string fullPath, int symlinkDepth)
    {
        if (symlinkDepth > 64)
        {
            throw new IOException("Settings path contains too many symbolic link indirections.");
        }

        string root = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException("Settings path must be rooted.", nameof(fullPath));
        string relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".")
        {
            return Path.GetFullPath(root);
        }

        string[] segments = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        string current = Path.GetFullPath(root);
        for (int index = 0; index < segments.Length; index++)
        {
            string candidate = Path.Combine(current, segments[index]);
            FileSystemInfo? resolved = ResolveExistingLink(candidate);
            if (resolved != null)
            {
                string restarted = resolved.FullName;
                for (int remaining = index + 1; remaining < segments.Length; remaining++)
                {
                    restarted = Path.Combine(restarted, segments[remaining]);
                }
                return CanonicalizePath(Path.GetFullPath(restarted), symlinkDepth + 1);
            }

            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                for (int remaining = index + 1; remaining < segments.Length; remaining++)
                {
                    candidate = Path.Combine(candidate, segments[remaining]);
                }
                return Path.GetFullPath(candidate);
            }
            current = Path.GetFullPath(candidate);
        }
        return current;
    }

    private static FileSystemInfo? ResolveExistingLink(string path)
    {
        if (Directory.Exists(path))
        {
            var directory = new DirectoryInfo(path);
            if (directory.LinkTarget == null)
            {
                return null;
            }
            return directory.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new IOException("Settings directory symbolic link could not be resolved.");
        }
        if (File.Exists(path))
        {
            var file = new FileInfo(path);
            if (file.LinkTarget == null)
            {
                return null;
            }
            return file.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new IOException("Settings file symbolic link could not be resolved.");
        }
        return null;
    }
}
