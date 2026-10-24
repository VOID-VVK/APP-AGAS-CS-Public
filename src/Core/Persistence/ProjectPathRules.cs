#nullable enable

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AppAgasCs.Core.Persistence;

/// <summary>
/// Where internal/build directory names may appear in a project-relative path.
/// </summary>
public enum ProjectInternalDirectoryPolicy
{
    /// <summary>Only the first segment is restricted (typical user asset paths / MCP).</summary>
    FirstSegmentOnly,

    /// <summary>Any segment is restricted (stricter tool input/output paths).</summary>
    AnySegment,
}

/// <summary>
/// Result of pure relative-path syntax normalization (no filesystem I/O).
/// </summary>
public readonly record struct ProjectRelativePathSyntaxResult(
    bool Success,
    string NormalizedRelative,
    string ErrorMessage)
{
    public static ProjectRelativePathSyntaxResult Ok(string normalizedRelative) =>
        new(true, normalizedRelative, string.Empty);

    public static ProjectRelativePathSyntaxResult Fail(string errorMessage) =>
        new(false, string.Empty, errorMessage);
}

/// <summary>
/// Options for pure project-relative path syntax checks shared by UI, MCP, and tools.
/// Domain adapters map failures to their own error codes.
/// </summary>
public readonly record struct ProjectRelativePathSyntaxOptions(
    bool AllowInternalDirectory = false,
    ProjectInternalDirectoryPolicy InternalDirectoryPolicy = ProjectInternalDirectoryPolicy.FirstSegmentOnly,
    bool RequireImageExtension = false,
    IReadOnlySet<string>? AllowedExtensionsWithDot = null,
    int? MaxLength = null,
    bool RejectUntrimmedWhitespace = false,
    bool RejectEmptyOrDotSegments = false,
    Func<string, bool>? ContainsInvalidPathChars = null);

