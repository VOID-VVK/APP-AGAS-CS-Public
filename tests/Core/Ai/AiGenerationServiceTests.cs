using System.Net;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Prompting;
using Xunit;
using Xunit.Sdk;

namespace AppAgasCs.Tests.Core.Ai;

public sealed class AiGenerationServiceTests
{
    private static readonly byte[] PngBytes = { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4 };

    [Theory]
    [InlineData("openai-compatible", "gpt-image-2")]
    [InlineData("newapi-chat-image", "gemini-image")]
    public async Task ReferenceRepaint_ProviderReceivesExplicitRepaintWithoutFillOnlyOverride(string adapter, string model)
    {
        using var project = new TemporaryProject();
        bool chat = adapter == "newapi-chat-image";
        var handler = new RecordingHandler((_, _) => chat
            ? JsonResponse(new { choices = new[] { new { message = new { content = "data:image/png;base64," + Convert.ToBase64String(PngBytes) } } } })
            : JsonResponse(new { data = new[] { new { b64_json = Convert.ToBase64String(PngBytes) } } }));
        using var client = new HttpClient(handler);
        var registry = new AiImageGenerationAdapterRegistry(new IAiImageGenerationAdapter[]
            { new OpenAiCompatibleImageAdapter(), new NewApiChatImageAdapter() });
        var service = new AiGenerationService(client, registry);
        var profile = new AiGenerationProviderProfile("repaint", "Repaint", adapter, model, "fake-key", "https://relay.example/v1");
        const string instruction = "Repaint the opaque occupancy map as grass and dirt.";
        string prompt = PromptComposer.Compose(Array.Empty<PromptFragmentContent>(), instruction, PromptCompositionMode.ReferenceEdit);
        await service.GenerateAsync(profile, new AiGenerationRequest(project.Path, prompt, 8, 8,
            new[] { AiGenerationReferenceImage.Create(PngRgba(8, 8, (_, _) => (255, 0, 255, 255)), "image/png") },
            operation: AiGenerationOperation.ReferenceEdit));

        using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        string sentPrompt = chat
            ? body.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString()!
            : body.RootElement.GetProperty("prompt").GetString()!;
        Assert.Contains(instruction, sentPrompt);
        Assert.DoesNotContain("仅补全其中的透明或空白部分", sentPrompt);
        Assert.DoesNotContain("non-transparent pixels are protected", sentPrompt);
        Assert.Equal(1, sentPrompt.Split(PromptComposer.TaskHeader, StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Request_RegionMetadataIsOptionalAndPreservesQwGridRegions()
    {
        var legacyRequest = new AiGenerationRequest("/tmp/project", "prompt");
        Assert.Null(legacyRequest.RegionMetadata);

        var selection = new AiGenerationGridRegion(-2, 3, 4, 5);
        var replacement = new AiGenerationGridRegion(0, 4, 2, 1);
        var request = new AiGenerationRequest(
            "/tmp/project",
            "prompt",
            regionMetadata: new AiGenerationRegionMetadata(selection, replacement));

        Assert.Equal(selection, request.RegionMetadata?.SelectionRegion);
        Assert.Equal(replacement, request.RegionMetadata?.ReplacementRegion);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiGenerationGridRegion(0, 0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiGenerationGridRegion(0, 0, 1, 0));
    }

    [Fact]
    public void Request_FreezesSceneAndLayerStorageScope()
    {
        var request = new AiGenerationRequest(
            "/tmp/project",
            "prompt",
            sceneId: " scene-01 ",
            sceneName: " Forest ",
            layerId: " layer-ground ",
            layerName: " Ground ");

        Assert.Equal("scene-01", request.StorageScope.SceneId);
        Assert.Equal("Forest", request.StorageScope.SceneName);
        Assert.Equal("layer-ground", request.StorageScope.LayerId);
        Assert.Equal("Ground", request.StorageScope.LayerName);
    }

    [Fact]
    public void ReferenceImageFactory_RejectsEmptyAndUnsupportedInputs()
    {
        Assert.Throws<ArgumentException>(() => AiGenerationReferenceImage.Create(Array.Empty<byte>(), "image/png"));
        Assert.Throws<AiGenerationException>(() => AiGenerationReferenceImage.Create(PngBytes, "image/gif"));
        Assert.Throws<AiGenerationException>(() => AiGenerationReferenceImage.Create(new byte[] { 1, 2, 3, 4 }, "image/png"));
        AiGenerationReferenceImage image = AiGenerationReferenceImage.Create(PngBytes, "image/png");
        Assert.NotSame(PngBytes, image.Bytes);
        byte[] exposedBytes = image.Bytes;
        exposedBytes[0] = 0;
        Assert.Equal(137, image.Bytes[0]);
    }

    [Fact]
    public void Request_EnforcesTotalReferenceImageByteLimitWithoutOverflow()
    {
        AiGenerationReferenceImage eightMiB = AiGenerationReferenceImage.Create(PngBuffer(8 * 1024 * 1024), "image/png");
        AiGenerationReferenceImage nineMiB = AiGenerationReferenceImage.Create(PngBuffer(9 * 1024 * 1024), "image/png");

        _ = new AiGenerationRequest("/tmp/project", "prompt", referenceImages: Enumerable.Repeat(eightMiB, 8).ToArray());
        Assert.Throws<ArgumentException>(() =>
            new AiGenerationRequest("/tmp/project", "prompt", referenceImages: Enumerable.Repeat(nineMiB, 8).ToArray()));
    }

    [Fact]
    public void RegionEditContract_RequiresSameSizePngAndContainedPixelRegion()
    {
        AiGenerationReferenceImage source = AiGenerationReferenceImage.Create(PngRgba(
            4,
            3,
            (x, y) => (1, 2, 3, x is >= 1 and < 3 && y == 1 ? (byte)0 : (byte)255)), "image/png");
        AiGenerationReferenceImage mask = AiGenerationReferenceImage.Create(PngRgba(
            4,
            3,
            (x, y) => (255, 255, 255, x is >= 1 and < 3 && y == 1 ? (byte)0 : (byte)255)), "image/png");
        var edit = new AiGenerationEditInput(source, mask, new AiGenerationPixelRegion(1, 1, 2, 1));
        var request = new AiGenerationRequest("/tmp/project", "fill", 4, 3, operation: AiGenerationOperation.RegionEdit, editInput: edit);

        Assert.Equal(4, source.PixelWidth);
        Assert.Equal(3, source.PixelHeight);
        Assert.Equal(AiGenerationOperation.RegionEdit, request.Operation);
        Assert.Throws<AiGenerationException>(() => new AiGenerationEditInput(
            source,
            AiGenerationReferenceImage.Create(PngRgba(3, 3, (_, _) => (255, 255, 255, 255)), "image/png"),
            new AiGenerationPixelRegion(1, 1, 2, 1)));
        Assert.Throws<AiGenerationException>(() => new AiGenerationEditInput(
            source,
            AiGenerationReferenceImage.Create(PngRgba(4, 3, (x, y) => (255, 255, 255, x is >= 1 and < 3 && y == 1 ? (byte)255 : (byte)0)), "image/png"),
            new AiGenerationPixelRegion(1, 1, 2, 1)));
        AiGenerationException opaqueSource = Assert.Throws<AiGenerationException>(() => new AiGenerationEditInput(
            AiGenerationReferenceImage.Create(PngRgba(4, 3, (_, _) => (1, 2, 3, 255)), "image/png"),
            mask,
            new AiGenerationPixelRegion(1, 1, 2, 1)));
        Assert.Equal(AiGenerationErrorCode.InvalidEditInput, opaqueSource.Code);
        Assert.Throws<AiGenerationException>(() => new AiGenerationEditInput(source, mask, new AiGenerationPixelRegion(3, 2, 2, 1)));
        Assert.Throws<AiGenerationException>(() => new AiGenerationRequest("/tmp/project", "fill", operation: AiGenerationOperation.RegionEdit));
    }

    [Fact]
    public void RegionEditContract_ProtectsTransparentQPixelsOutsideEditRegion()
    {
        AiGenerationReferenceImage source = AiGenerationReferenceImage.Create(PngRgba(
            3,
            2,
            (_, _) => (10, 20, 30, 0)), "image/png");
        AiGenerationReferenceImage mask = AiGenerationReferenceImage.Create(PngRgba(
            3,
            2,
            (x, y) => (255, 255, 255, x == 1 && y == 0 ? (byte)0 : (byte)255)), "image/png");

        var edit = new AiGenerationEditInput(
            source,
            mask,
            new AiGenerationPixelRegion(1, 0, 1, 1));

        Assert.Equal(new AiGenerationPixelRegion(1, 0, 1, 1), edit.EditRegionPixels);
    }

    [Fact]
    public async Task GptRegionEdit_UsesLegacyJsonImagesAndMaskWireContract_AndReportsNativeGuarantee()
    {
        using var project = new TemporaryProject();
        byte[] sourceBytes = PngRgba(49, 65, (x, y) => (10, 20, 30, x is >= 10 and < 20 && y is >= 20 and < 30 ? (byte)0 : (byte)255));
        byte[] maskBytes = PngRgba(49, 65, (x, y) => (255, 255, 255, x is >= 10 and < 20 && y is >= 20 and < 30 ? (byte)0 : (byte)255));
        var edit = new AiGenerationEditInput(
            AiGenerationReferenceImage.Create(sourceBytes, "image/png"),
            AiGenerationReferenceImage.Create(maskBytes, "image/png"),
            new AiGenerationPixelRegion(10, 20, 10, 10));
        byte[] providerOutput = PngRgba(48, 64, (_, _) => (40, 50, 60, 255));
        var handler = new RecordingHandler((_, _) => JsonResponse(new
        {
            data = new[] { new { b64_json = Convert.ToBase64String(providerOutput) } }
        }));
        using var client = new HttpClient(handler);
        var service = new AiGenerationService(client);
        var profile = new AiGenerationProviderProfile("gpt", "GPT", "openai-compatible", "gpt-image-2", "secret", "https://relay.example/v1");

        AiGenerationResult result = await service.GenerateAsync(profile, new AiGenerationRequest(
            project.Path, "continue the road", 49, 65, operation: AiGenerationOperation.RegionEdit, editInput: edit));

        RecordedRequest sent = Assert.Single(handler.Requests);
        Assert.Equal("https://relay.example/v1/images/edits", sent.Uri.AbsoluteUri);
        Assert.StartsWith("application/json", sent.ContentType, StringComparison.OrdinalIgnoreCase);
        using JsonDocument requestBody = JsonDocument.Parse(sent.Body);
        JsonElement requestRoot = requestBody.RootElement;
        Assert.Equal(
            new[] { "model", "prompt", "n", "size", "background", "output_format", "images", "mask" },
            requestRoot.EnumerateObject().Select(property => property.Name));
        Assert.Equal("transparent", requestRoot.GetProperty("background").GetString());
        Assert.Equal("png", requestRoot.GetProperty("output_format").GetString());
        Assert.Equal("gpt-image-2", requestRoot.GetProperty("model").GetString());
        Assert.Equal("continue the road", requestRoot.GetProperty("prompt").GetString());
        Assert.Equal(1, requestRoot.GetProperty("n").GetInt32());
        Assert.False(requestRoot.TryGetProperty("response_format", out _),
            "gpt-image models reject response_format; they always return b64_json.");
        Assert.Equal("auto", requestRoot.GetProperty("size").GetString());
        JsonElement image = Assert.Single(requestRoot.GetProperty("images").EnumerateArray());
        Assert.Equal(new[] { "image_url" }, image.EnumerateObject().Select(property => property.Name));
        Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(sourceBytes)}", image.GetProperty("image_url").GetString());
        Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(maskBytes)}", requestRoot.GetProperty("mask").GetProperty("image_url").GetString());
        Assert.False(requestRoot.TryGetProperty("image", out _));
        Assert.DoesNotContain("multipart/form-data", sent.ContentType, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AiGenerationEffectiveOperation.MaskedEdit, result.EffectiveOperation);
        Assert.Equal(AiGenerationEditGuarantee.ProviderMasked, result.EditGuarantee);
        Assert.Equal(48, result.PixelWidth);
        Assert.Equal(64, result.PixelHeight);
    }

    [Fact]
    public async Task ChatRegionEdit_SendsTransparentHoleReferenceAndRegionPrompt_WithoutClaimingNativeMask()
    {
        using var project = new TemporaryProject();
        byte[] sourceBytes = PngRgba(4, 3, (x, y) => (10, 20, 30, x is >= 1 and < 3 && y == 1 ? (byte)0 : (byte)255));
        byte[] maskBytes = PngRgba(4, 3, (x, y) => (255, 255, 255, x is >= 1 and < 3 && y == 1 ? (byte)0 : (byte)255));
        var edit = new AiGenerationEditInput(
            AiGenerationReferenceImage.Create(sourceBytes, "image/png"),
            AiGenerationReferenceImage.Create(maskBytes, "image/png"),
            new AiGenerationPixelRegion(1, 1, 2, 1));
        var handler = new RecordingHandler((_, _) => JsonResponse(new
        {
            choices = new[] { new { message = new { content = "data:image/png;base64," + Convert.ToBase64String(PngBytes) } } }
        }));
        using var client = new HttpClient(handler);
        var diagnostics = new RecordingDiagnosticSink();
        var registry = new AiImageGenerationAdapterRegistry(new[] { new NewApiChatImageAdapter() });
        var service = new AiGenerationService(client, registry, diagnosticSink: diagnostics);
        var profile = new AiGenerationProviderProfile("gemini", "Gemini", "newapi-chat-image", "gemini-image", "secret", "https://relay.example/v1");

        AiGenerationResult result = await service.GenerateAsync(profile, new AiGenerationRequest(
            project.Path,
            "continue the road",
            4,
            3,
            regionMetadata: new AiGenerationRegionMetadata(
                new AiGenerationGridRegion(-2, 3, 4, 3),
                new AiGenerationGridRegion(-1, 4, 2, 1)),
            operation: AiGenerationOperation.RegionEdit,
            editInput: edit));

        RecordedRequest sent = Assert.Single(handler.Requests);
        using JsonDocument body = JsonDocument.Parse(sent.Body);
        Assert.Equal("https://relay.example/v1/chat/completions", sent.Uri.AbsoluteUri);
        JsonElement parts = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        string guidedPrompt = parts[0].GetProperty("text").GetString()!;
        Assert.Contains("Transparent pixels in the first reference image are editable", guidedPrompt, StringComparison.Ordinal);
        Assert.Contains("preserve the other pixels", guidedPrompt, StringComparison.Ordinal);
        Assert.True(
            guidedPrompt.IndexOf("[Technical details]", StringComparison.Ordinal)
                < guidedPrompt.IndexOf(PromptComposer.TaskHeader, StringComparison.Ordinal),
            "Provider technical details must precede the canonical task.");
        Assert.EndsWith(PromptComposer.ReferenceEditTask, guidedPrompt, StringComparison.Ordinal);
        string inlineUrl = parts[1].GetProperty("image_url").GetProperty("url").GetString()!;
        Assert.StartsWith("data:image/png;base64,", inlineUrl);
        byte[] inlinePng = Convert.FromBase64String(inlineUrl.Split(',')[1]);
        AiGenerationReferenceImage inlineQ = AiGenerationReferenceImage.Create(inlinePng, "image/png");
        Assert.Equal(4, inlineQ.PixelWidth);
        Assert.Equal(3, inlineQ.PixelHeight);
        Assert.Equal(sourceBytes, inlinePng);
        Assert.DoesNotContain("mask", sent.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aspect ratio 4:3", guidedPrompt);
        Assert.False(body.RootElement.TryGetProperty("generationConfig", out _));
        Assert.False(body.RootElement.TryGetProperty("width", out _));
        Assert.False(body.RootElement.TryGetProperty("height", out _));
        Assert.False(body.RootElement.TryGetProperty("size", out _));
        Assert.Equal(AiGenerationEffectiveOperation.GuidedRegionEdit, result.EffectiveOperation);
        Assert.Equal(AiGenerationEditGuarantee.GuidedReference, result.EditGuarantee);
        AiGenerationSafeRequestDiagnostic requestDiagnostic = Assert.Single(diagnostics.RequestDiagnostics);
        Assert.Equal("RegionEdit", requestDiagnostic.Operation);
        Assert.Equal(4, requestDiagnostic.RequestPixelWidth);
        Assert.Equal(3, requestDiagnostic.RequestPixelHeight);
        Assert.Equal(4, requestDiagnostic.QPixelWidth);
        Assert.Equal(3, requestDiagnostic.QPixelHeight);
        Assert.Equal(1, requestDiagnostic.WRelativeX);
        Assert.Equal(1, requestDiagnostic.WRelativeY);
        Assert.Equal(2, requestDiagnostic.WPixelWidth);
        Assert.Equal(1, requestDiagnostic.WPixelHeight);
        Assert.Equal(4, requestDiagnostic.SourcePngIhdrWidth);
        Assert.Equal(3, requestDiagnostic.SourcePngIhdrHeight);
        Assert.Equal(4, requestDiagnostic.MaskPngIhdrWidth);
        Assert.Equal(3, requestDiagnostic.MaskPngIhdrHeight);
        Assert.Equal(1, requestDiagnostic.ReferenceImageCount);
        string diagnosticJson = JsonSerializer.Serialize(requestDiagnostic);
        Assert.DoesNotContain("continue the road", diagnosticJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", diagnosticJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(sourceBytes), diagnosticJson, StringComparison.Ordinal);
        Assert.DoesNotContain("data:image", diagnosticJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(project.Path, diagnosticJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestDiagnostics_FailOpenWhenSinkThrows()
    {
        using var project = new TemporaryProject();
        var adapter = new FakeAdapter("openai-compatible");
        var registry = new AiImageGenerationAdapterRegistry(new[] { adapter });
        using var client = new HttpClient(new RecordingHandler(
            (Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>)((_, _) =>
                throw new InvalidOperationException("No HTTP expected."))));
        var service = new AiGenerationService(client, registry, diagnosticSink: new ThrowingRequestDiagnosticSink());
        var profile = new AiGenerationProviderProfile(
            "gpt",
            "GPT",
            "openai-compatible",
            "gpt-image-2",
            "secret",
            "https://relay.example/v1");

        AiGenerationResult result = await service.GenerateAsync(
            profile,
            new AiGenerationRequest(project.Path, "request diagnostics must not block generation"));

        Assert.Equal(1, adapter.Calls);
        Assert.EndsWith(".png", result.RelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegionEdit_AllowsSupportedNonPngProviderResultAndPreservesOperationMetadata()
    {
        using var project = new TemporaryProject();
        byte[] sourceBytes = PngRgba(2, 2, (x, y) => (10, 20, 30, x == 0 && y == 0 ? (byte)0 : (byte)255));
        byte[] maskBytes = PngRgba(2, 2, (x, y) => (255, 255, 255, x == 0 && y == 0 ? (byte)0 : (byte)255));
        var edit = new AiGenerationEditInput(
            AiGenerationReferenceImage.Create(sourceBytes, "image/png"),
            AiGenerationReferenceImage.Create(maskBytes, "image/png"),
            new AiGenerationPixelRegion(0, 0, 1, 1));
        byte[] jpegBytes = { 0xff, 0xd8, 0xff, 0xd9 };
        var handler = new RecordingHandler((_, _) => JsonResponse(new
        {
            data = new[] { new { b64_json = Convert.ToBase64String(jpegBytes), mime_type = "image/jpeg" } }
        }));
        using var client = new HttpClient(handler);
        var service = new AiGenerationService(client);
        var profile = new AiGenerationProviderProfile("gpt", "GPT", "openai-compatible", "gpt-image-2", "secret", "https://relay.example/v1");

        AiGenerationResult result = await service.GenerateAsync(profile, new AiGenerationRequest(
            project.Path, "fill", 2, 2, operation: AiGenerationOperation.RegionEdit, editInput: edit));

        Assert.Equal("image/jpeg", result.MimeType);
        Assert.EndsWith(".jpg", result.RelativePath, StringComparison.Ordinal);
        Assert.Equal(AiGenerationEffectiveOperation.MaskedEdit, result.EffectiveOperation);
        Assert.Equal(AiGenerationEditGuarantee.ProviderMasked, result.EditGuarantee);
    }

    [Fact]
    public async Task LegacyAdapter_DefaultCapabilitiesKeepOldOperationsButDoNotOptIntoRegionEdit()
    {
        using var project = new TemporaryProject();
        var legacy = new LegacyAdapter();
        IAiImageGenerationAdapter adapter = legacy;
        Assert.Equal(
            AiGenerationAdapterCapabilities.TextToImage | AiGenerationAdapterCapabilities.ReferenceEdit,
            adapter.Capabilities);
        var registry = new AiImageGenerationAdapterRegistry(new[] { adapter });
        using var client = new HttpClient(new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));
        var service = new AiGenerationService(client, registry);
        var profile = new AiGenerationProviderProfile("legacy", "Legacy", "openai-compatible", "legacy-model", "secret", "https://relay.example/v1");

        await service.GenerateAsync(profile, new AiGenerationRequest(project.Path, "old text request"));
        Assert.Equal(1, legacy.Calls);

        byte[] sourceBytes = PngRgba(1, 1, (_, _) => (1, 2, 3, 0));
        byte[] maskBytes = PngRgba(1, 1, (_, _) => (255, 255, 255, 0));
        var edit = new AiGenerationEditInput(
            AiGenerationReferenceImage.Create(sourceBytes, "image/png"),
            AiGenerationReferenceImage.Create(maskBytes, "image/png"),
            new AiGenerationPixelRegion(0, 0, 1, 1));
        AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(() => service.GenerateAsync(
            profile,
            new AiGenerationRequest(project.Path, "region", 1, 1, operation: AiGenerationOperation.RegionEdit, editInput: edit)));
        Assert.Equal(AiGenerationErrorCode.UnsupportedOperation, error.Code);
        Assert.Equal(1, legacy.Calls);
    }

    [Fact]
    public void ProviderHttpClientFactory_DisablesRedirectsAndDelegatesTimeoutToService()
    {
        using SocketsHttpHandler handler = AiProviderHttpClientFactory.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        using HttpClient client = AiProviderHttpClientFactory.Create();
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, AiSecureImageDownloadClient.Instance.Timeout);
        Assert.Equal(TimeSpan.FromMinutes(8), AiGenerationService.DefaultTimeout);
    }

    [Fact]
    public void ImageDownloadProxy_PrefersHttpsProxyEnvironment()
    {
        string? previous = Environment.GetEnvironmentVariable("HTTPS_PROXY");
        try
        {
            Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://127.0.0.1:19876");
            IWebProxy? proxy = AiImageDownloadProxy.Resolve(port => false);
            Assert.NotNull(proxy);
            Uri? destination = proxy!.GetProxy(new Uri("https://imgen.x.ai/image.png"));
            Assert.NotNull(destination);
            Assert.Equal(19876, destination!.Port);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HTTPS_PROXY", previous);
        }
    }

    [Fact]
    public void ImageDownloadProxy_UsesLocalClashPortWhenEnvironmentIsEmpty()
    {
        string? https = Environment.GetEnvironmentVariable("HTTPS_PROXY");
        string? http = Environment.GetEnvironmentVariable("HTTP_PROXY");
        try
        {
            Environment.SetEnvironmentVariable("HTTPS_PROXY", null);
            Environment.SetEnvironmentVariable("HTTP_PROXY", null);
            Environment.SetEnvironmentVariable("https_proxy", null);
            Environment.SetEnvironmentVariable("http_proxy", null);
            Environment.SetEnvironmentVariable("ALL_PROXY", null);
            Environment.SetEnvironmentVariable("all_proxy", null);
            IWebProxy? proxy = AiImageDownloadProxy.Resolve(port => port == 7897);
            Assert.NotNull(proxy);
            Uri? destination = proxy!.GetProxy(new Uri("https://imgen.x.ai/image.png"));
            Assert.Equal(AiImageDownloadProxy.ClashLoopback.Port, destination!.Port);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HTTPS_PROXY", https);
            Environment.SetEnvironmentVariable("HTTP_PROXY", http);
        }
    }

    [Fact]
    public async Task ProviderHttpClientFactory_DoesNotCancelAValidDelayedProviderResponse()
    {
        using var project = new TemporaryProject();
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(80, cancellationToken);
            return JsonResponse(new { data = new[] { new { b64_json = Convert.ToBase64String(PngBytes) } } });
        });
        using HttpClient client = AiProviderHttpClientFactory.Create(handler);
        var service = new AiGenerationService(client, timeout: TimeSpan.FromMilliseconds(500));
        var profile = new AiGenerationProviderProfile(
            "gpt",
            "GPT",
            "openai-compatible",
            "gpt-image-2",
            "secret",
            "https://relay.example/v1");

        AiGenerationResult result = await service.GenerateAsync(
            profile,
            new AiGenerationRequest(project.Path, "delayed provider response"));

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        Assert.EndsWith(".png", result.RelativePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1024, 1024, "1024x1024")]
    [InlineData(1536, 1024, "1536x1024")]
    [InlineData(1024, 1536, "1024x1536")]
    [InlineData(2048, 1536, "auto")]
    [InlineData(2448, 1392, "auto")]
    [InlineData(300, 900, "auto")]
    public async Task GptImageAdapter_UsesSupportedSizeEnumWithoutResponseFormat(int width, int height, string expectedSize)
    {
        using var project = new TemporaryProject();
        var handler = new RecordingHandler((_, _) => JsonResponse(new
        {
            data = new[] { new { b64_json = Convert.ToBase64String(PngBytes) } }
        }));
        using var client = new HttpClient(handler);
        var service = new AiGenerationService(client);
        var profile = new AiGenerationProviderProfile("gpt", "GPT", "openai-compatible", "gpt-image-2", "secret", "https://relay.example/v1");

        await service.GenerateAsync(profile, new AiGenerationRequest(project.Path, "tree", width, height));

        RecordedRequest sent = Assert.Single(handler.Requests);
        Assert.Equal("https://relay.example/v1/images/generations", sent.Uri.AbsoluteUri);
        Assert.StartsWith("application/json", sent.ContentType, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(sent.ContentLength);
        Assert.Equal(sent.BodyBytes.LongLength, sent.ContentLength.Value);
        using JsonDocument payload = JsonDocument.Parse(sent.Body);
        JsonElement root = payload.RootElement;
        Assert.Equal(
            new[] { "model", "prompt", "n", "size", "background", "output_format" },
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(expectedSize, root.GetProperty("size").GetString());
        Assert.Equal("transparent", root.GetProperty("background").GetString());
        Assert.Equal("png", root.GetProperty("output_format").GetString());
        Assert.False(root.TryGetProperty("response_format", out _),
            "gpt-image models must not send the legacy response_format field.");
        Assert.False(root.TryGetProperty("images", out _));
        Assert.False(root.TryGetProperty("mask", out _));
    }

    [Theory]
    [InlineData(300, 900, "300x900")]
    [InlineData(900, 300, "900x300")]
    [InlineData(800, 700, "800x700")]
    [InlineData(2048, 1536, "1024x1024")]
    public async Task LegacyOpenAiImageAdapter_KeepsIndependentDimensionCapAndResponseFormat(
        int width,
        int height,
        string expectedSize)
    {
        using var project = new TemporaryProject();
        var handler = new RecordingHandler((_, _) => JsonResponse(new
        {
            data = new[] { new { b64_json = Convert.ToBase64String(PngBytes) } }
        }));
        using var client = new HttpClient(handler);
        var service = new AiGenerationService(client);
        var profile = new AiGenerationProviderProfile(
            "dalle",
            "DALL·E Relay",
            "openai-compatible",
            "dall-e-3",
            "secret",
            "https://relay.example/v1");

        await service.GenerateAsync(profile, new AiGenerationRequest(project.Path, "tree", width, height));

        RecordedRequest sent = Assert.Single(handler.Requests);
        using JsonDocument payload = JsonDocument.Parse(sent.Body);
        JsonElement root = payload.RootElement;
        Assert.Equal(expectedSize, root.GetProperty("size").GetString());
        Assert.Equal("b64_json", root.GetProperty("response_format").GetString());
        Assert.False(root.TryGetProperty("background", out _),
            "Legacy DALL·E relays must not receive GPT Image background/alpha fields.");
        Assert.False(root.TryGetProperty("output_format", out _));
    }

    [Fact]
    public async Task GrokImagineAdapter_SendsB64ResponseFormat()
    {
        using var project = new TemporaryProject();
        var handler = new RecordingHandler((_, _) => JsonResponse(new
        {
            data = new[] { new { b64_json = Convert.ToBase64String(PngBytes) } }
        }));
        using var client = new HttpClient(handler);
        var service = new AiGenerationService(client);
        var profile = new AiGenerationProviderProfile(
            "grok",
            "Grok",
            "openai-compatible",
            "grok-imagine",
            "secret",
            "https://cpa.example/v1");

        await service.GenerateAsync(profile, new AiGenerationRequest(project.Path, "orange cat icon"));

        RecordedRequest sent = Assert.Single(handler.Requests);
        Assert.Equal("https://cpa.example/v1/images/generations", sent.Uri.AbsoluteUri);
        using JsonDocument payload = JsonDocument.Parse(sent.Body);
        JsonElement root = payload.RootElement;
        Assert.Equal("grok-imagine", root.GetProperty("model").GetString());
        Assert.Equal("b64_json", root.GetProperty("response_format").GetString());
        Assert.False(root.TryGetProperty("background", out _),
            "Grok Imagine must not send GPT Image background/alpha fields.");
        Assert.False(root.TryGetProperty("output_format", out _));
    }

    [Fact]
    public async Task GptAdapter_UsesOpenAiRouteHeaderAndPayload_ThenReturnsOnlyRelativePath()
    {
        using var project = new TemporaryProject();
        const string secret = "sk-test-secret-never-serialize";
        var handler = new RecordingHandler((request, _) => JsonResponse(new
        {
            data = new[] { new { b64_json = Convert.ToBase64String(PngBytes) } }
        }));
        using var client = new HttpClient(handler);
        var service = new AiGenerationService(client);
        var profile = new AiGenerationProviderProfile("gpt", "GPT Relay", "openai-compatible", "gpt-image-2", secret, "https://relay.example/v1");
        var reference = AiGenerationReferenceImage.Create(PngBytes, "image/png");

        AiGenerationResult result = await service.GenerateAsync(
            profile,
            new AiGenerationRequest(project.Path, "pixel art tree", 512, 256, new[] { reference }));

        RecordedRequest sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://relay.example/v1/images/edits", sent.Uri.AbsoluteUri);
        Assert.Equal($"Bearer {secret}", sent.Authorization);
        Assert.DoesNotContain(secret, sent.Body, StringComparison.Ordinal);
        Assert.StartsWith("application/json", sent.ContentType, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(sent.ContentLength);
        Assert.Equal(sent.BodyBytes.LongLength, sent.ContentLength.Value);
        using JsonDocument requestBody = JsonDocument.Parse(sent.Body);
        JsonElement requestRoot = requestBody.RootElement;
        Assert.Equal(
            new[] { "model", "prompt", "n", "size", "background", "output_format", "images" },
            requestRoot.EnumerateObject().Select(property => property.Name));
        Assert.Equal("gpt-image-2", requestRoot.GetProperty("model").GetString());
        Assert.Equal("pixel art tree", requestRoot.GetProperty("prompt").GetString());
        Assert.Equal(1, requestRoot.GetProperty("n").GetInt32());
        Assert.Equal("transparent", requestRoot.GetProperty("background").GetString());
        Assert.Equal("png", requestRoot.GetProperty("output_format").GetString());
        Assert.False(requestRoot.TryGetProperty("response_format", out _),
            "gpt-image models must omit response_format.");
        Assert.Equal("auto", requestRoot.GetProperty("size").GetString());
        JsonElement image = Assert.Single(requestRoot.GetProperty("images").EnumerateArray());
        Assert.Equal(new[] { "image_url" }, image.EnumerateObject().Select(property => property.Name));
        Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(PngBytes)}", image.GetProperty("image_url").GetString());
        Assert.False(requestRoot.TryGetProperty("image", out _));
        Assert.False(requestRoot.TryGetProperty("mask", out _));
        Assert.DoesNotContain("multipart/form-data", sent.ContentType, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("agas/scene/layer/.candidates/Candidate 1.png", result.RelativePath);
        Assert.False(System.IO.Path.IsPathRooted(result.RelativePath));
        Assert.Equal(PngBytes, await File.ReadAllBytesAsync(System.IO.Path.Combine(project.Path, result.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar))));
        string serializedResult = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("base64", serializedResult, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(PngBytes), serializedResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedGeneration_SavesFinalPngUnderSafeSceneAndLayerDirectories()
    {
        using var project = new TemporaryProject();
        var adapter = new FakeAdapter("openai-compatible");
        var registry = new AiImageGenerationAdapterRegistry(new[] { adapter });
        using var client = new HttpClient(new RecordingHandler(
            (Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>)((_, _) =>
                throw new InvalidOperationException("No HTTP expected."))));
        var service = new AiGenerationService(client, registry);
        var profile = new AiGenerationProviderProfile(
            "gpt",
            "GPT",
            "openai-compatible",
            "gpt-image-2",
            "secret",
            "https://relay.example/v1");

        AiGenerationResult result = await service.GenerateAsync(
            profile,
            new AiGenerationRequest(
                project.Path,
                "tree",
                sceneId: "scene-forest",
                sceneName: "Forest",
                layerId: "layer-ground",
                layerName: "Ground"));

        Assert.Equal("agas/Forest/Ground/.candidates/Candidate 1.png", result.RelativePath);
        Assert.False(System.IO.Path.IsPathRooted(result.RelativePath));
        string absolutePath = System.IO.Path.Combine(
            project.Path,
            result.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Assert.Equal(PngBytes, await File.ReadAllBytesAsync(absolutePath));
    }

    [Fact]
    public async Task ScopedGeneration_SanitizesTraversalAndIllegalScopeNamesInsideAgasRoot()
    {
        using var project = new TemporaryProject();
        var adapter = new FakeAdapter("openai-compatible");
        var registry = new AiImageGenerationAdapterRegistry(new[] { adapter });
        using var client = new HttpClient(new RecordingHandler(
            (Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>)((_, _) =>
                throw new InvalidOperationException("No HTTP expected."))));
        var service = new AiGenerationService(client, registry);
        var profile = new AiGenerationProviderProfile(
            "gpt",
            "GPT",
            "openai-compatible",
            "gpt-image-2",
            "secret",
            "https://relay.example/v1");

        AiGenerationResult result = await service.GenerateAsync(
            profile,
            new AiGenerationRequest(
                project.Path,
                "tree",
                sceneId: "../\\/:*?\"<>|",
                sceneName: "../../",
                layerId: "..\\//",
                layerName: "<>:*?|"));

        Assert.Equal("agas/scene/layer/.candidates/Candidate 1.png", result.RelativePath);
        Assert.DoesNotContain("../", result.RelativePath, StringComparison.Ordinal);
        string absolutePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            project.Path,
            result.RelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        Assert.StartsWith(
            System.IO.Path.GetFullPath(System.IO.Path.Combine(project.Path, "agas")) + System.IO.Path.DirectorySeparatorChar,
            absolutePath,
            StringComparison.Ordinal);
        Assert.True(File.Exists(absolutePath));
    }

    [Fact]
    public async Task ScopedGeneration_RejectsSceneDirectorySymlinkWithoutWritingOutsideProject()
    {
        using var project = new TemporaryProject();
        using var outside = new TemporaryProject();
        string agasDirectory = System.IO.Path.Combine(project.Path, "agas");
        Directory.CreateDirectory(agasDirectory);
        string sceneLink = System.IO.Path.Combine(agasDirectory, "Forest");
        CreateDirectorySymlinkOrSkip(sceneLink, outside.Path);

        var adapter = new FakeAdapter("openai-compatible");
        var registry = new AiImageGenerationAdapterRegistry(new[] { adapter });
        using var client = new HttpClient(new RecordingHandler(
            (Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>)((_, _) =>
                throw new InvalidOperationException("No HTTP expected."))));
        var service = new AiGenerationService(client, registry);
        var profile = new AiGenerationProviderProfile(
            "gpt",
            "GPT",
            "openai-compatible",
            "gpt-image-2",
            "secret",
            "https://relay.example/v1");

        AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(() =>
            service.GenerateAsync(
                profile,
                new AiGenerationRequest(
                    project.Path,
                    "tree",
                    sceneId: "scene-forest",
                    sceneName: "Forest",
                    layerId: "layer-ground",
                    layerName: "Ground")));

        Assert.Equal(AiGenerationErrorCode.SaveFailed, error.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
    }

    [Fact]
    public async Task ProviderHtmlSuccess_IsReportedAsInvalidResponseWithoutLeakingBody()
    {
        using var project = new TemporaryProject();
        const string secret = "html-response-secret";
        var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"<html><body>{secret} private prompt</body></html>", Encoding.UTF8, "text/html")
        });
        using var client = new HttpClient(handler);
        var service = new AiGenerationService(client);
        var profile = new AiGenerationProviderProfile(
            "image",
            "Image Relay",
            "openai-compatible",
            "gpt-image-2",
            secret,
            "https://relay.example");

        AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(() =>
            service.GenerateAsync(profile, new AiGenerationRequest(project.Path, "private prompt")));

        Assert.Equal(AiGenerationErrorCode.InvalidProviderResponse, error.Code);
        Assert.Contains("网页而不是 JSON", error.Message, StringComparison.Ordinal);
        Assert.Contains("API 前缀", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private prompt", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UrlResponse_IsDownloadedWithoutForwardingProviderKey()
    {
        using var project = new TemporaryProject();
        const string secret = "provider-secret";
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(new { data = new[] { new { url = "https://93.184.216.34/generated.png" } } });
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(PngBytes)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            return response;
        });
        using var client = new HttpClient(handler);
        var service = new AiGenerationService(client, imageDownloadClient: client);
        var profile = new AiGenerationProviderProfile("gpt", "GPT", "openai-compatible", "gpt-image-2", secret, "https://relay.example");

        AiGenerationResult result = await service.GenerateAsync(profile, new AiGenerationRequest(project.Path, "tree"));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("Bearer provider-secret", handler.Requests[0].Authorization);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal("https://93.184.216.34/generated.png", handler.Requests[1].Uri.AbsoluteUri);
        Assert.Null(handler.Requests[1].Authorization);
        Assert.Null(handler.Requests[1].ApiKey);
        Assert.EndsWith(".png", result.RelativePath);
    }

    [Fact]
    public async Task ProviderFailure_IsRedactedAndLeavesNoPartialFile()
    {
        using var project = new TemporaryProject();
        const string secret = "top-secret-api-key";
        const string highEntropyCorrelation = "corr_A9xK7mQ2pL8vN4sR6tY1uW5zB3cD0eF7";
        var handler = new RecordingHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    $"{{\"error\":{{\"status\":\"UNAUTHENTICATED\",\"message\":\"bad key {secret}\"}},\"request_id\":\"{secret}\",\"correlation_id\":\"{highEntropyCorrelation}\"}}",
                    Encoding.UTF8,
                    "application/json")
            };
            response.Headers.TryAddWithoutValidation("x-request-id", secret);
            response.Headers.TryAddWithoutValidation("x-client-request-id", highEntropyCorrelation);
            return response;
        });
        using var client = new HttpClient(handler);
        var diagnosticSink = new RecordingDiagnosticSink();
        var service = new AiGenerationService(client, diagnosticSink: diagnosticSink);
        var profile = new AiGenerationProviderProfile("gpt", "GPT", "openai-compatible", "gpt-image-2", secret, "https://relay.example");

        AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(() =>
            service.GenerateAsync(profile, new AiGenerationRequest(project.Path, "tree")));

        Assert.Equal(AiGenerationErrorCode.ProviderRequestFailed, error.Code);
        Assert.Contains("HTTP 401", error.Message, StringComparison.Ordinal);
        Assert.Contains("code=UNAUTHENTICATED", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("request-id", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(highEntropyCorrelation, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(highEntropyCorrelation, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        AiGenerationSafeDiagnostic diagnostic = Assert.Single(diagnosticSink.Diagnostics);
        Assert.Same(diagnostic, error.Diagnostic);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.DiagnosticId));
        Assert.Equal(AiGenerationFailurePhase.ProviderResponse, diagnostic.Phase);
        Assert.Equal(AiGenerationFailureCategory.ProviderRejected, diagnostic.Category);
        Assert.Equal(401, diagnostic.HttpStatus);
        Assert.Equal("UNAUTHENTICATED", diagnostic.UpstreamCode);
        Assert.Equal("relay.example", diagnostic.EndpointHost);
        string diagnosticJson = JsonSerializer.Serialize(diagnostic);
        Assert.DoesNotContain(secret, diagnosticJson, StringComparison.Ordinal);
        Assert.DoesNotContain("tree", diagnosticJson, StringComparison.Ordinal);
        Assert.DoesNotContain(highEntropyCorrelation, diagnosticJson, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", diagnosticJson, StringComparison.OrdinalIgnoreCase);
        string generated = System.IO.Path.Combine(project.Path, "assets", "generated");
        Assert.False(Directory.Exists(generated) && Directory.EnumerateFileSystemEntries(generated).Any());
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, "解析中转站域名")]
    [InlineData(HttpRequestError.SecureConnectionError, "TLS 安全连接")]
    [InlineData(HttpRequestError.ConnectionError, "无法连接中转站")]
    [InlineData(HttpRequestError.ResponseEnded, "响应完成前关闭")]
    [InlineData(HttpRequestError.InvalidResponse, "无效的 HTTP 响应")]
    [InlineData(HttpRequestError.HttpProtocolError, "HTTP 协议通信失败")]
    [InlineData(HttpRequestError.VersionNegotiationError, "协商 HTTP 协议版本")]
    [InlineData(HttpRequestError.UserAuthenticationError, "连接认证失败")]
    [InlineData(HttpRequestError.ProxyTunnelError, "网络代理连接")]
    [InlineData(HttpRequestError.ConfigurationLimitExceeded, "配置限制")]
    public async Task ProviderTransportFailure_IsClassifiedWithoutLeakingRequest(
        HttpRequestError requestError,
        string expectedMessage)
    {
        using var project = new TemporaryProject();
        const string secret = "transport-secret";
        var handler = new RecordingHandler(
            (Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>)((_, _) =>
                throw new HttpRequestException(requestError, $"transport detail {secret} private prompt")));
        using var client = new HttpClient(handler);
        var diagnosticSink = new RecordingDiagnosticSink();
        var service = new AiGenerationService(client, diagnosticSink: diagnosticSink);
        var profile = new AiGenerationProviderProfile(
            "image",
            "Image Relay",
            "openai-compatible",
            "gpt-image-2",
            secret,
            "https://relay.example/v1");

        AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(() =>
            service.GenerateAsync(profile, new AiGenerationRequest(project.Path, "private prompt")));

        Assert.Equal(AiGenerationErrorCode.ProviderRequestFailed, error.Code);
        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private prompt", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        AiGenerationSafeDiagnostic diagnostic = Assert.Single(diagnosticSink.Diagnostics);
        Assert.Same(diagnostic, error.Diagnostic);
        Assert.Equal(AiGenerationFailurePhase.ProviderRequest, diagnostic.Phase);
        Assert.Equal(AiGenerationDiagnostics.CategoryFor(requestError), diagnostic.Category);
        Assert.Null(diagnostic.HttpStatus);
        Assert.Equal(requestError.ToString(), diagnostic.UpstreamCode);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.DiagnosticId));
    }

    [Fact]
    public async Task ProviderRedirect_IsControlledFailureAndDoesNotForwardKey()
    {
        using var project = new TemporaryProject();
        const string secret = "redirect-test-secret";
        var handler = new RecordingHandler((_, _) =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            redirect.Headers.Location = new Uri("https://redirect.example/collect");
            return redirect;
        });
        using var client = new HttpClient(handler);
        var service = new AiGenerationService(client);
        var profile = new AiGenerationProviderProfile(
            "profile",
            "Provider",
            "openai-compatible",
            "gpt-image-2",
            secret,
            "https://relay.example/v1");

        AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(() =>
            service.GenerateAsync(profile, new AiGenerationRequest(project.Path, "tree")));

        Assert.Equal(AiGenerationErrorCode.ProviderRequestFailed, error.Code);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal($"Bearer {secret}", request.Authorization);
        Assert.Null(request.ApiKey);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidImageBytes_LeaveNoFinalOrTemporaryFile()
    {
        using var project = new TemporaryProject();
        var handler = new RecordingHandler((_, _) => JsonResponse(new
        {
            data = new[] { new { b64_json = Convert.ToBase64String(new byte[] { 1, 2, 3 }) } }
        }));
        using var client = new HttpClient(handler);
        var service = new AiGenerationService(client);
        var profile = new AiGenerationProviderProfile("gpt", "GPT", "openai-compatible", "gpt-image-2", "secret", "https://relay.example");

        AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(() =>
            service.GenerateAsync(profile, new AiGenerationRequest(project.Path, "tree")));

        Assert.Equal(AiGenerationErrorCode.UnsupportedImage, error.Code);
        string generated = System.IO.Path.Combine(project.Path, "assets", "generated");
        Assert.False(Directory.Exists(generated) && Directory.EnumerateFileSystemEntries(generated).Any());
    }

    [Fact]
    public async Task RetiredGoogleAdapter_CannotSendKeyOrSilentlySelectAnotherProtocol()
    {
        using var project = new TemporaryProject();
        var handler = new RecordingHandler((Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>)
            ((_, _) => throw new InvalidOperationException("No request expected.")));
        using var client = new HttpClient(handler);
        var profile = new AiGenerationProviderProfile("legacy", "Legacy Google", "gemini-compatible",
            "gemini-3-pro-image-preview", "legacy-key", "https://generativelanguage.googleapis.com/v1beta");
        var service = new AiGenerationService(client);
        AiGenerationException failure = await Assert.ThrowsAsync<AiGenerationException>(() =>
            service.GenerateAsync(profile, new AiGenerationRequest(project.Path, "private prompt")));
        Assert.Equal(AiGenerationErrorCode.AdapterNotFound, failure.Code);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain("legacy-key", failure.ToString());
    }

    [Fact]
    public async Task Registry_RoutesByExplicitAdapterKind()
    {
        using var project = new TemporaryProject();
        var gpt = new FakeAdapter("openai-compatible");
        var nano = new FakeAdapter("custom.vendor-images");
        var registry = new AiImageGenerationAdapterRegistry(new IAiImageGenerationAdapter[] { gpt, nano });
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> unexpectedHttp =
            (_, _) => throw new InvalidOperationException("No HTTP expected.");
        using var client = new HttpClient(new RecordingHandler(unexpectedHttp));
        var service = new AiGenerationService(client, registry);
        var profile = new AiGenerationProviderProfile("custom", "Custom", "custom.vendor-images", "custom-model", "secret", "https://relay.example");

        await service.GenerateAsync(profile, new AiGenerationRequest(project.Path, "tree"));

        Assert.Equal(0, gpt.Calls);
        Assert.Equal(1, nano.Calls);
    }

    [Fact]
    public async Task ResponseUrl_RejectsPrivateAndMetadataAddressesBeforeDownload()
    {
        using var project = new TemporaryProject();
        var handler = new RecordingHandler((_, _) => JsonResponse(new
        {
            data = new[] { new { url = "http://169.254.169.254/latest/meta-data/" } }
        }));
        using var client = new HttpClient(handler);
        var service = new AiGenerationService(client, imageDownloadClient: client);
        var profile = new AiGenerationProviderProfile("gpt", "GPT", "openai-compatible", "gpt-image-2", "secret", "https://relay.example");

        AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(() =>
            service.GenerateAsync(profile, new AiGenerationRequest(project.Path, "tree")));

        Assert.Equal(AiGenerationErrorCode.InvalidProviderResponse, error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ResponseUrl_DoesNotFollowRedirects()
    {
        using var project = new TemporaryProject();
        var handler = new RecordingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(new { data = new[] { new { url = "https://93.184.216.34/generated.png" } } });
            }

            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri("http://127.0.0.1/private.png");
            return redirect;
        });
        using var client = new HttpClient(handler);
        var service = new AiGenerationService(client, imageDownloadClient: client);
        var profile = new AiGenerationProviderProfile("gpt", "GPT", "openai-compatible", "gpt-image-2", "secret", "https://relay.example");

        AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(() =>
            service.GenerateAsync(profile, new AiGenerationRequest(project.Path, "tree")));

        Assert.Equal(AiGenerationErrorCode.ProviderRequestFailed, error.Code);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task OversizedJsonContentLength_IsRejectedBeforeReadingBody()
    {
        using var project = new TemporaryProject();
        var handler = new RecordingHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Array.Empty<byte>())
            };
            response.Content.Headers.ContentLength = 100L * 1024 * 1024;
            return response;
        });
        using var client = new HttpClient(handler);
        var service = new AiGenerationService(client);
        var profile = new AiGenerationProviderProfile("gpt", "GPT", "openai-compatible", "gpt-image-2", "secret", "https://relay.example");

        AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(() =>
            service.GenerateAsync(profile, new AiGenerationRequest(project.Path, "tree")));

        Assert.Equal(AiGenerationErrorCode.InvalidProviderResponse, error.Code);
    }

    [Fact]
    public async Task GeneratedDirectorySymlink_IsRejectedWithoutWritingOutsideProject()
    {
        if (OperatingSystem.IsWindows()) return;
        using var project = new TemporaryProject();
        string outside = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"agas-ai-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(System.IO.Path.Combine(project.Path, "agas"), outside);
            var adapter = new FakeAdapter("openai-compatible");
            var registry = new AiImageGenerationAdapterRegistry(new[] { adapter });
            using var client = new HttpClient(new RecordingHandler((_, _) => JsonResponse(new { })));
            var service = new AiGenerationService(client, registry);
            var profile = new AiGenerationProviderProfile("gpt", "GPT", "openai-compatible", "gpt-image-2", "secret", "https://relay.example");

            AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(() =>
                service.GenerateAsync(profile, new AiGenerationRequest(project.Path, "tree")));

            Assert.Equal(AiGenerationErrorCode.SaveFailed, error.Code);
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Timeout_IsReportedWithoutLeakingKey()
    {
        using var project = new TemporaryProject();
        const string secret = "timeout-secret";
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        });
        using var client = new HttpClient(handler);
        var service = new AiGenerationService(client, timeout: TimeSpan.FromMilliseconds(30));
        var profile = new AiGenerationProviderProfile("gpt", "GPT", "openai-compatible", "gpt-image-2", secret, "https://relay.example");

        AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(() =>
            service.GenerateAsync(profile, new AiGenerationRequest(project.Path, "tree")));

        Assert.Equal(AiGenerationErrorCode.RequestTimedOut, error.Code);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task CallerCancellation_IsClassifiedSeparatelyFromTimeout()
    {
        using var project = new TemporaryProject();
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        });
        using var client = new HttpClient(handler);
        var service = new AiGenerationService(client, timeout: TimeSpan.FromMinutes(1));
        var profile = new AiGenerationProviderProfile("gpt", "GPT", "openai-compatible", "gpt-image-2", "secret", "https://relay.example");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(() =>
            service.GenerateAsync(profile, new AiGenerationRequest(project.Path, "tree"), cancellation.Token));

        Assert.Equal(AiGenerationErrorCode.RequestCanceled, error.Code);
    }

    [Fact]
    public async Task CallerCancellation_AlsoStopsGeneratedImageDownload()
    {
        using var project = new TemporaryProject();
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(new { data = new[] { new { url = "https://93.184.216.34/generated.png" } } });
            }

            downloadStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        });
        using var client = new HttpClient(handler);
        var service = new AiGenerationService(
            client,
            timeout: TimeSpan.FromMinutes(1),
            imageDownloadClient: client);
        var profile = new AiGenerationProviderProfile(
            "gpt",
            "GPT",
            "openai-compatible",
            "gpt-image-2",
            "secret",
            "https://relay.example/v1");
        using var cancellation = new CancellationTokenSource();

        Task<AiGenerationResult> generation = service.GenerateAsync(
            profile,
            new AiGenerationRequest(project.Path, "download cancellation"),
            cancellation.Token);
        await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(() =>
            generation.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(AiGenerationErrorCode.RequestCanceled, error.Code);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task ServiceTimeout_AlsoStopsGeneratedImageDownload()
    {
        using var project = new TemporaryProject();
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse(new { data = new[] { new { url = "https://93.184.216.34/generated.png" } } });
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        });
        using var client = new HttpClient(handler);
        var service = new AiGenerationService(
            client,
            timeout: TimeSpan.FromMilliseconds(30),
            imageDownloadClient: client);
        var profile = new AiGenerationProviderProfile(
            "gpt",
            "GPT",
            "openai-compatible",
            "gpt-image-2",
            "secret",
            "https://relay.example/v1");

        AiGenerationException error = await Assert.ThrowsAsync<AiGenerationException>(() =>
            service.GenerateAsync(profile, new AiGenerationRequest(project.Path, "download timeout")));

        Assert.Equal(AiGenerationErrorCode.RequestTimedOut, error.Code);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("file:///tmp/provider")]
    [InlineData("https://user:pass@example.com")]
    [InlineData("https://example.com?api_key=secret")]
    [InlineData("http://example.com/v1")]
    public void Profile_RequiresSafeAbsoluteHttpBaseUri(string baseUrl)
    {
        Assert.Throws<ArgumentException>(() =>
            new AiGenerationProviderProfile("id", "name", "openai-compatible", "model", "key", baseUrl));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/v1")]
    [InlineData("http://localhost:8080/v1")]
    [InlineData("https://relay.example/v1")]
    public void Profile_AllowsHttpsAndLoopbackHttp(string baseUrl)
    {
        _ = new AiGenerationProviderProfile("id", "name", "openai-compatible", "model", "key", baseUrl);
    }

    private static HttpResponseMessage JsonResponse(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
    };

    private static byte[] PngBuffer(int length)
    {
        var bytes = new byte[length];
        PngBytes.CopyTo(bytes, 0);
        return bytes;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
            : this((request, token) => Task.FromResult(responder(request, token)))
        {
        }

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public List<RecordedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] bodyBytes = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            string body = Encoding.UTF8.GetString(bodyBytes);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("x-goog-api-key", out IEnumerable<string>? values) ? values.Single() : null,
                string.Join(",", request.Headers.Accept.Select(value => value.MediaType)),
                request.Content?.Headers.ContentType?.ToString(),
                request.Content?.Headers.ContentLength,
                body,
                bodyBytes));
            return await _responder(request, cancellationToken);
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Authorization,
        string? ApiKey,
        string Accept,
        string? ContentType,
        long? ContentLength,
        string Body,
        byte[] BodyBytes);

    private sealed class RecordingDiagnosticSink : IAiGenerationDiagnosticSink
    {
        public List<AiGenerationSafeDiagnostic> Diagnostics { get; } = new();
        public List<AiGenerationSafeRequestDiagnostic> RequestDiagnostics { get; } = new();

        public void TryWrite(AiGenerationSafeDiagnostic diagnostic) => Diagnostics.Add(diagnostic);

        public void TryWriteRequest(AiGenerationSafeRequestDiagnostic diagnostic) => RequestDiagnostics.Add(diagnostic);
    }

    private sealed class ThrowingRequestDiagnosticSink : IAiGenerationDiagnosticSink
    {
        public void TryWrite(AiGenerationSafeDiagnostic diagnostic)
        {
        }

        public void TryWriteRequest(AiGenerationSafeRequestDiagnostic diagnostic) =>
            throw new InvalidOperationException("diagnostic sink failed");
    }

    private sealed class FakeAdapter : IAiImageGenerationAdapter
    {
        public FakeAdapter(string adapterKind) => AdapterKind = adapterKind;

        public string AdapterKind { get; }
        public AiGenerationAdapterCapabilities Capabilities =>
            AiGenerationAdapterCapabilities.TextToImage
            | AiGenerationAdapterCapabilities.ReferenceEdit
            | AiGenerationAdapterCapabilities.RegionEdit;
        public int Calls { get; private set; }

        public Task<GeneratedImageData> GenerateAsync(
            AiGenerationProviderProfile profile,
            AiGenerationRequest request,
            HttpClient providerHttpClient,
            HttpClient imageDownloadClient,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new GeneratedImageData(PngBytes, "image/png"));
        }
    }

    private sealed class LegacyAdapter : IAiImageGenerationAdapter
    {
        public string AdapterKind => "openai-compatible";
        public int Calls { get; private set; }

        public Task<GeneratedImageData> GenerateAsync(
            AiGenerationProviderProfile profile,
            AiGenerationRequest request,
            HttpClient providerHttpClient,
            HttpClient imageDownloadClient,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new GeneratedImageData(PngBytes, "image/png"));
        }
    }

    private sealed class TemporaryProject : IDisposable
    {
        public TemporaryProject()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"agas-ai-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup should not hide the assertion result.
            }
        }
    }

    private static void CreateDirectorySymlinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            throw SkipException.ForSkip($"Directory symlink creation is unavailable: {exception.Message}");
        }
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0) return true;
        for (int offset = 0; offset <= haystack.Length - needle.Length; offset++)
        {
            if (haystack.AsSpan(offset, needle.Length).SequenceEqual(needle)) return true;
        }
        return false;
    }

    private static byte[] PngRgba(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        WritePngChunk(output, "IHDR", ihdr);
        using var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < width; x++)
            {
                (byte r, byte g, byte b, byte a) = pixel(x, y);
                raw.WriteByte(r); raw.WriteByte(g); raw.WriteByte(b); raw.WriteByte(a);
            }
        }
        raw.Position = 0;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) raw.CopyTo(zlib);
        WritePngChunk(output, "IDAT", compressed.ToArray());
        WritePngChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void WritePngChunk(Stream output, string type, byte[] data)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        byte[] length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        output.Write(length);
        output.Write(typeBytes);
        output.Write(data);
        byte[] crcInput = typeBytes.Concat(data).ToArray();
        uint crc = 0xffffffff;
        foreach (byte value in crcInput)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
        }
        crc ^= 0xffffffff;
        byte[] crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, unchecked((int)crc));
        output.Write(crcBytes);
    }

    private static void WriteBigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }
}
