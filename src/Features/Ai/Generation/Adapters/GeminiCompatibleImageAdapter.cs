using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Features.Ai.Prompting;

namespace AppAgasCs.Features.Ai.Generation;

public sealed class GeminiCompatibleImageAdapter : IAiImageGenerationAdapter
{
    private static readonly (int Width, int Height, string Value)[] StandardAspectRatios =
    {
        (9, 16, "9:16"),
        (2, 3, "2:3"),
        (3, 4, "3:4"),
        (4, 5, "4:5"),
        (1, 1, "1:1"),
        (5, 4, "5:4"),
        (4, 3, "4:3"),
        (3, 2, "3:2"),
        (16, 9, "16:9"),
        (21, 9, "21:9")
    };

    private static readonly (int Width, int Height, string Value)[] AntigravityAspectRatios =
    {
        (1, 8, "1:8"),
        (1, 4, "1:4"),
        (9, 16, "9:16"),
        (2, 3, "2:3"),
        (3, 4, "3:4"),
        (4, 5, "4:5"),
        (1, 1, "1:1"),
        (5, 4, "5:4"),
        (4, 3, "4:3"),
        (3, 2, "3:2"),
        (16, 9, "16:9"),
        (21, 9, "21:9"),
        (4, 1, "4:1"),
        (8, 1, "8:1")
    };

    public string AdapterKind => "gemini-compatible";

    public AiGenerationAdapterCapabilities Capabilities =>
        AiGenerationAdapterCapabilities.TextToImage
        | AiGenerationAdapterCapabilities.ReferenceEdit
        | AiGenerationAdapterCapabilities.RegionEdit
        | AiGenerationAdapterCapabilities.GuidedRegionEdit;

