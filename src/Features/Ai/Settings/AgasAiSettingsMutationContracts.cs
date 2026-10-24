using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AppAgasCs.Features.Ai.Settings;

public readonly record struct AgasAiSettingsDraftState(
    bool Name,
    bool Provider,
    bool Model,
    bool BaseUrl,
    bool Credential)
{
    public bool HasUnsavedChanges => Name || Provider || Model || BaseUrl || Credential;
}

public sealed record AgasAiSettingsMutationLease(
    string LeaseId,
    long Generation,
    string WorkspaceId,
    string? ActiveProfileId);

public sealed record AgasAiSettingsFileFingerprint(
    bool Exists,
    long Length,
    long LastWriteUtcTicks,
    long CreationUtcTicks,
    string Attributes,
    string LinkTarget,
    string Sha256);

public sealed record AgasAiSettingsPersistedFingerprint(
    AgasAiSettingsFileFingerprint File,
    string DocumentSha256);

/// <summary>
/// UI-owned lease port. Wave 3 supplies the main-thread adapter; the settings
/// service only depends on this engine-neutral contract.
/// </summary>
public interface IAgasAiSettingsMutationLeaseBridge
{
    AgasAiSettingsDraftState DraftState { get; }

    bool TryAcquire(out AgasAiSettingsMutationLease? lease);

    bool ValidateBeforeCommit(
        AgasAiSettingsMutationLease lease,
        AgasAiSettingsPersistedFingerprint persistedFingerprint);

    bool SynchronizeAfterCommit(
        AgasAiSettingsMutationLease lease,
        AgasAiProfilesDocument committedDocument);

    void Release(AgasAiSettingsMutationLease lease);
}

public interface IAgasAiSettingsCommitGate
{
    bool TryBeginAtomicReplace();
}

public sealed class AgasAllowAiSettingsCommitGate : IAgasAiSettingsCommitGate
{
    public bool TryBeginAtomicReplace() => true;
}

public enum AgasAiSettingsMutationFailure
{
    None,
    Conflict,
    ProfileNotFound,
    ProviderUnavailable,
    ModelInvalid,
    StorageFailure,
    Canceled,
}

public sealed record AgasAiSettingsMutationResult
{
    public AgasAiSettingsMutationResult(
        bool success,
        AgasAiSettingsMutationFailure failure,
        string profileId,
        string providerId,
        string modelName,
        bool credentialConfigured,
        bool persisted,
        bool uiSynchronized,
        bool knownPreset = false)
    {
        Success = success;
        Failure = failure;
        ProfileId = profileId ?? string.Empty;
        ProviderId = providerId ?? string.Empty;
        ModelName = modelName ?? string.Empty;
        CredentialConfigured = credentialConfigured;
        Persisted = persisted;
        UiSynchronized = uiSynchronized;
        KnownPreset = knownPreset;
    }

    public bool Success { get; }
    public AgasAiSettingsMutationFailure Failure { get; }
    public string ProfileId { get; }
    public string ProviderId { get; }
    public string ModelName { get; }
    public bool CredentialConfigured { get; }
    public bool Persisted { get; }
    public bool UiSynchronized { get; }
    public bool KnownPreset { get; }

    public static AgasAiSettingsMutationResult Fail(AgasAiSettingsMutationFailure failure) =>
        new(false, failure, string.Empty, string.Empty, string.Empty, false, false, false);
}

public sealed record AgasAiSafeProfileDescriptor
{
    public AgasAiSafeProfileDescriptor(
        string profileId,
        string name,
        string providerId,
        string providerDisplayName,
        string modelName,
        bool credentialConfigured,
        bool providerAvailable,
        IReadOnlyList<string> capabilities)
    {
        ProfileId = profileId;
        Name = name;
        ProviderId = providerId;
        ProviderDisplayName = providerDisplayName;
        ModelName = modelName;
        CredentialConfigured = credentialConfigured;
        ProviderAvailable = providerAvailable;
        Capabilities = new ReadOnlyCollection<string>((capabilities ?? Array.Empty<string>()).ToArray());
    }

    public string ProfileId { get; }
    public string Name { get; }
    public string ProviderId { get; }
    public string ProviderDisplayName { get; }
    public string ModelName { get; }
    public bool CredentialConfigured { get; }
    public bool ProviderAvailable { get; }
    public IReadOnlyList<string> Capabilities { get; }
}

