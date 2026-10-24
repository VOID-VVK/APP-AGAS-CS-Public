#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Mcp.Contracts;

namespace AppAgasCs.Mcp;

/// <summary>
/// MCP-facing path validation. Shared safety rules live in <see cref="ProjectPathRules"/>;
/// this type maps failures to MCP error codes and owns MCP-only concerns (asset listing, .agas targets).
/// </summary>
public sealed class AgasMcpPathValidator
{
    public static IReadOnlySet<string> DefaultAssetExtensions { get; } =
        ProjectPathRules.AllowedImageExtensionsWithoutDot;

    public AgasMcpPathValidationResult ValidateProjectRoot(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
        {
            return AgasMcpPathValidationResult.Fail(
                AgasMcpErrorCodes.ProjectRootUnsafe,
                "Current project root is unavailable or unsafe.");
        }

        try
        {
            DirectoryInfo root = new(ProjectPathRules.NormalizeRoot(projectRoot));
            if (ProjectPathRules.IsReparseOrLink(root))
            {
                return AgasMcpPathValidationResult.Fail(
                    AgasMcpErrorCodes.ProjectRootUnsafe,
                    "Current project root is unsafe for MCP operations.");
            }
        }
        catch
        {
            return AgasMcpPathValidationResult.Fail(
                AgasMcpErrorCodes.ProjectRootUnsafe,
                "Current project root is unsafe for MCP operations.");
        }

        return AgasMcpPathValidationResult.Ok(ProjectPathRules.NormalizeRoot(projectRoot), string.Empty);
    }

    public AgasMcpPathValidationResult ValidateImageFile(string projectRoot, string relativePath, bool mustExist = true)
    {
        AgasMcpPathValidationResult root = ValidateProjectRoot(projectRoot);
        if (!root.Success)
        {
            return root;
        }

        AgasMcpPathValidationResult path = NormalizeProjectRelativePath(
            root.AbsolutePath,
            relativePath,
            requireImageExtension: true);
        if (!path.Success)
        {
            return path;
        }

        if (mustExist && !File.Exists(path.AbsolutePath))
        {
            return AgasMcpPathValidationResult.Fail(
                AgasMcpErrorCodes.AssetNotFound,
                "Image asset was not found or is not accessible.");
        }

        if (mustExist && ProjectPathRules.IsReparsePointFile(path.AbsolutePath))
        {
            return AgasMcpPathValidationResult.Fail(
                AgasMcpErrorCodes.PathRejected,
                "ImagePath must not reference a symlink or reparse-point file.");
        }

        if (ProjectPathRules.PathContainsReparseDirectory(root.AbsolutePath, path.AbsolutePath))
        {
            return AgasMcpPathValidationResult.Fail(
                AgasMcpErrorCodes.PathRejected,
                "ImagePath must not pass through a symlink or reparse-point directory.");
        }

        return path;
    }

    public AgasMcpPathValidationResult ValidateAgasProjectFile(string projectRoot) =>
        ValidateAgasTargetFile(
            projectRoot,
            $"{AgasPaths.AgasDirectoryName}/{AgasPaths.ProjectFileName}",
            "Project file");

    public AgasMcpPathValidationResult ValidateAgasLayoutFile(string projectRoot) =>
        ValidateAgasTargetFile(
            projectRoot,
            $"{AgasPaths.AgasDirectoryName}/{AgasPaths.WorkspaceLayoutFileName}",
            "Workspace layout file");

    public AgasMcpPathValidationResult ValidateAgasSceneFile(string projectRoot, string sceneId)
    {
        if (!IsSafeIdentifier(sceneId))
        {
            return AgasMcpPathValidationResult.Fail(
                AgasMcpErrorCodes.PathRejected,
                "SceneId must be 1-120 characters and contain only ASCII letters, digits, underscore, or hyphen.");
        }

        return ValidateAgasTargetFile(
            projectRoot,
            $"{AgasPaths.AgasDirectoryName}/{AgasPaths.ScenesDirectoryName}/{sceneId}.tres",
            "Scene file");
    }

    public AgasMcpPathValidationResult ValidateAgasTargetFile(
        string projectRoot,
        string relativePath,
        string targetDescription)
    {
        AgasMcpPathValidationResult root = ValidateProjectRoot(projectRoot);
        if (!root.Success)
        {
            return root;
        }

        AgasMcpPathValidationResult path = NormalizeProjectRelativePath(
            root.AbsolutePath,
            relativePath,
            requireImageExtension: false,
            allowInternalDirectory: true);
        if (!path.Success)
        {
            return path;
        }

        string normalizedRelative = path.RelativePath;
        if (!normalizedRelative.StartsWith($"{AgasPaths.AgasDirectoryName}/", StringComparison.Ordinal)
            || normalizedRelative.Equals(AgasPaths.AgasDirectoryName, StringComparison.Ordinal))
        {
            return AgasMcpPathValidationResult.Fail(
                AgasMcpErrorCodes.PathRejected,
                $"{targetDescription} must be inside .agas.");
        }

        string fileName = Path.GetFileName(path.AbsolutePath);
        if (!fileName.EndsWith(".tres", StringComparison.OrdinalIgnoreCase))
        {
            return AgasMcpPathValidationResult.Fail(
                AgasMcpErrorCodes.PathRejected,
                $"{targetDescription} must be a .tres file.");
        }

        if (ProjectPathRules.PathContainsReparseDirectory(root.AbsolutePath, path.AbsolutePath))
        {
            return AgasMcpPathValidationResult.Fail(
                AgasMcpErrorCodes.PathRejected,
                $"{targetDescription} parent chain must not pass through a symlink or reparse-point directory.");
        }

        if (File.Exists(path.AbsolutePath) && ProjectPathRules.IsReparsePointFile(path.AbsolutePath))
        {
            return AgasMcpPathValidationResult.Fail(
                AgasMcpErrorCodes.PathRejected,
                $"{targetDescription} must not be a symlink or reparse-point file.");
        }

        string tempPath = path.AbsolutePath + ".tmp.tres";
        if ((File.Exists(tempPath) && ProjectPathRules.IsReparsePointFile(tempPath)) || Directory.Exists(tempPath))
        {
            return AgasMcpPathValidationResult.Fail(
                AgasMcpErrorCodes.PathRejected,
                $"{targetDescription} temporary save path is unsafe.");
        }

        return path;
    }

