#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AppAgasCs.Application;
using AppAgasCs.Core.Resources;

namespace AppAgasCs.Core.Persistence;

public sealed record ScenePackageExportResult(bool Success, string Message, string? PackagePath = null)
{
    [Obsolete("Use PackagePath. Scene packages are exported as ZIP archives.")]
    public string? PackageDirectory => PackagePath;

    public static ScenePackageExportResult Ok(string packagePath) =>
        new(true, "场景已导出。", packagePath);
    public static ScenePackageExportResult Fail(string message) => new(false, message);
}

public sealed record ScenePackageImportResult(
    bool Success,
    string Message,
    AgasSceneResource? Scene = null,
    IReadOnlyList<string>? CopiedRelativeImagePaths = null)
{
    public static ScenePackageImportResult Ok(AgasSceneResource scene, IReadOnlyList<string> copiedPaths) =>
        new(true, "场景已导入。", scene, copiedPaths);
    public static ScenePackageImportResult Fail(string message) => new(false, message);
}

/// <summary>
/// Manual scene package export/import under {project}/.agas/scene-exports/.
/// Schema v2 carries ordinary images and file integrity;
/// schema v1 remains import-compatible.
/// </summary>
public sealed class ScenePackageStore
{
    private readonly ScenePackagePathGuard _pathGuard = new();
    private readonly IScenePackageImageDecoder _imageDecoder = new GodotScenePackageImageDecoder();

    public ScenePackageExportResult Export(string projectRoot, AgasSceneResource scene)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(scene);

