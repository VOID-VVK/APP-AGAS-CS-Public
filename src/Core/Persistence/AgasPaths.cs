using System;
using System.IO;

namespace AppAgasCs.Core.Persistence;

public static class AgasPaths
{
    public const int MaxSafeIdLength = 120;
    public const string AgasDirectoryName = ".agas";
    public const string ScenesDirectoryName = "scenes";
    public const string SceneExportsDirectoryName = "scene-exports";
    public const string ProjectFileName = "project.tres";
    public const string WorkspaceLayoutFileName = "workspace-layout.tres";
    public const string ScenePackageManifestFileName = "scene.json";
    public const string ScenePackageArchiveExtension = ".zip";

    public static string AgasDirectory(string projectRoot) => Path.Combine(projectRoot, AgasDirectoryName);
    public static string ScenesDirectory(string projectRoot) => Path.Combine(AgasDirectory(projectRoot), ScenesDirectoryName);
    public static string SceneExportsDirectory(string projectRoot) => Path.Combine(AgasDirectory(projectRoot), SceneExportsDirectoryName);
    public static string ProjectFile(string projectRoot) => Path.Combine(AgasDirectory(projectRoot), ProjectFileName);
    public static string WorkspaceLayoutFile(string projectRoot) => Path.Combine(AgasDirectory(projectRoot), WorkspaceLayoutFileName);
    public static string SceneFile(string projectRoot, string sceneId) => Path.Combine(ScenesDirectory(projectRoot), $"{sceneId}.tres");
    public static string SceneExportPackageDirectory(string projectRoot, string packageFolderName) =>
        Path.Combine(SceneExportsDirectory(projectRoot), packageFolderName);
    public static string SceneExportPackageArchive(string projectRoot, string packageFolderName) =>
        Path.Combine(SceneExportsDirectory(projectRoot), packageFolderName + ScenePackageArchiveExtension);
    public static string ScenePackageManifestPath(string packageDirectory) =>
        Path.Combine(packageDirectory, ScenePackageManifestFileName);

    public static bool IsSafeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaxSafeIdLength)
        {
            return false;
        }

        foreach (char value in id)
        {
            bool safe = value is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '-' or '_';
            if (!safe)
            {
                return false;
            }
        }

        return true;
    }

    public static string ToProjectRelativePath(string projectRoot, string absolutePath)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(projectRoot), Path.GetFullPath(absolutePath));
        return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    public static string ToAbsoluteProjectPath(string projectRoot, string projectRelativePath)
    {
        if (Path.IsPathRooted(projectRelativePath))
        {
            throw new ArgumentException("项目相对路径不能是绝对路径。", nameof(projectRelativePath));
        }

        string normalized = projectRelativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(projectRoot, normalized));
    }

}
