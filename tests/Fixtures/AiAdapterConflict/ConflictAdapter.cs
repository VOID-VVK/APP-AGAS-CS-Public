using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Features.Ai.Generation;

public sealed class ConflictAdapter : IAiImageGenerationAdapter
{
    public string AdapterKind => "vendor.fixture-basic-v1";

    public Task<GeneratedImageData> GenerateAsync(
        AiGenerationProviderProfile profile,
        AiGenerationRequest request,
        HttpClient providerHttpClient,
        HttpClient imageDownloadClient,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