    public async Task<GeneratedImageData> GenerateAsync(
        AiGenerationProviderProfile profile,
        AiGenerationRequest request,
        HttpClient providerHttpClient,
        HttpClient imageDownloadClient,
        CancellationToken cancellationToken)
    {
        Uri endpoint = AtStage(
            AiGenerationAdapterStage.EndpointUriCreation,
            () => AiGenerationEndpointPolicy.ResolveNanoBananaEndpoint(profile.BaseUri, profile.ModelName));
        HttpClient httpClient = AtStage(
            AiGenerationAdapterStage.HttpClientAccess,
            () => providerHttpClient ?? throw new InvalidOperationException("Provider HTTP client is unavailable."));
        using HttpRequestMessage message = AtStage(AiGenerationAdapterStage.RequestConstruction, () =>
        {
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint);
            try
            {
                if (AiGenerationEndpointPolicy.UsesStreamingEndpoint(profile.BaseUri))
                {
                    requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                }
                if (AiGenerationEndpointPolicy.UsesGoogleApiKeyHeader(profile.BaseUri))
                {
                    requestMessage.Headers.TryAddWithoutValidation("x-goog-api-key", profile.ApiKey);
                }
                else
                {
                    try
                    {
                        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);
                    }
                    catch (FormatException)
                    {
                        throw new AiGenerationException(
                            AiGenerationErrorCode.InvalidConfiguration,
                            "API Key 包含无法用于 Bearer 认证头的字符，请重新检查中转站凭据。");
                    }
                }
                return requestMessage;
            }
            catch
            {
                requestMessage.Dispose();
                throw;
            }
        });

        object payload = AtStage(AiGenerationAdapterStage.PayloadConstruction, () =>
        {
            if (request.Operation == AiGenerationOperation.RegionEdit && request.EditInput is null)
            {
                throw new AiGenerationException(AiGenerationErrorCode.InvalidEditInput, "Guided region edit requires a transparent-hole source canvas.");
            }
            string effectivePrompt = request.Operation == AiGenerationOperation.RegionEdit
                ? BuildGuidedRegionPrompt(request.Prompt, request.EditInput!)
                : request.Prompt;
            var parts = new List<object> { new { text = effectivePrompt } };
            IEnumerable<AiGenerationReferenceImage> inputImages = request.Operation == AiGenerationOperation.RegionEdit
                ? new[] { request.EditInput!.SourceCanvas }.Concat(request.ReferenceImages)
                : request.ReferenceImages;
            foreach (AiGenerationReferenceImage image in inputImages)
            {
                parts.Add(new
                {
                    inlineData = new
                    {
                        mimeType = GeneratedImageData.NormalizeMimeType(image.MimeType),
                        data = Convert.ToBase64String(image.Bytes)
                    }
                });
            }

            return new
            {
                contents = new[] { new { role = "user", parts } },
                generationConfig = new
                {
                    responseModalities = new[] { "Image" },
                    imageConfig = new
                    {
                        aspectRatio = ClosestSupportedAspectRatio(profile.BaseUri, request.Width, request.Height),
                        imageSize = ImageSize(request.Width, request.Height)
                    }
                }
            };
        });

        message.Content = AtStage(
            AiGenerationAdapterStage.JsonSerialization,
            () => new StagedJsonContent(payload));

        using HttpResponseMessage response = await AtStageAsync(
            AiGenerationAdapterStage.RequestSend,
            () => AiAdapterHttp.SendAsync(httpClient, message, cancellationToken)).ConfigureAwait(false);
        ProviderImageReference? imageReference = await AtStageAsync(
            AiGenerationAdapterStage.ResponseRead,
            async () =>
            {
                if (AiAdapterHttp.IsEventStream(response))
                {
                    return await AiAdapterHttp.ReadSseJsonUntilAsync(
                        response,
                        TryFindImage,
                        cancellationToken).ConfigureAwait(false);
                }

                using JsonDocument document = await AiAdapterHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
                return TryFindImage(document.RootElement);
            }).ConfigureAwait(false);
        if (imageReference == null)
        {
            throw new AiGenerationException(AiGenerationErrorCode.InvalidProviderResponse, "The image provider response did not contain an image.");
        }
        GeneratedImageData generatedImage = await AiAdapterHttp.ResolveImageAsync(
            imageDownloadClient,
            imageReference.Value,
            imageReference.MimeType,
            cancellationToken).ConfigureAwait(false);
        return generatedImage with
        {
            EffectiveOperation = request.Operation switch
            {
                AiGenerationOperation.TextToImage => AiGenerationEffectiveOperation.TextToImage,
                AiGenerationOperation.ReferenceEdit => AiGenerationEffectiveOperation.ReferenceEdit,
                AiGenerationOperation.RegionEdit => AiGenerationEffectiveOperation.GuidedRegionEdit,
                _ => throw new AiGenerationException(AiGenerationErrorCode.UnsupportedOperation, "The requested image operation is unsupported.")
            },
            EditGuarantee = request.Operation == AiGenerationOperation.RegionEdit
                ? AiGenerationEditGuarantee.GuidedReference
                : AiGenerationEditGuarantee.None
        };
    }

    private static string BuildGuidedRegionPrompt(string prompt, AiGenerationEditInput editInput)
    {
        string technicalDetails = $"""
            [Technical details]
            Reference canvas: {editInput.SourceCanvas.PixelWidth}x{editInput.SourceCanvas.PixelHeight} pixels.
            Transparent pixels in the attached reference image are editable; non-transparent pixels are protected.
            """.Trim();
        string canonicalPrompt = PromptComposer.EnsureCanonicalTask(prompt, PromptCompositionMode.ReferenceEdit);
        return PromptComposer.InsertTechnicalBeforeTask(canonicalPrompt, technicalDetails);
    }

    private static ProviderImageReference? TryFindImage(JsonElement root)
    {
        if (root.TryGetProperty("response", out JsonElement wrapped) && wrapped.ValueKind == JsonValueKind.Object)
        {
            ProviderImageReference? wrappedResult = TryFindImage(wrapped);
            if (wrappedResult != null)
            {
                return wrappedResult;
            }
        }
        if (root.TryGetProperty("candidates", out JsonElement candidates) && candidates.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement candidate in candidates.EnumerateArray())
            {
                if (!candidate.TryGetProperty("content", out JsonElement content)
                    || !content.TryGetProperty("parts", out JsonElement parts)
                    || parts.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement part in parts.EnumerateArray())
                {
                    if (TryInlineData(part, "inlineData", out (string Value, string MimeType) result)
                        || TryInlineData(part, "inline_data", out result))
                    {
                        return new ProviderImageReference(result.Value, result.MimeType);
                    }

                    // Visionary Gemini 兼容返回 fileData.fileUri（HTTPS 图链）而非 inlineData base64。
                    if (TryFileUri(part, "fileData", out result)
                        || TryFileUri(part, "file_data", out result))
                    {
                        return new ProviderImageReference(result.Value, result.MimeType);
                    }
                }
            }
        }

        if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
        {
            JsonElement item = data[0];
            if (item.TryGetProperty("b64_json", out JsonElement base64) && base64.ValueKind == JsonValueKind.String)
            {
                return new ProviderImageReference(base64.GetString() ?? string.Empty, "image/png");
            }

            if (item.TryGetProperty("url", out JsonElement url) && url.ValueKind == JsonValueKind.String)
            {
                return new ProviderImageReference(url.GetString() ?? string.Empty, "image/png");
            }
        }

        return null;
    }

    private static bool TryInlineData(JsonElement part, string propertyName, out (string Value, string MimeType) result)
    {
        result = default;
        if (!part.TryGetProperty(propertyName, out JsonElement inlineData)
            || !inlineData.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string mimeType = "image/png";
        if (inlineData.TryGetProperty("mimeType", out JsonElement camelMime) && camelMime.ValueKind == JsonValueKind.String)
        {
            mimeType = camelMime.GetString() ?? mimeType;
        }
        else if (inlineData.TryGetProperty("mime_type", out JsonElement snakeMime) && snakeMime.ValueKind == JsonValueKind.String)
        {
            mimeType = snakeMime.GetString() ?? mimeType;
        }

        result = (data.GetString() ?? string.Empty, mimeType);
        return true;
    }

    private static bool TryFileUri(JsonElement part, string propertyName, out (string Value, string MimeType) result)
    {
        result = default;
        if (!part.TryGetProperty(propertyName, out JsonElement fileData)
            || fileData.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? uri = null;
        if (fileData.TryGetProperty("fileUri", out JsonElement camelUri) && camelUri.ValueKind == JsonValueKind.String)
        {
            uri = camelUri.GetString();
        }
        else if (fileData.TryGetProperty("file_uri", out JsonElement snakeUri) && snakeUri.ValueKind == JsonValueKind.String)
        {
            uri = snakeUri.GetString();
        }

        if (string.IsNullOrWhiteSpace(uri)
            || !Uri.TryCreate(uri, UriKind.Absolute, out Uri? absolute)
            || (absolute.Scheme != Uri.UriSchemeHttps && absolute.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        string mimeType = "image/png";
        if (fileData.TryGetProperty("mimeType", out JsonElement camelMime) && camelMime.ValueKind == JsonValueKind.String)
        {
            mimeType = camelMime.GetString() ?? mimeType;
        }
        else if (fileData.TryGetProperty("mime_type", out JsonElement snakeMime) && snakeMime.ValueKind == JsonValueKind.String)
        {
            mimeType = snakeMime.GetString() ?? mimeType;
        }

        result = (uri!, mimeType);
        return true;
    }

    private static string ImageSize(int width, int height)
    {
        int longest = Math.Max(width, height);
        return longest > 2048 ? "4K" : longest > 1024 ? "2K" : "1K";
    }

    private static string ClosestSupportedAspectRatio(Uri baseUri, int width, int height)
    {
        (int Width, int Height, string Value)[] supportedRatios =
            string.Equals(baseUri.AbsolutePath.Trim('/'), "antigravity/v1beta", StringComparison.OrdinalIgnoreCase)
                ? AntigravityAspectRatios
                : StandardAspectRatios;
        (int Width, int Height, string Value) closest = supportedRatios[0];
        double closestDistance = double.MaxValue;
        foreach ((int ratioWidth, int ratioHeight, string value) in supportedRatios)
        {
            double distance = Math.Abs(Math.Log(
                (double)width * ratioHeight / ((double)height * ratioWidth)));
            if (distance < closestDistance)
            {
                closest = (ratioWidth, ratioHeight, value);
                closestDistance = distance;
            }
        }

        return closest.Value;
    }

    private static T AtStage<T>(AiGenerationAdapterStage stage, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw AiGenerationDiagnostics.AtAdapterStage(exception, stage);
        }
    }

    private static async Task<T> AtStageAsync<T>(AiGenerationAdapterStage stage, Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw AiGenerationDiagnostics.AtAdapterStage(exception, stage);
        }
    }

    internal sealed class StagedJsonContent : HttpContent
    {
        private readonly object _payload;

        public StagedJsonContent(object payload)
        {
            _payload = payload ?? throw new ArgumentNullException(nameof(payload));
            Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeAsync(stream, CancellationToken.None);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) => SerializeAsync(stream, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private async Task SerializeAsync(Stream stream, CancellationToken cancellationToken)
        {
            try
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    _payload,
                    _payload.GetType(),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw AiGenerationDiagnostics.AtAdapterStage(
                    exception,
                    AiGenerationAdapterStage.JsonSerialization);
            }
        }
    }

    private sealed record ProviderImageReference(string Value, string MimeType);
}
