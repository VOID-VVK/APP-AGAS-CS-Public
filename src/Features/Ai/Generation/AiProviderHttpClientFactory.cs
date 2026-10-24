using System.Net.Http;

namespace AppAgasCs.Features.Ai.Generation;

public static class AiProviderHttpClientFactory
{
    public static HttpClient Create() => Create(CreateHandler());

    internal static HttpClient Create(HttpMessageHandler handler) => new(handler, disposeHandler: true)
    {
        // AiGenerationService owns the generation deadline through its linked
        // cancellation token. HttpClient's 100-second default is too short for
        // image generation and must not race that deadline.
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        // CPA is a public relay. Going through the local Clash system proxy
        // has dropped large Grok edit requests after ~80s while Sub2API
        // already finished generating.
        UseProxy = false,
    };
}
