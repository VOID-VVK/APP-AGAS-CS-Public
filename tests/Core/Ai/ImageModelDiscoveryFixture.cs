using System.Net;
using System.Text;
using System.Text.Json;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;

namespace AppAgasCs.Tests.Core.Ai;

/// <summary>A real discovery pipeline backed by a deterministic model endpoint.</summary>
internal sealed class ImageModelDiscoveryFixture : IDisposable
{
    private readonly HttpClient _client;
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public string Payload { get; set; } = CatalogJson();
    public AgasAiModelDiscovery Discovery { get; }

    public ImageModelDiscoveryFixture()
    {
        _client = new HttpClient(new CatalogHandler(this));
        Discovery = new AgasAiModelDiscovery(_client);
    }

    public AgasAiModelSnapshot Prime(AgasAiProfile connection) =>
        Discovery.RefreshAsync(connection).GetAwaiter().GetResult();

    public void Prime(AgasAiSettingsStore store)
    {
        foreach (AgasAiProfile profile in store.LoadProfiles().Profiles)
        {
            string? key = store.GetCredential(profile.Id);
            if (!string.IsNullOrWhiteSpace(key)) Prime(profile with { ApiKey = key });
        }
    }

    public AiImageGenerationAdapterRegistry Registry() => new(new[] { new NewApiImageAdapter(Discovery) });
    public void Dispose() => _client.Dispose();

    private static string CatalogJson()
    {
        var models = AgasAiUnifiedModelCatalog.Models.Select(model => (object)new
        {
            id = model.ModelName,
            image_generation = new
            {
                family = model.Category,
                protocol = model.WireAdapterKind == "newapi-chat-image" ? "chat-completions" : "openai-images",
                capabilities = model.Category == "gpt" ? new[] { "text-to-image", "image-to-image", "image-edit" }
                    : model.Category == "gemini" ? new[] { "text-to-image", "image-to-image" } : new[] { "text-to-image" },
                ratios = model.Ratios,
                resolutions = model.Resolutions,
                defaults = new { aspect_ratio = model.DefaultRatio, resolution = model.DefaultResolution },
                max_images = model.MaxImages,
            },
        }).Concat(new[] { "grok-imagine-image-quality", "grok-imagine-image", "grok-imagine-image-2.0", "future-partner-image" }
            .Select(id => (object)new { id, image_generation = new { family = "partner", protocol = "openai-images", capabilities = new[] { "text-to-image" } } }));
        return JsonSerializer.Serialize(new { data = models });
    }

    private sealed class CatalogHandler(ImageModelDiscoveryFixture fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Xunit.Assert.Equal(HttpMethod.Get, request.Method);
            Xunit.Assert.EndsWith("/v1/models", request.RequestUri!.AbsolutePath);
            Xunit.Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Task.FromResult(new HttpResponseMessage(fixture.Status)
            {
                Content = new StringContent(fixture.Payload, Encoding.UTF8, "application/json"),
            });
        }
    }
}

/// <summary>Only test setup discovers models; generation still uses the production dispatcher.</summary>
internal sealed class DiscoveredImageAdapter : IAiImageGenerationAdapter
{
    public string AdapterKind => AgasAiUnifiedModelCatalog.ConnectionAdapterKind;
    public AiGenerationAdapterCapabilities Capabilities => AiGenerationAdapterCapabilities.TextToImage
        | AiGenerationAdapterCapabilities.ReferenceEdit | AiGenerationAdapterCapabilities.RegionEdit;

    public async Task<GeneratedImageData> GenerateAsync(AiGenerationProviderProfile profile,
        AiGenerationRequest request, HttpClient providerHttpClient, HttpClient imageDownloadClient,
        CancellationToken cancellationToken)
    {
        using var fixture = new ImageModelDiscoveryFixture();
        fixture.Prime(new AgasAiProfile(profile.Id, profile.DisplayName, profile.ProviderId,
            profile.ModelName, profile.BaseUri.AbsoluteUri, profile.ApiKey));
        return await new NewApiImageAdapter(fixture.Discovery).GenerateAsync(profile, request,
            providerHttpClient, imageDownloadClient, cancellationToken);
    }
}
