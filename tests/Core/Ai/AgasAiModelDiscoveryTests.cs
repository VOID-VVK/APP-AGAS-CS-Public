using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Features.Ai.Settings;
using Xunit;

namespace AppAgasCs.Tests.Core.Ai;

public sealed class AgasAiModelDiscoveryTests
{
    private const string Catalog = """
        {"data":[{"id":"future-image","display_name":"Future image","image_generation":{"family":"gemini","protocol":"openai-images","capabilities":["text-to-image"],"ratios":["1:1"],"resolutions":["2k"],"defaults":{"aspect_ratio":"1:1","resolution":"2k"},"max_images":1}}]}
        """;

    [Fact]
    public void Metadata_KeepsClassificationIndependentOfProtocolAndPreservesLimits()
    {
        using JsonDocument json = JsonDocument.Parse(Catalog);
        AgasAiUnifiedModel model = Assert.Single(AgasAiModelMetadataParser.Parse(json.RootElement));
        Assert.Equal("gemini", model.Category);
        Assert.Equal("openai-compatible", model.WireAdapterKind);
        Assert.Equal(new[] { "textToImage" }, model.Capabilities);
        Assert.Equal(new[] { "1:1" }, model.Ratios);
        Assert.Equal(new[] { "2k" }, model.Resolutions);
        Assert.Equal("1:1", model.DefaultRatio);
        Assert.Equal("2k", model.DefaultResolution);
        Assert.Equal(1, model.MaxImages);
    }

    [Fact]
    public void Parser_ExcludesChatVideoAndMalformedRowsWithoutGuessingGeminiChatAsImage()
    {
        using JsonDocument json = JsonDocument.Parse("""
            {"data":[null,1,"oops",{"id":"gemini-3-pro"},{"id":"seedance-video"},{"id":"https://secret.example"},{"id":"gemini-image"},{"id":"custom","image_generation":{"family":"partner","protocol":"private-protocol","capabilities":["text-to-image","image-edit"]}}]}
            """);
        var models = AgasAiModelMetadataParser.Parse(json.RootElement);
        Assert.Equal(new[] { "gemini-image", "custom" }, models.Select(model => model.ModelName));
        Assert.True(models[0].IsAvailable);
        Assert.False(models[1].IsAvailable);
        Assert.Empty(models[1].Capabilities);
        Assert.Empty(models[1].Resolutions);
    }

    [Fact]
    public void Identity_NormalizesV1AliasButKeepsPathAndKeyBoundariesWithoutCredentials()
    {
        string first = AgasAiModelDiscovery.ConnectionIdentity("https://relay.example/prefix/v1/", "fixture-key-a");
        Assert.Equal(first, AgasAiModelDiscovery.ConnectionIdentity("https://relay.example/prefix", "fixture-key-a"));
        Assert.NotEqual(first, AgasAiModelDiscovery.ConnectionIdentity("https://relay.example/other", "fixture-key-a"));
        Assert.NotEqual(first, AgasAiModelDiscovery.ConnectionIdentity("https://relay.example/prefix", "fixture-key-b"));
        Assert.Equal(64, first.Length);
        Assert.DoesNotContain("fixture", first);
        Assert.Throws<ArgumentException>(() => AgasAiModelDiscovery.ConnectionIdentity("https://user:password@relay.example", "fixture-key"));
    }

    [Fact]
    public void MalformedMetadataCannotRewriteModelIdsOrLeaveDiscoveryPending()
    {
        string json = JsonSerializer.Serialize(new { data = new object[] {
            new { id = "gpt-image-" + new string('a', 210) }, new { id = "gpt-image-evil\n" },
            new { id = "custom", image_generation = new { family = "partner", protocol = "openai-images", capabilities = new[] { "text-to-image", "image-to-image", "image-edit" }, max_images = "invalid" } }
        } });
        using JsonDocument payload = JsonDocument.Parse(json);
        AgasAiUnifiedModel model = Assert.Single(AgasAiModelMetadataParser.Parse(payload.RootElement));
        Assert.Equal("custom", model.ModelName);
        Assert.Equal(new[] { "textToImage" }, model.Capabilities);
        Assert.Null(model.MaxImages);
    }

