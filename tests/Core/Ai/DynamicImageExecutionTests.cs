using System.Net;
using System.Text;
using System.Text.Json;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using Xunit;

namespace AppAgasCs.Tests.Core.Ai;

public sealed class DynamicImageExecutionTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==");
    private static AgasAiProfile Connection(string key = "fixture-key") =>
        new("connection", "Relay", AgasAiProviderIds.AxonCatNewApi, "ignored", "https://relay.example/prefix/v1", key);

    [Theory]
    [InlineData("jimeng-image-2.0-pro", "1k")]
    [InlineData("jimeng-image-5.0-lite", "2k")]
    public async Task Jimeng_UsesOnlyVerifiedMinimalBodyAndRejectsReferenceBeforePost(string model, string resolution)
    {
        using var models = new ImageModelDiscoveryFixture();
        models.Prime(Connection());
        AiGenerationProviderProfile runtime = AgasAiConnectionModelResolver.Resolve(Connection(), model, models.Discovery);
        Assert.Equal(new[] { "1:1" }, runtime.ModelConstraints!.Ratios);
        Assert.Equal(resolution, runtime.ModelConstraints.DefaultResolution);
        Assert.Equal(1, runtime.ModelConstraints.MaxImages);
        int posts = 0;
        using var client = new HttpClient(new Handler(async (message, ct) =>
        {
            posts++;
            Assert.Equal("https://relay.example/prefix/v1/images/generations", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer fixture-key", message.Headers.Authorization!.ToString());
            using JsonDocument payload = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(ct));
            Assert.Equal(new[] { "model", "n", "prompt" }, payload.RootElement.EnumerateObject().Select(item => item.Name).Order());
            Assert.Equal(model, payload.RootElement.GetProperty("model").GetString());
            Assert.Equal(1, payload.RootElement.GetProperty("n").GetInt32());
            return Image();
        }));
        var adapter = new NewApiImageAdapter(models.Discovery);
        Assert.Equal(Png, (await adapter.GenerateAsync(runtime,
            new AiGenerationRequest("/tmp/project", "cat", 1536, 1024), client, client, default)).Bytes);
        AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(() => adapter.GenerateAsync(runtime,
            new AiGenerationRequest("/tmp/project", "edit", referenceImages: new[] { AiGenerationReferenceImage.Create(Png, "image/png") }),
            client, client, default));
        Assert.Equal(AiGenerationErrorCode.UnsupportedOperation, error.Code);
        Assert.Equal(1, posts);
    }

    [Fact]
    public async Task MetadataDeclaredFutureModel_ExecutesThroughUnifiedDispatcher()
    {
        using var models = new ImageModelDiscoveryFixture();
        models.Prime(Connection());
        AiGenerationProviderProfile runtime = AgasAiConnectionModelResolver.Resolve(Connection(), "future-partner-image", models.Discovery);
        Assert.Equal("newapi-compatible", runtime.AdapterKind);
        int posts = 0;
        using var client = new HttpClient(new Handler(async (message, ct) =>
        {
            posts++;
            using JsonDocument payload = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(ct));
            Assert.Equal("future-partner-image", payload.RootElement.GetProperty("model").GetString());
            return Image();
        }));
        GeneratedImageData result = await new NewApiImageAdapter(models.Discovery).GenerateAsync(runtime,
            new AiGenerationRequest("/tmp/project", "future cat"), client, client, default);
        Assert.Equal(Png, result.Bytes);
        Assert.Equal(1, posts);
    }

    [Theory]
    [InlineData(401, "{}")]
    [InlineData(403, "{}")]
    [InlineData(200, "{\"data\":[]}")]
    [InlineData(200, "{\"invalid\":true}")]
    public async Task PermissionOrCatalogRevocation_BlocksPreviouslyResolvedExecutionAndSurvivesNetworkFailure(int status, string payload)
    {
        using var models = new ImageModelDiscoveryFixture();
        models.Prime(Connection());
        AiGenerationProviderProfile runtime = AgasAiConnectionModelResolver.Resolve(Connection(), "gpt-image-2", models.Discovery);
        models.Status = (HttpStatusCode)status;
        models.Payload = payload;
        models.Prime(Connection());
        models.Status = HttpStatusCode.BadGateway;
        models.Prime(Connection());
        Assert.False(models.Discovery.GetSnapshot(Connection()).TryGet("gpt-image-2", out _));
        using var client = new HttpClient(new Handler((_, _) => throw new Xunit.Sdk.XunitException("Revoked snapshot must not send a generation request.")));
        AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(() => new NewApiImageAdapter(models.Discovery)
            .GenerateAsync(runtime, new AiGenerationRequest("/tmp/project", "cat"), client, client, default));
        Assert.Equal(AiGenerationErrorCode.InvalidConfiguration, error.Code);
    }

    [Fact]
    public void RotatedKey_DoesNotBorrowPreviousKeysDiscoveredModels()
    {
        using var models = new ImageModelDiscoveryFixture();
        models.Prime(Connection());
        Assert.Throws<AiGenerationException>(() => AgasAiConnectionModelResolver.Resolve(
            Connection("rotated-key"), "future-partner-image", models.Discovery));
    }

    private static HttpResponseMessage Image() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { data = new[] { new { b64_json = Convert.ToBase64String(Png) } } }), Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
