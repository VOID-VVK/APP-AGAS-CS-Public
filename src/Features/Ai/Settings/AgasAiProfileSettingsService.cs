using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AppAgasCs.Features.Ai.Generation;

namespace AppAgasCs.Features.Ai.Settings;

/// <summary>Fresh, safe AI profile/provider queries and configuration preflight.</summary>
public sealed class AgasAiProfileSettingsService
{
    private const string UnifiedProviderDisplayName = "AxonCat New API";

    private static readonly string[] CanonicalCapabilities =
    {
        "textToImage", "referenceEdit", "regionEdit", "nativeAlphaMask", "guidedRegionEdit",
    };

    private readonly AgasAiSettingsStore _store;
    private readonly Func<AgasAiProviderCatalog> _providerCatalog;
    private readonly AgasAiSettingsRmwCoordinator _mutations;
    private readonly AgasAiConnectionModelAccess _models;
    private readonly AgasAiModelDiscovery _discovery;

    public AgasAiProfileSettingsService(
        AgasAiSettingsStore store,
        Func<AgasAiProviderCatalog>? providerCatalog = null,
        AgasAiModelDiscovery? discovery = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _providerCatalog = providerCatalog ?? (() => AgasAiProviderCatalog.Load(_store.HomeDirectory));
        _discovery = discovery ?? AgasAiModelDiscovery.Shared;
        _models = new AgasAiConnectionModelAccess(_store, _discovery, _providerCatalog);
        _mutations = new AgasAiSettingsRmwCoordinator(_store, _providerCatalog, _discovery);
    }

    public System.Threading.Tasks.Task<AgasAiModelSnapshot> RefreshModelsAsync(
        System.Threading.CancellationToken cancellationToken = default) =>
        _models.RefreshAsync(cancellationToken: cancellationToken);

    public AgasAiSettingsMutationResult ActivateProfile(
        string profileId,
        IAgasAiSettingsMutationLeaseBridge bridge,
        IAgasAiSettingsCommitGate? commitGate,
        System.Threading.CancellationToken cancellationToken = default) =>
        _mutations.ActivateProfile(profileId, bridge, commitGate, cancellationToken);

    public AgasAiSettingsMutationResult ActivateProfile(
        string profileId,
        IAgasAiSettingsMutationLeaseBridge bridge,
        System.Threading.CancellationToken cancellationToken = default) =>
        ActivateProfile(profileId, bridge, commitGate: null, cancellationToken);

    public AgasAiSettingsMutationResult SetActiveModel(
        string modelName,
        IAgasAiSettingsMutationLeaseBridge bridge,
        IAgasAiSettingsCommitGate? commitGate,
        System.Threading.CancellationToken cancellationToken = default)
    {
        string candidate = modelName ?? string.Empty;
        if (!string.Equals(candidate, candidate.Trim(), StringComparison.Ordinal)
            || !_models.TryGet(candidate, out AgasAiUnifiedModel model))
        {
            return AgasAiSettingsMutationResult.Fail(AgasAiSettingsMutationFailure.ModelInvalid);
        }

        return _mutations.SetActiveModel(model.ModelName, bridge, commitGate, cancellationToken);
    }

    public AgasAiSettingsMutationResult SetActiveModel(
        string modelName,
        IAgasAiSettingsMutationLeaseBridge bridge,
        System.Threading.CancellationToken cancellationToken = default) =>
        SetActiveModel(modelName, bridge, commitGate: null, cancellationToken);

    public IReadOnlyList<AgasAiSafeProfileDescriptor> ListProfiles()
    {
        lock (_store.SharedGate)
        {
            AgasAiProfilesDocument document = _store.LoadProfiles();
            IReadOnlyList<string> credentials = _store.LoadCredentialValuesInsideSharedGate();
            return new ReadOnlyCollection<AgasAiSafeProfileDescriptor>(
                ProjectProfiles(document, credentials));
        }
    }

    public AgasAiProfilesQueryResult QueryProfiles(AgasAiEffectiveProfileSelection? liveSelection = null)
    {
        lock (_store.SharedGate)
        {
            AgasAiProfilesDocument document = _store.LoadProfiles();
            AgasAiProviderCatalog providers = _providerCatalog();
            IReadOnlyList<string> credentials = _store.LoadCredentialValuesInsideSharedGate();
            AgasAiSafeProfileDescriptor[] rows = ProjectProfiles(document, credentials);
            string? activeProfileId = document.Profiles.Any(profile =>
                    string.Equals(profile.Id, document.ActiveProfileId, StringComparison.Ordinal))
                ? AgasMcpAiSafeProjector.SafeIdentifier(document.ActiveProfileId, credentials)
                : null;
            AgasAiSafeEffectiveProfileDescriptor? effective = ProjectEffectiveCurrent(
                document,
                credentials,
                liveSelection);
            return new AgasAiProfilesQueryResult(
                rows,
                providers.Errors.Count,
                activeProfileId,
                effective);
        }
    }

