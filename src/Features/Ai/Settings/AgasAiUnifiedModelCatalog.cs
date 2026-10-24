using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AppAgasCs.Features.Ai.Generation;

namespace AppAgasCs.Features.Ai.Settings;

public sealed record AgasAiUnifiedModel
{
    internal AgasAiUnifiedModel(
        string modelName,
        string displayName,
        string wireAdapterKind,
        IEnumerable<string> capabilities,
        string category = "other",
        IEnumerable<string>? ratios = null,
        IEnumerable<string>? resolutions = null,
        string defaultResolution = "",
        string defaultRatio = "",
        int? maxImages = null)
    {
        ModelName = modelName;
        DisplayName = displayName;
        AdapterKind = AgasAiUnifiedModelCatalog.ConnectionAdapterKind;
        WireAdapterKind = wireAdapterKind;
        Capabilities = new ReadOnlyCollection<string>(capabilities.ToArray());
        Category = category;
        Ratios = Array.AsReadOnly((ratios ?? Array.Empty<string>()).ToArray());
        Resolutions = Array.AsReadOnly((resolutions ?? Array.Empty<string>()).ToArray());
        DefaultResolution = defaultResolution;
        DefaultRatio = defaultRatio;
        MaxImages = maxImages;
    }

    public string ModelName { get; }
    public string DisplayName { get; }
    public string AdapterKind { get; }
    public IReadOnlyList<string> Capabilities { get; }
    public string WireAdapterKind { get; }
    public string Category { get; }
    public IReadOnlyList<string> Ratios { get; }
    public IReadOnlyList<string> Resolutions { get; }
    public string DefaultResolution { get; }
    public string DefaultRatio { get; }
    public int? MaxImages { get; }
    public bool IsAvailable => WireAdapterKind is "openai-compatible" or "newapi-chat-image"
        && Capabilities.Contains("textToImage", StringComparer.Ordinal);
}

/// <summary>Dated offline fallback; live connection snapshots take precedence.</summary>
public static class AgasAiUnifiedModelCatalog
{
    public const string ConnectionAdapterKind = "newapi-compatible";
    public const string SnapshotDate = "2026-10-24";
    public const string GrokImagineImage20ModelName = AiGenerationQualityPolicy.GrokImagineImage20ModelName;
    internal const string ImagesWireAdapterKind = "openai-compatible";
    internal const string ChatWireAdapterKind = "newapi-chat-image";

    private static readonly string[] NativeEditCapabilities =
    {
        "textToImage", "referenceEdit", "regionEdit", "nativeAlphaMask",
    };

    private static readonly string[] GuidedEditCapabilities =
    {
        "textToImage", "referenceEdit", "regionEdit", "guidedRegionEdit",
    };

    private static readonly IReadOnlyList<AgasAiUnifiedModel> Catalog =
        new ReadOnlyCollection<AgasAiUnifiedModel>(new[]
        {
            Images("gpt-image-2", "GPT Image 2", NativeEditCapabilities),
            Images("gpt-image-2.5-flare", "GPT Image 2.5 Flare", NativeEditCapabilities),
            Images("gpt-image-2.5-sunburst", "GPT Image 2.5 Sunburst", NativeEditCapabilities),
            new AgasAiUnifiedModel("gemini-image", "gemini-image", ChatWireAdapterKind, GuidedEditCapabilities, "gemini"),
            Jimeng("5.0-lite", "2k"), Jimeng("2.0-pro", "1k"), Jimeng("3.0", "1k"), Jimeng("3.1", "1k"),
            Jimeng("4.0", "2k"), Jimeng("4.1", "2k"), Jimeng("4.5", "2k"), Jimeng("4.6", "2k"),
            Jimeng("4.7", "2k"), Jimeng("5.0-pro", "2k"), Jimeng("seedream-5.0-flash", "2k"), Jimeng("v8.2", "2k"),
        });

    private static readonly IReadOnlyDictionary<string, AgasAiUnifiedModel> ByModelName =
        Catalog.ToDictionary(model => model.ModelName, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<AgasAiUnifiedModel> Models => Catalog;

    public static bool TryGet(string? modelName, out AgasAiUnifiedModel model) =>
        ByModelName.TryGetValue(modelName?.Trim() ?? string.Empty, out model!);

    public static AgasAiModelSnapshot Snapshot(AgasAiProfile connection) =>
        AgasAiModelDiscovery.Shared.GetSnapshot(connection);

    public static bool TryGet(AgasAiProfile connection, string? modelName, out AgasAiUnifiedModel model) =>
        Snapshot(connection).TryGet(modelName, out model);

    public static bool TryGet(AiGenerationProviderProfile connection, string? modelName, out AgasAiUnifiedModel model) =>
        AgasAiModelDiscovery.Shared.GetSnapshot(connection.BaseUri.AbsoluteUri, connection.ApiKey).TryGet(modelName, out model);

    private static AgasAiUnifiedModel Images(
        string modelName,
        string displayName,
        IEnumerable<string> capabilities) =>
        new(modelName, displayName, ImagesWireAdapterKind, capabilities, "gpt");

    private static AgasAiUnifiedModel Jimeng(string version, string resolution) =>
        new("jimeng-image-" + version, "jimeng-image-" + version, ImagesWireAdapterKind,
            new[] { "textToImage" }, "jimeng", new[] { "1:1" }, new[] { resolution }, resolution, "1:1", 1);
}
