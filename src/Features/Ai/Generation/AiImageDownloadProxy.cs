using System;
using System.Net;
using System.Net.Sockets;

namespace AppAgasCs.Features.Ai.Generation;

/// <summary>
/// Grok/xAI often return a CDN URL instead of image bytes. That host is not
/// reachable from some networks without the user's local proxy. Provider calls
/// to CPA stay direct; only public image downloads may use a proxy.
/// </summary>
internal static class AiImageDownloadProxy
{
    internal static readonly Uri ClashLoopback = new("http://127.0.0.1:7897/");

    internal static IWebProxy? Resolve(Func<int, bool>? localPortOpen = null)
    {
        if (TryEnvProxy(out IWebProxy envProxy))
        {
            return envProxy;
        }

        Func<int, bool> probe = localPortOpen ?? IsLocalPortOpen;
        if (probe(7897) || probe(7890))
        {
            return new WebProxy(ClashLoopback);
        }

        return null;
    }

    internal static bool TryEnvProxy(out IWebProxy proxy)
    {
        foreach (string key in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy" })
        {
            string? value = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                proxy = new WebProxy(uri);
                return true;
            }
        }

        proxy = null!;
        return false;
    }

    private static bool IsLocalPortOpen(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(IPAddress.Loopback, port).Wait(TimeSpan.FromMilliseconds(80));
        }
        catch
        {
            return false;
        }
    }
}
