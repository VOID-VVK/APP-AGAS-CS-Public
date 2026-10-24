using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AppAgasCs.Features.Ai.Generation;

public interface IAiImageGenerationAdapter
{
    /// <summary>
    /// Stable protocol identifier referenced by provider descriptor files.
    /// Every implementation must declare this explicitly so a valid third-party
    /// adapter can never compile and then fail through a throwing default member.
    /// </summary>
    string AdapterKind { get; }

    /// <summary>
    /// Legacy adapters that predate explicit capability negotiation retain
    /// their previous text/reference behavior. Region edits remain opt-in so
    /// an old adapter can never silently receive or downgrade a masked edit.
    /// </summary>
    AiGenerationAdapterCapabilities Capabilities =>
        AiGenerationAdapterCapabilities.TextToImage
        | AiGenerationAdapterCapabilities.ReferenceEdit;

    Task<GeneratedImageData> GenerateAsync(
        AiGenerationProviderProfile profile,
        AiGenerationRequest request,
        HttpClient providerHttpClient,
        HttpClient imageDownloadClient,
        CancellationToken cancellationToken);
}
