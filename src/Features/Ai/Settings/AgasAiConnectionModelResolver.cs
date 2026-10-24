using System;
using AppAgasCs.Features.Ai.Generation;

namespace AppAgasCs.Features.Ai.Settings;

/// <summary>Projects a stored connection and the independent active model into one runtime profile.</summary>
public static class AgasAiConnectionModelResolver
{
    public static AiGenerationProviderProfile Resolve(
        AgasAiProfile connection,
        string? activeModelName)
    {
        ArgumentNullException.ThrowIfNull(connection);
        string normalizedModelName = activeModelName?.Trim() ?? string.Empty;
        bool known = AgasAiUnifiedModelCatalog.TryGet(normalizedModelName, out AgasAiUnifiedModel model);
        string resolvedModelName = known ? model.ModelName : normalizedModelName;
        AiGenerationAdapterCapabilities capabilities = known
            ? AiGenerationCapabilityNames.Parse(model.Capabilities)
            : AiGenerationAdapterCapabilities.None;

        return new AiGenerationProviderProfile(
            connection.Id,
            connection.Name,
            AgasAiProviderIds.AxonCatNewApi,
            AgasAiUnifiedModelCatalog.ConnectionAdapterKind,
            resolvedModelName,
            connection.ApiKey ?? string.Empty,
            connection.BaseUrl,
            capabilities);
    }
}
