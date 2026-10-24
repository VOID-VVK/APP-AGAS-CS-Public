using System.Net;
using System.Text;
using System.Text.Json;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using Xunit;

namespace AppAgasCs.Tests.Core.Ai;

public sealed class AgasAiConnectionModelResolverTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==");

    [Fact]
    public async Task LegacyProviderAndModelFields_CannotChangeResolvedNewApiExecution()
    {
        var connection = new AgasAiProfile(
            "shared-connection",
            "Shared connection",
            "legacy-provider-that-must-be-ignored",
            "legacy-model-that-must-be-ignored",
            "https://relay.example/custom/v1/",
            "shared-secret");

        using var models = new ImageModelDiscoveryFixture();
        models.Prime(connection);
        AiGenerationProviderProfile resolved = AgasAiConnectionModelResolver.Resolve(
            connection,
            " GPT-IMAGE-2.5-SUNBURST ", models.Discovery);

        Assert.Equal(connection.Id, resolved.Id);
        Assert.Equal(connection.Name, resolved.DisplayName);
        Assert.Equal(AgasAiProviderIds.AxonCatNewApi, resolved.ProviderId);
        Assert.Equal(AgasAiUnifiedModelCatalog.ConnectionAdapterKind, resolved.AdapterKind);
        Assert.Equal("gpt-image-2.5-sunburst", resolved.ModelName);
        Assert.Equal(connection.BaseUrl, resolved.BaseUri.AbsoluteUri);
        Assert.True(resolved.DeclaredCapabilities!.Value.HasFlag(
            AiGenerationAdapterCapabilities.NativeAlphaMask));

        using var handler = new Handler(async (request, cancellationToken) =>
        {
            Assert.Equal(
                "https://relay.example/custom/v1/images/generations",
                request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer shared-secret", request.Headers.Authorization!.ToString());
            using JsonDocument body = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("gpt-image-2.5-sunburst", body.RootElement.GetProperty("model").GetString());
            return Json(new
            {
                data = new[] { new { b64_json = Convert.ToBase64String(Png) } },
            });
        });
        using var client = new HttpClient(handler);

        GeneratedImageData result = await new NewApiImageAdapter(models.Discovery).GenerateAsync(
            resolved,
            new AiGenerationRequest("/tmp/project", "cat"),
            client,
            client,
            CancellationToken.None);

        Assert.Equal(Png, result.Bytes);
    }

    [Fact]
    public void UnknownModel_IsRejectedUnlessDeclaredByThisConnectionsMetadata()
    {
        var connection = new AgasAiProfile(
            "shared-connection",
            "Shared connection",
            "ignored-provider",
            "ignored-model",
            "https://relay.example/v1",
            "shared-secret");

        using var models = new ImageModelDiscoveryFixture();
        models.Prime(connection);
        AiGenerationException error = Assert.Throws<AiGenerationException>(() =>
            AgasAiConnectionModelResolver.Resolve(connection, " custom-image-model ", models.Discovery));
        Assert.Equal(AiGenerationErrorCode.InvalidConfiguration, error.Code);

        AiGenerationProviderProfile resolved = AgasAiConnectionModelResolver.Resolve(
            connection, " future-partner-image ", models.Discovery);
        Assert.Equal("future-partner-image", resolved.ModelName);
        Assert.Equal(AiGenerationAdapterCapabilities.TextToImage, resolved.DeclaredCapabilities);
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
