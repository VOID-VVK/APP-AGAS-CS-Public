#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace AppAgasCs.Features.Ai.Generation;

public interface IAiGenerationRunner
{
    Task<AiGenerationResult> GenerateAsync(
        AiGenerationProviderProfile profile,
        AiGenerationRequest request,
        CancellationToken cancellationToken);
}

public sealed class AiGenerationServiceRunner : IAiGenerationRunner, IDisposable
{
    private readonly System.Net.Http.HttpClient _httpClient = AiProviderHttpClientFactory.Create();
    private readonly AiGenerationService _service;

    public AiGenerationServiceRunner()
    {
        _service = new AiGenerationService(_httpClient);
    }

    public Task<AiGenerationResult> GenerateAsync(
        AiGenerationProviderProfile profile,
        AiGenerationRequest request,
        CancellationToken cancellationToken) =>
        _service.GenerateAsync(profile, request, cancellationToken);

    public void Dispose() => _httpClient.Dispose();
}
