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
        IEnumerable<string> capabilities)
    {
        ModelName = modelName;
        DisplayName = displayName;
        AdapterKind = AgasAiUnifiedModelCatalog.ConnectionAdapterKind;
        WireAdapterKind = wireAdapterKind;
        Capabilities = new ReadOnlyCollection<string>(capabilities.ToArray());
    }

    public string ModelName { get; }
    public string DisplayName { get; }
    public string AdapterKind { get; }
    public IReadOnlyList<string> Capabilities { get; }
    internal string WireAdapterKind { get; }
}

/// <summary>Stable model catalog for every connection backed by the shared New API gateway.</summary>
public static class AgasAiUnifiedModelCatalog
{
    public const string ConnectionAdapterKind = "newapi-compatible";
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
            Images("grok-imagine-image-quality", "Grok Imagine Image Quality", new[] { "textToImage" }),
            Images("grok-imagine-image", "Grok Imagine Image", new[] { "textToImage" }),
            Images(GrokImagineImage20ModelName, "Grok Imagine Image 2.0", new[] { "textToImage" }),
            new AgasAiUnifiedModel("gemini-image", "Gemini Image", ChatWireAdapterKind, GuidedEditCapabilities),
        });

    private static readonly IReadOnlyDictionary<string, AgasAiUnifiedModel> ByModelName =
        Catalog.ToDictionary(model => model.ModelName, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<AgasAiUnifiedModel> Models => Catalog;

    public static bool TryGet(string? modelName, out AgasAiUnifiedModel model) =>
        ByModelName.TryGetValue(modelName?.Trim() ?? string.Empty, out model!);

    private static AgasAiUnifiedModel Images(
        string modelName,
        string displayName,
        IEnumerable<string> capabilities) =>
        new(modelName, displayName, ImagesWireAdapterKind, capabilities);
}
