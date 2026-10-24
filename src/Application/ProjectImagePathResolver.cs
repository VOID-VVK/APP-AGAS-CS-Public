#nullable enable

using System;
using System.IO;
using AppAgasCs.Core.Persistence;

namespace AppAgasCs.Application;

public sealed record ProjectImagePathResult(bool Success, string AbsolutePath, string RelativePath)
{
    public static ProjectImagePathResult Rejected { get; } = new(false, string.Empty, string.Empty);
}

/// <summary>
/// Narrow, UI-safe resolver for project-relative image assets.
/// Path safety rules live in <see cref="ProjectPathRules"/>; this type only applies
/// image existence / reparse checks and returns a simple success/reject result.
/// </summary>
public sealed class ProjectImagePathResolver
{
    public ProjectImagePathResult Resolve(string projectRoot, string relativePath, bool mustExist = true)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)
            || string.IsNullOrWhiteSpace(relativePath)
            || !Directory.Exists(projectRoot))
        {
            return ProjectImagePathResult.Rejected;
        }

        try
        {
            string root = ProjectPathRules.NormalizeRoot(projectRoot);
            DirectoryInfo rootInfo = new(root);
            if (ProjectPathRules.IsReparseOrLink(rootInfo))
            {
                return ProjectImagePathResult.Rejected;
            }

            var syntaxOptions = new ProjectRelativePathSyntaxOptions(
                AllowInternalDirectory: false,
                InternalDirectoryPolicy: ProjectInternalDirectoryPolicy.FirstSegmentOnly,
                RequireImageExtension: true,
                AllowedExtensionsWithDot: ProjectPathRules.AllowedImageExtensionsWithDot);

            if (!ProjectPathRules.TryResolveUnderProjectRoot(
                    root,
                    relativePath,
                    syntaxOptions,
                    out string absolute,
                    out string logicalRelative,
                    out _))
            {
                return ProjectImagePathResult.Rejected;
            }

            if (ProjectPathRules.PathContainsReparseDirectory(root, absolute))
            {
                return ProjectImagePathResult.Rejected;
            }

            if (mustExist)
            {
                if (!File.Exists(absolute) || ProjectPathRules.IsReparsePointFile(absolute))
                {
                    return ProjectImagePathResult.Rejected;
                }
            }

            return new ProjectImagePathResult(true, absolute, logicalRelative);
        }
        catch
        {
            return ProjectImagePathResult.Rejected;
        }
    }
}
