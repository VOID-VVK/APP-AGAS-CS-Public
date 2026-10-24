using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AppAgasCs.Features.Ai.Generation;

public sealed class OpenAiCompatibleImageAdapter : IAiImageGenerationAdapter
{
    private readonly bool _multipartEdits;

    public OpenAiCompatibleImageAdapter() { }

    internal OpenAiCompatibleImageAdapter(bool multipartEdits) => _multipartEdits = multipartEdits;

    public string AdapterKind => "openai-compatible";

    public AiGenerationAdapterCapabilities Capabilities =>
        AiGenerationAdapterCapabilities.TextToImage
        | AiGenerationAdapterCapabilities.ReferenceEdit
        | AiGenerationAdapterCapabilities.RegionEdit
        | AiGenerationAdapterCapabilities.NativeAlphaMask;

    public async Task<GeneratedImageData> GenerateAsync(
        AiGenerationProviderProfile profile,
        AiGenerationRequest request,
        HttpClient providerHttpClient,
        HttpClient imageDownloadClient,
        CancellationToken cancellationToken)
    {
        bool isTextToImage = request.Operation == AiGenerationOperation.TextToImage;
        bool isGrokImagineImage20 = AiGenerationQualityPolicy.Supports(profile.ModelName);
        if (request.Quality.HasValue && !isGrokImagineImage20)
        {
            throw new AiGenerationException(
                AiGenerationErrorCode.UnsupportedOperation,
                "The selected image model does not support a quality tier.");
        }
        if (isGrokImagineImage20 && !isTextToImage)
        {
            throw new AiGenerationException(
                AiGenerationErrorCode.UnsupportedOperation,
                "Grok Imagine Image 2.0 supports text-to-image generation only.");
        }
        if (request.Operation == AiGenerationOperation.RegionEdit && request.EditInput is null)
        {
            throw new AiGenerationException(AiGenerationErrorCode.InvalidEditInput, "Region edit requires a source canvas and mask.");
        }
        if (request.Operation == AiGenerationOperation.RegionEdit
            && (request.EditInput!.SourceCanvas.SizeBytes >= 50L * 1024 * 1024
                || request.EditInput.Mask.SizeBytes >= 4L * 1024 * 1024))
        {
            throw new AiGenerationException(AiGenerationErrorCode.InvalidEditInput, "The region edit source or mask exceeds the GPT Image upload limit.");
        }

        // gpt-image-* models reject the legacy DALL·E field response_format and only
        // accept size in {1024x1024, 1536x1024, 1024x1536, auto}. Banana's "auto"
        // handling lives only on the Gemini adapter (aspectRatio) and must not leak
        // into this GPT payload.
        bool isGptImageModel = IsGptImageModel(profile.ModelName);
        string normalizedSize = isGptImageModel
            ? NormalizeGptImageSize(request.Width, request.Height)
            : NormalizeLegacySize(request.Width, request.Height);
        bool requestTransparentPng = isGptImageModel;
        using HttpResponseMessage response = await SendGptCompatibleRequestAsync(
            profile,
            request,
            providerHttpClient,
            isTextToImage,
            isGrokImagineImage20,
            isGptImageModel,
            normalizedSize,
            requestTransparentPng,
            cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await AiAdapterHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        (string value, string? mimeType) = FindImage(document.RootElement);
        GeneratedImageData generatedImage = await AiAdapterHttp.ResolveImageAsync(imageDownloadClient, value, mimeType, cancellationToken).ConfigureAwait(false);
        return generatedImage with
        {
            EffectiveOperation = request.Operation switch
            {
                AiGenerationOperation.TextToImage => AiGenerationEffectiveOperation.TextToImage,
                AiGenerationOperation.ReferenceEdit => AiGenerationEffectiveOperation.ReferenceEdit,
                AiGenerationOperation.RegionEdit => AiGenerationEffectiveOperation.MaskedEdit,
                _ => throw new AiGenerationException(AiGenerationErrorCode.UnsupportedOperation, "The requested image operation is unsupported.")
            },
            EditGuarantee = request.Operation == AiGenerationOperation.RegionEdit
                ? AiGenerationEditGuarantee.ProviderMasked
                : AiGenerationEditGuarantee.None
        };
    }

    private async Task<HttpResponseMessage> SendGptCompatibleRequestAsync(
        AiGenerationProviderProfile profile,
        AiGenerationRequest request,
        HttpClient providerHttpClient,
        bool isTextToImage,
        bool isGrokImagineImage20,
        bool isGptImageModel,
        string normalizedSize,
        bool transparentPng,
        CancellationToken cancellationToken)
    {
        using var first = CreateGptCompatibleRequest(
            profile,
            request,
            isTextToImage,
            isGrokImagineImage20,
            isGptImageModel,
            normalizedSize,
            transparentPng);
        HttpResponseMessage response = await AiAdapterHttp.SendAsync(
            providerHttpClient,
            first,
            cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode
            || !transparentPng
            || !isGptImageModel
            || (int)response.StatusCode != 400)
        {
            return response;
        }

        response.Dispose();
        using var retry = CreateGptCompatibleRequest(
            profile,
            request,
            isTextToImage,
            isGrokImagineImage20,
            isGptImageModel,
            normalizedSize,
            transparentPng: false);
        return await AiAdapterHttp.SendAsync(
            providerHttpClient,
            retry,
            cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage CreateGptCompatibleRequest(
        AiGenerationProviderProfile profile,
        AiGenerationRequest request,
        bool isTextToImage,
        bool isGrokImagineImage20,
        bool isGptImageModel,
        string normalizedSize,
        bool transparentPng)
    {
        string endpointSuffix = isTextToImage ? "v1/images/generations" : "v1/images/edits";
        var message = new HttpRequestMessage(HttpMethod.Post, AiAdapterHttp.Endpoint(profile.BaseUri, endpointSuffix));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = profile.ModelName,
            ["prompt"] = request.Prompt,
            ["n"] = 1,
            ["size"] = normalizedSize
        };
        if (isGptImageModel && transparentPng)
        {
            payload["background"] = "transparent";
            payload["output_format"] = "png";
        }
        // Fixed provider defaults are metadata constraints, not Google or
        // legacy DALL-E parameters. The verified compatible route accepts the
        // minimal model/prompt/n body and chooses its registry resolution.
        bool providerDefaults = !isGptImageModel && profile.ModelConstraints is { } constraints
            && (constraints.Ratios.Count > 0 || constraints.Resolutions.Count > 0);
        if (providerDefaults) payload.Remove("size");
        if (!isGptImageModel && !providerDefaults)
        {
            payload["response_format"] = "b64_json";
        }
        if (isGrokImagineImage20)
        {
            payload["quality"] = QualityWireValue(request.Quality ?? AiGenerationQuality.Medium);
        }
        if (!isTextToImage && !_multipartEdits)
        {
            IReadOnlyList<AiGenerationReferenceImage> images = request.Operation == AiGenerationOperation.RegionEdit
                ? new[] { request.EditInput!.SourceCanvas }
                : request.ReferenceImages;
            var editImages = new List<object>(images.Count);
            foreach (AiGenerationReferenceImage image in images)
            {
                editImages.Add(new { image_url = DataUrl(image) });
            }
            payload["images"] = editImages;
            if (request.Operation == AiGenerationOperation.RegionEdit)
            {
                payload["mask"] = new { image_url = DataUrl(request.EditInput!.Mask) };
            }
        }
        if (!isTextToImage && _multipartEdits)
        {
            message.Content = NewApiImageEditContent.Create(
                profile.ModelName,
                request,
                normalizedSize,
                transparentBackground: isGptImageModel && transparentPng);
        }
        else
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload);
            message.Content = new ByteArrayContent(body);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        return message;
    }

    private static (string Value, string? MimeType) FindImage(JsonElement root)
    {
        JsonElement item = root;
        if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
        {
            item = data[0];
        }

        foreach (string propertyName in new[] { "b64_json", "b64Json", "image_base64", "base64" })
        {
            if (item.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String)
            {
                return (value.GetString() ?? string.Empty, ReadMimeType(item) ?? "image/png");
            }
        }

        foreach (string propertyName in new[] { "url", "image_url", "imageUrl" })
        {
            if (item.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String)
            {
                return (value.GetString() ?? string.Empty, ReadMimeType(item));
            }
        }

        throw new AiGenerationException(AiGenerationErrorCode.InvalidProviderResponse, "The image provider response did not contain an image.");
    }

    private static string? ReadMimeType(JsonElement item)
    {
        foreach (string name in new[] { "mime_type", "mimeType", "content_type", "contentType" })
        {
            if (item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    internal static bool IsGptImageModel(string modelName) =>
        !string.IsNullOrWhiteSpace(modelName)
        && modelName.Contains("gpt-image", StringComparison.OrdinalIgnoreCase);

    internal static string QualityWireValue(AiGenerationQuality quality) => quality switch
    {
        AiGenerationQuality.Low => "low",
        AiGenerationQuality.Medium => "medium",
        _ => throw new AiGenerationException(AiGenerationErrorCode.UnsupportedOperation, "Unsupported image quality tier.")
    };

    /// <summary>
    /// Maps canvas Q dimensions onto the discrete gpt-image size enum. Non-matching
    /// aspect ratios use auto so the provider can choose; local apply still resizes
    /// the returned PNG back to the frozen Q region.
    /// </summary>
    internal static string NormalizeGptImageSize(int width, int height)
    {
        int safeWidth = Math.Max(1, width);
        int safeHeight = Math.Max(1, height);
        if (safeWidth == safeHeight)
        {
            return "1024x1024";
        }

        double aspect = (double)safeWidth / safeHeight;
        const double landscape = 1536d / 1024d;
        const double portrait = 1024d / 1536d;
        const double tolerance = 0.08d;
        if (Math.Abs(aspect - landscape) <= tolerance)
        {
            return "1536x1024";
        }
        if (Math.Abs(aspect - portrait) <= tolerance)
        {
            return "1024x1536";
        }
        // Prefer auto for arbitrary Q boxes (e.g. 42×25 tiles) instead of forcing a
        // wrong square/landscape size that some relays reject with HTTP 400.
        return "auto";
    }

    /// <summary>Legacy DALL·E-style size string with independent per-axis caps.</summary>
    internal static string NormalizeLegacySize(int width, int height)
    {
        int maximum = MaximumOpenAiImageSize();
        return $"{Math.Min(Math.Max(1, width), maximum)}x{Math.Min(Math.Max(1, height), maximum)}";
    }

    // Back-compat for existing call sites/tests that still use the old name.
    internal static string NormalizeSize(string modelName, int width, int height) =>
        IsGptImageModel(modelName)
            ? NormalizeGptImageSize(width, height)
            : NormalizeLegacySize(width, height);

    private static int MaximumOpenAiImageSize()
    {
        string? configured = Environment.GetEnvironmentVariable("GPT_IMAGE_MAX_SIZE");
        if (!double.TryParse(
                configured,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double numeric)
            || !double.IsFinite(numeric))
        {
            return 1024;
        }

        double rounded = Math.Floor(numeric + 0.5d);
        return (int)Math.Max(256d, Math.Min(8192d, rounded));
    }

    private static string DataUrl(AiGenerationReferenceImage image) =>
        $"data:{image.MimeType};base64,{Convert.ToBase64String(image.Bytes)}";
}
