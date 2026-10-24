using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AppAgasCs.Tests.Core;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp;

[Collection(AgasMcpTestCollections.FixedPort)]
public sealed class AgasMcpHostTests
{
    private const string ProtocolVersion = "2025-11-25";
    [Fact]
    public async Task OfficialSdkClient_InitializesSessionAndListsAll30ToolsWithOutputSchemas()
    {
        await using HostLease lease = await StartHostAsync(CreateTools());

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(lease.Endpoint),
                TransportMode = HttpTransportMode.StreamableHttp,
                Name = "agas-mcp-tests",
                ConnectionTimeout = TimeSpan.FromSeconds(5),
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);

        await using McpClient client = await McpClient.CreateAsync(transport, cancellationToken: TestTimeout());
        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: TestTimeout());

        string[] names = tools.Select(tool => tool.Name).ToArray();
        Assert.Equal(30, names.Length);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(AgasMcpToolNames.All.Except(names, StringComparer.Ordinal));
        Assert.Empty(names.Except(AgasMcpToolNames.All, StringComparer.Ordinal));
        Assert.All(tools, tool =>
        {
            Assert.Equal(JsonValueKind.Object, tool.ProtocolTool.InputSchema.ValueKind);
            Assert.NotNull(tool.ProtocolTool.OutputSchema);
            Assert.Equal(JsonValueKind.Object, tool.ProtocolTool.OutputSchema.Value.ValueKind);
        });
    }

    [Fact]
    public async Task RawHttp_InitializeWithoutProtocolHeaderIsAllowedAndReturnsSessionHeader()
    {
        await using HostLease lease = await StartHostAsync(CreateTools());

        using HttpClient client = CreateRawClient();
        using HttpRequestMessage request = CreateJsonRpcRequest(lease, "initialize", includeProtocolHeader: false);

        using HttpResponseMessage response = await client.SendAsync(request, TestTimeout());

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.Contains("mcp-session-id"));
    }

    [Fact]
    public async Task RawHttp_RejectsMcpPostWithoutProtocolHeaderAfterInitializeException()
    {
        await using HostLease lease = await StartHostAsync(CreateTools());

        using HttpClient client = CreateRawClient();
        using HttpRequestMessage request = CreateJsonRpcRequest(lease, "tools/list", includeProtocolHeader: false);

        using HttpResponseMessage response = await client.SendAsync(request, TestTimeout());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("MCP-Protocol-Version", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RawHttp_RejectsInvalidHostHeader()
    {
        await using HostLease lease = await StartHostAsync(CreateTools());

        using HttpClient client = CreateRawClient();
        using HttpRequestMessage request = CreateJsonRpcRequest(lease, "initialize", includeProtocolHeader: false);
        request.Headers.Host = "evil.example:54119";

        using HttpResponseMessage response = await client.SendAsync(request, TestTimeout());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("AGAS_MCP_FORBIDDEN", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RawHttp_RejectsInvalidOriginHeader()
    {
        await using HostLease lease = await StartHostAsync(CreateTools());

        using HttpClient client = CreateRawClient();
        using HttpRequestMessage request = CreateJsonRpcRequest(lease, "initialize", includeProtocolHeader: false);
        request.Headers.Add("Origin", "http://evil.example");

        using HttpResponseMessage response = await client.SendAsync(request, TestTimeout());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("AGAS_MCP_FORBIDDEN", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null, null, HttpStatusCode.OK)]
    [InlineData(null, "__TEST_ORIGIN__", HttpStatusCode.OK)]
    [InlineData("__TEST_HOST__", "__TEST_ORIGIN__", HttpStatusCode.OK)]
    [InlineData(null, "http://evil.example", HttpStatusCode.Forbidden)]
    [InlineData("evil.example:54119", null, HttpStatusCode.Forbidden)]
    [InlineData("evil.example:54119", "__TEST_ORIGIN__", HttpStatusCode.Forbidden)]
    [InlineData("__TEST_HOST__", "http://evil.example", HttpStatusCode.Forbidden)]
    [InlineData("localhost:54119", null, HttpStatusCode.Forbidden)]
    [InlineData("0.0.0.0:54119", null, HttpStatusCode.Forbidden)]
    [InlineData("__WRONG_TEST_HOST__", null, HttpStatusCode.Forbidden)]
    public async Task RawHttp_HostAndOriginMatrixEnforcesLoopbackHostAndAllowedOrigin(
        string? host,
        string? origin,
        HttpStatusCode expectedStatus)
    {
        await using HostLease lease = await StartHostAsync(CreateTools());

        using HttpClient client = CreateRawClient();
        using HttpRequestMessage request = CreateJsonRpcRequest(lease, "initialize", includeProtocolHeader: false);
        if (host != null)
        {
            request.Headers.Host = string.Equals(host, "__TEST_HOST__", StringComparison.Ordinal)
                ? $"127.0.0.1:{lease.Port}"
                : string.Equals(host, "__WRONG_TEST_HOST__", StringComparison.Ordinal)
                    ? $"127.0.0.1:{(lease.Port == 65535 ? 65534 : lease.Port + 1)}"
                : host;
        }
        if (origin != null)
        {
            request.Headers.Add("Origin", string.Equals(origin, "__TEST_ORIGIN__", StringComparison.Ordinal)
                ? lease.Origin
                : origin);
        }

        using HttpResponseMessage response = await client.SendAsync(request, TestTimeout());

        Assert.Equal(expectedStatus, response.StatusCode);
    }

    [Fact]
    public async Task RawHttp_AcceptsAllowedOriginAndRequiresJsonOrEventStreamAccept()
    {
        await using HostLease lease = await StartHostAsync(CreateTools());

        using HttpClient client = CreateRawClient();
        using HttpRequestMessage request = CreateJsonRpcRequest(lease, "initialize", includeProtocolHeader: false);
        request.Headers.Add("Origin", lease.Origin);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));

        using HttpResponseMessage response = await client.SendAsync(request, TestTimeout());

        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
    }

    [Fact]
    public async Task Host_RejectsRequestBodyOverConfiguredLimit()
    {
        await using HostLease lease = await StartHostAsync(CreateTools());

        using HttpClient client = CreateRawClient();
        string padding = new('x', checked((int)AgasMcpHostFactory.MaxRequestBodyBytes + 1));
        string json = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 413,
            method = "initialize",
            @params = new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = "agas-core-tests", version = "1.0.0" },
            },
            padding,
        });
        using JsonDocument validOversizedJson = JsonDocument.Parse(json);
        Assert.Equal("initialize", validOversizedJson.RootElement.GetProperty("method").GetString());
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        content.Headers.ContentLength = Encoding.UTF8.GetByteCount(json);
        Assert.True(content.Headers.ContentLength > AgasMcpHostFactory.MaxRequestBodyBytes);
        using var request = new HttpRequestMessage(HttpMethod.Post, lease.Endpoint) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using HttpResponseMessage response = await client.SendAsync(request, TestTimeout());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RawHttp_ToolsCallPassesInitializeSessionIdToToolExecutorAndExposesIsErrorContract()
    {
        var executor = new RecordingToolExecutor(AgasMcpToolResponse.Failure(
            saved: false,
            dirty: false,
            AgasMcpErrorCodes.InvalidOperation,
            "recording executor failure",
            "assert isError contract"));
        await using HostLease lease = await StartHostAsync(CreateTools(executor), executor);

        using HttpClient client = CreateRawClient();
        string sessionId = await InitializeSessionAsync(lease, client);
        await SendInitializedNotificationAsync(lease, client, sessionId);
        using HttpRequestMessage request = CreateJsonRpcRequest(lease, "tools/call", includeProtocolHeader: true, sessionId);
        request.Content = JsonContent.Create(new
        {
            jsonrpc = "2.0",
            id = 22,
            method = "tools/call",
            @params = new
            {
                name = AgasMcpToolNames.ProjectSnapshot,
                arguments = new
                {
                    includeScenes = true,
                    includeLayers = true,
                    includeImages = false,
                    includePrompts = false,
                },
            },
        });

        using HttpResponseMessage response = await client.SendAsync(request, TestTimeout());
        string body = await response.Content.ReadAsStringAsync(TestTimeout());

        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Equal(new[] { sessionId }, executor.SessionIds);
        using JsonDocument document = JsonDocument.Parse(ExtractJsonPayload(body));
        JsonElement result = document.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.False(result.GetProperty("structuredContent").GetProperty("ok").GetBoolean());
        Assert.Equal(
            AgasMcpErrorCodes.InvalidOperation,
            result.GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString());
        JsonElement content = Assert.Single(result.GetProperty("content").EnumerateArray());
        Assert.Equal("text", content.GetProperty("type").GetString());
        using JsonDocument textJson = JsonDocument.Parse(content.GetProperty("text").GetString()!);
        Assert.True(JsonElementDeepEquals(result.GetProperty("structuredContent"), textJson.RootElement));
    }

    [Theory]
    [InlineData("/mcp-evil")]
    [InlineData("/mcpwhatever")]
    public async Task RawHttp_McpPrefixLookalikePathDoesNotTriggerMcpMiddlewareOrExecutorCleanup(string path)
    {
        var executor = new SpyToolExecutor();
        await using HostLease lease = await StartHostAsync(CreateTools(executor), executor);

        using HttpClient client = CreateRawClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{lease.Origin}{path}");
        request.Headers.Host = "evil.example:54119";
        request.Headers.Add(AgasMcpProtocolHeaderMiddleware.ProtocolVersionHeader, ProtocolVersion);
        request.Headers.Add(AgasMcpSessionDeleteMiddleware.SessionIdHeader, "evil-session");

        using HttpResponseMessage response = await client.SendAsync(request, TestTimeout());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(executor.CancelledSessions);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task RawHttp_GetAndDeleteRejectMissingProtocolHeader(string method)
    {
        await using HostLease lease = await StartHostAsync(CreateTools());

        using HttpClient client = CreateRawClient();
        using HttpRequestMessage request = CreateMcpRequest(lease, new HttpMethod(method));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestTimeout());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("MCP-Protocol-Version", await response.Content.ReadAsStringAsync(TestTimeout()));
    }

    [Theory]
    [InlineData("GET", null, HttpStatusCode.BadRequest, false)]
    [InlineData("DELETE", null, HttpStatusCode.OK, false)]
    [InlineData("GET", "unknown-session", HttpStatusCode.NotFound, false)]
    [InlineData("DELETE", "unknown-session", HttpStatusCode.OK, true)]
    public async Task RawHttp_GetAndDeleteUseSdkSessionStatusCodesForMissingOrUnknownSession(
        string method,
        string? sessionId,
        HttpStatusCode expectedStatus,
        bool expectedExecutorCleanup)
    {
        var executor = new SpyToolExecutor();
        await using HostLease lease = await StartHostAsync(CreateTools(executor), executor);

        using HttpClient client = CreateRawClient();
        using HttpRequestMessage request = CreateSessionRequest(lease, new HttpMethod(method), sessionId);
        if (method == "GET")
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        }

        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestTimeout());

        Assert.Equal(expectedStatus, response.StatusCode);
        if (expectedExecutorCleanup)
        {
            string expectedSessionId = Assert.IsType<string>(sessionId);
            Assert.Equal(new[] { expectedSessionId }, executor.CancelledSessions);
        }
        else
        {
            Assert.Empty(executor.CancelledSessions);
        }
    }

    [Fact]
    public async Task RawHttp_InitializedNotificationAndDeleteTerminateOnlyThatSessionAndCleanupExecutor()
    {
        var executor = new SpyToolExecutor();
        await using HostLease lease = await StartHostAsync(CreateTools(executor), executor);

        using HttpClient client = CreateRawClient();
        string sessionId = await InitializeSessionAsync(lease, client);
        await SendInitializedNotificationAsync(lease, client, sessionId);

        using HttpRequestMessage delete = CreateSessionRequest(lease, HttpMethod.Delete, sessionId);
        using HttpResponseMessage deleteResponse = await client.SendAsync(delete, TestTimeout());

        Assert.True(deleteResponse.IsSuccessStatusCode, await deleteResponse.Content.ReadAsStringAsync());
        Assert.True(
            executor.CancelledSessions.Contains(sessionId, StringComparer.Ordinal),
            $"Expected executor cleanup for session '{sessionId}'. Seen: {string.Join(", ", executor.CancelledSessions)}");

        using HttpRequestMessage oldPost = CreateJsonRpcRequest(lease, "tools/list", includeProtocolHeader: true, sessionId);
        using HttpResponseMessage oldPostResponse = await client.SendAsync(oldPost, TestTimeout());
        Assert.Equal(HttpStatusCode.NotFound, oldPostResponse.StatusCode);

        using HttpRequestMessage oldGet = CreateSessionRequest(lease, HttpMethod.Get, sessionId);
        oldGet.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using HttpResponseMessage oldGetResponse = await client.SendAsync(oldGet, HttpCompletionOption.ResponseHeadersRead, TestTimeout());
        Assert.Equal(HttpStatusCode.NotFound, oldGetResponse.StatusCode);
    }

    [Fact]
    public async Task Lifecycle_StartStopTransitionsToRunningThenStopped_WhenPortIsAvailable()
    {
        var factory = AgasMcpHostFactory.CreateTestDynamicPort();
        await using var lifecycle = new AgasMcpLifecycleService(factory, CreateTools());
        var statuses = new List<AgasMcpRuntimeStatus>();
        lifecycle.StatusChanged += statuses.Add;

        Assert.Equal(AgasMcpRuntimeState.Disabled, lifecycle.Status.State);
        Assert.Equal(factory.Endpoint, lifecycle.Status.Endpoint);
        Assert.Equal("http://127.0.0.1:0/mcp", lifecycle.Status.Endpoint);

        try
        {
            await lifecycle.StartAsync(TestTimeout());

            Assert.Equal(AgasMcpRuntimeState.Running, lifecycle.Status.State);
            Assert.Equal(factory.Endpoint, lifecycle.Status.Endpoint);
            Assert.InRange(factory.Port, 1, 65535);
        }
        finally
        {
            if (lifecycle.Status.State == AgasMcpRuntimeState.Running)
            {
                await lifecycle.StopAsync(TestTimeout());
            }
        }


        Assert.Equal(AgasMcpRuntimeState.Stopped, lifecycle.Status.State);
        Assert.Equal(factory.Endpoint, lifecycle.Status.Endpoint);
        Assert.Equal(
            new[]
            {
                (AgasMcpRuntimeState.Starting, "http://127.0.0.1:0/mcp"),
                (AgasMcpRuntimeState.Running, factory.Endpoint),
                (AgasMcpRuntimeState.Stopping, factory.Endpoint),
                (AgasMcpRuntimeState.Stopped, factory.Endpoint),
            },
            statuses.Select(status => (status.State, status.Endpoint)).ToArray());
    }

    [Fact]
    public async Task Lifecycle_PortConflictReportsErrorWithoutThrowing()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            var factory = new AgasMcpHostFactory(port);
            await using var lifecycle = new AgasMcpLifecycleService(factory, CreateTools());

            await lifecycle.StartAsync(TestTimeout());

            Assert.Equal(AgasMcpRuntimeState.Error, lifecycle.Status.State);
            Assert.Equal(factory.Endpoint, lifecycle.Status.Endpoint);
            Assert.Equal($"http://127.0.0.1:{port}/mcp", lifecycle.Status.Endpoint);
            Assert.Contains(port.ToString(), lifecycle.Status.LastError);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<HostLease> StartHostAsync(IEnumerable<McpServerTool> tools, IAgasMcpToolExecutor? toolExecutor = null)
    {
        var factory = AgasMcpHostFactory.CreateTestDynamicPort();
        IHost host = factory.Create(tools, toolExecutor);
        try
        {
            await host.StartAsync(TestTimeout());
            Assert.InRange(factory.Port, 1, 65535);
            Assert.Equal($"http://127.0.0.1:{factory.Port}/mcp", factory.Endpoint);
            return new HostLease(host, factory);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            await DisposeHostAsync(host);
            throw new InvalidOperationException(
                $"Failed to start AGAS MCP test host on isolated endpoint {factory.Endpoint}; failing instead of silently passing.",
                ex);
        }
    }

    private static async ValueTask DisposeHostAsync(IHost host)
    {
        if (host is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            host.Dispose();
        }
    }

    private sealed class HostLease : IAsyncDisposable
    {
        public HostLease(IHost? host, AgasMcpHostFactory factory)
        {
            Host = host;
            Port = factory.Port;
            Endpoint = factory.Endpoint;
            Origin = $"http://{AgasMcpSettingsResource.FixedHost}:{Port}";
        }

        public IHost? Host { get; }
        public int Port { get; }
        public string Endpoint { get; }
        public string Origin { get; }

        public async ValueTask DisposeAsync()
        {
            if (Host != null)
            {
                await DisposeHostAsync(Host);
            }
        }
    }

    private static HttpClient CreateRawClient() => new() { Timeout = TimeSpan.FromSeconds(5) };

    private static HttpRequestMessage CreateJsonRpcRequest(
        HostLease lease,
        string method,
        bool includeProtocolHeader,
        string? sessionId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, lease.Endpoint)
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                method,
                @params = method == "initialize" ? new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { },
                    clientInfo = new { name = "agas-core-tests", version = "1.0.0" }
                } : null,
            })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (includeProtocolHeader)
        {
            request.Headers.Add(AgasMcpProtocolHeaderMiddleware.ProtocolVersionHeader, ProtocolVersion);
        }
        if (sessionId != null)
        {
            request.Headers.Add(AgasMcpSessionDeleteMiddleware.SessionIdHeader, sessionId);
        }
        return request;
    }

    private static HttpRequestMessage CreateMcpRequest(HostLease lease, HttpMethod method)
    {
        return new HttpRequestMessage(method, lease.Endpoint);
    }

    private static HttpRequestMessage CreateSessionRequest(HostLease lease, HttpMethod method, string? sessionId)
    {
        HttpRequestMessage request = CreateMcpRequest(lease, method);
        request.Headers.Add(AgasMcpProtocolHeaderMiddleware.ProtocolVersionHeader, ProtocolVersion);
        if (sessionId != null)
        {
            request.Headers.Add(AgasMcpSessionDeleteMiddleware.SessionIdHeader, sessionId);
        }

        return request;
    }

    private static async Task<string> InitializeSessionAsync(HostLease lease, HttpClient client)
    {
        using HttpRequestMessage initialize = CreateJsonRpcRequest(lease, "initialize", includeProtocolHeader: false);
        using HttpResponseMessage response = await client.SendAsync(initialize, TestTimeout());

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.TryGetValues(AgasMcpSessionDeleteMiddleware.SessionIdHeader, out IEnumerable<string>? values));
        string sessionId = Assert.Single(values);
        Assert.False(string.IsNullOrWhiteSpace(sessionId));
        return sessionId;
    }

    private static async Task SendInitializedNotificationAsync(HostLease lease, HttpClient client, string sessionId)
    {
        using HttpRequestMessage request = CreateSessionRequest(lease, HttpMethod.Post, sessionId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = JsonContent.Create(new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized"
        });

        using HttpResponseMessage response = await client.SendAsync(request, TestTimeout());

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string ExtractJsonPayload(string body)
    {
        if (body.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return body;
        }

        string? data = body.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => line["data:".Length..].TrimStart())
            .FirstOrDefault(line => line.StartsWith("{", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(data), body);
        return data;
    }

    private static IReadOnlyList<McpServerTool> CreateTools(IAgasMcpToolExecutor? executor = null)
    {
        var runtime = new AgasMcpToolRuntime(
            executor ?? new StubToolExecutor(), null!, (AgasMcpProjectFacade?)null);
        return AgasMcpToolCatalog.Create(runtime);
    }

    private static CancellationToken TestTimeout() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static bool JsonElementDeepEquals(JsonElement left, JsonElement right) =>
        JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    private sealed class StubToolExecutor : IAgasMcpToolExecutor
    {
        public Task<T> ExecuteAsync<T>(string? sessionId, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
        {
            object response = AgasMcpToolResponse.Failure(false, false, AgasMcpErrorCodes.NoOpenProject, "No project in host test.", "Open a project.");
            return Task.FromResult((T)response);
        }

        public Task ExecuteAsync(string? sessionId, Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Stop()
        {
        }
    }

    private sealed class SpyToolExecutor : IAgasMcpToolExecutor
    {
        private readonly ConcurrentQueue<string> _cancelledSessions = new();

        public IReadOnlyCollection<string> CancelledSessions => _cancelledSessions.ToArray();

        public Task<T> ExecuteAsync<T>(string? sessionId, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
        {
            object response = AgasMcpToolResponse.Failure(false, false, AgasMcpErrorCodes.NoOpenProject, "No project in host test.", "Open a project.");
            return Task.FromResult((T)response);
        }

        public Task ExecuteAsync(string? sessionId, Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CancelSessionAsync(string? sessionId, CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                _cancelledSessions.Enqueue(sessionId);
            }

            return Task.CompletedTask;
        }

        public void Stop()
        {
        }
    }

    private sealed class RecordingToolExecutor : IAgasMcpToolExecutor
    {
        private readonly ConcurrentQueue<string?> _sessionIds = new();
        private readonly AgasMcpToolResponse _response;

        public RecordingToolExecutor(AgasMcpToolResponse response)
        {
            _response = response;
        }

        public IReadOnlyList<string?> SessionIds => _sessionIds.ToArray();

        public Task<T> ExecuteAsync<T>(string? sessionId, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
        {
            _sessionIds.Enqueue(sessionId);
            return Task.FromResult((T)(object)_response);
        }

        public Task ExecuteAsync(string? sessionId, Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        {
            _sessionIds.Enqueue(sessionId);
            return Task.CompletedTask;
        }

        public void Stop()
        {
        }
    }
}
