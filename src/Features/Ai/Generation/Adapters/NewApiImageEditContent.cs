using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;

namespace AppAgasCs.Features.Ai.Generation;

internal static class NewApiImageEditContent
{
    public static MultipartFormDataContent Create(
        string model,
        AiGenerationRequest request,
        string size,
        bool transparentBackground = false)
    {
        var content = new MultipartFormDataContent();
        try
        {
            content.Add(new StringContent(model), "model");
            content.Add(new StringContent(request.Prompt), "prompt");
            content.Add(new StringContent("1"), "n");
            content.Add(new StringContent(size), "size");
            if (transparentBackground)
            {
                content.Add(new StringContent("transparent"), "background");
                content.Add(new StringContent("png"), "output_format");
            }
            IReadOnlyList<AiGenerationReferenceImage> images = request.Operation == AiGenerationOperation.RegionEdit
                ? new[] { request.EditInput!.SourceCanvas } : request.ReferenceImages;
            for (int index = 0; index < images.Count; index++)
                AddImage(content, images[index], "image[]", $"reference-{index}");
            if (request.Operation == AiGenerationOperation.RegionEdit)
                AddImage(content, request.EditInput!.Mask, "mask", "mask");
            return content;
        }
        catch
        {
            content.Dispose();
            throw;
        }
    }

    private static void AddImage(MultipartFormDataContent content, AiGenerationReferenceImage image, string field, string filename)
    {
        if (image.SizeBytes >= 50L * 1024 * 1024)
            throw new AiGenerationException(AiGenerationErrorCode.InvalidEditInput, "The reference image exceeds the GPT Image upload limit.");
        var part = new ByteArrayContent(image.Bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(image.MimeType);
        string extension = image.MimeType switch { "image/jpeg" => ".jpg", "image/webp" => ".webp", _ => ".png" };
        content.Add(part, field, filename + extension);
    }
}
