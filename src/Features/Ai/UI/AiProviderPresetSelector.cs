#nullable enable

using System;
using System.Linq;
using AppAgasCs.Features.Ai.Settings;
using Godot;

namespace AppAgasCs.Features.Ai.UI;

internal static class AiProviderPresetSelector
{
    private const char PackedSeparator = '\n';

    internal static void PopulateProviders(OptionButton options, AgasAiProviderCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(catalog);
        options.Clear();
        foreach (AgasAiProviderDescriptor provider in catalog.Providers)
        {
            options.AddItem(provider.DisplayName);
            options.SetItemMetadata(options.ItemCount - 1, provider.Id);
        }
    }

    internal static int FindProviderIndex(OptionButton options, string providerId)
    {
        ArgumentNullException.ThrowIfNull(options);
        for (int index = 0; index < options.ItemCount; index++)
        {
            if (string.Equals(options.GetItemMetadata(index).AsString(), providerId, StringComparison.Ordinal)) return index;
        }
        return -1;
    }

    internal static AgasAiProviderDescriptor? SelectedProvider(OptionButton options, AgasAiProviderCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(catalog);
        int index = options.Selected;
        if (index < 0 || index >= options.ItemCount) return null;
        return catalog.TryGet(options.GetItemMetadata(index).AsString(), out AgasAiProviderDescriptor provider)
            ? provider
            : null;
    }

    internal static void RefreshPresets(
        OptionButton presets,
        Label recommendation,
        AgasAiProviderCatalog catalog,
        string? modelName)
    {
        ArgumentNullException.ThrowIfNull(presets);
        ArgumentNullException.ThrowIfNull(recommendation);
        ArgumentNullException.ThrowIfNull(catalog);
        presets.Clear();
        presets.AddItem("自定义模型名");
        presets.SetItemMetadata(0, string.Empty);
        // New API has its own model list; legacy profiles must not implicitly
        // move to its origin when a shared model name is selected.
        foreach (AgasAiProviderDescriptor provider in catalog.Providers.Where(
                     provider => provider.Id != AgasAiProviderIds.AxonCatNewApi))
        {
            foreach (AgasAiModelPreset preset in provider.Models)
            {
                presets.AddItem(preset.Label);
                presets.SetItemMetadata(presets.ItemCount - 1, Pack(provider.Id, preset.ModelName));
            }
        }

        SyncModel(presets, recommendation, catalog, modelName);
    }

    internal static void RefreshPresets(
        OptionButton presets,
        Label recommendation,
        AgasAiProviderDescriptor provider,
        string? modelName)
    {
        ArgumentNullException.ThrowIfNull(presets);
        ArgumentNullException.ThrowIfNull(recommendation);
        ArgumentNullException.ThrowIfNull(provider);
        presets.Clear();
        presets.AddItem("自定义模型名");
        presets.SetItemMetadata(0, string.Empty);
        foreach (AgasAiModelPreset preset in provider.Models)
        {
            presets.AddItem(preset.Label);
            presets.SetItemMetadata(presets.ItemCount - 1, Pack(provider.Id, preset.ModelName));
        }

        SyncModel(presets, recommendation, provider, modelName);
    }

    internal static void SyncModel(
        OptionButton presets,
        Label recommendation,
        AgasAiProviderCatalog catalog,
        string? modelName)
    {
        ArgumentNullException.ThrowIfNull(presets);
        ArgumentNullException.ThrowIfNull(recommendation);
        ArgumentNullException.ThrowIfNull(catalog);
        SelectMatchingPreset(presets, modelName);
        AgasAiProviderDescriptor? owner = catalog.FindByKnownModel(modelName);
        recommendation.Text = owner == null
            ? $"支持自定义模型名。推荐默认：{catalog.Providers.FirstOrDefault()?.DefaultModelName ?? string.Empty}"
            : ModelGuidance(owner, modelName);
    }

    internal static void SyncModel(
        OptionButton presets,
        Label recommendation,
        AgasAiProviderDescriptor provider,
        string? modelName)
    {
        ArgumentNullException.ThrowIfNull(presets);
        ArgumentNullException.ThrowIfNull(recommendation);
        ArgumentNullException.ThrowIfNull(provider);
        SelectMatchingPreset(presets, modelName);
        recommendation.Text = provider.IsKnownModel(modelName)
            ? ModelGuidance(provider, modelName)
            : $"支持自定义模型名。推荐默认：{provider.DefaultModelName}";
    }

    private static string ModelGuidance(AgasAiProviderDescriptor provider, string? modelName) =>
        provider.Id == AgasAiProviderIds.AxonCatNewApi
        && string.Equals(modelName?.Trim(), "gemini-image", StringComparison.OrdinalIgnoreCase)
            ? "尺寸为提示要求，返回尺寸由渠道决定。"
            : string.Empty;

    internal static bool TryGetPreset(
        OptionButton presets,
        long index,
        out string providerId,
        out string modelName)
    {
        ArgumentNullException.ThrowIfNull(presets);
        providerId = string.Empty;
        modelName = string.Empty;
        if (index < 0 || index >= presets.ItemCount)
        {
            return false;
        }

        return TryUnpack(presets.GetItemMetadata((int)index).AsString(), out providerId, out modelName);
    }

    internal static bool TryGetPresetModel(OptionButton presets, long index, out string modelName)
    {
        return TryGetPreset(presets, index, out _, out modelName);
    }

    internal static int FindPresetIndex(OptionButton presets, string modelName)
    {
        ArgumentNullException.ThrowIfNull(presets);
        string normalized = modelName?.Trim() ?? string.Empty;
        for (int index = 0; index < presets.ItemCount; index++)
        {
            if (TryUnpack(presets.GetItemMetadata(index).AsString(), out _, out string presetModel)
                && string.Equals(presetModel, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static void SelectMatchingPreset(OptionButton presets, string? modelName)
    {
        string normalized = modelName?.Trim() ?? string.Empty;
        int selected = 0;
        for (int index = 1; index < presets.ItemCount; index++)
        {
            if (!TryUnpack(presets.GetItemMetadata(index).AsString(), out _, out string presetModel)
                || !string.Equals(presetModel, normalized, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            selected = index;
            break;
        }

        presets.Select(selected);
    }

    private static string Pack(string providerId, string modelName) => providerId + PackedSeparator + modelName;

    private static bool TryUnpack(string packed, out string providerId, out string modelName)
    {
        providerId = string.Empty;
        modelName = string.Empty;
        if (string.IsNullOrWhiteSpace(packed))
        {
            return false;
        }

        int separator = packed.IndexOf(PackedSeparator);
        if (separator <= 0 || separator >= packed.Length - 1)
        {
            modelName = packed.Trim();
            return modelName.Length > 0;
        }

        providerId = packed[..separator];
        modelName = packed[(separator + 1)..].Trim();
        return providerId.Length > 0 && modelName.Length > 0;
    }
}
