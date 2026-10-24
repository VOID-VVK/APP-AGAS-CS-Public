using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Features.Ai.Prompting;

namespace AppAgasCs.Features.Ai.Generation;

/// <summary>The gateway's gemini-image route uses non-streaming OpenAI Chat content.</summary>
internal sealed class NewApiChatImageAdapter : IAiImageGenerationAdapter
{
    public string AdapterKind => "newapi-chat-image";
    public AiGenerationAdapterCapabilities Capabilities =>
        AiGenerationAdapterCapabilities.TextToImage | AiGenerationAdapterCapabilities.ReferenceEdit
        | AiGenerationAdapterCapabilities.RegionEdit | AiGenerationAdapterCapabilities.GuidedRegionEdit;

    public async Task<GeneratedImageData> GenerateAsync(
        AiGenerationProviderProfile profile,
        AiGenerationRequest request,
        HttpClient providerHttpClient,
        HttpClient imageDownloadClient,
        CancellationToken cancellationToken)
    {
        bool regionEdit = request.Operation == AiGenerationOperation.RegionEdit;
        if (regionEdit && request.EditInput is null)
            throw new AiGenerationException(AiGenerationErrorCode.InvalidEditInput, "Guided region edit requires a source canvas.");
        int divisor = GreatestCommonDivisor(request.Width, request.Height);
        // This Chat route has no verified imageConfig contract. Carry canvas preferences
        // in the prompt; the workspace still fits the returned image to its target area.
        string technical = $"[Technical details]\nRequested output canvas: {request.Width}x{request.Height} pixels; aspect ratio {request.Width / divisor}:{request.Height / divisor}. Match this preference where supported.";
        if (regionEdit)
        {
            technical += "\nTransparent pixels in the first reference image are editable; preserve the other pixels.";
        }
        string prompt = PromptComposer.InsertTechnicalBeforeTask(
            PromptComposer.EnsureCanonicalTask(request.Prompt, request.Operation == AiGenerationOperation.TextToImage
                ? PromptCompositionMode.TextToImage : PromptCompositionMode.ReferenceEdit), technical);
        var content = new List<object> { new { type = "text", text = prompt } };
        IEnumerable<AiGenerationReferenceImage> references = regionEdit
            ? new[] { request.EditInput!.SourceCanvas }.Concat(request.ReferenceImages)
            : request.ReferenceImages;
        foreach (AiGenerationReferenceImage reference in references)
            content.Add(new { type = "image_url", image_url = new { url = AiAdapterHttp.DataUrl(reference) } });
        using var message = new HttpRequestMessage(HttpMethod.Post, AiAdapterHttp.Endpoint(profile.BaseUri, "v1/chat/completions"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);
        message.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = profile.ModelName,
            messages = new[] { new { role = "user", content } },
            stream = false
        }));
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using HttpResponseMessage response = await AiAdapterHttp.SendAsync(providerHttpClient, message, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await AiAdapterHttp.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        string image = NewApiChatImageResponse.FindImage(document.RootElement);
        GeneratedImageData result = await AiAdapterHttp.ResolveImageAsync(imageDownloadClient, image, null, cancellationToken).ConfigureAwait(false);
        return result with
        {
            EffectiveOperation = request.Operation switch
            {
                AiGenerationOperation.TextToImage => AiGenerationEffectiveOperation.TextToImage,
                AiGenerationOperation.ReferenceEdit => AiGenerationEffectiveOperation.ReferenceEdit,
                AiGenerationOperation.RegionEdit => AiGenerationEffectiveOperation.GuidedRegionEdit,
                _ => throw new AiGenerationException(AiGenerationErrorCode.UnsupportedOperation, "The requested image operation is unsupported.")
            },
            EditGuarantee = regionEdit ? AiGenerationEditGuarantee.GuidedReference : AiGenerationEditGuarantee.None
        };
    }

    private static int GreatestCommonDivisor(int width, int height)
    {
        while (height != 0) (width, height) = (height, width % height);
        return width;
    }
}