    public AgasMcpPathValidationResult NormalizeProjectRelativePath(
        string projectRoot,
        string relativePath,
        bool requireImageExtension,
        bool allowInternalDirectory = false)
    {
        // Preserve historical MCP failure messages for the common reject classes.
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return AgasMcpPathValidationResult.Fail(
                AgasMcpErrorCodes.PathRejected,
                "Path must be a non-empty project-relative path.");
        }

        string input = relativePath.Trim();
        if (ProjectPathRules.ContainsUrlOrBase64(input)
            || input.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return AgasMcpPathValidationResult.Fail(
                AgasMcpErrorCodes.PathRejected,
                "Path must not be a URL, file URI, data URI, or base64-like value.");
        }

        if (Path.IsPathRooted(input)
            || ProjectPathRules.LooksLikeWindowsAbsolute(input)
            || ProjectPathRules.LooksLikeWindowsDrivePrefix(input))
        {
            return AgasMcpPathValidationResult.Fail(
                AgasMcpErrorCodes.PathRejected,
                "Path must be project-relative.");
        }

        var options = new ProjectRelativePathSyntaxOptions(
            AllowInternalDirectory: allowInternalDirectory,
            InternalDirectoryPolicy: ProjectInternalDirectoryPolicy.FirstSegmentOnly,
            RequireImageExtension: requireImageExtension,
            AllowedExtensionsWithDot: ProjectPathRules.AllowedImageExtensionsWithDot);

        if (!ProjectPathRules.TryResolveUnderProjectRoot(
                projectRoot,
                input,
                options,
                out string absolute,
                out string logical,
                out string errorMessage))
        {
            return AgasMcpPathValidationResult.Fail(
                AgasMcpErrorCodes.PathRejected,
                string.IsNullOrWhiteSpace(errorMessage)
                    ? "Path escapes the current project root."
                    : errorMessage);
        }

        return AgasMcpPathValidationResult.Ok(absolute, logical);
    }

    public IReadOnlyList<AgasMcpAssetItemDto> ListAssets(
        string projectRoot,
        string query,
        IReadOnlySet<string> extensions)
    {
        AgasMcpPathValidationResult root = ValidateProjectRoot(projectRoot);
        if (!root.Success)
        {
            return Array.AsReadOnly(Array.Empty<AgasMcpAssetItemDto>());
        }

        string rootPath = root.AbsolutePath;
        if (!Directory.Exists(rootPath))
        {
            return Array.AsReadOnly(Array.Empty<AgasMcpAssetItemDto>());
        }

        string normalizedQuery = query.Trim();
        List<AgasMcpAssetItemDto> result = new();
        Stack<DirectoryInfo> stack = new();
        stack.Push(new DirectoryInfo(rootPath));

        while (stack.Count > 0)
        {
            DirectoryInfo current = stack.Pop();
            DirectoryInfo[] directories;
            FileInfo[] files;
            try
            {
                directories = current.GetDirectories();
                files = current.GetFiles();
            }
            catch
            {
                continue;
            }

            foreach (DirectoryInfo directory in directories.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (ProjectPathRules.IsSkippedDirectoryName(directory.Name)
                    || ProjectPathRules.IsReparseOrLink(directory))
                {
                    continue;
                }

                stack.Push(directory);
            }

            foreach (FileInfo file in files)
            {
                if (ProjectPathRules.IsReparseOrLink(file))
                {
                    continue;
                }

                string extension = file.Extension.TrimStart('.').ToLowerInvariant();
                if (!extensions.Contains(extension))
                {
                    continue;
                }

                string relative = ToLogicalRelativePath(rootPath, file.FullName);
                if (!string.IsNullOrEmpty(normalizedQuery)
                    && relative.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) == false)
                {
                    continue;
                }

                result.Add(new AgasMcpAssetItemDto(
                    relative,
                    extension,
                    file.Length,
                    new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds()));
            }
        }

        return Array.AsReadOnly(
            result.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static bool IsAllowedImageExtension(string path) =>
        ProjectPathRules.IsAllowedImageExtension(path);

    public static bool IsSafeIdentifier(string? value) =>
        ProjectPathRules.IsSafeAsciiIdentifier(value);

    public static string ToLogicalRelativePath(string projectRoot, string absolutePath) =>
        ProjectPathRules.ToLogicalRelativePath(projectRoot, absolutePath);

    public static string NormalizeRoot(string projectRoot) =>
        ProjectPathRules.NormalizeRoot(projectRoot);
}

public sealed record AgasMcpPathValidationResult(
    bool Success,
    string Code,
    string Message,
    string AbsolutePath,
    string RelativePath)
{
    public static AgasMcpPathValidationResult Ok(string absolutePath, string relativePath) =>
        new(true, string.Empty, string.Empty, absolutePath, relativePath);

    public static AgasMcpPathValidationResult Fail(string code, string message) =>
        new(false, code, message, string.Empty, string.Empty);
}
