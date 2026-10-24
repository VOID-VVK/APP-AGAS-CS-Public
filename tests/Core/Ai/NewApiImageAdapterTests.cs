using System.Net;
using System.Text;
using System.Text.Json;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Mcp;
using Xunit;

namespace AppAgasCs.Tests.Core.Ai;

public sealed class NewApiImageAdapterTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==");
    private static string DataUrl => "data:image/png;base64," + Convert.ToBase64String(Png);

    [Theory]
    [InlineData("gpt-image-2", "images/generations")]
    [InlineData("gpt-image-2.5-flare", "images/generations")]
    [InlineData("gpt-image-2.5-sunburst", "images/generations")]
    [InlineData("grok-imagine-image-quality", "images/generations")]
    [InlineData("grok-imagine-image", "images/generations")]
    [InlineData("grok-imagine-image-2.0", "images/generations")]
    [InlineData("gemini-image", "chat/completions")]
    public async Task EveryUnifiedModel_UsesSameBaseUriAndBearerWithItsCanonicalWireRoute(
        string model,
        string endpoint)
    {
        const string baseUrl = "https://relay.example/shared/v1";
        const string apiKey = "one-key-for-every-model";
        using var handler = new Handler(async (message, token) =>
        {
            Assert.Equal($"{baseUrl}/{endpoint}", message.RequestUri!.AbsoluteUri);
            Assert.Equal($"Bearer {apiKey}", message.Headers.Authorization!.ToString());
            using JsonDocument body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.Equal(model, body.RootElement.GetProperty("model").GetString());
            if (endpoint == "chat/completions")
            {
                Assert.True(body.RootElement.TryGetProperty("messages", out _));
                Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
                return Chat("![result](" + DataUrl + ")");
            }

            Assert.False(body.RootElement.TryGetProperty("messages", out _));
            Assert.Equal(
                model.StartsWith("grok-", StringComparison.Ordinal),
                body.RootElement.TryGetProperty("response_format", out JsonElement responseFormat)
                && responseFormat.GetString() == "b64_json");
            return Json(new { data = new[] { new { b64_json = Convert.ToBase64String(Png) } } });
        });
        using var client = new HttpClient(handler);
        var profile = new AiGenerationProviderProfile(
            "shared-newapi",
            "Shared New API",
            AgasAiProviderIds.AxonCatNewApi,
            AgasAiUnifiedModelCatalog.ConnectionAdapterKind,
            model,
            apiKey,
            baseUrl);

        GeneratedImageData result = await Adapter().GenerateAsync(
            profile,
            new AiGenerationRequest("/tmp/project", "cat"),
            client,
            client,
            CancellationToken.None);

        Assert.Equal(Png, result.Bytes);
    }

    [Theory]
    [InlineData("gpt-image-2", false)]
    [InlineData("gpt-image-2.5-flare", false)]
    [InlineData("gpt-image-2.5-sunburst", false)]
    [InlineData("gpt-image-2.5-flare", true)]
    [InlineData("gpt-image-2.5-sunburst", true)]
    public async Task GptModels_PreserveModelAndImagesContract(string model, bool referenceEdit)
    {
        using var handler = new Handler(async (message, token) =>
        {
            Assert.Equal(referenceEdit ? "/v1/images/edits" : "/v1/images/generations", message.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer test-key", message.Headers.Authorization!.ToString());
            if (referenceEdit)
            {
                MultipartFormDataContent multipart = Assert.IsType<MultipartFormDataContent>(message.Content);
                var parts = multipart.ToDictionary(part => part.Headers.ContentDisposition!.Name!.Trim('"'));
                Assert.Equal(model, await parts["model"].ReadAsStringAsync(token));
                Assert.Equal(Png, await parts["image[]"].ReadAsByteArrayAsync(token));
                Assert.Equal("image/png", parts["image[]"].Headers.ContentType!.MediaType);
                Assert.Equal("transparent", await parts["background"].ReadAsStringAsync(token));
                Assert.Equal("png", await parts["output_format"].ReadAsStringAsync(token));
                Assert.False(parts.ContainsKey("response_format"));
                Assert.False(parts.ContainsKey("images"));
            }
            else
            {
                using JsonDocument body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
                Assert.Equal(model, body.RootElement.GetProperty("model").GetString());
                Assert.Equal("transparent", body.RootElement.GetProperty("background").GetString());
                Assert.Equal("png", body.RootElement.GetProperty("output_format").GetString());
                Assert.False(body.RootElement.TryGetProperty("response_format", out _));
                Assert.False(body.RootElement.TryGetProperty("messages", out _));
            }
            return Json(new { data = new[] { new { b64_json = Convert.ToBase64String(Png) } } });
        });
        using var client = new HttpClient(handler);
        var request = new AiGenerationRequest("/tmp/project", "cat", referenceImages: referenceEdit ? new[] { AiGenerationReferenceImage.Create(Png, "image/png") } : null);
        GeneratedImageData result = await Adapter().GenerateAsync(Profile(model), request, client, client, CancellationToken.None);
        Assert.Equal(Png, result.Bytes);
        Assert.Equal(referenceEdit ? AiGenerationEffectiveOperation.ReferenceEdit : AiGenerationEffectiveOperation.TextToImage, result.EffectiveOperation);
    }

    [Fact]
    public async Task GptImageEdit_RetriesWithoutTransparentBackgroundWhenRelayReturns400()
    {
        int calls = 0;
        using var handler = new Handler(async (message, token) =>
        {
            calls++;
            var parts = Assert.IsType<MultipartFormDataContent>(message.Content)
                .ToDictionary(part => part.Headers.ContentDisposition!.Name!.Trim('"'));
            if (calls == 1)
            {
                Assert.Equal("transparent", await parts["background"].ReadAsStringAsync(token));
                Assert.Equal("png", await parts["output_format"].ReadAsStringAsync(token));
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"unknown parameter\"}}"),
                };
            }

            Assert.False(parts.ContainsKey("background"));
            Assert.False(parts.ContainsKey("output_format"));
            return Json(new { data = new[] { new { b64_json = Convert.ToBase64String(Png) } } });
        });
        using var client = new HttpClient(handler);
        var request = new AiGenerationRequest(
            "/tmp/project",
            "cat",
            referenceImages: new[] { AiGenerationReferenceImage.Create(Png, "image/png") });
        GeneratedImageData result = await Adapter().GenerateAsync(
            Profile("gpt-image-2.5-flare"),
            request,
            client,
            client,
            CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.Equal(Png, result.Bytes);
    }

    [Fact]
    public async Task ModelLookup_IsCaseInsensitiveButSendsTheCanonicalModelName()
    {
        using var handler = new Handler(async (message, token) =>
        {
            using JsonDocument body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.Equal("gpt-image-2.5-flare", body.RootElement.GetProperty("model").GetString());
            return Json(new { data = new[] { new { b64_json = Convert.ToBase64String(Png) } } });
        });
        using var client = new HttpClient(handler);

        GeneratedImageData result = await Adapter().GenerateAsync(
            Profile("GPT-IMAGE-2.5-FLARE"),
            new AiGenerationRequest("/tmp/project", "cat"),
            client,
            client,
            CancellationToken.None);

        Assert.Equal(Png, result.Bytes);
    }

    [Theory]
    [InlineData(null, "medium")]
    [InlineData(AiGenerationQuality.Low, "low")]
    [InlineData(AiGenerationQuality.Medium, "medium")]
    public async Task GrokImagineImage20_SendsDefaultOrExplicitQualityAsLowercaseJson(
        AiGenerationQuality? quality,
        string expectedQuality)
    {
        const string baseUrl = "https://relay.example/shared/v1";
        const string apiKey = "shared-grok-key";
        using var handler = new Handler(async (message, token) =>
        {
            Assert.Equal($"{baseUrl}/images/generations", message.RequestUri!.AbsoluteUri);
            Assert.Equal($"Bearer {apiKey}", message.Headers.Authorization!.ToString());
            using JsonDocument body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.Equal("grok-imagine-image-2.0", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(expectedQuality, body.RootElement.GetProperty("quality").GetString());
            Assert.False(body.RootElement.TryGetProperty("background", out _));
            Assert.False(body.RootElement.TryGetProperty("output_format", out _));
            return Json(new { data = new[] { new { b64_json = Convert.ToBase64String(Png) } } });
        });
        using var client = new HttpClient(handler);
        var profile = new AiGenerationProviderProfile(
            "shared-newapi",
            "Shared New API",
            AgasAiProviderIds.AxonCatNewApi,
            AgasAiUnifiedModelCatalog.ConnectionAdapterKind,
            "grok-imagine-image-2.0",
            apiKey,
            baseUrl);

        GeneratedImageData result = await Adapter().GenerateAsync(
            profile,
            new AiGenerationRequest("/tmp/project", "cat", quality: quality),
            client,
            client,
            CancellationToken.None);

        Assert.Equal(Png, result.Bytes);
    }

    [Theory]
    [InlineData("gpt-image-2")]
    [InlineData("gpt-image-2.5-flare")]
    [InlineData("gpt-image-2.5-sunburst")]
    [InlineData("grok-imagine-image-quality")]
    [InlineData("grok-imagine-image")]
    [InlineData("gemini-image")]
    public async Task NonGrok20Models_RejectQualityBeforeAnyHttpRequest(string model)
    {
        int requestCount = 0;
        using var handler = new Handler((_, _) =>
        {
            requestCount++;
            throw new Xunit.Sdk.XunitException("Unexpected upstream request");
        });
        using var client = new HttpClient(handler);

        AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(() =>
            Adapter().GenerateAsync(
                Profile(model),
                new AiGenerationRequest("/tmp/project", "cat", quality: AiGenerationQuality.Low),
                client,
                client,
                CancellationToken.None));

        Assert.Equal(AiGenerationErrorCode.UnsupportedOperation, error.Code);
        Assert.Equal(0, requestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gemini_UsesChatWithSameCredentialAndParsesMarkdown(bool withReference)
    {
        using var handler = new Handler(async (message, token) =>
        {
            Assert.Equal("https://ainewdev.cc/v1/chat/completions", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer test-key", message.Headers.Authorization!.ToString());
            using JsonDocument body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            JsonElement root = body.RootElement;
            Assert.Equal("gemini-image", root.GetProperty("model").GetString());
            Assert.False(root.GetProperty("stream").GetBoolean());
            Assert.False(root.TryGetProperty("contents", out _));
            JsonElement content = root.GetProperty("messages")[0].GetProperty("content");
            Assert.Contains("cat", content[0].GetProperty("text").GetString());
            Assert.Contains("1024x1024 pixels; aspect ratio 1:1", content[0].GetProperty("text").GetString());
            Assert.Equal(withReference ? 2 : 1, content.GetArrayLength());
            if (withReference)
                Assert.Equal(DataUrl, content[1].GetProperty("image_url").GetProperty("url").GetString());
            return Chat("Here is your image: ![cat](" + DataUrl + ")");
        });
        using var client = new HttpClient(handler);
        var request = new AiGenerationRequest("/tmp/project", "cat", referenceImages: withReference ? new[] { AiGenerationReferenceImage.Create(Png, "image/png") } : null);
        GeneratedImageData result = await Adapter().GenerateAsync(Profile("gemini-image", "/v1"), request, client, client, CancellationToken.None);
        Assert.Equal(Png, result.Bytes);
        Assert.Equal("image/png", result.MimeType);
        Assert.Equal(AiGenerationEditGuarantee.None, result.EditGuarantee);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("image_url")]
    public async Task Gemini_ParsesStructuredContent(string type)
    {
        object part = type == "text" ? new { type, text = "![cat](" + DataUrl + ")" } : new { type, image_url = new { url = DataUrl } };
        using var handler = new Handler((_, _) => Task.FromResult(Json(new { choices = new[] { new { message = new { content = new[] { part } } } } })));
        using var client = new HttpClient(handler);
        GeneratedImageData result = await Adapter().GenerateAsync(Profile("gemini-image"), new AiGenerationRequest("/tmp/project", "cat"), client, client, CancellationToken.None);
        Assert.Equal(Png, result.Bytes);
    }

    [Theory]
    [InlineData(2048, 1152, "16:9")]
    [InlineData(1024, 1024, "1:1")]
    public async Task Gemini_CarriesCanvasSizeAsPromptPreference(int width, int height, string ratio)
    {
        using var handler = new Handler(async (message, token) =>
        {
            using JsonDocument body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            string prompt = body.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
            Assert.Contains($"{width}x{height} pixels; aspect ratio {ratio}", prompt);
            Assert.Contains("cat", prompt);
            Assert.False(body.RootElement.TryGetProperty("imageConfig", out _));
            return Chat("![cat](" + DataUrl + ")");
        });
        using var client = new HttpClient(handler);
        await Adapter().GenerateAsync(Profile("gemini-image"), new AiGenerationRequest("/tmp/project", "cat", width, height), client, client, CancellationToken.None);
    }

    [Theory]
    [InlineData("No image available.")]
    [InlineData("![cat](data:image/png;base64,invalid)")]
    [InlineData("![cat](http://127.0.0.1/private.png)")]
    public async Task Gemini_RejectsMissingMalformedOrPrivateImages(string content)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Chat(content)));
        using var client = new HttpClient(handler);
        AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(() => Adapter().GenerateAsync(Profile("gemini-image"), new AiGenerationRequest("/tmp/project", "cat"), client, client, CancellationToken.None));
        Assert.Equal(AiGenerationErrorCode.InvalidProviderResponse, error.Code);
    }

    [Fact]
    public async Task Gemini_PropagatesCancellation()
    {
        using var handler = new Handler((_, token) => Task.FromCanceled<HttpResponseMessage>(token));
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Adapter().GenerateAsync(Profile("gemini-image"), new AiGenerationRequest("/tmp/project", "cat"), client, client, cancellation.Token));
    }

    [Fact]
    public async Task UnsupportedModels_AreRejectedBeforeSendingInsteadOfRemapped()
    {
        using var handler = new Handler((_, _) => throw new Xunit.Sdk.XunitException("Unexpected upstream request"));
        using var client = new HttpClient(handler);
        AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(() => Adapter().GenerateAsync(Profile("gemini-3-pro-image-preview"), new AiGenerationRequest("/tmp/project", "cat"), client, client, CancellationToken.None));
        Assert.Equal(AiGenerationErrorCode.InvalidConfiguration, error.Code);
    }

    [Theory]
    [InlineData("grok-imagine-image-quality")]
    [InlineData("grok-imagine-image")]
    [InlineData("grok-imagine-image-2.0")]
    public async Task GrokModels_RejectUnverifiedEditOperationsBeforeSending(string model)
    {
        using var handler = new Handler((_, _) => throw new Xunit.Sdk.XunitException("Unexpected upstream request"));
        using var client = new HttpClient(handler);
        var request = new AiGenerationRequest(
            "/tmp/project",
            "edit cat",
            referenceImages: new[] { AiGenerationReferenceImage.Create(Png, "image/png") });

        AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(() =>
            Adapter().GenerateAsync(Profile(model), request, client, client, CancellationToken.None));

        Assert.Equal(AiGenerationErrorCode.UnsupportedOperation, error.Code);
    }

    [Theory]
    [InlineData("gpt-image-2.5-flare", true)]
    [InlineData("gpt-image-2.5-sunburst", true)]
    [InlineData("gemini-image", false)]
    public async Task RegionEdit_UsesModelSpecificWireAndGuarantees(string model, bool nativeMask)
    {
        byte[] transparentPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGNgAAIAAAUAAen63NgAAAAASUVORK5CYII=");
        AiGenerationReferenceImage source = AiGenerationReferenceImage.Create(transparentPng, "image/png");
        var edit = new AiGenerationEditInput(source, source, new AiGenerationPixelRegion(0, 0, 1, 1));
        using var handler = new Handler(async (message, token) =>
        {
            if (nativeMask)
            {
                var parts = Assert.IsType<MultipartFormDataContent>(message.Content).ToDictionary(part => part.Headers.ContentDisposition!.Name!.Trim('"'));
                Assert.Equal(transparentPng, await parts["image[]"].ReadAsByteArrayAsync(token));
                Assert.Equal(transparentPng, await parts["mask"].ReadAsByteArrayAsync(token));
                Assert.Equal("image/png", parts["mask"].Headers.ContentType!.MediaType);
                return Json(new { data = new[] { new { b64_json = Convert.ToBase64String(Png) } } });
            }
            using JsonDocument body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            JsonElement content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
            Assert.Equal(2, content.GetArrayLength());
            Assert.Contains("Transparent pixels", content[0].GetProperty("text").GetString());
            Assert.Equal("data:image/png;base64," + Convert.ToBase64String(transparentPng), content[1].GetProperty("image_url").GetProperty("url").GetString());
            Assert.False(body.RootElement.TryGetProperty("mask", out _));
            return Chat("![cat](" + DataUrl + ")");
        });
        using var client = new HttpClient(handler);
        var request = new AiGenerationRequest("/tmp/project", "cat", 1, 1, operation: AiGenerationOperation.RegionEdit, editInput: edit);
        GeneratedImageData result = await Adapter().GenerateAsync(Profile(model), request, client, client, CancellationToken.None);
        Assert.Equal(nativeMask ? AiGenerationEffectiveOperation.MaskedEdit : AiGenerationEffectiveOperation.GuidedRegionEdit, result.EffectiveOperation);
        Assert.Equal(nativeMask ? AiGenerationEditGuarantee.ProviderMasked : AiGenerationEditGuarantee.GuidedReference, result.EditGuarantee);
        Assert.Equal(result.EffectiveOperation.ToString(), AgasMcpAiFacade.ResolveEffectiveOperation(request.Operation, "newapi-compatible", AiGenerationAdapterCapabilities.RegionEdit, model));
        // Even an overbroad custom provider declaration must not advertise a native Gemini mask.
        Assert.Equal(result.EffectiveOperation.ToString(), AgasMcpAiFacade.ResolveEffectiveOperation(request.Operation, "newapi-compatible", AiGenerationAdapterCapabilities.NativeAlphaMask, model));
    }

    [Fact]
    public async Task Gemini_ProviderErrorsDoNotExposeCredentialsOrResponseBody()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":{\"code\":\"invalid_api_key\",\"message\":\"test-key\"}}") }));
        using var client = new HttpClient(handler);
        AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(() => Adapter().GenerateAsync(Profile("gemini-image"), new AiGenerationRequest("/tmp/project", "cat"), client, client, CancellationToken.None));
        Assert.Equal(AiGenerationErrorCode.ProviderRequestFailed, error.Code);
        Assert.Contains("401", error.Message);
        Assert.DoesNotContain("test-key", error.ToString());
    }

    private static IAiImageGenerationAdapter Adapter() => AiImageGenerationAdapterRegistry.CreateDefault("/tmp/agas-no-adapter-plugins").Resolve("newapi-compatible");
    private static AiGenerationProviderProfile Profile(string model, string path = "") => new("newapi", "New API", "axoncat-newapi", "newapi-compatible", model, "test-key", "https://ainewdev.cc" + path);
    private static HttpResponseMessage Chat(string text) => Json(new { choices = new[] { new { message = new { content = text } } } });
    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
