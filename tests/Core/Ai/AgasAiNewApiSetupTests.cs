using AppAgasCs.Features.Ai.Settings;
using Xunit;

namespace AppAgasCs.Tests.Core.Ai;

public sealed class AgasAiNewApiSetupTests
{
    [Fact]
    public void SeedingRespectsExistingNewApiProviderUnderCustomFilename()
    {
        using var home = new TemporaryHome();
        _ = AgasAiProviderCatalog.Load(home.Path);
        string directory = AgasAiSettingsPaths.ProvidersDirectory(home.Path);
        string builtInPath = Path.Combine(directory, "05-axoncat-newapi.json");
        string customPath = Path.Combine(directory, "my-newapi.json");
        File.Move(builtInPath, customPath);
        string custom = File.ReadAllText(customPath).Replace("\"displayName\": \"New API\"", "\"displayName\": \"My custom New API\"");
        File.WriteAllText(customPath, custom);
        File.Delete(Path.Combine(directory, ".agas-provider-schema-1"));
        File.Delete(Path.Combine(directory, ".agas-provider-newapi-1"));

        var catalog = AgasAiProviderCatalog.Load(home.Path);
        Assert.Equal("My custom New API", catalog.GetRequired(AgasAiProviderIds.AxonCatNewApi).DisplayName);
        Assert.Empty(catalog.Errors);
        Assert.False(File.Exists(builtInPath));
        Assert.Equal(custom, File.ReadAllText(customPath));
    }

    [Fact]
    public void UpgradeAddsNewTemplateWithoutOverwritingExistingFilesAndDoesNotReseedDeletedTemplate()
    {
        using var home = new TemporaryHome();
        string providers = AgasAiSettingsPaths.ProvidersDirectory(home.Path);
        Directory.CreateDirectory(providers);
        File.WriteAllText(Path.Combine(providers, ".agas-provider-schema-1"), "1\n");
        string customPath = Path.Combine(providers, "custom.json");
        const string custom = "{\"intentionally\":\"invalid-user-file\"}";
        File.WriteAllText(customPath, custom);

        var catalog = AgasAiProviderCatalog.Load(home.Path);
        var provider = catalog.GetRequired(AgasAiProviderIds.AxonCatNewApi);
        Assert.Equal("https://ainewdev.cc", provider.DefaultBaseUrl);
        Assert.Equal(provider.DefaultBaseUrl, AgasAiProfile.CreateDefault().BaseUrl);
        Assert.Equal("newapi-compatible", provider.AdapterKind);
        Assert.Equal(AgasAiUnifiedModelCatalog.Models.Select(model => model.ModelName),
            provider.Models.Select(model => model.ModelName));
        Assert.Equal(custom, File.ReadAllText(customPath));

        File.Delete(Path.Combine(providers, "05-axoncat-newapi.json"));
        Assert.False(AgasAiProviderCatalog.Load(home.Path).TryGet(AgasAiProviderIds.AxonCatNewApi, out _));
    }

    [Fact]
    public void UpgradeKeepsLegacyCredentialsAndCreatesOneEmptyNewApiConnectionThenRespectsSelection()
    {
        using var home = new TemporaryHome();
        var store = new AgasAiSettingsStore(home.Path);
        var legacy = new AgasAiProfile("legacy", "My relay", AgasAiProviderIds.OpenAiCompatible,
            "gpt-image-2", "https://legacy.example/v1", "synthetic-legacy-key");
        store.SaveProfiles(new(3, legacy.Id, new[] { legacy }));
        var catalog = AgasAiProviderCatalog.Load(home.Path);

        var upgraded = AgasAiNewApiSetup.EnsureProfile(store, catalog);
        Assert.Equal(legacy, upgraded.Profiles.Single(profile => profile.Id == legacy.Id));
        var newApi = upgraded.Profiles.Single(profile => profile.ProviderId == AgasAiProviderIds.AxonCatNewApi);
        Assert.Null(newApi.ApiKey);
        Assert.Equal(legacy.Id, upgraded.ActiveProfileId);

        new AgasAiSettingsFieldMergeService(store).Activate(legacy.Id);
        string saved = File.ReadAllText(store.ProfilesPath);
        Assert.Equal(legacy.Id, AgasAiNewApiSetup.EnsureProfile(store, catalog).ActiveProfileId);
        Assert.Equal(saved, File.ReadAllText(store.ProfilesPath));
        store.SaveProfiles(new(3, legacy.Id, new[] { legacy }));
        Assert.Single(AgasAiNewApiSetup.EnsureProfile(store, catalog).Profiles);
    }

