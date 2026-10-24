#nullable enable

using System;
using System.IO;

namespace AppAgasCs.Features.Tools.Execution;

internal static class ImageToolProjectPaths
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static string ValidateRoot(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !Path.IsPathFullyQualified(projectRoot))
            throw Invalid("A fully qualified project root is required.");
        string root = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(root) || IsLink(root))
            throw Invalid("Project root must be an existing ordinary directory.");
        return root;
    }

    public static string ExistingImage(string root, string relativePath)
    {
        string path = Resolve(root, relativePath);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp"))
            throw Invalid("Input must be a PNG, JPEG, or WebP image.");
        ValidateSegments(root, path, requireFile: true);
        return path;
    }

    public static string NewPng(string root, string relativePath)
    {
        string path = Resolve(root, relativePath);
        if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            throw Invalid("Output must be a PNG image.");
        ValidateSegments(root, Path.GetDirectoryName(path)!, requireFile: false);
        if (File.Exists(path) || Directory.Exists(path) || IsLink(path))
            throw new ImageToolRunException(ImageToolRunErrorCodes.OutputExists, "Output path already exists.");
        return path;
    }

    public static void RevalidateOutput(string root, string path)
    {
        ValidateSegments(root, Path.GetDirectoryName(path)!, requireFile: false);
        if (File.Exists(path) || Directory.Exists(path) || IsLink(path))
            throw new ImageToolRunException(ImageToolRunErrorCodes.OutputExists, "Output path already exists.");
    }

    private static string Resolve(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw Invalid("A project-relative path is required.");
        string normalized = relativePath.Replace('\\', '/');
        foreach (string segment in normalized.Split('/'))
            if (segment is "" or "." or ".." || segment.Contains(':'))
                throw Invalid("Path contains an unsafe segment.");
        string path = Path.GetFullPath(Path.Combine(root, normalized));
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, PathComparison))
            throw Invalid("Path must remain inside the project root.");
        return path;
    }

    private static void ValidateSegments(string root, string target, bool requireFile)
    {
        string relative = Path.GetRelativePath(root, target);
        string path = root;
        string[] parts = relative.Split(Path.DirectorySeparatorChar);
        for (int index = 0; index < parts.Length; index++)
        {
            path = Path.Combine(path, parts[index]);
            bool isFinal = index == parts.Length - 1;
            if (IsLink(path)) throw Invalid("Symlinks and reparse points are not accepted inside a project image path.");
            if (isFinal && requireFile)
            {
                if (!File.Exists(path) || Directory.Exists(path)) throw Invalid("Input image does not exist as a regular file.");
            }
            else if (!Directory.Exists(path))
            {
                throw Invalid("Image parent directory does not exist.");
            }
        }
    }

    private static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static ImageToolRunException Invalid(string message) =>
        new(ImageToolRunErrorCodes.PathInvalid, message);
}
