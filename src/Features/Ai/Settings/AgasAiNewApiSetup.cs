using System;
using System.IO;
using System.Linq;

namespace AppAgasCs.Features.Ai.Settings;

/// <summary>Adds the new connection once without moving any existing credentials.</summary>
public static class AgasAiNewApiSetup
{
    private const string MarkerFileName = ".agas-newapi-profile-1";

    public static AgasAiProfilesDocument EnsureProfile(
        AgasAiSettingsStore store, AgasAiProviderCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);
        lock (store.SharedGate)
        {
            AgasAiProfilesDocument document = store.LoadProfiles();
            string marker = Path.Combine(store.HomeDirectory, MarkerFileName);
            if (File.Exists(marker) || !catalog.TryGet(AgasAiProviderIds.AxonCatNewApi, out var provider))
                return document;
            AgasAiProfile? seed = document.Profiles.FirstOrDefault(profile => profile.ProviderId == provider.Id);
            if (seed == null)
            {
                seed = new AgasAiProfile(Guid.NewGuid().ToString("N"), provider.DisplayName,
                    provider.Id, provider.DefaultModelName, provider.DefaultBaseUrl);
            }
            bool activeIsValid = document.ActiveProfileId != null
                && document.Profiles.Any(profile => profile.Id == document.ActiveProfileId);
            if (!document.Profiles.Contains(seed) || !activeIsValid)
            {
                document = new AgasAiProfilesDocument(AgasAiProfilesDocument.CurrentResourceVersion,
                    activeIsValid ? document.ActiveProfileId : seed.Id,
                    document.ActiveModelName,
                    document.Profiles.Contains(seed) ? document.Profiles : document.Profiles.Append(seed).ToArray());
                store.SaveProfiles(document);
            }
            Directory.CreateDirectory(store.HomeDirectory);
            File.WriteAllText(marker, "1\n");
            return document;
        }
    }
}