    [Fact]
    public void FallbackModelsShareOneCredentialAndModelLookupPrefersCurrentConnection()
    {
        using var home = new TemporaryHome();
        var store = new AgasAiSettingsStore(home.Path);
        var catalog = AgasAiProviderCatalog.Load(home.Path);
        var profile = Assert.Single(AgasAiNewApiSetup.EnsureProfile(store, catalog).Profiles);
        store.SetCredential(profile.Id, "synthetic-newapi-key");
        foreach (var model in catalog.GetRequired(profile.ProviderId).Models)
        {
            var document = new AgasAiSettingsFieldMergeService(store)
                .UpdateActiveSelection(profile.Id, modelName: model.ModelName);
            var saved = Assert.Single(document.Profiles);
            Assert.Equal("synthetic-newapi-key", saved.ApiKey);
            Assert.Equal("https://ainewdev.cc", saved.BaseUrl);
            Assert.Equal(profile.Id, saved.Id);
            Assert.Equal(profile.ProviderId, catalog.FindByKnownModel(model.ModelName, profile.ProviderId)?.Id);
        }
        Assert.Equal(AgasAiProviderIds.OpenAiCompatible,
            catalog.FindByKnownModel("gpt-image-2", AgasAiProviderIds.OpenAiCompatible)?.Id);
        Assert.Equal(profile.ProviderId,
            catalog.FindByKnownModel("jimeng-image-5.0-lite", profile.ProviderId)?.Id);
        Assert.Equal(AgasAiProviderIds.OpenAiCompatible,
            catalog.FindByKnownModel("gpt-image-2.5-flare", AgasAiProviderIds.OpenAiCompatible)?.Id);
    }

    [Fact]
    public void UpgradeSelectsSeedOnlyWhenNoValidActiveProfileExists()
    {
        using var home = new TemporaryHome();
        var store = new AgasAiSettingsStore(home.Path);
        var existing = new AgasAiProfile("existing", "Existing", AgasAiProviderIds.OpenAiCompatible,
            "gpt-image-2", "https://relay.example/v1", "synthetic-existing-key");
        store.SaveProfiles(new(3, null, new[] { existing }));

        AgasAiProfilesDocument upgraded = AgasAiNewApiSetup.EnsureProfile(
            store, AgasAiProviderCatalog.Load(home.Path));

        AgasAiProfile seed = upgraded.Profiles.Single(profile =>
            profile.ProviderId == AgasAiProviderIds.AxonCatNewApi);
        Assert.Equal(seed.Id, upgraded.ActiveProfileId);
        Assert.Equal("synthetic-existing-key", upgraded.Profiles.Single(profile => profile.Id == existing.Id).ApiKey);
    }

    [Fact]
    public void UpgradeAddingSeedPreservesIndependentActiveModel()
    {
        using var home = new TemporaryHome();
        var store = new AgasAiSettingsStore(home.Path);
        var existing = new AgasAiProfile("existing", "Existing", AgasAiProviderIds.OpenAiCompatible,
            "legacy-profile-model", "https://relay.example/v1", "synthetic-existing-key");
        store.SaveProfiles(new(
            AgasAiProfilesDocument.CurrentResourceVersion,
            existing.Id,
            "gpt-image-2.5-flare",
            new[] { existing }));

        AgasAiProfilesDocument upgraded = AgasAiNewApiSetup.EnsureProfile(
            store, AgasAiProviderCatalog.Load(home.Path));

        Assert.Equal(existing.Id, upgraded.ActiveProfileId);
        Assert.Equal("gpt-image-2.5-flare", upgraded.ActiveModelName);
        Assert.Equal("legacy-profile-model", upgraded.Profiles.Single(profile => profile.Id == existing.Id).ModelName);
    }

    [Fact]
    public void OriginChangeClearsOldCredentialButAcceptsExplicitNewCredential()
    {
        using var home = new TemporaryHome();
        var store = new AgasAiSettingsStore(home.Path);
        var profile = AgasAiProfile.CreateDefault() with { ApiKey = "synthetic-key" };
        store.SaveProfiles(new(3, profile.Id, new[] { profile }));
        var merge = new AgasAiSettingsFieldMergeService(store);
        string activeModelName = store.LoadProfiles().ActiveModelName;
        AgasAiProfilesDocument sameOrigin = merge.UpdateActiveSelection(
            profile.Id,
            baseUrl: "https://ainewdev.cc/v1/");
        Assert.Equal("synthetic-key", Assert.Single(sameOrigin.Profiles).ApiKey);
        Assert.Equal(activeModelName, sameOrigin.ActiveModelName);
        AgasAiProfilesDocument changedOrigin = merge.UpdateActiveSelection(
            profile.Id,
            baseUrl: "https://other.example");
        Assert.Null(Assert.Single(changedOrigin.Profiles).ApiKey);
        Assert.Equal(activeModelName, changedOrigin.ActiveModelName);
        merge.UpdateProfileAndCredential(profile with { BaseUrl = "https://third.example" },
            "explicit-new-key");
        Assert.Equal("explicit-new-key", store.GetCredential(profile.Id));
        merge.UpdateProfileAndCredential(profile, null);
        Assert.Null(store.GetCredential(profile.Id));
    }

    private sealed class TemporaryHome : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agas-newapi-" + Guid.NewGuid().ToString("N"));
        public TemporaryHome() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
