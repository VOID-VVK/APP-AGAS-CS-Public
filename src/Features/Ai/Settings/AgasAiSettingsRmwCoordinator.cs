using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AppAgasCs.Features.Ai.Settings;

/// <summary>
/// Field-level, process-local RMW coordinator. UI/Main integration is supplied
/// later through the lease bridge; this type performs no engine or MCP wiring.
/// </summary>
public sealed class AgasAiSettingsRmwCoordinator
{
    private readonly AgasAiSettingsStore _store;
    private readonly AgasAiConnectionModelAccess _models;
    private readonly Func<AgasAiProviderCatalog> _providerCatalog;
    public AgasAiSettingsRmwCoordinator(
        AgasAiSettingsStore store,
        Func<AgasAiProviderCatalog>? providerCatalog = null,
        AgasAiModelDiscovery? discovery = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _providerCatalog = providerCatalog ?? (() => AgasAiProviderCatalog.Load(_store.HomeDirectory));
        _models = new AgasAiConnectionModelAccess(_store, discovery, _providerCatalog);
    }

    public AgasAiSettingsMutationResult ActivateProfile(
        string profileId,
        IAgasAiSettingsMutationLeaseBridge bridge,
        IAgasAiSettingsCommitGate? commitGate,
        CancellationToken cancellationToken = default) =>
        Mutate(profileId, modelName: null, activate: true, bridge, commitGate, cancellationToken);

    public AgasAiSettingsMutationResult ActivateProfile(
        string profileId,
        IAgasAiSettingsMutationLeaseBridge bridge,
        CancellationToken cancellationToken = default) =>
        ActivateProfile(profileId, bridge, commitGate: null, cancellationToken);

    public AgasAiSettingsMutationResult SetActiveModel(
        string modelName,
        IAgasAiSettingsMutationLeaseBridge bridge,
        IAgasAiSettingsCommitGate? commitGate,
        CancellationToken cancellationToken = default) =>
        Mutate(profileId: string.Empty, modelName, activate: false, bridge, commitGate, cancellationToken);

    public AgasAiSettingsMutationResult SetActiveModel(
        string modelName,
        IAgasAiSettingsMutationLeaseBridge bridge,
        CancellationToken cancellationToken = default) =>
        SetActiveModel(modelName, bridge, commitGate: null, cancellationToken);

