using System.Text.Json;
using AppAgasCs.Features.Ai.Settings;
using Xunit;

namespace AppAgasCs.Tests.Core.Ai;

public sealed class AgasAiLegacyCredentialMigrationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void LegacyProviderSchemasRetainTheirMatchingCredentialDuringUpgrade(int version)
    {
        using var home = new LegacyHome();
        home.WriteLegacyProfiles(version);
        home.WriteCredentials(new() { ["old"] = "synthetic-legacy-key" });
        var store = new AgasAiSettingsStore(home.Path);
        Assert.Equal("synthetic-legacy-key", store.GetCredential("old"));
        AgasAiProfilesDocument loaded = store.LoadProfiles();
        Assert.Equal(AgasAiProviderIds.OpenAiCompatible, Assert.Single(loaded.Profiles).ProviderId);
        Assert.Equal(AgasAiProfilesDocument.CurrentResourceVersion, loaded.ResourceVersion);
        Assert.Equal("gpt-image-2", loaded.ActiveModelName);
    }

    [Fact]
    public void Version3CredentialsMigrateByExactExistingProfileIdAndKeepLegacyFile()
    {
        using var home = new LegacyHome();
        home.WriteProfiles(OldProfile("old"), OldProfile("other"));
        home.WriteCredentials(new() { ["old"] = "synthetic-old-key", ["orphan"] = "synthetic-orphan" });
        string original = File.ReadAllText(home.CredentialsPath);

        var store = new AgasAiSettingsStore(home.Path);
        Assert.Equal("synthetic-old-key", store.GetCredential("old"));
        Assert.Null(store.GetCredential("other"));
        Assert.Null(store.GetCredential("orphan"));
        AgasAiProfilesDocument loaded = store.LoadProfiles();
        Assert.Equal(AgasAiProfilesDocument.CurrentResourceVersion, loaded.ResourceVersion);
        Assert.Equal("gpt-image-2", loaded.ActiveModelName);
        Assert.Equal(original, File.ReadAllText(home.CredentialsPath));
    }

    [Fact]
    public void EmbeddedCredentialWinsAndMigrationDoesNotResurrectClearedKeys()
    {
        using var home = new LegacyHome();
        home.WriteProfiles(OldProfile("old") with { ApiKey = "synthetic-embedded-key" });
        home.WriteCredentials(new() { ["old"] = "synthetic-outdated-key" });
        var store = new AgasAiSettingsStore(home.Path);
        Assert.Equal("synthetic-embedded-key", store.GetCredential("old"));
        string saved = File.ReadAllText(store.ProfilesPath);
        _ = new AgasAiSettingsStore(home.Path).LoadProfiles();
        Assert.Equal(saved, File.ReadAllText(store.ProfilesPath));
        store.DeleteCredential("old");
        Assert.Null(new AgasAiSettingsStore(home.Path).GetCredential("old"));
    }

    [Fact]
    public void NewApiNeverImportsAKeyEvenIfLegacyFileHasSameId()
    {
        using var home = new LegacyHome();
        var newApi = AgasAiProfile.CreateDefault() with { Id = "newapi" };
        home.WriteProfiles(OldProfile("old"), newApi);
        home.WriteCredentials(new() { ["old"] = "synthetic-old-key", ["newapi"] = "synthetic-wrong-origin-key" });
        var store = new AgasAiSettingsStore(home.Path);
        Assert.Equal("synthetic-old-key", store.GetCredential("old"));
        Assert.Null(store.GetCredential("newapi"));
        Assert.Equal("https://ainewdev.cc", store.LoadProfiles().Profiles.Single(p => p.Id == "newapi").BaseUrl);
    }

    [Fact]
    public void MalformedLegacyFileDoesNotLoseProfilesOrBlockNewApiSetup()
    {
        using var home = new LegacyHome();
        var old = OldProfile("old");
        home.WriteProfiles(old);
        const string malformed = "{invalid-synthetic-secret";
        File.WriteAllText(home.CredentialsPath, malformed);
        var store = new AgasAiSettingsStore(home.Path);

        Assert.Equal(old, Assert.Single(store.LoadProfiles().Profiles));
        Assert.NotNull(store.CredentialMigrationWarning);
        Assert.DoesNotContain("invalid-synthetic-secret", store.CredentialMigrationWarning!);
        Assert.DoesNotContain(home.Path, store.CredentialMigrationWarning!);
        Assert.Null(store.GetCredential("old"));
        Assert.Equal(malformed, File.ReadAllText(home.CredentialsPath));
        var upgraded = AgasAiNewApiSetup.EnsureProfile(store, AgasAiProviderCatalog.Load(home.Path));
        Assert.Equal(old, upgraded.Profiles.Single(p => p.Id == "old"));
        Assert.Null(upgraded.Profiles.Single(p => p.ProviderId == AgasAiProviderIds.AxonCatNewApi).ApiKey);
        Assert.Null(new AgasAiSettingsStore(home.Path).CredentialMigrationWarning);
    }

    [Theory]
    [InlineData("{\"resourceVersion\":1,\"credentials\":{\"old\":null}}")]
    [InlineData("{\"resourceVersion\":1,\"credentials\":{\"old\":\"a\",\"old\":\"b\"}}")]
    [InlineData("{\"resourceVersion\":2,\"credentials\":{\"old\":\"a\"}}")]
    public void InvalidLegacyCredentialSchemaIsNonfatalAndNeverPartiallyImports(string legacy)
    {
        using var home = new LegacyHome();
        home.WriteProfiles(OldProfile("old"));
        File.WriteAllText(home.CredentialsPath, legacy);
        var store = new AgasAiSettingsStore(home.Path);
        Assert.Null(store.GetCredential("old"));
        Assert.NotNull(store.CredentialMigrationWarning);
        AgasAiProfilesDocument loaded = store.LoadProfiles();
        Assert.Equal(AgasAiProfilesDocument.CurrentResourceVersion, loaded.ResourceVersion);
        Assert.Equal("gpt-image-2", loaded.ActiveModelName);
        Assert.Equal(legacy, File.ReadAllText(home.CredentialsPath));
    }

    [Fact]
    public void MigratedCredentialIsNotRecoveredAfterChangingOrigin()
    {
        using var home = new LegacyHome();
        home.WriteProfiles(OldProfile("old"));
        home.WriteCredentials(new() { ["old"] = "synthetic-old-key" });
        var store = new AgasAiSettingsStore(home.Path);
        Assert.Equal("synthetic-old-key", store.GetCredential("old"));
        new AgasAiSettingsFieldMergeService(store).UpdateActiveSelection("old", baseUrl: "https://new.example");
        Assert.Null(new AgasAiSettingsStore(home.Path).GetCredential("old"));
    }

    private static AgasAiProfile OldProfile(string id) => new(id, "Legacy", AgasAiProviderIds.OpenAiCompatible,
        "gpt-image-2", "https://legacy.example/v1");

    private sealed class LegacyHome : IDisposable
    {
        private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agas-legacy-key-" + Guid.NewGuid().ToString("N"));
        public string CredentialsPath => System.IO.Path.Combine(Path, "credentials.json");
        public LegacyHome() => Directory.CreateDirectory(Path);
        public void WriteProfiles(params AgasAiProfile[] profiles) => File.WriteAllText(
            AgasAiSettingsPaths.ProfilesFile(Path), JsonSerializer.Serialize(new AgasAiProfilesDocument(3, profiles[0].Id, profiles), Options));
        public void WriteCredentials(Dictionary<string, string> credentials) => File.WriteAllText(CredentialsPath,
            JsonSerializer.Serialize(new { resourceVersion = 1, credentials }, Options));
        public void WriteLegacyProfiles(int version) => File.WriteAllText(AgasAiSettingsPaths.ProfilesFile(Path),
            JsonSerializer.Serialize(new { resourceVersion = version, activeProfileId = "old", profiles = new[]
            {
                new { id = "old", name = "Legacy", modelType = "gptImage", modelName = "gpt-image-2", baseUrl = "https://legacy.example/v1" },
            } }, Options));
        public void Dispose() => Directory.Delete(Path, true);
    }
}
