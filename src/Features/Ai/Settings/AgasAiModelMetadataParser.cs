using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AppAgasCs.Features.Ai.Settings;

public static class AgasAiModelMetadataParser
{
    public static IReadOnlyList<AgasAiUnifiedModel> Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Array) throw new JsonException("模型接口返回格式不正确。");
        var models = new Dictionary<string, AgasAiUnifiedModel>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement row in data.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            string id = row.TryGetProperty("id", out JsonElement rawId) && rawId.ValueKind == JsonValueKind.String
                ? rawId.GetString() ?? string.Empty : string.Empty;
            if (id.Length is < 1 or > 200 || !Regex.IsMatch(id, "\\A[A-Za-z0-9][A-Za-z0-9._/-]{0,199}\\z")) continue;
            bool hasMetadata = row.TryGetProperty("image_generation", out JsonElement metadata)
                && metadata.ValueKind == JsonValueKind.Object;
            string family = hasMetadata ? Text(metadata, "family") : InferFamily(id);
            if (!hasMetadata && family.Length == 0) continue;
            string category = family.ToLowerInvariant() switch { "gpt" => "gpt", "gemini" => "gemini", "jimeng" => "jimeng", _ => "other" };
            string protocol = hasMetadata ? Text(metadata, "protocol") : category == "gemini" ? "chat-completions" : "openai-images";
            string wire = protocol switch { "openai-images" => "openai-compatible", "chat-completions" => "newapi-chat-image", _ => string.Empty };
            var capabilities = new List<string>();
            string[] rawCapabilities = hasMetadata ? Strings(metadata, "capabilities") : InferredCapabilities(category);
            if (rawCapabilities.Contains("text-to-image", StringComparer.Ordinal)) capabilities.Add("textToImage");
            if (category != "other" && rawCapabilities.Contains("image-to-image", StringComparer.Ordinal)) capabilities.Add("referenceEdit");
            if (category == "gpt" && wire == "openai-compatible" && rawCapabilities.Contains("image-edit", StringComparer.Ordinal))
                capabilities.AddRange(new[] { "regionEdit", "nativeAlphaMask" });
            else if (wire == "newapi-chat-image" && capabilities.Contains("referenceEdit"))
                capabilities.AddRange(new[] { "regionEdit", "guidedRegionEdit" });
            if (wire.Length == 0 || !capabilities.Contains("textToImage")) capabilities.Clear();
            JsonElement defaults = hasMetadata && metadata.TryGetProperty("defaults", out JsonElement value) ? value : default;
            string[] ratios = Strings(metadata, "ratios").Where(item => Regex.IsMatch(item, "^[0-9]{1,3}:[0-9]{1,3}$")).ToArray();
            string[] resolutions = Strings(metadata, "resolutions").Where(item => Regex.IsMatch(item, "^[1-9][0-9]?[kK]$")).Select(item => item.ToLowerInvariant()).ToArray();
            string defaultResolution = Text(defaults, "resolution").ToLowerInvariant();
            if (!resolutions.Contains(defaultResolution, StringComparer.Ordinal)) defaultResolution = resolutions.FirstOrDefault() ?? string.Empty;
            string defaultRatio = Text(defaults, "aspect_ratio");
            if (!ratios.Contains(defaultRatio, StringComparer.Ordinal)) defaultRatio = ratios.FirstOrDefault() ?? string.Empty;
            int? maxImages = hasMetadata && metadata.TryGetProperty("max_images", out JsonElement max)
                && max.ValueKind == JsonValueKind.Number && max.TryGetInt32(out int limit) && limit > 0 ? limit : null;
            string label = Text(row, "display_name");
            if (label.Length == 0) label = AgasAiUnifiedModelCatalog.TryGet(id, out AgasAiUnifiedModel fallback) ? fallback.DisplayName : id;
            models[id] = new AgasAiUnifiedModel(id, label, wire, capabilities, category, ratios, resolutions, defaultResolution, defaultRatio, maxImages);
        }
        return Array.AsReadOnly(models.Values.ToArray());
    }

    private static string Text(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out JsonElement text) && text.ValueKind == JsonValueKind.String
            ? new string((text.GetString() ?? string.Empty).Trim().Where(character => !char.IsControl(character)).Take(200).ToArray()) : string.Empty;

    private static string[] Strings(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out JsonElement array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty).Where(item => item.Length is > 0 and <= 40).Distinct(StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();

    private static string InferFamily(string id)
    {
        if (Regex.IsMatch(id, "(?:^|[-_/])(?:video|seedance)(?:[-_/]|$)", RegexOptions.IgnoreCase)) return string.Empty;
        if (id.StartsWith("gpt-image-", StringComparison.OrdinalIgnoreCase)) return "gpt";
        if (Regex.IsMatch(id, "^(?:gemini-.*image(?:-|$)|nano-banana(?:-|$))", RegexOptions.IgnoreCase)) return "gemini";
        if (Regex.IsMatch(id, "^(?:jimeng-image-|(?:doubao-)?seedream(?:-|$))", RegexOptions.IgnoreCase)) return "jimeng";
        if (id.StartsWith("grok-imagine-image", StringComparison.OrdinalIgnoreCase)) return "grok";
        return string.Empty;
    }

    private static string[] InferredCapabilities(string category) => category switch
    {
        "gpt" => new[] { "text-to-image", "image-to-image", "image-edit" },
        "gemini" => new[] { "text-to-image", "image-to-image" },
        _ => new[] { "text-to-image" },
    };
}
