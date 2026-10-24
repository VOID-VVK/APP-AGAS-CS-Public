using System;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AppAgasCs.Features.Ai.Generation;

internal static class NewApiChatImageResponse
{
    private static readonly Regex MarkdownImage = new(
        @"!\[[^\]\r\n]*\]\(\s*<?(?<url>data:image/(?:png|jpeg|webp);base64,[A-Za-z0-9+/=\r\n]+|https?://[^\s)<>]+)>?\s*\)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(2));

    public static string FindImage(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("choices", out JsonElement choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement choice in choices.EnumerateArray())
            {
                if (choice.ValueKind != JsonValueKind.Object
                    || !choice.TryGetProperty("message", out JsonElement message) || message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("content", out JsonElement content)) continue;
                string? result = ReadContent(content);
                if (result != null) return result;
            }
        }
        throw new AiGenerationException(AiGenerationErrorCode.InvalidProviderResponse,
            "New API 图片接口没有返回有效图片，请检查模型渠道和接口配置。");
    }

    private static string? ReadContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return ReadText(content.GetString());
        if (content.ValueKind != JsonValueKind.Array) return null;
        foreach (JsonElement part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) continue;
            if (part.TryGetProperty("image_url", out JsonElement image))
            {
                string? url = image.ValueKind == JsonValueKind.String ? image.GetString()
                    : image.ValueKind == JsonValueKind.Object && image.TryGetProperty("url", out JsonElement value)
                      && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (url != null && IsImageLocation(url)) return url;
            }
            if (part.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
            {
                string? result = ReadText(text.GetString());
                if (result != null) return result;
            }
        }
        return null;
    }

    private static string? ReadText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string value = text.Trim();
        if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return value;
        try
        {
            Match match = MarkdownImage.Match(value);
            return match.Success ? match.Groups["url"].Value : null;
        }
        catch (RegexMatchTimeoutException)
        {
            throw new AiGenerationException(AiGenerationErrorCode.InvalidProviderResponse, "图片响应格式无法安全解析。");
        }
    }

    private static bool IsImageLocation(string value) =>
        value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
}
