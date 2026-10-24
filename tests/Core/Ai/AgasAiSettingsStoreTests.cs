using System.Text.Json;
using AppAgasCs.Features.Ai.Settings;
using Xunit;

namespace AppAgasCs.Tests.Core.Ai;

public sealed class AgasAiSettingsStoreTests
{
    [Fact]
    public void ModelCatalog_UsesCurrentRecommendedDefaults()
    {
        Assert.Equal("gpt-image-2", AgasAiModelCatalog.DefaultGptImageModel);
        Assert.Equal("gemini-3-pro-image-preview", AgasAiModelCatalog.DefaultNanoBananaModel);
        Assert.Equal("gpt-image-2", AgasAiProfile.CreateDefault().ModelName);
    }

    [Fact]
    public void Version1Load_MigratesOnlyWrongFamilyAndDoesNotTouchCredentialBytes()
    {
        using var home = new TemporarySettingsHome();
        var store = new AgasAiSettingsStore(home.Path);
        File.WriteAllText(store.ProfilesPath, """
        {
          "resourceVersion": 1,
          "activeProfileId": "nano_wrong",
          "profiles": [
            {
              "id": "nano_wrong",
              "name": "Nano Wrong",
              "modelType": "nanoBanana",
              "modelName": "gpt-image-1",
              "baseUrl": "https://nano.example/"
            },
            {
              "id": "gpt_wrong",
              "name": "GPT Wrong",
              "modelType": "gptImage",
              "modelName": "gemini-2.5-flash-image",
              "baseUrl": "https://gpt.example/v1/"
            },
            {
              "id": "custom_alias",
              "name": "Custom Alias",
              "modelType": "nanoBanana",
              "modelName": "relay-custom-image-v7",
              "baseUrl": "https://custom.example/"
            },
            {
              "id": "gpt_prefix_alias",
              "name": "GPT Prefix Relay Alias",
              "modelType": "nanoBanana",
              "modelName": "gpt-image-company-route",
              "baseUrl": "https://gpt-prefix.example/"
            },
            {
              "id": "gemini_prefix_alias",
              "name": "Gemini Prefix Relay Alias",
              "modelType": "gptImage",
              "modelName": "gemini-openai-relay",
              "baseUrl": "https://gemini-prefix.example/"
            }
          ]
        }
        """);

        AgasAiProfilesDocument loaded = store.LoadProfiles();

        Assert.Equal(AgasAiProfilesDocument.CurrentResourceVersion, loaded.ResourceVersion);
        Assert.Equal("gemini-3-pro-image-preview", loaded.ActiveModelName);
        Assert.Equal("gemini-3-pro-image-preview", loaded.Profiles.Single(profile => profile.Id == "nano_wrong").ModelName);
        Assert.Equal("gpt-image-2", loaded.Profiles.Single(profile => profile.Id == "gpt_wrong").ModelName);
        Assert.Equal("relay-custom-image-v7", loaded.Profiles.Single(profile => profile.Id == "custom_alias").ModelName);
        Assert.Equal("gpt-image-company-route", loaded.Profiles.Single(profile => profile.Id == "gpt_prefix_alias").ModelName);
        Assert.Equal("gemini-openai-relay", loaded.Profiles.Single(profile => profile.Id == "gemini_prefix_alias").ModelName);
        using JsonDocument migratedJson = JsonDocument.Parse(File.ReadAllText(store.ProfilesPath));
        Assert.Equal(AgasAiProfilesDocument.CurrentResourceVersion, migratedJson.RootElement.GetProperty("resourceVersion").GetInt32());
        JsonElement migratedProfiles = migratedJson.RootElement.GetProperty("profiles");
        Assert.Equal(AgasAiProviderIds.GeminiCompatible, migratedProfiles[0].GetProperty("providerId").GetString());
        Assert.Equal(AgasAiProviderIds.OpenAiCompatible, migratedProfiles[1].GetProperty("providerId").GetString());
        Assert.DoesNotContain("modelType", File.ReadAllText(store.ProfilesPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Version2Load_PreservesExplicitLegacyNanoModelAndCredentials()
    {
        using var home = new TemporarySettingsHome();
        var store = new AgasAiSettingsStore(home.Path);
        File.WriteAllText(store.ProfilesPath, """
        {
          "resourceVersion": 2,
          "activeProfileId": "legacy_nano",
          "profiles": [
            {
              "id": "legacy_nano",
              "name": "Legacy Nano",
              "modelType": "nanoBanana",
              "modelName": "gemini-3-pro-image-preview",
              "baseUrl": "https://cpa.axoncat.com/antigravity/v1beta"
            },
            {
              "id": "custom_nano",
              "name": "Custom Nano",
              "modelType": "nanoBanana",
              "modelName": "relay-custom-image-v7",
              "baseUrl": "https://relay.example/api/v1beta"
            }
          ]
        }
        """);

        AgasAiProfilesDocument loaded = store.LoadProfiles();

        Assert.Equal(AgasAiProfilesDocument.CurrentResourceVersion, loaded.ResourceVersion);
        Assert.Equal("gemini-3-pro-image-preview", loaded.ActiveModelName);
        Assert.Equal("gemini-3-pro-image-preview", loaded.Profiles.Single(profile => profile.Id == "legacy_nano").ModelName);
        Assert.Equal("relay-custom-image-v7", loaded.Profiles.Single(profile => profile.Id == "custom_nano").ModelName);
        using JsonDocument persistedJson = JsonDocument.Parse(File.ReadAllText(store.ProfilesPath));
        Assert.Equal(AgasAiProfilesDocument.CurrentResourceVersion, persistedJson.RootElement.GetProperty("resourceVersion").GetInt32());
        Assert.Equal(
            "gemini-3-pro-image-preview",
            persistedJson.RootElement.GetProperty("profiles")[0].GetProperty("modelName").GetString());
        Assert.All(loaded.Profiles, profile => Assert.Equal(AgasAiProviderIds.GeminiCompatible, profile.ProviderId));
        Assert.All(
            persistedJson.RootElement.GetProperty("profiles").EnumerateArray(),
            profile => Assert.Equal(AgasAiProviderIds.GeminiCompatible, profile.GetProperty("providerId").GetString()));
        Assert.DoesNotContain("modelType", File.ReadAllText(store.ProfilesPath), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Version3And4Load_MigrateActiveModelFromActiveProfile(int version)
    {
        using var home = new TemporarySettingsHome();
        var store = new AgasAiSettingsStore(home.Path);
        File.WriteAllText(store.ProfilesPath, $$"""
        {
          "resourceVersion": {{version}},
          "activeProfileId": "second",
          "profiles": [
            {
              "id": "first",
              "name": "First",
              "providerId": "openai-compatible",
              "modelName": "first-model",
              "baseUrl": "https://first.example/v1/"
            },
            {
              "id": "second",
              "name": "Second",
              "providerId": "openai-compatible",
              "modelName": "second-model",
              "baseUrl": "https://second.example/v1/"
            }
          ]
        }
        """);

        AgasAiProfilesDocument loaded = store.LoadProfiles();

        Assert.Equal(5, loaded.ResourceVersion);
        Assert.Equal("second-model", loaded.ActiveModelName);
        using JsonDocument persisted = JsonDocument.Parse(File.ReadAllText(store.ProfilesPath));
        Assert.Equal("second-model", persisted.RootElement.GetProperty("activeModelName").GetString());
    }

    [Fact]
    public void Version5_RoundTripsIndependentActiveModel()
    {
        using var home = new TemporarySettingsHome();
        var store = new AgasAiSettingsStore(home.Path);
        AgasAiProfile profile = Profile("profile_01", "Relay", "https://relay.example/v1/");

        store.SaveProfiles(new AgasAiProfilesDocument(5, profile.Id, "  gpt-image-2.5-sunburst  ", new[] { profile }));

        AgasAiProfilesDocument loaded = store.LoadProfiles();
        Assert.Equal("gpt-image-2.5-sunburst", loaded.ActiveModelName);
        Assert.Equal("gpt-image-1", Assert.Single(loaded.Profiles).ModelName);
        using JsonDocument persisted = JsonDocument.Parse(File.ReadAllText(store.ProfilesPath));
        Assert.Equal("gpt-image-2.5-sunburst", persisted.RootElement.GetProperty("activeModelName").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("model\u001fvalue")]
    [InlineData("model\u007fvalue")]
    public void Version5_RejectsMissingBlankOrControlCharacterActiveModel(string? invalidModel)
    {
        using var home = new TemporarySettingsHome();
        var store = new AgasAiSettingsStore(home.Path);
        AgasAiProfile profile = Profile("profile_01", "Relay", "https://relay.example/v1/");

        Assert.Throws<InvalidDataException>(() => store.SaveProfiles(
            new AgasAiProfilesDocument(5, profile.Id, invalidModel, new[] { profile })));
    }

    [Fact]
    public void Version5_RejectsActiveModelLongerThanLimit()
    {
        using var home = new TemporarySettingsHome();
        var store = new AgasAiSettingsStore(home.Path);
        AgasAiProfile profile = Profile("profile_01", "Relay", "https://relay.example/v1/");

        Assert.Throws<InvalidDataException>(() => store.SaveProfiles(
            new AgasAiProfilesDocument(5, profile.Id, new string('m', 201), new[] { profile })));
    }

    [Fact]
    public void Version4Load_FallsBackFromInvalidActiveLegacyModelToFirstValidModel()
    {
        using var home = new TemporarySettingsHome();
        var store = new AgasAiSettingsStore(home.Path);
        File.WriteAllText(store.ProfilesPath, """
        {
          "resourceVersion": 4,
          "activeProfileId": "first",
          "profiles": [
            {"id":"first","name":"First","providerId":"openai-compatible","modelName":"","baseUrl":"https://first.example/v1/"},
            {"id":"second","name":"Second","providerId":"openai-compatible","modelName":"fallback-model","baseUrl":"https://second.example/v1/"}
          ]
        }
        """);

        AgasAiProfilesDocument migrated = store.LoadProfiles();

        Assert.Equal("fallback-model", migrated.ActiveModelName);
        Assert.Equal(string.Empty, migrated.Profiles.Single(profile => profile.Id == "first").ModelName);
        Assert.Equal("fallback-model", store.LoadProfiles().ActiveModelName);
    }

    [Fact]
    public void Version4Load_FallsBackToDefaultWhenEveryLegacyModelIsInvalid()
    {
        using var home = new TemporarySettingsHome();
        var store = new AgasAiSettingsStore(home.Path);
        File.WriteAllText(store.ProfilesPath, """
        {
          "resourceVersion": 4,
          "activeProfileId": "first",
          "profiles": [
            {"id":"first","name":"First","providerId":"openai-compatible","modelName":"","baseUrl":"https://first.example/v1/"}
          ]
        }
        """);

        AgasAiProfilesDocument migrated = store.LoadProfiles();

        Assert.Equal(AgasAiModelCatalog.DefaultGptImageModel, migrated.ActiveModelName);
        Assert.Equal(AgasAiModelCatalog.DefaultGptImageModel, store.LoadProfiles().ActiveModelName);
    }

    [Fact]
    public void PersistedFingerprint_ChangesWhenOnlyActiveModelChanges()
    {
        using var home = new TemporarySettingsHome();
        var store = new AgasAiSettingsStore(home.Path);
        AgasAiProfile profile = Profile("profile_01", "Relay", "https://relay.example/v1/");
        var beforeDocument = new AgasAiProfilesDocument(5, profile.Id, "before-model", new[] { profile });
        store.SaveProfiles(beforeDocument);
        AgasAiSettingsPersistedFingerprint before = store.CapturePersistedFingerprint(beforeDocument);
        var afterDocument = new AgasAiProfilesDocument(5, profile.Id, "after-model", new[] { profile });

        AgasAiSettingsPersistedFingerprint after = store.CapturePersistedFingerprint(afterDocument);

        Assert.Equal(before.File, after.File);
        Assert.NotEqual(before.DocumentSha256, after.DocumentSha256);
    }

    [Fact]
    public void ConnectionMutations_PreserveIndependentActiveModelAndLegacyFields()
    {
        using var home = new TemporarySettingsHome();
        var store = new AgasAiSettingsStore(home.Path);
        AgasAiProfile first = Profile("first", "First", "https://first.example/v1/");
        AgasAiProfile second = Profile("second", "Second", "https://second.example/v1/") with
        {
            ProviderId = "legacy-provider",
            ModelName = "legacy-model",
        };
        store.SaveProfiles(new AgasAiProfilesDocument(5, first.Id, "gpt-image-2.5-flare", new[] { first, second }));
        var merge = new AgasAiSettingsFieldMergeService(store);

        AgasAiProfilesDocument activated = merge.Activate(second.Id);
        Assert.Equal("gpt-image-2.5-flare", activated.ActiveModelName);

        AgasAiProfilesDocument saved = merge.UpdateProfileAndCredential(
            second with
            {
                Name = "Renamed",
                ProviderId = "ignored-provider",
                ModelName = "ignored-model",
            },
            replacementCredential: null);
        AgasAiProfile savedSecond = saved.Profiles.Single(profile => profile.Id == second.Id);
        Assert.Equal("Renamed", savedSecond.Name);
        Assert.Equal("legacy-provider", savedSecond.ProviderId);
        Assert.Equal("legacy-model", savedSecond.ModelName);
        Assert.Equal("gpt-image-2.5-flare", saved.ActiveModelName);

        AgasAiProfile added = Profile("third", "Third", "https://third.example/v1/");
        Assert.Equal("gpt-image-2.5-flare", merge.Add(added).ActiveModelName);
        AgasAiProfilesDocument deleted = merge.Delete(second.Id);
        Assert.Equal("gpt-image-2.5-flare", deleted.ActiveModelName);
        Assert.DoesNotContain(deleted.Profiles, profile => profile.Id == second.Id);
    }

    [Fact]
    public void ProfilesAndApiKey_RoundTripInTheSameFile()
    {
        using var home = new TemporarySettingsHome();
        var store = new AgasAiSettingsStore(home.Path);
        const string profileId = "relay_stable_01";
        const string secret = "test-secret-in-profile";
        var profile = new AgasAiProfile(
            profileId,
            "Relay One",
            AgasAiProviderIds.OpenAiCompatible,
            "gpt-image-1",
            "https://relay.example/v1/",
            secret);

        store.SaveProfiles(new AgasAiProfilesDocument(1, profileId, new[] { profile }));

        AgasAiProfilesDocument loaded = store.LoadProfiles();
        Assert.Equal(profileId, loaded.ActiveProfileId);
        Assert.Equal(profile, Assert.Single(loaded.Profiles));
        Assert.True(store.HasCredential(profileId));
        Assert.Equal(secret, store.GetCredential(profileId));

        string profilesJson = File.ReadAllText(store.ProfilesPath);
        Assert.Contains(secret, profilesJson, StringComparison.Ordinal);
        using JsonDocument parsed = JsonDocument.Parse(profilesJson);
        Assert.Equal(profileId, parsed.RootElement.GetProperty("activeProfileId").GetString());
        Assert.Equal(secret, parsed.RootElement.GetProperty("profiles")[0].GetProperty("apiKey").GetString());
    }

    [Fact]
    public void InvalidProfilesFile_DoesNotExposeStoredContents()
    {
        using var home = new TemporarySettingsHome();
        var store = new AgasAiSettingsStore(home.Path);
        const string secret = "never-echo-this-secret";
        Directory.CreateDirectory(home.Path);
        File.WriteAllText(store.ProfilesPath, "{\"resourceVersion\":3,\"profiles\":[{\"id\":\"profile\",\"apiKey\":\"" + secret);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => store.HasCredential("profile"));

        Assert.Equal("AI 中转站配置文件无效。", error.Message);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteCredential_RemovesSecretAndDoesNotCreateBackups()
    {
        using var home = new TemporarySettingsHome();
        var store = new AgasAiSettingsStore(home.Path);
        var profile = new AgasAiProfile(
            "profile_01",
            "Relay",
            AgasAiProviderIds.OpenAiCompatible,
            "gpt-image-1",
            "https://relay.example/v1/",
            "secret-one");
        store.SaveProfiles(new AgasAiProfilesDocument(1, profile.Id, new[] { profile }));
        store.DeleteCredential(profile.Id);

        Assert.False(store.HasCredential(profile.Id));
        Assert.DoesNotContain("secret-one", File.ReadAllText(store.ProfilesPath), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(home.Path, "*.bak", SearchOption.TopDirectoryOnly));
        Assert.Empty(Directory.EnumerateFiles(home.Path, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void ResolveHome_UsesAgasHomeOverride()
    {
        using var home = new TemporarySettingsHome();
        string? previous = Environment.GetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, home.Path);
            Assert.Equal(System.IO.Path.GetFullPath(home.Path), AgasAiSettingsPaths.ResolveHome());
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void DefaultHome_IsDotAgasUnderUserHomeWhenOverrideIsAbsent()
    {
        string? previous = Environment.GetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, null);
            string expected = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                AgasAiSettingsPaths.DefaultDirectoryName);
            Assert.Equal(expected, AgasAiSettingsPaths.ResolveHome());
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void UnixFileMode_IsPrivateForSettingsHomeAndCredentialFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var home = new TemporarySettingsHome(create: false);
        var store = new AgasAiSettingsStore(home.Path);
        var profile = new AgasAiProfile(
            "profile_01",
            "Relay",
            AgasAiProviderIds.OpenAiCompatible,
            "gpt-image-1",
            "https://relay.example/v1/",
            "secret-one");
        store.SaveProfiles(new AgasAiProfilesDocument(1, profile.Id, new[] { profile }));

        UnixFileMode directoryMode = File.GetUnixFileMode(home.Path);
        UnixFileMode fileMode = File.GetUnixFileMode(store.ProfilesPath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, directoryMode);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, fileMode);
    }

    [Theory]
    [InlineData("https://relay.example/v1/")]
    [InlineData("http://127.0.0.1:8080/v1/")]
    [InlineData("http://localhost:8080/v1/")]
    [InlineData("http://[::1]:8080/v1/")]
    public void BaseUrl_AllowsHttpsAndLoopbackHttp(string baseUrl)
    {
        using var home = new TemporarySettingsHome();
        var store = new AgasAiSettingsStore(home.Path);
        AgasAiProfile profile = Profile("profile_01", "Relay", baseUrl);

        store.SaveProfiles(new AgasAiProfilesDocument(1, profile.Id, new[] { profile }));

        Assert.Equal(baseUrl, Assert.Single(store.LoadProfiles().Profiles).BaseUrl);
    }

    [Theory]
    [InlineData("http://relay.example/v1/")]
    [InlineData("https://user:pass@relay.example/v1/")]
    [InlineData("https://relay.example/v1/?api_key=value")]
    [InlineData("https://relay.example/v1/#fragment")]
    public void BaseUrl_RejectsUnsafeOrRuntimeIncompatibleValues(string baseUrl)
    {
        using var home = new TemporarySettingsHome();
        var store = new AgasAiSettingsStore(home.Path);
        AgasAiProfile profile = Profile("profile_01", "Relay", baseUrl);

        Assert.Throws<InvalidDataException>(() =>
            store.SaveProfiles(new AgasAiProfilesDocument(1, profile.Id, new[] { profile })));
    }

    [Fact]
    public void SaveTransaction_WriteFailureRestoresProfileAndKeyWithoutLeaking()
    {
        using var home = new TemporarySettingsHome();
        var originalStore = new AgasAiSettingsStore(home.Path);
        AgasAiProfile original = Profile("profile_01", "Original", "https://relay.example/v1/") with
        {
            ApiKey = "old-test-secret",
        };
        originalStore.SaveProfiles(new AgasAiProfilesDocument(1, original.Id, new[] { original }));
        var failingStore = new AgasAiSettingsStore(home.Path, _ => throw new IOException("injected-secret-shaped-message"));
        AgasAiProfile updated = original with { Name = "Updated" };

        IOException error = Assert.Throws<IOException>(() => failingStore.SaveProfilesAndCredential(
            new AgasAiProfilesDocument(1, updated.Id, new[] { updated }),
            updated.Id,
            "new-test-secret"));

        Assert.DoesNotContain("new-test-secret", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("injected-secret-shaped-message", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(original, Assert.Single(originalStore.LoadProfiles().Profiles));
        Assert.Equal("old-test-secret", originalStore.GetCredential(original.Id));
    }

    [Fact]
    public void DeleteTransaction_WriteFailureRestoresProfileAndKey()
    {
        using var home = new TemporarySettingsHome();
        var originalStore = new AgasAiSettingsStore(home.Path);
        AgasAiProfile original = Profile("profile_01", "Original", "https://relay.example/v1/") with
        {
            ApiKey = "old-test-secret",
        };
        originalStore.SaveProfiles(new AgasAiProfilesDocument(1, original.Id, new[] { original }));
        var failingStore = new AgasAiSettingsStore(home.Path, _ => throw new IOException("injected failure"));

        Assert.Throws<IOException>(() => failingStore.DeleteProfileAndCredential(
            AgasAiProfilesDocument.Empty,
            original.Id));

        Assert.Equal(original, Assert.Single(originalStore.LoadProfiles().Profiles));
        Assert.Equal("old-test-secret", originalStore.GetCredential(original.Id));
        Assert.Empty(Directory.EnumerateFiles(home.Path, "*.bak", SearchOption.TopDirectoryOnly));
        Assert.Empty(Directory.EnumerateFiles(home.Path, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    private static AgasAiProfile Profile(string id, string name, string baseUrl) => new(
        id,
        name,
        AgasAiProviderIds.OpenAiCompatible,
        "gpt-image-1",
        baseUrl);

    private sealed class TemporarySettingsHome : IDisposable
    {
        public TemporarySettingsHome(bool create = true)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "agas-ai-settings-tests",
                Guid.NewGuid().ToString("N"));
            if (create)
            {
                Directory.CreateDirectory(Path);
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
