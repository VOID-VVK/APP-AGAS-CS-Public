using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Features.Ai.Settings;

namespace AppAgasCs.Features.Ai.Generation;

/// <summary>One gateway credential with an explicit protocol route for each supported model.</summary>
public sealed class NewApiImageAdapter : IAiImageGenerationAdapter
{
    private static readonly IAiImageGenerationAdapter Images = new OpenAiCompatibleImageAdapter(multipartEdits: true);
    private static readonly IAiImageGenerationAdapter Chat = new NewApiChatImageAdapter();

    public string AdapterKind => AgasAiUnifiedModelCatalog.ConnectionAdapterKind;

    internal static bool SupportsNativeMask(string model) =>
        AgasAiUnifiedModelCatalog.TryGet(model, out AgasAiUnifiedModel route)
        && route.Capabilities.Contains("nativeAlphaMask", StringComparer.Ordinal);
    public AiGenerationAdapterCapabilities Capabilities =>
        AiGenerationAdapterCapabilities.TextToImage
        | AiGenerationAdapterCapabilities.ReferenceEdit
        | AiGenerationAdapterCapabilities.RegionEdit;

    public Task<GeneratedImageData> GenerateAsync(
        AiGenerationProviderProfile profile,
        AiGenerationRequest request,
        HttpClient providerHttpClient,
        HttpClient imageDownloadClient,
        CancellationToken cancellationToken)
    {
        if (!AgasAiUnifiedModelCatalog.TryGet(profile.ModelName, out AgasAiUnifiedModel route))
            throw new AiGenerationException(AiGenerationErrorCode.InvalidConfiguration,
                "当前 New API 预设不支持该模型，请选择已配置的图片模型或添加自定义服务商。");
        if (request.Quality.HasValue
            && !AiGenerationQualityPolicy.Supports(route.ModelName))
        {
            throw new AiGenerationException(
                AiGenerationErrorCode.UnsupportedOperation,
                $"模型 '{route.ModelName}' 不支持质量档位。");
        }
        AiGenerationAdapterCapabilities capabilities = AiGenerationCapabilityNames.Parse(route.Capabilities);
        AiGenerationAdapterCapabilities required = request.Operation switch
        {
            AiGenerationOperation.TextToImage => AiGenerationAdapterCapabilities.TextToImage,
            AiGenerationOperation.ReferenceEdit => AiGenerationAdapterCapabilities.ReferenceEdit,
            AiGenerationOperation.RegionEdit => AiGenerationAdapterCapabilities.RegionEdit,
            _ => AiGenerationAdapterCapabilities.None,
        };
        if (required == AiGenerationAdapterCapabilities.None || !capabilities.HasFlag(required))
        {
            throw new AiGenerationException(
                AiGenerationErrorCode.UnsupportedOperation,
                $"模型 '{route.ModelName}' 不支持当前图片操作。");
        }
        IAiImageGenerationAdapter adapter = route.WireAdapterKind switch
        {
            AgasAiUnifiedModelCatalog.ImagesWireAdapterKind => Images,
            AgasAiUnifiedModelCatalog.ChatWireAdapterKind => Chat,
            _ => throw new AiGenerationException(
                AiGenerationErrorCode.InvalidConfiguration,
                "当前 New API 模型没有有效的协议路由。"),
        };
        AiGenerationProviderProfile canonicalProfile = string.Equals(
            profile.ModelName, route.ModelName, StringComparison.Ordinal)
            ? profile
            : new AiGenerationProviderProfile(
                profile.Id,
                profile.DisplayName,
                profile.ProviderId,
                profile.AdapterKind,
                route.ModelName,
                profile.ApiKey,
                profile.BaseUri.AbsoluteUri,
                profile.DeclaredCapabilities);
        return adapter.GenerateAsync(
            canonicalProfile, request, providerHttpClient, imageDownloadClient, cancellationToken);
    }
}
