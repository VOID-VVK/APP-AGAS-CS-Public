using System;
using AppAgasCs.Features.Ai.Generation;

namespace AppAgasCs.Features.Ai.Settings;

/// <summary>Projects a stored connection and the independent active model into one runtime profile.</summary>
public static class AgasAiConnectionModelResolver
{
    public static AiGenerationProviderProfile Resolve(
        AgasAiProfile connection,
        string? activeModelName,
        AgasAiModelDiscovery? discovery = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (UsesRetiredProtocol(connection))
            throw new AiGenerationException(AiGenerationErrorCode.InvalidConfiguration,
                "旧 Google 私有协议已停用，请在设置中改为统一 API 地址并重新保存连接及 Key。");
        AgasAiModelSnapshot snapshot = (discovery ?? AgasAiModelDiscovery.Shared).GetSnapshot(connection);
        if (!snapshot.IsReady || !snapshot.TryGet(activeModelName, out AgasAiUnifiedModel model) || !model.IsAvailable)
            throw new AiGenerationException(AiGenerationErrorCode.InvalidConfiguration,
                string.IsNullOrWhiteSpace(snapshot.Message) ? "当前 Key 的模型列表中没有该生图模型，请刷新后选择。" : snapshot.Message);

        return new AiGenerationProviderProfile(
            connection.Id,
            connection.Name,
            AgasAiProviderIds.AxonCatNewApi,
            AgasAiUnifiedModelCatalog.ConnectionAdapterKind,
            model.ModelName,
            connection.ApiKey ?? string.Empty,
            connection.BaseUrl,
            AiGenerationCapabilityNames.Parse(model.Capabilities),
            modelConstraints: new AiGenerationModelConstraints(model.Ratios, model.Resolutions,
                model.DefaultRatio, model.DefaultResolution, model.MaxImages));
    }

    internal static bool UsesRetiredProtocol(AgasAiProfile connection, AgasAiProviderCatalog? catalog = null) =>
        string.Equals(connection.ProviderId, AgasAiProviderIds.GeminiCompatible, StringComparison.Ordinal)
        || (catalog != null && catalog.TryGet(connection.ProviderId, out AgasAiProviderDescriptor descriptor)
            && string.Equals(descriptor.AdapterKind, AgasAiProviderIds.GeminiCompatible, StringComparison.Ordinal))
        || (Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out Uri? uri)
            && string.Equals(uri.IdnHost, "generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase));
}
