using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AppAgasCs.Features.Ai.Settings;

public sealed record AgasMcpAiExecutionIdentity(
    string ProfileId,
    string ProviderId,
    string AdapterKind,
    string ModelName);

/// <summary>Stateless, credential-aware projection for AI-facing safe values.</summary>
public static class AgasMcpAiSafeProjector
{
    private static readonly Regex SafeId = new("^[A-Za-z0-9_-]{1,120}$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeProviderId = new("^[a-z0-9][a-z0-9._-]{0,79}$", RegexOptions.CultureInvariant);

    public static string SafeIdentifier(string? value, IEnumerable<string> credentials)
    {
        string candidate = value?.Trim() ?? string.Empty;
        return candidate.Length > 0
            && SafeId.IsMatch(candidate)
            && !ContainsCredential(candidate, credentials)
                ? candidate
                : "redacted";
    }

    public static string SafeText(string? value, IEnumerable<string> credentials)
    {
        string candidate = value?.Trim() ?? string.Empty;
        return ContainsCredential(candidate, credentials) ? "[redacted]" : candidate;
    }

    public static string SafeProviderIdentifier(string? value, IEnumerable<string> credentials)
    {
        string candidate = value?.Trim() ?? string.Empty;
        return SafeProviderId.IsMatch(candidate)
            && !ContainsCredential(candidate, credentials)
                ? candidate
                : "redacted";
    }

    public static AgasMcpAiExecutionIdentity ProjectExecutionIdentity(
        string? profileId,
        string? providerId,
        string? adapterKind,
        string? modelName,
        IEnumerable<string> credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        string[] currentCredentials = credentials
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new AgasMcpAiExecutionIdentity(
            SafeIdentifier(profileId, currentCredentials),
            SafeProviderIdentifier(providerId, currentCredentials),
            SafeText(adapterKind, currentCredentials),
            SafeText(modelName, currentCredentials));
    }

    public static bool ContainsCredential(string? value, IEnumerable<string> credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (string credential in credentials)
        {
            if (!string.IsNullOrEmpty(credential)
                && value.Contains(credential, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