    public IReadOnlyList<AgasAiSafeProviderDescriptor> ListProviders()
    {
        lock (_store.SharedGate)
        {
            IReadOnlyList<string> credentials = _store.LoadCredentialValuesInsideSharedGate();
            return new ReadOnlyCollection<AgasAiSafeProviderDescriptor>(new[]
            {
                ProjectUnifiedProvider(credentials),
            });
        }
    }

    public AgasAiProvidersQueryResult QueryProviders()
    {
        lock (_store.SharedGate)
        {
            AgasAiProviderCatalog providers = _providerCatalog();
            IReadOnlyList<string> credentials = _store.LoadCredentialValuesInsideSharedGate();
            return new AgasAiProvidersQueryResult(
                new[] { ProjectUnifiedProvider(credentials) },
                providers.Errors.Count);
        }
    }

    public bool IsKnownPreset(string providerId, string modelName)
    {
        lock (_store.SharedGate)
        {
            return _models.TryGet(modelName, out _);
        }
    }

    public AgasAiConfigurationDiagnostic Diagnose(string? profileId = null) =>
        Diagnose(profileId, liveSelection: null);

    public AgasAiConfigurationDiagnostic Diagnose(
        string? profileId,
        AgasAiEffectiveProfileSelection? liveSelection)
    {
        lock (_store.SharedGate)
        {
            AgasAiProfilesDocument profiles = _store.LoadProfiles();
            bool useLive = string.IsNullOrWhiteSpace(profileId) && liveSelection != null;
            AgasAiProfile? profile = useLive
                ? profiles.Profiles.FirstOrDefault(item =>
                    string.Equals(item.Id, liveSelection!.ProfileId, StringComparison.Ordinal))
                : string.IsNullOrWhiteSpace(profileId)
                ? profiles.Profiles.FirstOrDefault(item =>
                    string.Equals(item.Id, profiles.ActiveProfileId, StringComparison.Ordinal))
                : profiles.Profiles.FirstOrDefault(item =>
                    string.Equals(item.Id, profileId.Trim(), StringComparison.Ordinal));
            if (profile == null)
            {
                return new AgasAiConfigurationDiagnostic(
                    "redacted", "redacted", string.Empty, false, false, false, false,
                    Array.Empty<string>(), new[] { "PROFILE_NOT_FOUND" });
            }

            AiGenerationProviderProfile? execution = ResolveExecution(profile, profiles.ActiveModelName);
            string effectiveModelName = execution?.ModelName ?? CanonicalModelName(profiles.ActiveModelName);
            bool knownModel = _models.TryGet(effectiveModelName, out AgasAiUnifiedModel? model, profile.Id);
            bool credentialConfigured = _store.HasCredential(profile.Id);
            IReadOnlyList<string> credentials = _store.LoadCredentialValuesInsideSharedGate();
            var issues = new List<string>();
            if (!credentialConfigured) issues.Add("CREDENTIAL_MISSING");
            if (AgasAiConnectionModelResolver.UsesRetiredProtocol(profile, _providerCatalog())) issues.Add("PROTOCOL_RETIRED");
            if (string.IsNullOrWhiteSpace(effectiveModelName)) issues.Add("MODEL_MISSING");
            else if (!knownModel) issues.Add("MODEL_UNSUPPORTED");
            return new AgasAiConfigurationDiagnostic(
                AgasMcpAiSafeProjector.SafeIdentifier(profile.Id, credentials),
                AgasMcpAiSafeProjector.SafeProviderIdentifier(
                    execution?.ProviderId ?? AgasAiProviderIds.AxonCatNewApi,
                    credentials),
                AgasMcpAiSafeProjector.SafeText(effectiveModelName, credentials),
                credentialConfigured,
                true,
                issues.Count == 0,
                false,
                OrderCapabilities(knownModel ? model.Capabilities : Array.Empty<string>()),
                issues.OrderBy(code => code, StringComparer.Ordinal).ToArray());
        }
    }

    private static IReadOnlyList<string> OrderCapabilities(IReadOnlyList<string>? capabilities)
    {
        var supported = new HashSet<string>(capabilities ?? Array.Empty<string>(), StringComparer.Ordinal);
        return new ReadOnlyCollection<string>(CanonicalCapabilities.Where(supported.Contains).ToArray());
    }

