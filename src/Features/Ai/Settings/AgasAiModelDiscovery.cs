using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Features.Ai.Generation;

namespace AppAgasCs.Features.Ai.Settings;

/// <summary>Memory-only discovery scoped by endpoint and credential digest.</summary>
public sealed class AgasAiModelDiscovery
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AgasAiModelSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgasAiModelSnapshot> _settled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _settledRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _requests = new(StringComparer.Ordinal);
    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;
    public static AgasAiModelDiscovery Shared { get; } = new(AiProviderHttpClientFactory.Create());

    public AgasAiModelDiscovery(HttpClient client, TimeSpan? timeout = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _timeout = timeout ?? TimeSpan.FromSeconds(12);
    }

    public AgasAiModelSnapshot GetSnapshot(AgasAiProfile connection) => GetSnapshot(connection.BaseUrl, connection.ApiKey ?? string.Empty);

    public AgasAiModelSnapshot GetSnapshot(string baseUrl, string apiKey)
    {
        string id;
        try { id = ConnectionIdentity(baseUrl, apiKey); }
        catch (ArgumentException) { return Empty("error", "连接地址不正确。", string.Empty); }
        lock (_gate) return _snapshots.TryGetValue(id, out AgasAiModelSnapshot? snapshot)
            ? snapshot : Empty("pending", "请刷新模型列表。", id);
    }

    public async Task<AgasAiModelSnapshot> RefreshAsync(AgasAiProfile connection, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string id;
        try { id = ConnectionIdentity(connection.BaseUrl, connection.ApiKey ?? string.Empty); }
        catch (ArgumentException) { return Empty("error", "连接地址不正确。", string.Empty); }
        AgasAiModelSnapshot previous;
        long request;
        lock (_gate)
        {
            previous = _settled.TryGetValue(id, out AgasAiModelSnapshot? stable)
                ? stable : Empty("pending", "请刷新模型列表。", id);
            request = _requests.TryGetValue(id, out long current) ? current + 1 : 1;
            _requests[id] = request;
            _snapshots[id] = new AgasAiModelSnapshot(previous.Models, "pending", "正在获取当前 Key 可用模型…", id, false);
        }
        AgasAiModelSnapshot result;
        bool recoverable = false;
        string key = connection.ApiKey ?? string.Empty;
        if (key.Length == 0 || key.Any(character => character < 33 || character > 126))
        {
            result = Empty("auth", "请填写有效的 API Key 后刷新模型。", id);
        }
        else
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_timeout);
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, AiAdapterHttp.Endpoint(SecureBaseUri(connection.BaseUrl), "v1/models"));
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                message.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                using HttpResponseMessage response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if ((int)response.StatusCode >= 500) { recoverable = true; result = previous; }
                else if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    result = Empty("auth", $"获取模型失败（HTTP {(int)response.StatusCode}），请检查 Key 权限。", id);
                else if (!response.IsSuccessStatusCode) result = Empty("error", $"获取模型失败（HTTP {(int)response.StatusCode}）。", id);
                else
                {
                    using Stream stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                    using var bytes = new MemoryStream();
                    byte[] buffer = new byte[16 * 1024];
                    int count;
                    while ((count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                    {
                        if (bytes.Length + count > 2 * 1024 * 1024) throw new JsonException();
                        bytes.Write(buffer, 0, count);
                    }
                    using JsonDocument payload = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
                    IReadOnlyList<AgasAiUnifiedModel> models = AgasAiModelMetadataParser.Parse(payload.RootElement);
                    result = new AgasAiModelSnapshot(models, models.Count == 0 ? "empty" : "live",
                        models.Count == 0 ? "当前 Key 没有可用的生图模型。" : string.Empty, id, true);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { recoverable = true; result = previous; }
            catch (HttpRequestException) { recoverable = true; result = previous; }
            catch (IOException) { recoverable = true; result = previous; }
            catch (JsonException) { result = Empty("error", "模型接口返回格式不正确。", id); }
            catch (OperationCanceledException)
            {
                RestoreAfterCancellation(id, request, previous);
                throw;
            }
        }
        if (cancellationToken.IsCancellationRequested)
        {
            RestoreAfterCancellation(id, request, previous);
            cancellationToken.ThrowIfCancellationRequested();
        }
        lock (_gate)
        {
            if (recoverable) result = Recover(_settled.GetValueOrDefault(id) ?? previous, id);
            // A denial observed by an older overlapping request still invalidates
            // prior permissions, until a newer successful response supersedes it.
            bool settlesAuthority = !recoverable
                && (!_settledRequests.TryGetValue(id, out long settledRequest) || request >= settledRequest);
            if (settlesAuthority)
            {
                _settled[id] = result;
                _settledRequests[id] = request;
            }
            else if (recoverable && _requests[id] == request) _settled[id] = result;
            // A newer caller may have canceled and restored an older live view.
            // Only a newer authoritative completion, tracked above, supersedes
            // this response; a restored view must not keep revoked permissions.
            bool replacesSettledView = settlesAuthority && _snapshots[id].Source != "pending";
            if (_requests[id] == request || replacesSettledView)
                _snapshots[id] = result;
            return _snapshots[id];
        }
    }

    public static string ConnectionIdentity(string baseUrl, string apiKey)
    {
        Uri uri = SecureBaseUri(baseUrl);
        string canonical = uri.AbsoluteUri.TrimEnd('/');
        if (canonical.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) canonical = canonical[..^3];
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { canonical, apiKey })))).ToLowerInvariant();
    }

    private static Uri SecureBaseUri(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("连接地址不正确。");
        return uri;
    }

    private static AgasAiModelSnapshot Empty(string source, string message, string id) =>
        new(Array.Empty<AgasAiUnifiedModel>(), source, message, id, source != "pending");

    private void RestoreAfterCancellation(string id, long request, AgasAiModelSnapshot previous)
    {
        lock (_gate)
        {
            if (_requests[id] != request) return;
            AgasAiModelSnapshot restored = _settled.GetValueOrDefault(id) ?? previous;
            _snapshots[id] = restored.Source == "pending"
                ? new AgasAiModelSnapshot(Array.Empty<AgasAiUnifiedModel>(), "canceled", "模型刷新已取消。", id, false)
                : restored;
        }
    }

    private static AgasAiModelSnapshot Recover(AgasAiModelSnapshot previous, string id)
    {
        // Never resurrect permissions after an authoritative denial/empty list.
        if (previous.Source is "auth" or "error" or "empty") return previous;
        return previous.IsReady && previous.Models.Count > 0
            ? new(previous.Models, "cache", "模型接口暂不可用，使用此连接最近的模型列表。", id, true)
            : new(AgasAiUnifiedModelCatalog.Models, "local", $"模型接口暂不可用，使用 {AgasAiUnifiedModelCatalog.SnapshotDate} 内置模型列表。", id, true);
    }
}