    [Fact]
    public async Task Discovery_UsesBearerPrefixAndOnlyThisConnectionsLastSuccess()
    {
        int calls = 0;
        using var client = Client((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://relay.example/prefix/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return Task.FromResult(calls == 1 ? Json(Catalog) : new HttpResponseMessage(HttpStatusCode.BadGateway));
        });
        var discovery = new AgasAiModelDiscovery(client);
        AgasAiProfile first = Profile();
        Assert.False(discovery.GetSnapshot(first).IsReady);
        Assert.Empty(discovery.GetSnapshot(first).Models);
        Assert.Equal("live", (await discovery.RefreshAsync(first)).Source);
        AgasAiModelSnapshot cached = await discovery.RefreshAsync(first);
        Assert.Equal("cache", cached.Source);
        Assert.Equal("future-image", Assert.Single(cached.Models).ModelName);
        AgasAiModelSnapshot otherKey = await discovery.RefreshAsync(first with { ApiKey = "fixture-key-b" });
        Assert.Equal("local", otherKey.Source);
        Assert.Equal(16, otherKey.Models.Count);
        Assert.DoesNotContain(otherKey.Models, model => model.ModelName == "future-image");
    }

    [Theory]
    [InlineData(401, null, "auth")]
    [InlineData(403, null, "auth")]
    [InlineData(200, "{\"data\":[]}", "empty")]
    [InlineData(200, "{\"wrong\":[]}", "error")]
    [InlineData(200, "not-json", "error")]
    public async Task Authority_ClearsEarlierSuccessAndSurvivesLaterNetworkFailure(int status, string? body, string source)
    {
        int calls = 0;
        using var client = Client((_, _) => Task.FromResult(++calls switch
        {
            1 => Json(Catalog),
            2 => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body ?? string.Empty) },
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
        }));
        var discovery = new AgasAiModelDiscovery(client);
        await discovery.RefreshAsync(Profile());
        AgasAiModelSnapshot authoritative = await discovery.RefreshAsync(Profile());
        Assert.Equal(source, authoritative.Source);
        Assert.Empty(authoritative.Models);
        AgasAiModelSnapshot later = await discovery.RefreshAsync(Profile());
        Assert.Equal(source, later.Source);
        Assert.Empty(later.Models);
    }

    [Fact]
    public async Task Cancellation_DoesNotCreateFallbackOrLeavePendingState()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = Client(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json(Catalog);
        });
        var discovery = new AgasAiModelDiscovery(client);
        using var cancel = new CancellationTokenSource();
        Task<AgasAiModelSnapshot> task = discovery.RefreshAsync(Profile(), cancel.Token);
        await started.Task;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(discovery.GetSnapshot(Profile()).IsReady);
        Assert.Equal("canceled", discovery.GetSnapshot(Profile()).Source);
        Assert.Empty(discovery.GetSnapshot(Profile()).Models);
    }

    [Fact]
    public async Task Timeout_UsesDatedCompleteFallbackWithKnownJimengDefaults()
    {
        using var client = Client(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Json(Catalog); });
        var snapshot = await new AgasAiModelDiscovery(client, TimeSpan.FromMilliseconds(20)).RefreshAsync(Profile());
        Assert.Equal("local", snapshot.Source);
        Assert.Contains("2026-10-24", snapshot.Message);
        Assert.Equal(16, snapshot.Models.Count);
        Assert.Equal(3, snapshot.Models.Count(model => model.Category == "gpt"));
        Assert.Single(snapshot.Models, model => model.Category == "gemini");
        Assert.Equal(12, snapshot.Models.Count(model => model.Category == "jimeng"));
        Assert.Equal(3, snapshot.Models.Count(model => model.Category == "jimeng" && model.DefaultResolution == "1k"));
        Assert.All(snapshot.Models.Where(model => model.Category == "jimeng"), model => Assert.Equal(new[] { "textToImage" }, model.Capabilities));
    }

    [Fact]
    public async Task OlderConcurrentSuccessCannotOverwriteNewerAuthority()
    {
        var first = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var client = Client((_, _) => ++calls == 1 ? first.Task : Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var discovery = new AgasAiModelDiscovery(client);
        Task<AgasAiModelSnapshot> stale = discovery.RefreshAsync(Profile());
        Assert.Equal("auth", (await discovery.RefreshAsync(Profile())).Source);
        first.SetResult(Json(Catalog));
        await stale;
        Assert.Equal("auth", discovery.GetSnapshot(Profile()).Source);
        Assert.Empty(discovery.GetSnapshot(Profile()).Models);
    }

    [Fact]
    public async Task OverlappingDenialInvalidatesCacheBeforeNewerTransientResponse()
    {
        var denial = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transient = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var client = Client((_, _) => ++calls switch { 1 => Task.FromResult(Json(Catalog)), 2 => denial.Task, _ => transient.Task });
        var discovery = new AgasAiModelDiscovery(client);
        await discovery.RefreshAsync(Profile());
        Task<AgasAiModelSnapshot> older = discovery.RefreshAsync(Profile());
        Task<AgasAiModelSnapshot> newer = discovery.RefreshAsync(Profile());
        denial.SetResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        await older;
        transient.SetResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        Assert.Equal("auth", (await newer).Source);
        Assert.Empty(discovery.GetSnapshot(Profile()).Models);
    }

    [Fact]
    public async Task LateOverlappingDenialAlsoInvalidatesAlreadyPublishedTransientCache()
    {
        var denial = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var client = Client((_, _) => ++calls switch {
            1 => Task.FromResult(Json(Catalog)), 2 => denial.Task,
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)) });
        var discovery = new AgasAiModelDiscovery(client);
        await discovery.RefreshAsync(Profile());
        Task<AgasAiModelSnapshot> older = discovery.RefreshAsync(Profile());
        Assert.Equal("cache", (await discovery.RefreshAsync(Profile())).Source);
        denial.SetResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        await older;
        Assert.Equal("auth", discovery.GetSnapshot(Profile()).Source);
        Assert.Empty(discovery.GetSnapshot(Profile()).Models);
    }

    [Fact]
    public async Task NewerCanceledRefreshCannotProtectRestoredLiveCatalogFromOlderDenial()
    {
        var denial = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var client = Client(async (_, token) =>
        {
            int call = ++calls;
            if (call == 1) return Json(Catalog);
            if (call == 2) return await denial.Task;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json(Catalog);
        });
        var discovery = new AgasAiModelDiscovery(client);
        await discovery.RefreshAsync(Profile());
        Task<AgasAiModelSnapshot> older = discovery.RefreshAsync(Profile());
        using var cancellation = new CancellationTokenSource();
        Task<AgasAiModelSnapshot> newer = discovery.RefreshAsync(Profile(), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => newer);
        Assert.Equal("live", discovery.GetSnapshot(Profile()).Source);
        denial.SetResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        await older;
        Assert.Equal("auth", discovery.GetSnapshot(Profile()).Source);
        Assert.Empty(discovery.GetSnapshot(Profile()).Models);
    }

    [Fact]
    public async Task NewerSuccessfulCatalogSupersedesOlderDenial()
    {
        var denial = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var client = Client((_, _) => ++calls == 1 ? denial.Task : Task.FromResult(Json(Catalog)));
        var discovery = new AgasAiModelDiscovery(client);
        Task<AgasAiModelSnapshot> older = discovery.RefreshAsync(Profile());
        Assert.Equal("live", (await discovery.RefreshAsync(Profile())).Source);
        denial.SetResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        await older;
        Assert.Equal("live", discovery.GetSnapshot(Profile()).Source);
        Assert.NotEmpty(discovery.GetSnapshot(Profile()).Models);
    }

    private static AgasAiProfile Profile() => new("fixture", "Relay", "axoncat-newapi", "gpt-image-2", "https://relay.example/prefix/v1", "fixture-key-a");
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => new(new Handler(respond));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
