using System;

namespace AppAgasCs.Features.Ai.Settings;

internal static class AgasAiCredentialOrigin
{
    internal static bool IsSame(string left, string right) =>
        Uri.TryCreate(left, UriKind.Absolute, out var a)
        && Uri.TryCreate(right, UriKind.Absolute, out var b)
        && string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;
}
