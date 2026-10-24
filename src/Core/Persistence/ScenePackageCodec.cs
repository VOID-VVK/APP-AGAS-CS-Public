#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AppAgasCs.Core.Resources;

namespace AppAgasCs.Core.Persistence;

/// <summary>
/// Interchange package for scene layers, prompts, and images.
/// Schema v1 contains only layer reference images. Schema v2 can additionally include
/// ordinary canvas images and file integrity.
/// </summary>
public sealed record ScenePackageDocument(
    int SchemaVersion,
    string SourceSceneId,
    string Name,
    string? ActiveLayerId,
    IReadOnlyList<ScenePackageLayer> Layers,
    IReadOnlyList<ScenePackageFileIntegrity>? Integrity = null);

public sealed record ScenePackageLayer(
    string Id,
    string Name,
    int Order,
    bool Visible,
    bool Locked,
    IReadOnlyList<ScenePackagePromptFragment> PromptFragments,
    IReadOnlyList<ScenePackageReferenceImage> ReferenceImages,
    IReadOnlyList<ScenePackageImage>? Images = null,
    bool Shared = false);

public sealed record ScenePackagePromptFragment(
    string Id,
    string Text,
    bool Enabled,
    int Order,
    string Role);

public sealed record ScenePackageReferenceImage(
    string Id,
    string FileName,
    int Order,
    double ScaleX,
    double ScaleY,
    double RotationDegrees,
    double Opacity);

public static class ScenePackageCodec
{
    public const int LegacySchemaVersion = 1;
    public const int CurrentSchemaVersion = 2;
    public const string ReferencesDirectoryName = "references";
    public const string ImagesDirectoryName = "images";
    public const int MaximumManifestBytes = 2 * 1024 * 1024;
    public const int MaximumJsonDepth = 64;

    private static readonly JsonSerializerOptions LegacyJsonOptions = CreateJsonOptions(
        propertyNameCaseInsensitive: true,
        rejectUnknownProperties: false);
    private static readonly JsonSerializerOptions V2JsonOptions = CreateJsonOptions(
        propertyNameCaseInsensitive: false,
        rejectUnknownProperties: true);
    private static readonly Regex SafeIdentifier = new(
        "^[a-z0-9][a-z0-9_-]{0,79}$",
        RegexOptions.CultureInvariant);

