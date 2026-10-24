using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Features.Ai.Generation;
using Vendor.Adapter.Dependency;

public sealed class VendorBasicAdapter : IAiImageGenerationAdapter
{
    public string AdapterKind => "vendor.fixture-basic-v1";

    public AiGenerationAdapterCapabilities Capabilities =>
        AiGenerationAdapterCapabilities.TextToImage;

    public Task<GeneratedImageData> GenerateAsync(
        AiGenerationProviderProfile profile,
        AiGenerationRequest request,
        HttpClient providerHttpClient,
        HttpClient imageDownloadClient,
        CancellationToken cancellationToken)
    {
        _ = profile.ApiKey;
        throw new NotSupportedException();
    }
}

public sealed class VendorDependencyAdapter : IAiImageGenerationAdapter
{
    public string AdapterKind => DependencyMarker.AdapterKind;

    public AiGenerationAdapterCapabilities Capabilities =>
        AiGenerationAdapterCapabilities.TextToImage;

    public Task<GeneratedImageData> GenerateAsync(
        AiGenerationProviderProfile profile,
        AiGenerationRequest request,
        HttpClient providerHttpClient,
        HttpClient imageDownloadClient,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(DependencyMarker.VersionMarker);
}
