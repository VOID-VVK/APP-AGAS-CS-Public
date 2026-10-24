using System;
using System.Collections.Generic;
using System.Linq;

namespace AppAgasCs.Features.Ai.Settings;

/// <summary>
/// Fresh-load field merge boundary shared by Settings UI and generation selectors.
/// Every operation enters the same canonical settings-root gate used by MCP RMW.
/// </summary>
public sealed class AgasAiSettingsFieldMergeService
{
    private readonly AgasAiSettingsStore _store;

    public AgasAiSettingsFieldMergeService(AgasAiSettingsStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public AgasAiProfilesDocument Activate(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        lock (_store.SharedGate)
        {
            AgasAiProfilesDocument fresh = _store.LoadProfiles();
            AgasAiProfile target = fresh.Profiles.FirstOrDefault(profile =>
                    string.Equals(profile.Id, profileId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("AI 中转站不存在。");
            var updated = new AgasAiProfilesDocument(
                AgasAiProfilesDocument.CurrentResourceVersion,
                target.Id,
                fresh.ActiveModelName,
                fresh.Profiles);
            _store.SaveProfiles(updated);
            return updated;
        }
    }

    public AgasAiProfilesDocument UpdateActiveSelection(
        string profileId,
        string? providerId = null,
        string? modelName = null,
        string? baseUrl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        lock (_store.SharedGate)
        {
            AgasAiProfilesDocument fresh = _store.LoadProfiles();
            AgasAiProfile target = fresh.Profiles.FirstOrDefault(profile =>
                    string.Equals(profile.Id, profileId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("AI 中转站不存在。");
            AgasAiProfile merged = target with
            {
                BaseUrl = baseUrl ?? target.BaseUrl,
            };
            if (!AgasAiCredentialOrigin.IsSame(target.BaseUrl, merged.BaseUrl))
                merged = merged with { ApiKey = null };
            var updated = new AgasAiProfilesDocument(
                AgasAiProfilesDocument.CurrentResourceVersion,
                target.Id,
                modelName?.Trim() ?? fresh.ActiveModelName,
                fresh.Profiles.Select(profile =>
                    string.Equals(profile.Id, target.Id, StringComparison.Ordinal) ? merged : profile).ToArray());
            _store.SaveProfiles(updated);
            return updated;
        }
    }

    public AgasAiProfilesDocument UpdateProfileAndCredential(
        AgasAiProfile profile,
        string? replacementCredential) => UpdateProfileAndCredential(
            profile,
            new AgasAiSettingsDraftState(true, true, true, true, replacementCredential != null),
            replacementCredential);

    public AgasAiProfilesDocument UpdateProfileAndCredential(
        AgasAiProfile profile,
        AgasAiSettingsDraftState drafts,
        string? replacementCredential)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_store.SharedGate)
        {
            AgasAiProfilesDocument fresh = _store.LoadProfiles();
            AgasAiProfile current = fresh.Profiles.FirstOrDefault(item =>
                    string.Equals(item.Id, profile.Id, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("AI 中转站不存在。");
            AgasAiProfile merged = current with
            {
                Name = drafts.Name ? profile.Name : current.Name,
                BaseUrl = drafts.BaseUrl ? profile.BaseUrl : current.BaseUrl,
            };
            if (!AgasAiCredentialOrigin.IsSame(current.BaseUrl, merged.BaseUrl))
                merged = merged with { ApiKey = null };
            var updated = new AgasAiProfilesDocument(
                AgasAiProfilesDocument.CurrentResourceVersion,
                profile.Id,
                fresh.ActiveModelName,
                fresh.Profiles.Select(item =>
                    string.Equals(item.Id, profile.Id, StringComparison.Ordinal) ? merged : item).ToArray());
            _store.SaveProfilesAndCredential(updated, profile.Id, replacementCredential);
            return updated;
        }
    }

    public AgasAiProfilesDocument Add(AgasAiProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_store.SharedGate)
        {
            AgasAiProfilesDocument fresh = _store.LoadProfiles();
            if (fresh.Profiles.Any(item => string.Equals(item.Id, profile.Id, StringComparison.Ordinal)))
                throw new InvalidOperationException("AI 中转站 ID 已存在。");
            var updated = new AgasAiProfilesDocument(
                AgasAiProfilesDocument.CurrentResourceVersion,
                profile.Id,
                fresh.ActiveModelName,
                fresh.Profiles.Append(profile).ToArray());
            _store.SaveProfiles(updated);
            return updated;
        }
    }

    public AgasAiProfilesDocument Delete(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        lock (_store.SharedGate)
        {
            AgasAiProfilesDocument fresh = _store.LoadProfiles();
            AgasAiProfile[] remaining = fresh.Profiles
                .Where(profile => !string.Equals(profile.Id, profileId, StringComparison.Ordinal))
                .ToArray();
            if (remaining.Length == fresh.Profiles.Count)
                throw new InvalidOperationException("AI 中转站不存在。");
            string? active = string.Equals(fresh.ActiveProfileId, profileId, StringComparison.Ordinal)
                ? remaining.FirstOrDefault()?.Id
                : fresh.ActiveProfileId;
            var updated = new AgasAiProfilesDocument(
                AgasAiProfilesDocument.CurrentResourceVersion,
                active,
                fresh.ActiveModelName,
                remaining);
            _store.DeleteProfileAndCredential(updated, profileId);
            return updated;
        }
    }

    public void ClearCredential(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        lock (_store.SharedGate)
        {
            AgasAiProfilesDocument fresh = _store.LoadProfiles();
            if (!fresh.Profiles.Any(profile => string.Equals(profile.Id, profileId, StringComparison.Ordinal)))
                throw new InvalidOperationException("AI 中转站不存在。");
            _store.DeleteCredential(profileId);
        }
    }

    public AgasAiProfilesDocument SetActiveModel(string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        lock (_store.SharedGate)
        {
            AgasAiProfilesDocument fresh = _store.LoadProfiles();
            var updated = new AgasAiProfilesDocument(
                AgasAiProfilesDocument.CurrentResourceVersion,
                fresh.ActiveProfileId,
                modelName.Trim(),
                fresh.Profiles);
            _store.SaveProfiles(updated);
            return updated;
        }
    }
}