public sealed record AgasAiSafeProviderDescriptor
{
    public AgasAiSafeProviderDescriptor(
        string providerId,
        string displayName,
        string adapterKind,
        string defaultModelName,
        IReadOnlyList<string> capabilities,
        bool supportsCustomModel,
        IReadOnlyList<AgasAiModelPreset> models)
    {
        ProviderId = providerId;
        DisplayName = displayName;
        AdapterKind = adapterKind;
        DefaultModelName = defaultModelName;
        Capabilities = new ReadOnlyCollection<string>((capabilities ?? Array.Empty<string>()).ToArray());
        SupportsCustomModel = supportsCustomModel;
        Models = new ReadOnlyCollection<AgasAiModelPreset>((models ?? Array.Empty<AgasAiModelPreset>()).ToArray());
    }

    public string ProviderId { get; }
    public string DisplayName { get; }
    public string AdapterKind { get; }
    public string DefaultModelName { get; }
    public IReadOnlyList<string> Capabilities { get; }
    public bool SupportsCustomModel { get; }
    public IReadOnlyList<AgasAiModelPreset> Models { get; }
}

public sealed record AgasAiEffectiveProfileSelection(
    string ProfileId,
    string ProviderId,
    string AdapterKind,
    string ModelName);

public sealed record AgasAiSafeEffectiveProfileDescriptor(
    string ProfileId,
    string ProviderId,
    string AdapterKind,
    string ModelName,
    string Source);

public sealed record AgasAiProfilesQueryResult
{
    public AgasAiProfilesQueryResult(
        IReadOnlyList<AgasAiSafeProfileDescriptor> profiles,
        int providerConfigurationErrorCount,
        string? activeProfileId,
        AgasAiSafeEffectiveProfileDescriptor? effectiveCurrent)
    {
        Profiles = new ReadOnlyCollection<AgasAiSafeProfileDescriptor>((profiles ?? Array.Empty<AgasAiSafeProfileDescriptor>()).ToArray());
        ProviderConfigurationErrorCount = providerConfigurationErrorCount;
        ActiveProfileId = activeProfileId;
        EffectiveCurrent = effectiveCurrent;
    }

    public IReadOnlyList<AgasAiSafeProfileDescriptor> Profiles { get; }
    public int ProviderConfigurationErrorCount { get; }
    public string? ActiveProfileId { get; }
    public AgasAiSafeEffectiveProfileDescriptor? EffectiveCurrent { get; }
}

public sealed record AgasAiProvidersQueryResult
{
    public AgasAiProvidersQueryResult(
        IReadOnlyList<AgasAiSafeProviderDescriptor> providers,
        int providerConfigurationErrorCount)
    {
        Providers = new ReadOnlyCollection<AgasAiSafeProviderDescriptor>((providers ?? Array.Empty<AgasAiSafeProviderDescriptor>()).ToArray());
        ProviderConfigurationErrorCount = providerConfigurationErrorCount;
    }

    public IReadOnlyList<AgasAiSafeProviderDescriptor> Providers { get; }
    public int ProviderConfigurationErrorCount { get; }
}

public sealed record AgasAiConfigurationDiagnostic
{
    public AgasAiConfigurationDiagnostic(
        string profileId,
        string providerId,
        string modelName,
        bool credentialConfigured,
        bool providerAvailable,
        bool ready,
        bool networkChecked,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<string> issueCodes)
    {
        ProfileId = profileId;
        ProviderId = providerId;
        ModelName = modelName;
        CredentialConfigured = credentialConfigured;
        ProviderAvailable = providerAvailable;
        Ready = ready;
        NetworkChecked = networkChecked;
        Capabilities = new ReadOnlyCollection<string>((capabilities ?? Array.Empty<string>()).ToArray());
        IssueCodes = new ReadOnlyCollection<string>((issueCodes ?? Array.Empty<string>()).ToArray());
    }

    public string ProfileId { get; }
    public string ProviderId { get; }
    public string ModelName { get; }
    public bool CredentialConfigured { get; }
    public bool ProviderAvailable { get; }
    public bool Ready { get; }
    public bool NetworkChecked { get; }
    public IReadOnlyList<string> Capabilities { get; }
    public IReadOnlyList<string> IssueCodes { get; }
}

internal sealed class AgasAiSettingsConflictException : Exception
{
}

internal sealed class AgasAiSettingsCommitRejectedException : Exception
{
}
