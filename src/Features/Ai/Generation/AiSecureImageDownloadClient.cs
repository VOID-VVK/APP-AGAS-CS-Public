using System;
using System.Net;
using System.Net.Http;

namespace AppAgasCs.Features.Ai.Generation;

internal static class AiSecureImageDownloadClient
{
    public static HttpClient Instance { get; } = Create();

    private static HttpClient Create()
    {
        IWebProxy? proxy = AiImageDownloadProxy.Resolve();
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = proxy != null,
            Proxy = proxy,
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            // The generation service's linked token owns the end-to-end
            // deadline, including provider-hosted image downloads.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }
}