    private static JsonSerializerOptions CreateJsonOptions(
        bool propertyNameCaseInsensitive,
        bool rejectUnknownProperties) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PropertyNameCaseInsensitive = propertyNameCaseInsensitive,
        UnmappedMemberHandling = rejectUnknownProperties
            ? JsonUnmappedMemberHandling.Disallow
            : JsonUnmappedMemberHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
        },
    };

    public static ScenePackageDocument FromScene(AgasSceneResource scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var layers = scene.LayersSnapshot
            .OrderBy(layer => layer.Order)
            .Select(layer => new ScenePackageLayer(
                layer.Id,
                layer.Name,
                layer.Order,
                layer.Visible,
                layer.Locked,
                layer.PromptFragmentsSnapshot
                    .OrderBy(fragment => fragment.Order)
                    .Select(fragment => new ScenePackagePromptFragment(
                        fragment.Id,
                        fragment.Text ?? string.Empty,
                        fragment.Enabled,
                        fragment.Order,
                        fragment.Role.ToString()))
                    .ToArray(),
                Array.Empty<ScenePackageReferenceImage>(),
                layer.Shared
                    ? layer.ImagesSnapshot
                        .OrderBy(image => image.Order)
                        .ThenBy(image => image.Id, StringComparer.Ordinal)
                        .Select(image => new ScenePackageImage(
                            image.Id,
                            ImageRelativePath(layer.Id, image.Id, image.ImagePath),
                            image.Order,
                            image.X,
                            image.Y,
                            image.ScaleX,
                            image.ScaleY,
                            image.RotationDegrees,
                            image.Opacity))
                        .ToArray()
                    : Array.Empty<ScenePackageImage>(),
                layer.Shared))
            .ToArray();

        return new ScenePackageDocument(
            CurrentSchemaVersion,
            scene.Id,
            scene.Name,
            scene.ActiveLayerId,
            layers);
    }

    public static string Serialize(ScenePackageDocument document)
    {
        Validate(document);
        JsonSerializerOptions options = document.SchemaVersion == LegacySchemaVersion
            ? LegacyJsonOptions
            : V2JsonOptions;
        string json = JsonSerializer.Serialize(document, options);
        if (Encoding.UTF8.GetByteCount(json) > MaximumManifestBytes)
            throw new InvalidDataException($"场景包 JSON 不能超过 {MaximumManifestBytes} 字节。");
        return json;
    }

    public static ScenePackageDocument Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("场景包 JSON 为空。");
        }

        if (Encoding.UTF8.GetByteCount(json) > MaximumManifestBytes)
            throw new InvalidDataException($"场景包 JSON 不能超过 {MaximumManifestBytes} 字节。");

        try
        {
            using JsonDocument raw = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumJsonDepth,
            });
            if (raw.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("场景包 JSON 根节点必须是对象。");
            RejectDuplicateProperties(raw.RootElement);
            int schemaVersion = ReadSchemaVersion(raw.RootElement);
            JsonSerializerOptions options = schemaVersion == LegacySchemaVersion
                ? LegacyJsonOptions
                : V2JsonOptions;
            string normalized = schemaVersion == CurrentSchemaVersion
                ? StripEmptyDemoDependencyFields(raw.RootElement)
                : json;
            ScenePackageDocument document = JsonSerializer.Deserialize<ScenePackageDocument>(normalized, options)
                ?? throw new InvalidDataException("场景包 JSON 无效。");
            Validate(document);
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("场景包不是有效的严格 JSON。", exception);
        }
    }

    public static void Validate(ScenePackageDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion is not (LegacySchemaVersion or CurrentSchemaVersion))
        {
            throw new InvalidDataException($"不支持的场景包 schemaVersion：{document.SchemaVersion}。");
        }
        if (string.IsNullOrWhiteSpace(document.Name))
        {
            throw new InvalidDataException("场景包名称不能为空。");
        }
        if (document.Layers == null || document.Layers.Count == 0)
        {
            throw new InvalidDataException("场景包至少需要一个图层。");
        }

        if (document.SchemaVersion == LegacySchemaVersion)
        {
            ValidateLegacy(document);
            return;
        }

        RequireSafeIdentifier(document.SourceSceneId, "sourceSceneId");
        var layerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var promptIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packagePaths = new Dictionary<string, ScenePackageFileType>(StringComparer.OrdinalIgnoreCase);
        foreach (ScenePackageLayer layer in document.Layers)
        {
            if (layer == null)
                throw new InvalidDataException("场景包图层不能包含 null。");
            RequireSafeIdentifier(layer.Id, "图层 ID");
            if (!layerIds.Add(layer.Id))
            {
                throw new InvalidDataException("场景包图层 ID 重复或存在大小写冲突。");
            }
            if (string.IsNullOrWhiteSpace(layer.Name))
            {
                throw new InvalidDataException($"图层 {layer.Id} 名称不能为空。");
            }
            foreach (ScenePackagePromptFragment prompt in layer.PromptFragments ?? Array.Empty<ScenePackagePromptFragment>())
            {
                if (prompt == null)
                    throw new InvalidDataException($"图层 {layer.Id} 提示词不能包含 null。");
                RequireSafeIdentifier(prompt.Id, $"图层 {layer.Id} 提示词 ID");
                if (!promptIds.Add(prompt.Id))
                    throw new InvalidDataException($"图层 {layer.Id} 提示词 ID 无效、重复或存在大小写冲突。");
                if (!IsKnownRole(prompt.Role))
                    throw new InvalidDataException($"图层 {layer.Id} 提示词 {prompt.Id} 的 role 无效。");
            }

            foreach (ScenePackageReferenceImage reference in layer.ReferenceImages ?? Array.Empty<ScenePackageReferenceImage>())
            {
                if (reference == null)
                    throw new InvalidDataException($"图层 {layer.Id} 参考图不能包含 null。");
                RequireSafeIdentifier(reference.Id, $"图层 {layer.Id} 参考图 ID");
                if (!imageIds.Add(reference.Id))
                    throw new InvalidDataException($"图层 {layer.Id} 参考图 ID 无效、重复或存在大小写冲突。");
                if (!IsSafeBasename(reference.FileName))
                    throw new InvalidDataException($"图层 {layer.Id} 参考图文件名无效：{reference.FileName}");
                ValidateTransform(reference.ScaleX, reference.ScaleY, reference.RotationDegrees, reference.Opacity, reference.Id);
                AddDeclaredPath(
                    packagePaths,
                    ReferenceRelativePath(layer.Id, reference.FileName),
                    ScenePackageFileType.ReferenceImage);
            }

            foreach (ScenePackageImage image in layer.Images ?? Array.Empty<ScenePackageImage>())
            {
                if (image == null)
                    throw new InvalidDataException($"图层 {layer.Id} 普通图片不能包含 null。");
                RequireSafeIdentifier(image.Id, $"图层 {layer.Id} 普通图片 ID");
                if (!imageIds.Add(image.Id))
                    throw new InvalidDataException($"图层 {layer.Id} 普通图片 ID 无效、重复或存在大小写冲突。");
                RequirePackagePath(image.FilePath, $"普通图片 {image.Id}");
                RequireImageExtension(image.FilePath, $"普通图片 {image.Id}");
                ValidateTransform(image.ScaleX, image.ScaleY, image.RotationDegrees, image.Opacity, image.Id);
                if (!double.IsFinite(image.X) || !double.IsFinite(image.Y))
                    throw new InvalidDataException($"图片 {image.Id} 坐标必须是有限数值。");
                AddDeclaredPath(packagePaths, image.FilePath, ScenePackageFileType.CanvasImage);
            }
        }

        if (!string.IsNullOrWhiteSpace(document.ActiveLayerId)
            && !layerIds.Contains(document.ActiveLayerId))
            throw new InvalidDataException("activeLayerId 未指向包内图层。");

        ValidateIntegrity(document.Integrity ?? Array.Empty<ScenePackageFileIntegrity>(), packagePaths);
    }

    /// <summary>
    /// Strict publication/import gate. Exporters may use <see cref="Validate"/> while
    /// assembling a document, but every schema-v2 file must have integrity metadata
    /// before the document crosses a transport boundary.
    /// </summary>
    public static void ValidateForTransport(ScenePackageDocument document)
    {
        Validate(document);
        if (document.SchemaVersion != CurrentSchemaVersion) return;

        int expectedFileCount = document.Layers.Sum(layer =>
            (layer.ReferenceImages?.Count ?? 0) + (layer.Images?.Count ?? 0));
        int integrityCount = document.Integrity?.Count ?? 0;
        if (integrityCount != expectedFileCount)
            throw new InvalidDataException("schemaVersion 2 transport package requires complete integrity metadata.");
    }

    public static string PackageFolderName(string sceneId, string sceneName)
    {
        string safeName = string.Concat((sceneName ?? "scene")
            .Trim()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-'));
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "scene";
        }
        string safeId = string.IsNullOrWhiteSpace(sceneId) ? Guid.NewGuid().ToString("N")[..8] : sceneId;
        return $"{safeName}-{safeId}";
    }

    public static string ReferenceRelativePath(string layerId, string fileName) =>
        $"{ReferencesDirectoryName}/{layerId}/{fileName}";

    public static string ImageRelativePath(string layerId, string imageId, string sourcePath)
    {
        string extension = Path.GetExtension((sourcePath ?? string.Empty).Replace('\\', '/')).ToLowerInvariant();
        return $"{ImagesDirectoryName}/{layerId}/{imageId}{extension}";
    }

    public static PromptFragmentRole ParseRole(string? role)
    {
        if (Enum.TryParse(role, ignoreCase: true, out PromptFragmentRole parsed))
        {
            return parsed;
        }
        return PromptFragmentRole.General;
    }

    private static void ValidateLegacy(ScenePackageDocument document)
    {
        var layerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScenePackageLayer layer in document.Layers)
        {
            if (layer == null || string.IsNullOrWhiteSpace(layer.Id) || !layerIds.Add(layer.Id))
                throw new InvalidDataException("场景包图层 ID 无效或重复。");
            if (string.IsNullOrWhiteSpace(layer.Name))
                throw new InvalidDataException($"图层 {layer.Id} 名称不能为空。");
            foreach (ScenePackageReferenceImage reference in
                     layer.ReferenceImages ?? Array.Empty<ScenePackageReferenceImage>())
            {
                if (reference == null || !IsLegacySafeBasename(reference.FileName))
                    throw new InvalidDataException($"图层 {layer.Id} 参考图文件名无效：{reference?.FileName}");
            }
            if ((layer.Images?.Count ?? 0) != 0)
                throw new InvalidDataException("schemaVersion 1 不能包含普通图片。");
        }
        if ((document.Integrity?.Count ?? 0) != 0)
            throw new InvalidDataException("schemaVersion 1 不能包含 v2 字段。");
    }

    private static void ValidateIntegrity(
        IReadOnlyList<ScenePackageFileIntegrity> integrity,
        IReadOnlyDictionary<string, ScenePackageFileType> declaredPaths)
    {
        var integrityPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ScenePackageFileIntegrity file in integrity)
        {
            if (file == null) throw new InvalidDataException("integrity 不能包含 null。");
            RequirePackagePath(file.Path, "integrity path");
            if (!integrityPaths.Add(file.Path))
                throw new InvalidDataException($"integrity path 重复或存在大小写冲突：{file.Path}");
            if (file.Size <= 0) throw new InvalidDataException($"integrity size 无效：{file.Path}");
            RequireDigest(file.Sha256, $"integrity sha256：{file.Path}");
            if (file.Type == ScenePackageFileType.Unspecified || !Enum.IsDefined(file.Type))
                throw new InvalidDataException($"integrity type 无效：{file.Path}");
            if (!declaredPaths.TryGetValue(file.Path, out ScenePackageFileType expectedType))
                throw new InvalidDataException($"integrity 包含未声明文件：{file.Path}");
            if (file.Type != expectedType)
                throw new InvalidDataException($"integrity type 与声明文件类型不匹配：{file.Path}");
            ValidateIntegrityTypePath(file);
        }

        // Empty integrity remains valid while an exporter is assembling a v2 document.
        // Once any entry is supplied, the inventory must cover every declared package file.
        if (integrity.Count > 0 && declaredPaths.Keys.Any(path => !integrityPaths.Contains(path)))
            throw new InvalidDataException("integrity 未覆盖所有声明的包文件。");
    }

    private static void ValidateIntegrityTypePath(ScenePackageFileIntegrity file)
    {
        switch (file.Type)
        {
            case ScenePackageFileType.ReferenceImage:
            case ScenePackageFileType.CanvasImage:
                RequireImageExtension(file.Path, "integrity image");
                break;
            default:
                throw new InvalidDataException($"integrity type 无效：{file.Path}");
        }
    }

    private static string StripEmptyDemoDependencyFields(JsonElement root)
    {
        string[] discardedFields = { "blueprintBindings", "blueprints", "modules" };
        if (!discardedFields.Any(name => root.TryGetProperty(name, out _)))
            return root.GetRawText();

        JsonObject normalized = JsonNode.Parse(root.GetRawText())!.AsObject();
        foreach (string field in discardedFields)
        {
            if (!root.TryGetProperty(field, out JsonElement value)) continue;
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 0)
                throw new InvalidDataException($"场景包已不支持 {field} 数据。");
            normalized.Remove(field);
        }
        return normalized.ToJsonString();
    }

    private static int ReadSchemaVersion(JsonElement root)
    {
        JsonProperty? versionProperty = null;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "schemaVersion", StringComparison.OrdinalIgnoreCase))
                versionProperty = property;
        }
        if (versionProperty is not JsonProperty found
            || found.Value.ValueKind != JsonValueKind.Number
            || !found.Value.TryGetInt32(out int version))
            throw new InvalidDataException("场景包 schemaVersion 缺失或类型无效。");
        if (version is not (LegacySchemaVersion or CurrentSchemaVersion))
            throw new InvalidDataException($"不支持的场景包 schemaVersion：{version}。");
        if (version == CurrentSchemaVersion
            && !string.Equals(found.Name, "schemaVersion", StringComparison.Ordinal))
            throw new InvalidDataException("schemaVersion 2 属性名称必须使用 camelCase。");
        return version;
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException($"场景包包含重复或大小写冲突的 JSON 属性：{property.Name}");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private static bool IsSafeBasename(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Contains('/')
        && !value.Contains('\\')
        && !value.Contains("..", StringComparison.Ordinal)
        && !value.Contains(':')
        && !value.Any(char.IsControl)
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && !value.EndsWith(".", StringComparison.Ordinal);

    private static bool IsLegacySafeBasename(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Contains('/')
        && !value.Contains('\\')
        && !value.Contains("..", StringComparison.Ordinal);

    private static void RequirePackagePath(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 512
            || value.Contains('\\')
            || value.Contains(':')
            || value.Contains('\0')
            || value.Any(char.IsControl)
            || value.StartsWith("/", StringComparison.Ordinal)
            || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || Path.IsPathRooted(value))
            throw new InvalidDataException($"{label} 必须是安全的包内相对路径。");

        string[] segments = value.Split('/');
        if (segments.Any(segment =>
                segment.Length == 0
                || segment is "." or ".."
                || !string.Equals(segment, segment.Trim(), StringComparison.Ordinal)
                || segment.EndsWith(".", StringComparison.Ordinal)))
            throw new InvalidDataException($"{label} 包含非法路径段。");
        if (string.Equals(value, "scene.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{label} 不能指向场景包 manifest。");
    }

    private static void AddDeclaredPath(
        Dictionary<string, ScenePackageFileType> paths,
        string path,
        ScenePackageFileType type)
    {
        RequirePackagePath(path, "包文件路径");
        if (!paths.TryAdd(path, type))
            throw new InvalidDataException($"包文件路径重复或存在大小写冲突：{path}");
    }

    private static void RequireImageExtension(string path, string label)
    {
        string extension = Path.GetExtension(path);
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp"))
            throw new InvalidDataException($"{label} 图片类型无效。");
    }

    private static void ValidateTransform(
        double scaleX,
        double scaleY,
        double rotationDegrees,
        double opacity,
        string imageId)
    {
        if (!double.IsFinite(scaleX) || !double.IsFinite(scaleY)
            || scaleX == 0 || scaleY == 0
            || !double.IsFinite(rotationDegrees)
            || !double.IsFinite(opacity)
            || opacity is < 0 or > 1)
            throw new InvalidDataException($"图片 {imageId} transform 无效。");
    }

    private static void RequireSafeIdentifier(string? value, string label)
    {
        if (value == null || !SafeIdentifier.IsMatch(value))
            throw new InvalidDataException($"{label} 无效。");
    }

    private static void RequireDigest(string? value, string label)
    {
        if (value == null || value.Length != 64 || value.Any(ch => ch is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException($"{label} 必须是 64 位小写 SHA-256 hex。");
    }

    private static bool IsKnownRole(string? value) => value is
        "General" or "Environment" or "NegativeConstraint"
        or "general" or "environment" or "negativeConstraint";
}