    private AgasAiSettingsMutationResult Mutate(
        string profileId,
        string? modelName,
        bool activate,
        IAgasAiSettingsMutationLeaseBridge bridge,
        IAgasAiSettingsCommitGate? commitGate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        commitGate ??= new AgasAllowAiSettingsCommitGate();
        if (bridge.DraftState.HasUnsavedChanges
            || !bridge.TryAcquire(out AgasAiSettingsMutationLease? lease)
            || lease == null)
        {
            return AgasAiSettingsMutationResult.Fail(AgasAiSettingsMutationFailure.Conflict);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            AgasAiProfilesDocument committedDocument;
            string safeProfileId;
            string safeProviderId;
            string safeModelName;
            bool credentialConfigured;
            bool knownPreset;
            lock (_store.SharedGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AgasAiProfilesDocument fresh = _store.LoadProfiles();
                if (!string.Equals(fresh.ActiveProfileId, lease.ActiveProfileId, StringComparison.Ordinal))
                {
                    return AgasAiSettingsMutationResult.Fail(AgasAiSettingsMutationFailure.Conflict);
                }

                AgasAiProfile? target = activate
                    ? fresh.Profiles.FirstOrDefault(profile =>
                        string.Equals(profile.Id, profileId?.Trim(), StringComparison.Ordinal))
                    : fresh.Profiles.FirstOrDefault(profile =>
                        string.Equals(profile.Id, fresh.ActiveProfileId, StringComparison.Ordinal));
                if (target == null)
                {
                    return AgasAiSettingsMutationResult.Fail(AgasAiSettingsMutationFailure.ProfileNotFound);
                }
                if (AgasAiConnectionModelResolver.UsesRetiredProtocol(target, _providerCatalog()))
                    return AgasAiSettingsMutationResult.Fail(AgasAiSettingsMutationFailure.ProviderUnavailable);

                IReadOnlyList<string> credentials = _store.LoadCredentialValuesInsideSharedGate();
                credentialConfigured = _store.HasCredential(target.Id);
                string effectiveModel = fresh.ActiveModelName;
                if (!activate)
                {
                    effectiveModel = modelName?.Trim() ?? string.Empty;
                    if (effectiveModel.Length is < 1 or > 200
                        || effectiveModel.Any(character => character <= '\u001f' || character == '\u007f')
                        || AgasMcpAiSafeProjector.ContainsCredential(effectiveModel, credentials))
                    {
                        return AgasAiSettingsMutationResult.Fail(AgasAiSettingsMutationFailure.ModelInvalid);
                    }
                }
                knownPreset = _models.TryGet(effectiveModel, out _, target.Id);

                AgasAiProfilesDocument updated = activate
                    ? new AgasAiProfilesDocument(
                        AgasAiProfilesDocument.CurrentResourceVersion,
                        target.Id,
                        fresh.ActiveModelName,
                        fresh.Profiles)
                    : new AgasAiProfilesDocument(
                        AgasAiProfilesDocument.CurrentResourceVersion,
                        fresh.ActiveProfileId,
                        effectiveModel,
                        fresh.Profiles);

                safeProfileId = AgasMcpAiSafeProjector.SafeIdentifier(target.Id, credentials);
                safeProviderId = AgasMcpAiSafeProjector.SafeProviderIdentifier(
                    AgasAiProviderIds.AxonCatNewApi,
                    credentials);
                safeModelName = AgasMcpAiSafeProjector.SafeText(effectiveModel, credentials);

                AgasAiSettingsPersistedFingerprint expected = _store.CapturePersistedFingerprint(fresh);
                _store.SaveProfilesCoordinated(updated, () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AgasAiSettingsPersistedFingerprint current = _store.CapturePersistedFingerprint(fresh);
                    if (current != expected || !bridge.ValidateBeforeCommit(lease, expected))
                    {
                        throw new AgasAiSettingsConflictException();
                    }
                    if (!commitGate.TryBeginAtomicReplace())
                    {
                        throw new AgasAiSettingsCommitRejectedException();
                    }
                });
                committedDocument = updated;
            }

            bool synchronized;
            try
            {
                synchronized = bridge.SynchronizeAfterCommit(lease, committedDocument);
            }
            catch
            {
                // Atomic replace is the commit point. UI drift or adapter
                // failure after it must not roll persisted settings back.
                synchronized = false;
            }
            return new AgasAiSettingsMutationResult(
                true,
                AgasAiSettingsMutationFailure.None,
                safeProfileId,
                safeProviderId,
                safeModelName,
                credentialConfigured,
                persisted: true,
                uiSynchronized: synchronized,
                knownPreset: knownPreset);
        }
        catch (AgasAiSettingsConflictException)
        {
            return AgasAiSettingsMutationResult.Fail(AgasAiSettingsMutationFailure.Conflict);
        }
        catch (OperationCanceledException)
        {
            return AgasAiSettingsMutationResult.Fail(AgasAiSettingsMutationFailure.Canceled);
        }
        catch (AgasAiSettingsCommitRejectedException)
        {
            return AgasAiSettingsMutationResult.Fail(AgasAiSettingsMutationFailure.Canceled);
        }
        catch
        {
            return AgasAiSettingsMutationResult.Fail(AgasAiSettingsMutationFailure.StorageFailure);
        }
        finally
        {
            try
            {
                bridge.Release(lease);
            }
            catch
            {
                // A release callback cannot change the already-determined
                // persistence outcome or trigger rollback after commit.
            }
        }
    }
}
