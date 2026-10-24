using System;

namespace AppAgasCs.Features.Ai.Settings;

public sealed record AgasAiModelPreset(string ModelName, string Label, bool Recommended = false);

public sealed record AgasAiModelSwitchResult(string ModelName, bool Changed, bool PreservedCustomModel);

/// <summary>
/// Central model-name guidance for provider types. Model names remain editable
/// because relay stations can expose custom aliases; the catalog only replaces
/// empty names or exact catalog presets from the previously selected type.
/// Prefix-shaped relay aliases are never treated as provider-owned models.
/// </summary>
public static class AgasAiModelCatalog
{
    public const string DefaultGptImageModel = "gpt-image-2";
    public const string LegacyGptImageDefault = "gpt-image-1";
    public const string DefaultNanoBananaModel = "gemini-3-pro-image-preview";

    public static AgasAiProfile MigrateVersion1Profile(AgasAiProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string normalized = Normalize(profile.ModelName);
        bool mismatched = string.Equals(profile.ProviderId, AgasAiProviderIds.OpenAiCompatible, StringComparison.Ordinal)
            ? IsKnownGeminiModel(normalized)
            : string.Equals(profile.ProviderId, AgasAiProviderIds.GeminiCompatible, StringComparison.Ordinal)
              && IsKnownOpenAiModel(normalized);
        if (!mismatched)
        {
            return profile;
        }

        string defaultModel = string.Equals(profile.ProviderId, AgasAiProviderIds.GeminiCompatible, StringComparison.Ordinal)
            ? DefaultNanoBananaModel
            : DefaultGptImageModel;
        return profile with { ModelName = defaultModel };
    }

    private static bool IsKnownOpenAiModel(string modelName) =>
        string.Equals(modelName, DefaultGptImageModel, StringComparison.OrdinalIgnoreCase)
        || string.Equals(modelName, LegacyGptImageDefault, StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownGeminiModel(string modelName) =>
        string.Equals(modelName, DefaultNanoBananaModel, StringComparison.OrdinalIgnoreCase)
        || string.Equals(modelName, "gemini-2.5-flash-image", StringComparison.OrdinalIgnoreCase)
        || string.Equals(modelName, "gemini-3.1-flash-image-preview", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;
}
