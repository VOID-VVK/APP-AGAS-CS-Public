using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Features.Ai.UI;

namespace AppAgasCs.Tests.Godot;

internal sealed class ImageModelsAcceptanceFixture : IDisposable
{
    private readonly HttpClient _client;
    public AgasAiModelDiscovery Discovery { get; }
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public TaskCompletionSource<bool>? Gate { get; set; }
    public string[] Models { get; } = AgasAiUnifiedModelCatalog.Models.Select(model => model.ModelName)
        .Concat(new[] { "grok-imagine-image-quality", "grok-imagine-image", "grok-imagine-image-2.0", "future-partner-image" }).ToArray();

    public ImageModelsAcceptanceFixture()
    {
        _client = new HttpClient(new ModelHandler(this));
        Discovery = new AgasAiModelDiscovery(_client);
    }

    public void Prime(AgasAiProfile connection) => Discovery.RefreshAsync(connection).GetAwaiter().GetResult();
    public AiGenerationPanel Panel(AgasAiSettingsStore store) => new(store, new NeverRunner(), Discovery);
    public AiImageGenerationAdapterRegistry Registry() => new(new[] { new NewApiImageAdapter(Discovery) });
    public void Dispose() => _client.Dispose();

    private sealed class ModelHandler(ImageModelsAcceptanceFixture fixture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get || !request.RequestUri!.AbsolutePath.EndsWith("/v1/models", StringComparison.Ordinal))
                throw new InvalidOperationException("Only intercepted model discovery is permitted.");
            if (fixture.Gate != null) await fixture.Gate.Task.WaitAsync(cancellationToken);
            var models = AgasAiUnifiedModelCatalog.Models.Select(model => (object)new
            {
                id = model.ModelName,
                image_generation = new
                {
                    family = model.Category,
                    protocol = model.WireAdapterKind == "newapi-chat-image" ? "chat-completions" : "openai-images",
                    capabilities = model.Category == "gpt" ? new[] { "text-to-image", "image-to-image", "image-edit" }
                        : model.Category == "gemini" ? new[] { "text-to-image", "image-to-image" } : new[] { "text-to-image" },
                    ratios = model.Ratios, resolutions = model.Resolutions,
                    defaults = new { aspect_ratio = model.DefaultRatio, resolution = model.DefaultResolution },
                    max_images = model.MaxImages,
                },
            }).Concat(fixture.Models.Skip(16).Select(id => (object)new { id,
                image_generation = new { family = "partner", protocol = "openai-images", capabilities = new[] { "text-to-image" } } }));
            return new HttpResponseMessage(fixture.Status)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { data = models }), Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class NeverRunner : IAiGenerationRunner
    {
        public Task<AiGenerationResult> GenerateAsync(AiGenerationProviderProfile profile,
            AiGenerationRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Panel generation is outside settings acceptance.");
    }
}
