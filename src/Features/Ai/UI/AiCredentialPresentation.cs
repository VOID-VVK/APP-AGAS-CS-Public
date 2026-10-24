#nullable enable

using AppAgasCs.Features.Ai.Settings;

namespace AppAgasCs.Features.Ai.UI;

/// <summary>Config-driven, secret-free credential copy shared by settings UI states.</summary>
internal readonly record struct AiCredentialPresentation(
    string Label,
    string Placeholder,
    string Hint,
    string ClearButtonText,
    string ConfirmationTitle,
    string ConfirmationText)
{
    internal static AiCredentialPresentation Create(
        AgasAiProviderDescriptor? provider,
        bool hasCredential,
        string credentialMask)
    {
        string configured = provider?.Credential?.Label?.Trim() ?? string.Empty;
        string label = string.IsNullOrWhiteSpace(configured) ? "API Key" : configured;
        return new AiCredentialPresentation(
            label,
            hasCredential
                ? $"输入新的 {label} 以覆盖；留空保持不变"
                : $"输入 {label}",
            hasCredential ? $"已保存：{credentialMask}" : $"未保存 {label}。",
            $"清除 {label}",
            $"清除 {label}",
            $"确认清除所选连接已保存的 {label}？");
    }
}