        string? stagingDirectory = null;
        try
        {
            string root = Path.GetFullPath(projectRoot);
            if (!Directory.Exists(root))
                return ScenePackageExportResult.Fail("项目目录不存在。");

            ScenePackageDocument source = ScenePackageCodec.FromScene(scene);
            string folderName = ScenePackageCodec.PackageFolderName(scene.Id, scene.Name);
            string packagePath = AgasPaths.SceneExportPackageArchive(root, folderName);
            Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
            stagingDirectory = Path.Combine(
                Path.GetDirectoryName(packagePath)!,
                ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);

            var integrity = new List<ScenePackageFileIntegrity>();
            IReadOnlyDictionary<string, AgasLayerResource> liveLayers = scene.LayersSnapshot
                .ToDictionary(layer => layer.Id, StringComparer.Ordinal);
            foreach (ScenePackageLayer layer in source.Layers)
            {
                if (!liveLayers.TryGetValue(layer.Id, out AgasLayerResource? liveLayer))
                    throw new InvalidDataException($"找不到场景图层：{layer.Id}");
                IReadOnlyDictionary<string, ImageInstanceResource> liveImages = liveLayer.ImagesSnapshot
                    .ToDictionary(image => image.Id, StringComparer.Ordinal);

                foreach (ScenePackageReferenceImage reference in layer.ReferenceImages)
                {
                    if (!liveImages.TryGetValue(reference.Id, out ImageInstanceResource? image))
                    {
                        throw new InvalidDataException($"找不到图层参考图：{layer.Name}/{reference.FileName}");
                    }
                    integrity.Add(CopyProjectImageToPackage(
                        root,
                        image.ImagePath,
                        stagingDirectory,
                        ScenePackageCodec.ReferenceRelativePath(layer.Id, reference.FileName),
                        ScenePackageFileType.ReferenceImage));
                }

                foreach (ScenePackageImage packageImage in layer.Images ?? Array.Empty<ScenePackageImage>())
                {
                    if (!liveImages.TryGetValue(packageImage.Id, out ImageInstanceResource? image))
                    {
                        throw new InvalidDataException($"找不到普通图片：{layer.Name}/{packageImage.Id}");
                    }
                    integrity.Add(CopyProjectImageToPackage(
                        root,
                        image.ImagePath,
                        stagingDirectory,
                        packageImage.FilePath,
                        ScenePackageFileType.CanvasImage));
                }
            }

            ScenePackageDocument document = source with
            {
                Integrity = integrity
                    .OrderBy(item => item.Path, StringComparer.Ordinal)
                    .ToArray(),
            };
            ScenePackageCodec.ValidateForTransport(document);
            File.WriteAllText(
                AgasPaths.ScenePackageManifestPath(stagingDirectory),
                ScenePackageCodec.Serialize(document));
            string stagingArchive = packagePath + ".staging-" + Guid.NewGuid().ToString("N");
            try
            {
                ScenePackageArchive.Create(stagingDirectory, stagingArchive);
                ReplaceFile(stagingArchive, packagePath);
            }
            finally
            {
                DeleteFileIfPresent(stagingArchive);
            }
            return ScenePackageExportResult.Ok(packagePath);
        }
        catch (Exception exception)
        {
            return ScenePackageExportResult.Fail($"导出失败：{exception.Message}");
        }
        finally
        {
            DeleteDirectoryIfPresent(stagingDirectory);
        }
    }

    public ScenePackageImportResult Import(string projectRoot, string packageManifestOrDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageManifestOrDirectory);

        string? stagingDirectory = null;
        string? extractedPackageDirectory = null;
        try
        {
            string root = Path.GetFullPath(projectRoot);
            if (!Directory.Exists(root))
                return ScenePackageImportResult.Fail("项目目录不存在。");

            string packageDirectory = ResolvePackageDirectory(
                packageManifestOrDirectory,
                out extractedPackageDirectory);
            string manifestPath = AgasPaths.ScenePackageManifestPath(packageDirectory);
            if (!File.Exists(manifestPath))
                return ScenePackageImportResult.Fail($"未找到 {AgasPaths.ScenePackageManifestFileName}。");

            ScenePackageDocument document = ScenePackageCodec.Deserialize(ReadManifest(manifestPath));
            ScenePackageCodec.ValidateForTransport(document);
            IReadOnlyDictionary<string, byte[]> files = document.SchemaVersion == ScenePackageCodec.CurrentSchemaVersion
                ? ReadVerifiedFiles(packageDirectory, document)
                : ReadLegacyFiles(packageDirectory, document);
            PreparedScene prepared = PrepareScene(root, document, files);
            stagingDirectory = prepared.StagingDirectory;
            if (Directory.Exists(prepared.FinalImageDirectory))
                throw new IOException("导入场景的图片目录已存在。");
            Directory.Move(prepared.StagingDirectory, prepared.FinalImageDirectory);
            stagingDirectory = null;
            return ScenePackageImportResult.Ok(prepared.Scene, prepared.CopiedRelativeImagePaths);
        }
        catch (Exception exception)
        {
            return ScenePackageImportResult.Fail($"导入失败：{exception.Message}");
        }
        finally
        {
            DeleteDirectoryIfPresent(stagingDirectory);
            DeleteDirectoryIfPresent(extractedPackageDirectory);
        }
    }

    private ScenePackageFileIntegrity CopyProjectImageToPackage(
        string projectRoot,
        string projectRelativePath,
        string packageDirectory,
        string packagePath,
        ScenePackageFileType type)
    {
        var options = new ProjectRelativePathSyntaxOptions(
            RequireImageExtension: true,
            MaxLength: 1024,
            RejectUntrimmedWhitespace: true,
            RejectEmptyOrDotSegments: true);
        if (!ProjectPathRules.TryResolveUnderProjectRoot(
                projectRoot,
                projectRelativePath,
                options,
                out string sourcePath,
                out _,
                out string error)
            || !_pathGuard.TryResolveExistingFile(projectRoot, projectRelativePath, out sourcePath))
        {
            throw new InvalidDataException($"图片路径无效：{projectRelativePath}（{error}）");
        }

        byte[] bytes = File.ReadAllBytes(sourcePath);
        if (bytes.Length == 0 || bytes.Length > ScenePackageSafetyLimits.MaximumImageBytes)
            throw new InvalidDataException($"图片文件大小无效：{projectRelativePath}");
        WritePackageFile(packageDirectory, packagePath, bytes);
        return ScenePackageIntegrity.Create(packagePath, bytes, type);
    }

    private IReadOnlyDictionary<string, byte[]> ReadVerifiedFiles(
        string packageDirectory,
        ScenePackageDocument document)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (ScenePackageFileIntegrity integrity in document.Integrity!)
        {
            if (!_pathGuard.TryResolveExistingFile(packageDirectory, integrity.Path, out string absolutePath))
                throw new InvalidDataException($"包内文件缺失或路径不安全：{integrity.Path}");
            if (integrity.Type is ScenePackageFileType.ReferenceImage or ScenePackageFileType.CanvasImage
                && integrity.Size > ScenePackageSafetyLimits.MaximumImageBytes)
            {
                throw new InvalidDataException($"包内图片超过大小限制：{integrity.Path}");
            }
            totalBytes = checked(totalBytes + integrity.Size);
            if (totalBytes > ScenePackageSafetyLimits.MaximumPackagePayloadBytes)
                throw new InvalidDataException("场景包内容超过总大小限制。");

            byte[] bytes = File.ReadAllBytes(absolutePath);
            ScenePackageIntegrity.Verify(integrity, bytes);
            if (integrity.Type is ScenePackageFileType.ReferenceImage or ScenePackageFileType.CanvasImage)
                ScenePackageImageInspector.Inspect(absolutePath, Path.GetExtension(integrity.Path), _imageDecoder);
            result.Add(integrity.Path, bytes);
        }
        return result;
    }

    private IReadOnlyDictionary<string, byte[]> ReadLegacyFiles(
        string packageDirectory,
        ScenePackageDocument document)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (ScenePackageLayer layer in document.Layers)
        {
            foreach (ScenePackageReferenceImage reference in layer.ReferenceImages)
            {
                string path = ScenePackageCodec.ReferenceRelativePath(layer.Id, reference.FileName);
                if (!_pathGuard.TryResolveExistingFile(packageDirectory, path, out string absolutePath))
                    throw new InvalidDataException($"包内缺少参考图：{layer.Id}/{reference.FileName}");
                byte[] bytes = File.ReadAllBytes(absolutePath);
                if (bytes.Length == 0 || bytes.Length > ScenePackageSafetyLimits.MaximumImageBytes)
                    throw new InvalidDataException($"参考图文件大小无效：{reference.FileName}");
                ScenePackageImageInspector.Inspect(
                    absolutePath,
                    Path.GetExtension(reference.FileName),
                    _imageDecoder);
                totalBytes = checked(totalBytes + bytes.Length);
                if (totalBytes > ScenePackageSafetyLimits.MaximumPackagePayloadBytes)
                    throw new InvalidDataException("场景包内容超过总大小限制。");
                result.Add(path, bytes);
            }
        }
        return result;
    }

    private static PreparedScene PrepareScene(
        string projectRoot,
        ScenePackageDocument document,
        IReadOnlyDictionary<string, byte[]> files)
    {
        string sceneId = $"scene-{Guid.NewGuid():N}";
        string sceneName = document.Name.Trim();
        AgasSceneResource scene = AgasProjectInitializer.CreateDefaultScene(sceneId, sceneName, 0);
        scene.Name = sceneName;
        scene.Layers.Clear();

        string imageRoot = Path.Combine(projectRoot, "assets", "scene-imports");
        Directory.CreateDirectory(imageRoot);
        string stagingDirectory = Path.Combine(imageRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        string finalDirectory = Path.Combine(imageRoot, sceneId);
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            var layerIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var copiedPaths = new List<string>();
            double legacyReferenceX = 0;

            foreach (ScenePackageLayer packageLayer in document.Layers.OrderBy(layer => layer.Order))
            {
                string layerId = $"layer-{Guid.NewGuid():N}";
                layerIds.Add(packageLayer.Id, layerId);
                var layer = new AgasLayerResource
                {
                    Id = layerId,
                    Name = packageLayer.Name,
                    Order = packageLayer.Order,
                    Visible = packageLayer.Visible,
                    Locked = packageLayer.Locked,
                    Shared = packageLayer.Shared,
                };

                foreach (ScenePackagePromptFragment fragment in packageLayer.PromptFragments.OrderBy(item => item.Order))
                {
                    layer.AddPromptFragment(new PromptFragmentResource
                    {
                        Id = $"prompt-{Guid.NewGuid():N}",
                        Text = fragment.Text ?? string.Empty,
                        Enabled = fragment.Enabled,
                        Order = fragment.Order,
                        Role = ScenePackageCodec.ParseRole(fragment.Role),
                    });
                }

                foreach (ScenePackageReferenceImage reference in packageLayer.ReferenceImages.OrderBy(item => item.Order))
                {
                    string packagePath = ScenePackageCodec.ReferenceRelativePath(packageLayer.Id, reference.FileName);
                    string imageId = $"image-{Guid.NewGuid():N}";
                    string relative = ImportedImagePath(sceneId, layerId, imageId, reference.FileName);
                    WriteStagedImage(stagingDirectory, layerId, imageId, reference.FileName, files[packagePath]);
                    copiedPaths.Add(relative);
                    var image = new ImageInstanceResource
                    {
                        Id = imageId,
                        ImagePath = relative,
                        Order = reference.Order,
                        X = legacyReferenceX,
                        Y = 0,
                        ScaleX = reference.ScaleX,
                        ScaleY = reference.ScaleY,
                        RotationDegrees = reference.RotationDegrees,
                        Opacity = reference.Opacity,
                    };
                    legacyReferenceX += 288;
                    layer.AddImage(image);
                }

                foreach (ScenePackageImage packageImage in
                         (packageLayer.Images ?? Array.Empty<ScenePackageImage>()).OrderBy(item => item.Order))
                {
                    string imageId = $"image-{Guid.NewGuid():N}";
                    string relative = ImportedImagePath(sceneId, layerId, imageId, packageImage.FilePath);
                    WriteStagedImage(stagingDirectory, layerId, imageId, packageImage.FilePath, files[packageImage.FilePath]);
                    copiedPaths.Add(relative);
                    var image = new ImageInstanceResource
                    {
                        Id = imageId,
                        ImagePath = relative,
                        Order = packageImage.Order,
                        X = packageImage.X,
                        Y = packageImage.Y,
                        ScaleX = packageImage.ScaleX,
                        ScaleY = packageImage.ScaleY,
                        RotationDegrees = packageImage.RotationDegrees,
                        Opacity = packageImage.Opacity,
                    };
                    layer.AddImage(image);
                }
                scene.AddLayer(layer);
            }

            if (scene.Layers.Count == 0)
                throw new InvalidDataException("导入结果没有图层。");
            scene.ActiveLayerId = document.ActiveLayerId != null
                && layerIds.TryGetValue(document.ActiveLayerId, out string? activeLayerId)
                    ? activeLayerId
                    : scene.Layers.OrderBy(layer => layer.Order).First().Id;
            return new PreparedScene(
                scene,
                stagingDirectory,
                finalDirectory,
                Array.AsReadOnly(copiedPaths.ToArray()));
        }
        catch
        {
            DeleteDirectoryIfPresent(stagingDirectory);
            throw;
        }
    }

    private static void WriteStagedImage(
        string stagingDirectory,
        string layerId,
        string imageId,
        string sourcePath,
        byte[] bytes)
    {
        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!ProjectPathRules.AllowedImageExtensionsWithDot.Contains(extension))
            throw new InvalidDataException($"不支持的图片格式：{sourcePath}");
        string destination = Path.Combine(stagingDirectory, layerId, imageId + extension);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, bytes);
    }

    private static string ImportedImagePath(
        string sceneId,
        string layerId,
        string imageId,
        string sourcePath) =>
        $"assets/scene-imports/{sceneId}/{layerId}/{imageId}{Path.GetExtension(sourcePath).ToLowerInvariant()}";

    private static void WritePackageFile(string packageDirectory, string packagePath, byte[] bytes)
    {
        string destination = Path.GetFullPath(Path.Combine(
            packageDirectory,
            packagePath.Replace('/', Path.DirectorySeparatorChar)));
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectory))
            + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(
                root,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("包内目标路径越界。");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, bytes);
    }

    private static string ResolvePackageDirectory(
        string packageManifestOrDirectory,
        out string? extractedPackageDirectory)
    {
        extractedPackageDirectory = null;
        string path = Path.GetFullPath(packageManifestOrDirectory);
        if (Directory.Exists(path)) return path;
        if (File.Exists(path)
            && string.Equals(
                Path.GetExtension(path),
                AgasPaths.ScenePackageArchiveExtension,
                StringComparison.OrdinalIgnoreCase))
        {
            extractedPackageDirectory = ScenePackageArchive.ExtractToTemporaryDirectory(path);
            return extractedPackageDirectory;
        }
        if (File.Exists(path)
            && string.Equals(
                Path.GetFileName(path),
                AgasPaths.ScenePackageManifestFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(path)
                ?? throw new InvalidDataException("无法解析场景包目录。");
        }
        throw new InvalidDataException("请选择 ZIP 场景包、scene.json 或包含 scene.json 的导出目录。");
    }

    private static string ReadManifest(string manifestPath)
    {
        var info = new FileInfo(manifestPath);
        if (!info.Exists || info.Length <= 0)
            throw new InvalidDataException("场景包 scene.json 为空。");
        if (info.Length > ScenePackageSafetyLimits.MaximumManifestBytes)
            throw new InvalidDataException("场景包 scene.json 超过大小限制。");
        return File.ReadAllText(manifestPath);
    }

    private static void ReplaceFile(string stagingPath, string destinationPath)
    {
        string? backup = null;
        try
        {
            if (File.Exists(destinationPath))
            {
                backup = destinationPath + ".backup-" + Guid.NewGuid().ToString("N");
                File.Move(destinationPath, backup);
            }
            File.Move(stagingPath, destinationPath);
            DeleteFileIfPresent(backup);
        }
        catch
        {
            if (!File.Exists(destinationPath)
                && backup != null
                && File.Exists(backup))
            {
                File.Move(backup, destinationPath);
            }
            throw;
        }
    }

    private static void DeleteDirectoryIfPresent(string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static void DeleteFileIfPresent(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
    }

    private sealed record PreparedScene(
        AgasSceneResource Scene,
        string StagingDirectory,
        string FinalImageDirectory,
        IReadOnlyList<string> CopiedRelativeImagePaths);
}
