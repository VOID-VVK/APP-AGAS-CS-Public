using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AppAgasCs.Features.Ai.Settings;

/// <summary>
/// Snapshot of provider descriptors loaded from AGAS_HOME/providers. A caller
/// gets a fresh snapshot on each load, so adding/removing a JSON file takes
/// effect when the UI is refreshed or a generation profile is resolved.
/// Invalid files are isolated and reported without hiding valid providers.
/// </summary>
public sealed class AgasAiProviderCatalog
{
    private const string SeedMarkerFileName = ".agas-provider-schema-1";
    private static readonly Regex SafeProviderId = new("^[a-z0-9][a-z0-9._-]{0,79}$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeAdapterKind = new("^[a-z0-9][a-z0-9._-]{0,79}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> CapabilityNames = new(StringComparer.Ordinal)
    {
        "textToImage", "referenceEdit", "regionEdit", "nativeAlphaMask", "guidedRegionEdit",
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IReadOnlyDictionary<string, AgasAiProviderDescriptor> _byId;

    private AgasAiProviderCatalog(
        IEnumerable<AgasAiProviderDescriptor> providers,
        IEnumerable<AgasAiProviderConfigurationError> errors)
    {
        Providers = new ReadOnlyCollection<AgasAiProviderDescriptor>(providers
            .OrderBy(provider => provider.Order)
            .ThenBy(provider => provider.DisplayName, StringComparer.Ordinal)
            .ThenBy(provider => provider.Id, StringComparer.Ordinal)
            .ToArray());
        Errors = new ReadOnlyCollection<AgasAiProviderConfigurationError>(errors.ToArray());
        _byId = Providers.ToDictionary(provider => provider.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<AgasAiProviderDescriptor> Providers { get; }
    public IReadOnlyList<AgasAiProviderConfigurationError> Errors { get; }

    public static AgasAiProviderCatalog Load(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        string directory = AgasAiSettingsPaths.ProvidersDirectory(Path.GetFullPath(homeDirectory));
        return LoadDirectory(directory, seedDefaults: true);
    }

    /// <summary>Loads exactly one directory without seeding, for tests and managed deployments.</summary>
    public static AgasAiProviderCatalog LoadFromDirectory(string providerDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerDirectory);
        return LoadDirectory(Path.GetFullPath(providerDirectory), seedDefaults: false);
    }

    private static AgasAiProviderCatalog LoadDirectory(string directory, bool seedDefaults)
    {
        IReadOnlyDictionary<string, AgasAiProviderDescriptor> builtIns;
        if (seedDefaults) builtIns = SeedDefaultsOnce(directory);
        else
        {
            Directory.CreateDirectory(directory);
            builtIns = new Dictionary<string, AgasAiProviderDescriptor>(StringComparer.Ordinal);
        }

        var providers = new List<AgasAiProviderDescriptor>();
        var errors = new List<AgasAiProviderConfigurationError>();
        var candidates = new List<(string Path, AgasAiProviderDescriptor Provider)>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            try
            {
                AgasAiProviderDescriptor provider = ReadAndValidate(path);
                if (builtIns.TryGetValue(provider.Id, out AgasAiProviderDescriptor? builtIn)
                    && HasCompatibleRequestContract(provider, builtIn))
                {
                    // Built-in presets are product capabilities: compatible built-in
                    // providers always expose them without rewriting the user file.
                    provider = MergeBuiltInModels(provider, builtIn);
                }
                Validate(provider);
                candidates.Add((path, provider));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                errors.Add(new AgasAiProviderConfigurationError(
                    Path.GetFileName(path),
                    exception is InvalidDataException ? exception.Message : "配置无法读取或不是有效 JSON。"));
            }
        }

        foreach (IGrouping<string, (string Path, AgasAiProviderDescriptor Provider)> group in
                 candidates.GroupBy(item => item.Provider.Id, StringComparer.Ordinal))
        {
            if (group.Count() == 1)
            {
                providers.Add(group.Single().Provider);
                continue;
            }
            foreach ((string path, _) in group)
            {
                errors.Add(new AgasAiProviderConfigurationError(
                    Path.GetFileName(path),
                    $"provider id '{group.Key}' 重复；所有同 ID 配置均已隔离。"));
            }
        }

        return new AgasAiProviderCatalog(providers, errors);
    }

    public AgasAiProviderDescriptor? FindByKnownModel(string? modelName, string? preferredProviderId = null)
    {
        string normalized = modelName?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            return null;
        }

        if (preferredProviderId != null && TryGet(preferredProviderId, out var preferred))
        {
            if (preferred.IsKnownModel(normalized)) return preferred;
            if (preferred.Id == AgasAiProviderIds.AxonCatNewApi) return null;
        }
        return Providers.FirstOrDefault(provider => provider.IsKnownModel(normalized)
            && (preferredProviderId == null || provider.Id != AgasAiProviderIds.AxonCatNewApi));
    }

    public bool TryGet(string providerId, out AgasAiProviderDescriptor descriptor) =>
        _byId.TryGetValue(providerId?.Trim() ?? string.Empty, out descriptor!);

    public AgasAiProviderDescriptor GetRequired(string providerId) => TryGet(providerId, out AgasAiProviderDescriptor descriptor)
        ? descriptor
        : throw new InvalidDataException($"AI Provider '{providerId}' 未安装或配置无效。");

    private static AgasAiProviderDescriptor ReadAndValidate(string path)
    {
        AgasAiProviderDescriptor descriptor = ReadDescriptor(path);
        Validate(descriptor);
        return descriptor;
    }

    private static AgasAiProviderDescriptor ReadDescriptor(string path)
    {
        FileInfo info = new(path);
        if (info.Length > 256 * 1024)
        {
            throw new InvalidDataException("Provider 配置不能超过 256 KiB。");
        }

        byte[] json = File.ReadAllBytes(path);
        if (json.Length > 256 * 1024)
            throw new InvalidDataException("Provider 配置不能超过 256 KiB。");
        using (JsonDocument raw = JsonDocument.Parse(json, new JsonDocumentOptions
               {
                   AllowTrailingCommas = false,
                   CommentHandling = JsonCommentHandling.Disallow,
                   MaxDepth = 16,
               }))
        {
            RejectSecretFields(raw.RootElement);
        }

        AgasAiProviderDescriptor descriptor = JsonSerializer.Deserialize<AgasAiProviderDescriptor>(json, JsonOptions)
            ?? throw new InvalidDataException("Provider 配置不能为空。");
        return descriptor;
    }

    private static void Validate(AgasAiProviderDescriptor descriptor)
    {
        if (descriptor.SchemaVersion != AgasAiProviderDescriptor.CurrentSchemaVersion)
            throw new InvalidDataException("不支持的 Provider schemaVersion。");
        if (!SafeProviderId.IsMatch(descriptor.Id ?? string.Empty))
            throw new InvalidDataException("Provider id 必须是 1-80 位小写字母、数字、点、下划线或连字符。");
        if (string.IsNullOrWhiteSpace(descriptor.DisplayName) || descriptor.DisplayName.Length > 120)
            throw new InvalidDataException("Provider displayName 必须是 1-120 个字符。");
        if (!SafeAdapterKind.IsMatch(descriptor.AdapterKind ?? string.Empty))
            throw new InvalidDataException("adapterKind 必须是 1-80 位小写字母、数字、点、下划线或连字符。");
        if (!TryParseSecureBaseUri(descriptor.DefaultBaseUrl))
            throw new InvalidDataException("defaultBaseUrl 必须是安全 HTTPS 地址；仅 loopback 允许 HTTP，且不能含凭据、查询或片段。");
        if (descriptor.Models.Count == 0
            || descriptor.Models.Any(model => model == null)
            || descriptor.Models.Count(model => model.Recommended) != 1)
            throw new InvalidDataException("models 必须包含且只能包含一个 recommended 模型。");
        if (descriptor.Models.Any(model => string.IsNullOrWhiteSpace(model.ModelName)
                                           || model.ModelName.Length > 200
                                           || string.IsNullOrWhiteSpace(model.Label)
                                           || model.Label.Length > 200)
            || descriptor.Models.Select(model => model.ModelName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != descriptor.Models.Count)
            throw new InvalidDataException("models 包含无效或重复的模型预设。");
        if (descriptor.Capabilities.Count == 0
            || descriptor.Capabilities.Any(capability => !CapabilityNames.Contains(capability))
            || descriptor.Capabilities.Distinct(StringComparer.Ordinal).Count() != descriptor.Capabilities.Count)
            throw new InvalidDataException("capabilities 包含无效或重复的能力。");
        if (descriptor.Credential == null
            || !string.Equals(descriptor.Credential.Kind, "profileSettings", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(descriptor.Credential.Label)
            || descriptor.Credential.Label.Length > 120
            || !string.Equals(descriptor.Credential.SettingsKey, "apiKey", StringComparison.Ordinal))
            throw new InvalidDataException("credential 必须引用 profileSettings/apiKey；Provider 文件不得保存密钥。");
        if (descriptor.BaseUrlHint.Length > 1000)
            throw new InvalidDataException("baseUrlHint 过长。");
        if (!TryParseSecureBaseUri(descriptor.BaseUrlPlaceholder))
            throw new InvalidDataException("baseUrlPlaceholder 必须是安全 HTTPS 地址；仅 loopback 允许 HTTP。");
    }

    private static bool TryParseSecureBaseUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);

    private static void RejectSecretFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string normalized = property.Name.Replace("_", string.Empty, StringComparison.Ordinal)
                    .Replace("-", string.Empty, StringComparison.Ordinal);
                if (normalized.Equals("apikey", StringComparison.OrdinalIgnoreCase)
                    || normalized.Equals("token", StringComparison.OrdinalIgnoreCase)
                    || normalized.Equals("secret", StringComparison.OrdinalIgnoreCase)
                    || normalized.Equals("password", StringComparison.OrdinalIgnoreCase))
                {
                    // The schema permits only the literal credential.settingsKey
                    // reference; a value named apiKey anywhere else is a secret risk.
                    bool isReference = property.Name == "settingsKey"
                        && property.Value.ValueKind == JsonValueKind.String
                        && property.Value.GetString() == "apiKey";
                    if (!isReference)
                        throw new InvalidDataException("Provider 配置禁止包含 API Key、token、secret 或 password。");
                }
                RejectSecretFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray()) RejectSecretFields(item);
        }
    }

    private static IReadOnlyDictionary<string, AgasAiProviderDescriptor> SeedDefaultsOnce(string directory)
    {
        Directory.CreateDirectory(directory);
        string marker = Path.Combine(directory, SeedMarkerFileName);

        string? defaultsDirectory = FindDefaultsDirectory();
        if (defaultsDirectory == null)
        {
            return new Dictionary<string, AgasAiProviderDescriptor>(StringComparer.Ordinal);
        }
        if (!File.Exists(marker))
        {
            foreach (string source in Directory.EnumerateFiles(defaultsDirectory, "*.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal))
            {
                if (Path.GetFileName(source) == "05-axoncat-newapi.json") continue;
                WriteIfMissing(Path.Combine(directory, Path.GetFileName(source)), File.ReadAllText(source));
            }
            File.WriteAllText(marker, "1\n", new UTF8Encoding(false));
        }
        SeedNewApiTemplate(directory, defaultsDirectory);
        return LoadBuiltInProviders(defaultsDirectory);
    }

    private static IReadOnlyDictionary<string, AgasAiProviderDescriptor> LoadBuiltInProviders(string defaultsDirectory)
    {
        var candidates = new List<AgasAiProviderDescriptor>();
        foreach (string source in Directory.EnumerateFiles(defaultsDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                candidates.Add(ReadAndValidate(source));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // A bad packaged descriptor remains isolated like a bad user descriptor.
            }
        }

        return candidates.GroupBy(provider => provider.Id, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
    }

    private static AgasAiProviderDescriptor MergeBuiltInModels(
        AgasAiProviderDescriptor existing,
        AgasAiProviderDescriptor builtIn)
    {
        var knownNames = existing.Models.Select(model => model.ModelName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool hasRecommended = existing.Models.Any(model => model.Recommended);
        AgasAiModelPreset[] additions = builtIn.Models
            .Where(model => !knownNames.Contains(model.ModelName))
            .Select(model => hasRecommended && model.Recommended
                ? model with { Recommended = false }
                : model)
            .ToArray();
        if (additions.Length == 0) return existing;

        return new AgasAiProviderDescriptor(
            existing.SchemaVersion,
            existing.Id,
            existing.DisplayName,
            existing.AdapterKind,
            existing.DefaultBaseUrl,
            existing.Models.Concat(additions).ToArray(),
            existing.Capabilities,
            existing.Credential,
            existing.BaseUrlHint,
            existing.BaseUrlPlaceholder,
            existing.Order);
    }

    private static bool HasCompatibleRequestContract(
        AgasAiProviderDescriptor existing,
        AgasAiProviderDescriptor builtIn)
    {
        if (!string.Equals(existing.AdapterKind, builtIn.AdapterKind, StringComparison.Ordinal)
            || !string.Equals(existing.Credential.Kind, builtIn.Credential.Kind, StringComparison.Ordinal)
            || !string.Equals(existing.Credential.SettingsKey, builtIn.Credential.SettingsKey, StringComparison.Ordinal)
            || !existing.Capabilities.ToHashSet(StringComparer.Ordinal).SetEquals(builtIn.Capabilities))
        {
            return false;
        }

        // Adapter kind fixes the HTTP method and wire protocol. The endpoint origin
        // remains user-owned, while its API path must match the packaged contract.
        var existingUri = new Uri(existing.DefaultBaseUrl, UriKind.Absolute);
        var builtInUri = new Uri(builtIn.DefaultBaseUrl, UriKind.Absolute);
        return string.Equals(
            NormalizeEndpointPath(existingUri.AbsolutePath),
            NormalizeEndpointPath(builtInUri.AbsolutePath),
            StringComparison.Ordinal);
    }

    private static string NormalizeEndpointPath(string path)
    {
        string normalized = path.TrimEnd('/');
        return normalized.Length == 0 ? "/" : normalized;
    }

    private static void SeedNewApiTemplate(string directory, string defaultsDirectory)
    {
        string marker = Path.Combine(directory, ".agas-provider-newapi-1");
        if (File.Exists(marker)) return;
        string source = Path.Combine(defaultsDirectory, "05-axoncat-newapi.json");
        if (!File.Exists(source)) return;
        bool hasProvider = Directory.EnumerateFiles(directory, "*.json").Any(path =>
        {
            try { return ReadAndValidate(path).Id == AgasAiProviderIds.AxonCatNewApi; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { return false; }
        });
        if (!hasProvider)
            WriteIfMissing(Path.Combine(directory, Path.GetFileName(source)), File.ReadAllText(source));
        File.WriteAllText(marker, "1\n", new UTF8Encoding(false));
    }

    private static string? FindDefaultsDirectory()
    {
        string packagedDirectory = Path.Combine(AppContext.BaseDirectory, "ai-providers.defaults");
        return Directory.Exists(packagedDirectory) ? packagedDirectory : null;
    }

    private static void WriteIfMissing(string path, string contents)
    {
        if (!File.Exists(path)) File.WriteAllText(path, contents, new UTF8Encoding(false));
    }
}