    private AgasAiSafeProfileDescriptor[] ProjectProfiles(
        AgasAiProfilesDocument document,
        IReadOnlyList<string> credentials)
    {
        string modelName = CanonicalModelName(document.ActiveModelName);
        return document.Profiles
            .OrderBy(profile => profile.Id, StringComparer.Ordinal)
            .Select(profile => new AgasAiSafeProfileDescriptor(
                AgasMcpAiSafeProjector.SafeIdentifier(profile.Id, credentials),
                AgasMcpAiSafeProjector.SafeText(profile.Name, credentials),
                AgasMcpAiSafeProjector.SafeProviderIdentifier(AgasAiProviderIds.AxonCatNewApi, credentials),
                AgasMcpAiSafeProjector.SafeText(UnifiedProviderDisplayName, credentials),
                AgasMcpAiSafeProjector.SafeText(modelName, credentials),
                _store.HasCredential(profile.Id),
                true,
                OrderCapabilities(_models.TryGet(modelName, out AgasAiUnifiedModel model, profile.Id)
                    ? model.Capabilities : Array.Empty<string>())))
            .ToArray();
    }

    private AgasAiSafeProviderDescriptor ProjectUnifiedProvider(
        IReadOnlyList<string> credentials)
    {
        AgasAiModelSnapshot snapshot = _models.Snapshot();
        AgasAiUnifiedModel[] available = snapshot.IsReady
            ? snapshot.Models.Where(model => model.IsAvailable).ToArray()
            : Array.Empty<AgasAiUnifiedModel>();
        string defaultModel = available.FirstOrDefault(model => string.Equals(
            model.ModelName, AgasAiModelCatalog.DefaultGptImageModel, StringComparison.Ordinal))?.ModelName
            ?? available.FirstOrDefault()?.ModelName ?? string.Empty;
        return new(
        AgasMcpAiSafeProjector.SafeProviderIdentifier(AgasAiProviderIds.AxonCatNewApi, credentials),
        AgasMcpAiSafeProjector.SafeText(UnifiedProviderDisplayName, credentials),
        AgasMcpAiSafeProjector.SafeText(AgasAiUnifiedModelCatalog.ConnectionAdapterKind, credentials),
        AgasMcpAiSafeProjector.SafeText(defaultModel, credentials),
        OrderCapabilities(available
            .SelectMany(model => model.Capabilities).Distinct(StringComparer.Ordinal).ToArray()),
        false,
        available
            .Select(model => new AgasAiModelPreset(
                AgasMcpAiSafeProjector.SafeText(model.ModelName, credentials),
                AgasMcpAiSafeProjector.SafeText(model.DisplayName, credentials),
                string.Equals(
                    model.ModelName,
                    defaultModel,
                    StringComparison.Ordinal)))
            .ToArray());
    }

    private AgasAiSafeEffectiveProfileDescriptor? ProjectEffectiveCurrent(
        AgasAiProfilesDocument document,
        IReadOnlyList<string> credentials,
        AgasAiEffectiveProfileSelection? liveSelection)
    {
        string? selectedProfileId = liveSelection?.ProfileId ?? document.ActiveProfileId;
        AgasAiProfile? selected = document.Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, selectedProfileId, StringComparison.Ordinal));
        if (selected == null)
        {
            return null;
        }

        AiGenerationProviderProfile? execution = ResolveExecution(selected, document.ActiveModelName);
        return new AgasAiSafeEffectiveProfileDescriptor(
            AgasMcpAiSafeProjector.SafeIdentifier(selected.Id, credentials),
            AgasMcpAiSafeProjector.SafeProviderIdentifier(
                execution?.ProviderId ?? AgasAiProviderIds.AxonCatNewApi,
                credentials),
            AgasMcpAiSafeProjector.SafeText(
                execution?.AdapterKind ?? AgasAiUnifiedModelCatalog.ConnectionAdapterKind,
                credentials),
            AgasMcpAiSafeProjector.SafeText(
                execution?.ModelName ?? CanonicalModelName(document.ActiveModelName),
                credentials),
            liveSelection == null ? "persisted" : "liveUi");
    }

    private AiGenerationProviderProfile? ResolveExecution(
        AgasAiProfile connection,
        string activeModelName)
    {
        string? credential = _store.GetCredential(connection.Id);
        if (string.IsNullOrWhiteSpace(credential)
            || AgasAiConnectionModelResolver.UsesRetiredProtocol(connection, _providerCatalog())) return null;
        try
        {
            return AgasAiConnectionModelResolver.Resolve(connection with { ApiKey = credential }, activeModelName, _discovery);
        }
        catch (AiGenerationException) { return null; }
    }

    private string CanonicalModelName(string? modelName) =>
        _models.TryGet(modelName, out AgasAiUnifiedModel model)
            ? model.ModelName
            : modelName?.Trim() ?? string.Empty;
}
