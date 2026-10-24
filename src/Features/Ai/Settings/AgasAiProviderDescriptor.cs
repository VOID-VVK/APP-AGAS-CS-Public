using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AppAgasCs.Features.Ai.Settings;

public static class AgasAiProviderIds
{
    public const string AxonCatNewApi = "axoncat-newapi";
    public const string OpenAiCompatible = "openai-compatible";
    public const string GeminiCompatible = "gemini-compatible";

    internal static string FromLegacyProviderKind(string? providerKind) => providerKind?.Trim() switch
    {
        "gptImage" or "GptImage" => OpenAiCompatible,
        "nanoBanana" or "NanoBanana" => GeminiCompatible,
        _ => throw new ArgumentOutOfRangeException(nameof(providerKind)),
    };
}

public sealed record AgasAiCredentialDescriptor(string Kind, string Label, string SettingsKey);

public sealed record AgasAiProviderDescriptor
{
    public const int CurrentSchemaVersion = 1;

    public AgasAiProviderDescriptor(
        int schemaVersion,
        string id,
        string displayName,
        string adapterKind,
        string defaultBaseUrl,
        IReadOnlyList<AgasAiModelPreset> models,
        IReadOnlyList<string> capabilities,
        AgasAiCredentialDescriptor credential,
        string? baseUrlHint = null,
        string? baseUrlPlaceholder = null,
        int order = 100)
    {
        SchemaVersion = schemaVersion;
        Id = id;
        DisplayName = displayName;
        AdapterKind = adapterKind;
        DefaultBaseUrl = defaultBaseUrl;
        Models = new ReadOnlyCollection<AgasAiModelPreset>((models ?? Array.Empty<AgasAiModelPreset>()).ToArray());
        Capabilities = new ReadOnlyCollection<string>((capabilities ?? Array.Empty<string>()).ToArray());
        Credential = credential;
        BaseUrlHint = baseUrlHint ?? string.Empty;
        BaseUrlPlaceholder = baseUrlPlaceholder ?? defaultBaseUrl;
        Order = order;
    }

    public int SchemaVersion { get; }
    public string Id { get; }
    public string DisplayName { get; }
    public string AdapterKind { get; }
    public string DefaultBaseUrl { get; }
    public IReadOnlyList<AgasAiModelPreset> Models { get; }
    public IReadOnlyList<string> Capabilities { get; }
    public AgasAiCredentialDescriptor Credential { get; }
    public string BaseUrlHint { get; }
    public string BaseUrlPlaceholder { get; }
    public int Order { get; }
    public string DefaultModelName => Models.First(model => model.Recommended).ModelName;

    public bool HasCapability(string capability) => Capabilities.Contains(capability, StringComparer.Ordinal);

    public bool IsKnownModel(string? modelName) => Models.Any(model =>
        string.Equals(model.ModelName, modelName?.Trim(), StringComparison.OrdinalIgnoreCase));

    public AgasAiModelSwitchResult ResolveSwitchFrom(
        AgasAiProviderDescriptor? previousProvider,
        string? currentModelName)
    {
        string current = currentModelName?.Trim() ?? string.Empty;
        bool replace = current.Length == 0 || (previousProvider?.IsKnownModel(current) ?? false);
        if (replace)
        {
            return new AgasAiModelSwitchResult(
                DefaultModelName,
                !string.Equals(current, DefaultModelName, StringComparison.Ordinal),
                PreservedCustomModel: false);
        }

        return new AgasAiModelSwitchResult(
            current,
            Changed: false,
            PreservedCustomModel: !IsKnownModel(current));
    }
}

public sealed record AgasAiProviderConfigurationError(string FileName, string Message);
