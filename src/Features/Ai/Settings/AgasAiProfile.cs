using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace AppAgasCs.Features.Ai.Settings;

[method: JsonConstructor]
public sealed record AgasAiProfile(
    string Id,
    string Name,
    string ProviderId,
    string ModelName,
    string BaseUrl,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ApiKey = null)
{
    public const string DefaultBaseUrl = "https://ainewdev.cc";

    public static AgasAiProfile CreateDefault() => new(
        Guid.NewGuid().ToString("N"),
        "New API",
        AgasAiProviderIds.AxonCatNewApi,
        AgasAiModelCatalog.DefaultGptImageModel,
        DefaultBaseUrl);
}

public sealed record AgasAiProfilesDocument
{
    [JsonConstructor]
    public AgasAiProfilesDocument(
        int resourceVersion,
        string? activeProfileId,
        string? activeModelName,
        IReadOnlyList<AgasAiProfile>? profiles)
    {
        ResourceVersion = resourceVersion;
        ActiveProfileId = activeProfileId;
        ActiveModelName = activeModelName ?? string.Empty;
        Profiles = Array.AsReadOnly((profiles ?? Array.Empty<AgasAiProfile>()).ToArray());
    }

    public AgasAiProfilesDocument(
        int resourceVersion,
        string? activeProfileId,
        IReadOnlyList<AgasAiProfile>? profiles)
        : this(resourceVersion, activeProfileId, ResolveLegacyActiveModel(activeProfileId, profiles), profiles)
    {
    }

    public int ResourceVersion { get; }
    public string? ActiveProfileId { get; }
    public string ActiveModelName { get; }
    public IReadOnlyList<AgasAiProfile> Profiles { get; }
    public const int CurrentResourceVersion = 5;

    public static AgasAiProfilesDocument Empty { get; } =
        new(CurrentResourceVersion, null, AgasAiModelCatalog.DefaultGptImageModel, Array.Empty<AgasAiProfile>());

    internal static string ResolveLegacyActiveModel(
        string? activeProfileId,
        IReadOnlyList<AgasAiProfile>? profiles)
    {
        IReadOnlyList<AgasAiProfile> candidates = profiles ?? Array.Empty<AgasAiProfile>();
        AgasAiProfile? active = candidates.FirstOrDefault(profile =>
            string.Equals(profile.Id, activeProfileId, StringComparison.Ordinal));
        if (IsValidActiveModelName(active?.ModelName))
        {
            return active!.ModelName.Trim();
        }

        AgasAiProfile? fallback = candidates.FirstOrDefault(profile =>
            IsValidActiveModelName(profile.ModelName));
        return fallback == null
            ? AgasAiModelCatalog.DefaultGptImageModel
            : fallback.ModelName.Trim();
    }

    internal static bool IsValidActiveModelName(string? modelName)
    {
        string normalized = modelName?.Trim() ?? string.Empty;
        return normalized.Length is >= 1 and <= 200
            && !normalized.Any(character => character <= '\u001f' || character == '\u007f');
    }
}