/// <summary>
/// Single source of truth for project path safety rules that used to be duplicated across
/// MCP, tools, and Application image resolution (SRP / DRY / OCP).
/// Does not own domain error codes — callers map failures.
/// </summary>
public static class ProjectPathRules
{
    public static FrozenSet<string> AllowedImageExtensionsWithDot { get; } =
        new[] { ".png", ".jpg", ".jpeg", ".webp" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static FrozenSet<string> AllowedImageExtensionsWithoutDot { get; } =
        new[] { "png", "jpg", "jpeg", "webp" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Directory names skipped for user assets and treated as internal product/build trees.</summary>
    public static FrozenSet<string> SkippedDirectoryNames { get; } =
        new[] { ".agas", ".godot", ".git", "bin", "obj" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsAllowedImageExtension(string pathOrExtension)
    {
        if (string.IsNullOrWhiteSpace(pathOrExtension))
        {
            return false;
        }

        string extension = pathOrExtension.Contains('.', StringComparison.Ordinal)
            ? Path.GetExtension(pathOrExtension)
            : pathOrExtension.StartsWith('.')
                ? pathOrExtension
                : "." + pathOrExtension;
        return AllowedImageExtensionsWithDot.Contains(extension);
    }

    public static bool IsSkippedDirectoryName(string? name) =>
        !string.IsNullOrEmpty(name) && SkippedDirectoryNames.Contains(name);

    public static bool IsSafeAsciiIdentifier(string? value, int maxLength = 120)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > maxLength)
        {
            return false;
        }

        foreach (char character in trimmed)
        {
            if ((character is >= 'A' and <= 'Z')
                || (character is >= 'a' and <= 'z')
                || (character is >= '0' and <= '9')
                || character is '_' or '-')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    public static string NormalizeRoot(string projectRoot) =>
        Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static bool IsInsideRoot(string root, string absolutePath)
    {
        string normalizedRoot = NormalizeRoot(root);
        string normalizedPath = Path.GetFullPath(absolutePath);
        string rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.Equals(normalizedRoot, StringComparison.Ordinal)
            || normalizedPath.StartsWith(rootPrefix, StringComparison.Ordinal);
    }

    public static string ToLogicalRelativePath(string projectRoot, string absolutePath)
    {
        string relative = Path.GetRelativePath(NormalizeRoot(projectRoot), Path.GetFullPath(absolutePath));
        return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    public static bool LooksLikeWindowsAbsolute(string input) =>
        input.Length >= 3
        && char.IsAsciiLetter(input[0])
        && input[1] == ':'
        && (input[2] is '\\' or '/');

    /// <summary>Windows drive prefix without requiring a following separator.</summary>
    public static bool LooksLikeWindowsDrivePrefix(string input) =>
        input.Length >= 2 && char.IsAsciiLetter(input[0]) && input[1] == ':';

    public static bool ContainsUrlOrBase64(string input) =>
        input.Contains("://", StringComparison.Ordinal)
        || input.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
        || input.Contains(";base64", StringComparison.OrdinalIgnoreCase)
        || input.Contains("base64,", StringComparison.OrdinalIgnoreCase);

    public static bool HasReparsePoint(FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) != 0;

    public static bool IsReparseOrLink(FileSystemInfo info) =>
        HasReparsePoint(info.Attributes) || !string.IsNullOrEmpty(info.LinkTarget);

    public static bool IsReparsePointFile(string absolutePath)
    {
        try
        {
            return IsReparseOrLink(new FileInfo(absolutePath));
        }
        catch
        {
            return true;
        }
    }

    public static bool PathContainsReparseDirectory(string projectRoot, string absolutePath)
    {
        string root = NormalizeRoot(projectRoot);
        DirectoryInfo? directory = Directory.GetParent(Path.GetFullPath(absolutePath));
        while (directory != null
            && IsInsideRoot(root, directory.FullName)
            && !directory.FullName.Equals(root, StringComparison.Ordinal))
        {
            try
            {
                if (IsReparseOrLink(directory))
                {
                    return true;
                }
            }
            catch
            {
                return true;
            }

            directory = directory.Parent;
        }

        return false;
    }

    /// <summary>
    /// Pure syntax normalization for project-relative paths. No existence or symlink I/O.
    /// </summary>
    public static ProjectRelativePathSyntaxResult TryNormalizeRelativePath(
        string? relativePath,
        ProjectRelativePathSyntaxOptions options = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return ProjectRelativePathSyntaxResult.Fail("Path must be a non-empty project-relative path.");
        }

        string input = relativePath;
        if (options.RejectUntrimmedWhitespace && input != input.Trim())
        {
            return ProjectRelativePathSyntaxResult.Fail("Path must be a non-empty project-relative path.");
        }

        input = input.Trim();
        if (options.MaxLength is int maxLength && input.Length > maxLength)
        {
            return ProjectRelativePathSyntaxResult.Fail("Path must be a non-empty project-relative path.");
        }

        if (input.IndexOf('\0') >= 0)
        {
            return ProjectRelativePathSyntaxResult.Fail("Path must not be absolute, a URI, or inline data.");
        }

        if (options.ContainsInvalidPathChars?.Invoke(input) == true)
        {
            return ProjectRelativePathSyntaxResult.Fail("Path must not be absolute, a URI, or inline data.");
        }

        if (ContainsUrlOrBase64(input)
            || input.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || Path.IsPathRooted(input)
            || LooksLikeWindowsAbsolute(input)
            || LooksLikeWindowsDrivePrefix(input))
        {
            return ProjectRelativePathSyntaxResult.Fail(
                options.RejectEmptyOrDotSegments
                    ? "Path must not be absolute, a URI, or inline data."
                    : "Path must be project-relative.");
        }

        // Re-check rooted after common slash normalization for mixed separators.
        string normalized = input
            .Replace('\\', '/')
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        if (Path.IsPathRooted(normalized) || LooksLikeWindowsAbsolute(normalized) || LooksLikeWindowsDrivePrefix(normalized))
        {
            return ProjectRelativePathSyntaxResult.Fail(
                options.RejectEmptyOrDotSegments
                    ? "Path must not be absolute, a URI, or inline data."
                    : "Path must be project-relative.");
        }

        string[] segments = normalized.Split('/');
        if (options.RejectEmptyOrDotSegments)
        {
            if (segments.Length == 0
                || segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
            {
                return ProjectRelativePathSyntaxResult.Fail("Path contains an unsafe segment.");
            }
        }
        else if (segments.Any(segment => segment == ".."))
        {
            return ProjectRelativePathSyntaxResult.Fail("Path must not contain parent-directory traversal.");
        }

        if (!options.AllowInternalDirectory)
        {
            bool hitsInternal = options.InternalDirectoryPolicy switch
            {
                ProjectInternalDirectoryPolicy.AnySegment =>
                    segments.Any(IsSkippedDirectoryName),
                _ => IsSkippedDirectoryName(segments.FirstOrDefault(static s => !string.IsNullOrEmpty(s))),
            };
            if (hitsInternal)
            {
                return ProjectRelativePathSyntaxResult.Fail("Path points to an internal or build directory.");
            }
        }

        if (options.RequireImageExtension)
        {
            IReadOnlySet<string> allowed = options.AllowedExtensionsWithDot ?? AllowedImageExtensionsWithDot;
            if (!allowed.Contains(Path.GetExtension(normalized)))
            {
                return ProjectRelativePathSyntaxResult.Fail(
                    "ImagePath must be a project-relative png/jpg/jpeg/webp path.");
            }
        }

        return ProjectRelativePathSyntaxResult.Ok(normalized);
    }

    /// <summary>
    /// Combines pure relative syntax checks with absolute containment under a project root.
    /// Does not require the file to exist.
    /// </summary>
    public static bool TryResolveUnderProjectRoot(
        string projectRoot,
        string relativePath,
        ProjectRelativePathSyntaxOptions options,
        out string absolutePath,
        out string logicalRelativePath,
        out string errorMessage)
    {
        absolutePath = string.Empty;
        logicalRelativePath = string.Empty;
        errorMessage = string.Empty;

        ProjectRelativePathSyntaxResult syntax = TryNormalizeRelativePath(relativePath, options);
        if (!syntax.Success)
        {
            errorMessage = syntax.ErrorMessage;
            return false;
        }

        try
        {
            string root = NormalizeRoot(projectRoot);
            absolutePath = Path.GetFullPath(Path.Combine(
                root,
                syntax.NormalizedRelative.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsInsideRoot(root, absolutePath))
            {
                errorMessage = "Path escapes the current project root.";
                absolutePath = string.Empty;
                return false;
            }

            logicalRelativePath = ToLogicalRelativePath(root, absolutePath);
            if (logicalRelativePath.StartsWith("../", StringComparison.Ordinal)
                || logicalRelativePath == ".."
                || Path.IsPathRooted(logicalRelativePath))
            {
                errorMessage = "Path escapes the current project root.";
                absolutePath = string.Empty;
                logicalRelativePath = string.Empty;
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            errorMessage = exception.Message;
            absolutePath = string.Empty;
            logicalRelativePath = string.Empty;
            return false;
        }
    }
}
