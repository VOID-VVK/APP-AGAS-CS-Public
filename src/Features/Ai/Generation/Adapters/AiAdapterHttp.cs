using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AppAgasCs.Features.Ai.Generation;

internal static class AiAdapterHttp
{
    private const int MaximumImageBytes = 64 * 1024 * 1024;
    private const int MaximumEncodedBase64Characters = ((MaximumImageBytes + 2) / 3 * 4) + 4096;
    private const int MaximumJsonBytes = MaximumEncodedBase64Characters + (1024 * 1024);
    private const int MaximumSseStreamBytes = MaximumJsonBytes + (4 * 1024 * 1024);
    private const int MaximumErrorJsonBytes = 64 * 1024;
    private static readonly HashSet<string> SafeUpstreamCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "UNAUTHENTICATED",
        "PERMISSION_DENIED",
        "INVALID_ARGUMENT",
        "RESOURCE_EXHAUSTED",
        "NOT_FOUND",
        "RATE_LIMITED",
        "TOO_MANY_REQUESTS",
        "INTERNAL",
        "UNAVAILABLE",
        "DEADLINE_EXCEEDED",
        "MODEL_NOT_FOUND",
        "NO_AVAILABLE_ACCOUNTS",
        "invalid_api_key",
        "insufficient_quota",
        "rate_limit_exceeded",
        "server_error",
        "invalid_request_error",
    };

    public static Uri Endpoint(Uri baseUri, string suffix)
    {
        string baseText = baseUri.AbsoluteUri.TrimEnd('/');
        string normalizedSuffix = suffix.TrimStart('/');
        string basePath = baseUri.AbsolutePath.TrimEnd('/');
        if (basePath.EndsWith('/' + normalizedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return baseUri;
        }

        if (normalizedSuffix.StartsWith("v1/", StringComparison.OrdinalIgnoreCase)
            && basePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            normalizedSuffix = normalizedSuffix[3..];
        }

        return new Uri($"{baseText}/{normalizedSuffix}", UriKind.Absolute);
    }

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            string message = exception.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "无法解析中转站域名，请检查 Base URL 和网络连接。",
                HttpRequestError.SecureConnectionError => "无法与中转站建立 TLS 安全连接，请检查证书和 Base URL。",
                HttpRequestError.ConnectionError => "无法连接中转站，请检查地址、端口和网络连接。",
                HttpRequestError.ResponseEnded => "中转站在响应完成前关闭了连接，请稍后重试或检查中转站状态。",
                HttpRequestError.InvalidResponse => "中转站返回了无效的 HTTP 响应。",
                HttpRequestError.HttpProtocolError => "中转站 HTTP 协议通信失败。",
                HttpRequestError.VersionNegotiationError => "无法与中转站协商 HTTP 协议版本。",
                HttpRequestError.UserAuthenticationError => "中转站连接认证失败。",
                HttpRequestError.ProxyTunnelError => "无法通过网络代理连接中转站。",
                HttpRequestError.ConfigurationLimitExceeded => "HTTP 客户端配置限制阻止了请求。",
                HttpRequestError.ExtendedConnectNotSupported => "当前中转站不支持所需的扩展连接方式。",
                _ => "中转站请求发送失败，请检查网络连接和 Base URL。",
            };
            throw new AiGenerationException(
                AiGenerationErrorCode.ProviderRequestFailed,
                message,
                AiGenerationDiagnostics.Create(
                    AiGenerationErrorCode.ProviderRequestFailed,
                    AiGenerationFailurePhase.ProviderRequest,
                    AiGenerationDiagnostics.CategoryFor(exception),
                    httpStatus: exception.StatusCode.HasValue ? (int)exception.StatusCode.Value : null,
                    upstreamCode: exception.HttpRequestError.ToString()));
        }
    }

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateProviderHttpExceptionAsync(
                response,
                AiGenerationFailurePhase.ProviderResponse,
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (response.Content.Headers.ContentLength > MaximumJsonBytes)
            {
                throw new AiGenerationException(AiGenerationErrorCode.InvalidProviderResponse, "The image provider JSON response is too large.");
            }

            byte[] json = await ReadLimitedAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                MaximumJsonBytes,
                "The image provider JSON response is too large.",
                cancellationToken).ConfigureAwait(false);
            if (IsHtmlResponse(response, json))
            {
                throw new AiGenerationException(
                    AiGenerationErrorCode.InvalidProviderResponse,
                    "中转站返回了网页而不是 JSON。请检查 Base URL 是否包含正确的 API 前缀。");
            }
            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (AiGenerationException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new AiGenerationException(
                AiGenerationErrorCode.InvalidProviderResponse,
                "中转站返回了无法解析的 JSON。请检查 Base URL 和接口兼容性。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw new AiGenerationException(
                AiGenerationErrorCode.InvalidProviderResponse,
                "读取中转站响应失败，请稍后重试。");
        }
    }

    public static bool IsEventStream(HttpResponseMessage response) =>
        string.Equals(
            response.Content.Headers.ContentType?.MediaType,
            "text/event-stream",
            StringComparison.OrdinalIgnoreCase);

    public static async Task<T?> ReadSseJsonUntilAsync<T>(
        HttpResponseMessage response,
        Func<JsonElement, T?> select,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(select);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateProviderHttpExceptionAsync(
                response,
                AiGenerationFailurePhase.ProviderResponse,
                cancellationToken).ConfigureAwait(false);
        }
        if (response.Content.Headers.ContentLength > MaximumSseStreamBytes)
        {
            throw new AiGenerationException(
                AiGenerationErrorCode.InvalidProviderResponse,
                "The image provider SSE response is too large.");
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var line = new MemoryStream();
            using var eventData = new MemoryStream();
            long totalBytes = 0;
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    AppendSseDataLine(line, eventData);
                    return SelectSseEvent(eventData, select, out _);
                }

                totalBytes += read;
                if (totalBytes > MaximumSseStreamBytes)
                {
                    throw new AiGenerationException(
                        AiGenerationErrorCode.InvalidProviderResponse,
                        "The image provider SSE response is too large.");
                }

                for (int index = 0; index < read; index++)
                {
                    byte value = buffer[index];
                    if (value != (byte)'\n')
                    {
                        if (line.Length >= MaximumJsonBytes)
                        {
                            throw new AiGenerationException(
                                AiGenerationErrorCode.InvalidProviderResponse,
                                "The image provider SSE event is too large.");
                        }
                        line.WriteByte(value);
                        continue;
                    }

                    bool eventBoundary = AppendSseDataLine(line, eventData);
                    line.SetLength(0);
                    if (!eventBoundary)
                    {
                        continue;
                    }

                    T? selected = SelectSseEvent(eventData, select, out bool done);
                    eventData.SetLength(0);
                    if (selected != null || done)
                    {
                        return selected;
                    }
                }
            }
        }
        catch (AiGenerationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            throw new AiGenerationException(
                AiGenerationErrorCode.InvalidProviderResponse,
                "读取中转站 SSE 响应失败，请稍后重试。");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async Task<GeneratedImageData> ResolveImageAsync(
        HttpClient httpClient,
        string value,
        string? mimeType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AiGenerationException(AiGenerationErrorCode.InvalidProviderResponse, "The image provider response did not contain an image.");
        }

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = value.IndexOf(',');
            if (comma < 0 || !value[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
            {
                throw new AiGenerationException(AiGenerationErrorCode.InvalidProviderResponse, "The image provider returned an invalid data URL.");
            }

            string dataMimeType = value[5..comma].Split(';', 2)[0];
            return DecodeBase64(value[(comma + 1)..], dataMimeType);
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            await ValidateRemoteImageUriAsync(uri, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", "AGAS-ImageDownload/1.0");
            request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/*,*/*");
            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException exception)
            {
                throw new AiGenerationException(
                    AiGenerationErrorCode.ProviderRequestFailed,
                    "生成结果是图片链接，但本机无法下载该图片。请检查网络或本地代理后重试。",
                    AiGenerationDiagnostics.Create(
                        AiGenerationErrorCode.ProviderRequestFailed,
                        AiGenerationFailurePhase.ImageDownload,
                        AiGenerationDiagnostics.CategoryFor(exception),
                        httpStatus: exception.StatusCode.HasValue ? (int)exception.StatusCode.Value : null,
                        upstreamCode: exception.HttpRequestError.ToString()));
            }
            using (response)
            {
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateProviderHttpExceptionAsync(
                    response,
                    AiGenerationFailurePhase.ImageDownload,
                    cancellationToken).ConfigureAwait(false);
            }

            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength > MaximumImageBytes)
            {
                throw new AiGenerationException(AiGenerationErrorCode.InvalidProviderResponse, "The generated image is too large.");
            }

            byte[] bytes = await ReadLimitedAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                MaximumImageBytes,
                "The generated image is too large.",
                cancellationToken).ConfigureAwait(false);
            // The byte signature is authoritative. CDNs commonly return
            // application/octet-stream or stale Content-Type metadata.
            return new GeneratedImageData(bytes, SniffMimeType(bytes));
            }
        }

        return DecodeBase64(value, mimeType ?? "image/png");
    }

    public static GeneratedImageData DecodeBase64(string value, string mimeType)
    {
        try
        {
            if (value.Length > MaximumEncodedBase64Characters)
            {
                throw new AiGenerationException(AiGenerationErrorCode.InvalidProviderResponse, "The generated image base64 payload is too large.");
            }
            byte[] bytes = Convert.FromBase64String(value);
            if (bytes.Length > MaximumImageBytes)
            {
                throw new AiGenerationException(AiGenerationErrorCode.InvalidProviderResponse, "The generated image is too large.");
            }

            return new GeneratedImageData(bytes, GeneratedImageData.NormalizeMimeType(mimeType));
        }
        catch (AiGenerationException)
        {
            throw;
        }
        catch
        {
            throw new AiGenerationException(AiGenerationErrorCode.InvalidProviderResponse, "The image provider returned invalid base64 image data.");
        }
    }

    public static string DataUrl(AiGenerationReferenceImage image) =>
        $"data:{GeneratedImageData.NormalizeMimeType(image.MimeType)};base64,{Convert.ToBase64String(image.Bytes)}";

    private static bool AppendSseDataLine(MemoryStream line, MemoryStream eventData)
    {
        int length = checked((int)line.Length);
        byte[] bytes = line.GetBuffer();
        if (length > 0 && bytes[length - 1] == (byte)'\r')
        {
            length--;
        }
        if (length == 0)
        {
            return true;
        }
        if (bytes[0] == (byte)':')
        {
            return false;
        }
        ReadOnlySpan<byte> prefix = "data:"u8;
        if (length < prefix.Length || !bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix))
        {
            return false;
        }

        int offset = prefix.Length;
        if (offset < length && bytes[offset] == (byte)' ')
        {
            offset++;
        }
        int dataLength = length - offset;
        long separatorBytes = eventData.Length == 0 ? 0 : 1;
        if (eventData.Length + separatorBytes + dataLength > MaximumJsonBytes)
        {
            throw new AiGenerationException(
                AiGenerationErrorCode.InvalidProviderResponse,
                "The image provider SSE event is too large.");
        }
        if (separatorBytes != 0)
        {
            eventData.WriteByte((byte)'\n');
        }
        eventData.Write(bytes, offset, dataLength);
        return false;
    }

    private static T? SelectSseEvent<T>(
        MemoryStream eventData,
        Func<JsonElement, T?> select,
        out bool done)
        where T : class
    {
        done = false;
        int length = checked((int)eventData.Length);
        if (length == 0)
        {
            return null;
        }

        byte[] bytes = eventData.GetBuffer();
        int start = 0;
        while (start < length && bytes[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            start++;
        }
        while (length > start && bytes[length - 1] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            length--;
        }
        ReadOnlySpan<byte> payload = bytes.AsSpan(start, length - start);
        if (payload.SequenceEqual("[DONE]"u8))
        {
            done = true;
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                bytes.AsMemory(start, length - start),
                new JsonDocumentOptions { MaxDepth = 64 });
            return select(document.RootElement);
        }
        catch (AiGenerationException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new AiGenerationException(
                AiGenerationErrorCode.InvalidProviderResponse,
                "中转站返回了无法解析的 SSE JSON。请检查 Base URL 和接口兼容性。");
        }
    }

    private static async Task<AiGenerationException> CreateProviderHttpExceptionAsync(
        HttpResponseMessage response,
        AiGenerationFailurePhase phase,
        CancellationToken cancellationToken)
    {
        byte[] body = Array.Empty<byte>();
        try
        {
            if (response.Content.Headers.ContentLength is not long length || length <= MaximumErrorJsonBytes)
            {
                body = await ReadLimitedAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    MaximumErrorJsonBytes,
                    "Upstream error response is too large.",
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            body = Array.Empty<byte>();
        }

        string? upstreamCode = ExtractSafeUpstreamCode(body);
        var details = new List<string>();
        if (upstreamCode != null)
        {
            details.Add($"code={upstreamCode}");
        }
        string suffix = details.Count == 0 ? string.Empty : $"（{string.Join("，", details)}）";
        return new AiGenerationException(
            AiGenerationErrorCode.ProviderRequestFailed,
            $"中转站返回 HTTP {(int)response.StatusCode}{suffix}。",
            new AiGenerationSafeDiagnostic(
                Guid.NewGuid().ToString("N"),
                AiGenerationErrorCode.ProviderRequestFailed,
                phase,
                AiGenerationFailureCategory.ProviderRejected,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                (int)response.StatusCode,
                upstreamCode));
    }

    private static bool IsHtmlResponse(HttpResponseMessage response, ReadOnlySpan<byte> body)
    {
        string mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        int index = 0;
        while (index < body.Length && body[index] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            index++;
        }
        return index < body.Length && body[index] == (byte)'<';
    }

    private static string? ExtractSafeUpstreamCode(byte[] body)
    {
        if (body.Length == 0)
        {
            return null;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            JsonElement source = root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object
                ? error
                : root;
            foreach (string propertyName in new[] { "status", "code", "type" })
            {
                if (!source.TryGetProperty(propertyName, out JsonElement value))
                {
                    continue;
                }
                string? text = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.GetRawText(),
                    _ => null,
                };
                    string? safe = SafeUpstreamCode(text);
                if (safe != null)
                {
                    return safe;
                }
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static string? SafeToken(string? value, int maximumLength)
    {
        string token = value?.Trim() ?? string.Empty;
        if (token.Length is < 1 || token.Length > maximumLength)
        {
            return null;
        }
        return token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')
            ? token
            : null;
    }

    private static string? SafeUpstreamCode(string? value)
    {
        string? token = SafeToken(value, 64);
        if (token == null)
        {
            return null;
        }
        if (int.TryParse(token, out int numericCode) && numericCode is >= 400 and <= 599)
        {
            return token;
        }
        return SafeUpstreamCodes.Contains(token) ? token : null;
    }

    private static async Task<byte[]> ReadLimitedAsync(
        Stream stream,
        int maximumBytes,
        string tooLargeMessage,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return output.ToArray();
                }

                if (output.Length + read > maximumBytes)
                {
                    throw new AiGenerationException(AiGenerationErrorCode.InvalidProviderResponse, tooLargeMessage);
                }

                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string SniffMimeType(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff) return "image/jpeg";
        if (bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        throw new AiGenerationException(AiGenerationErrorCode.UnsupportedImage, "The generated image type could not be determined.");
    }

    private static async Task ValidateRemoteImageUriAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            throw new AiGenerationException(AiGenerationErrorCode.InvalidProviderResponse, "The generated image URL must use HTTP(S).");
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new AiGenerationException(AiGenerationErrorCode.InvalidProviderResponse, "The generated image URL must not contain credentials.");
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host, out IPAddress? literalAddress))
        {
            addresses = new[] { literalAddress };
        }
        else
        {
            if (uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            {
                throw new AiGenerationException(AiGenerationErrorCode.InvalidProviderResponse, "The generated image URL points to a private address.");
            }
            try
            {
                addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                throw new AiGenerationException(AiGenerationErrorCode.InvalidProviderResponse, "The generated image host could not be resolved.");
            }
        }

        if (addresses.Length == 0 || addresses.Any(IsNonPublicAddress))
        {
            throw new AiGenerationException(AiGenerationErrorCode.InvalidProviderResponse, "The generated image URL points to a private address.");
        }
    }

    private static bool IsNonPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None)
            || address.Equals(IPAddress.IPv6None)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || address.IsIPv6SiteLocal)
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] ipv6 = address.GetAddressBytes();
            if ((ipv6[0] & 0xfe) == 0xfc) return true; // fc00::/7 unique local
            if (address.IsIPv4MappedToIPv6) return IsNonPublicAddress(address.MapToIPv4());
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        int first = bytes[0];
        int second = bytes[1];
        return first == 0
            || first == 10
            || first == 127
            || (first == 100 && second is >= 64 and <= 127)
            || (first == 169 && second == 254)
            || (first == 172 && second is >= 16 and <= 31)
            || (first == 192 && second == 168)
            || (first == 192 && second == 0)
            || (first == 198 && second is 18 or 19)
            || first >= 224;
    }
}
