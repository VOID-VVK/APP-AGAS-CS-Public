using System.Text.Json;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Contracts.Runtime;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp;

public sealed class AgasMcpUnifiedConnectionSettingsTests
{
    private static readonly string[] UnifiedModels =
    {
        "gpt-image-2",
        "gpt-image-2.5-flare",
        "gpt-image-2.5-sunburst",
        "grok-imagine-image-quality",
        "grok-imagine-image",
        "grok-imagine-image-2.0",
        "gemini-image",
    };

    [Fact]
    public void QueriesProjectEveryConnectionAgainstTheGlobalModelAndUnifiedRoute()
    {
        using var fixture = new Fixture("GPT-IMAGE-2.5-FLARE");

        AgasMcpToolResponse profilesResponse = fixture.Facade.Profiles(new AgasAiProfilesListInput());
        AgasMcpToolResponse modelsResponse = fixture.Facade.Models(new AgasAiModelsListInput());
        AgasMcpToolResponse diagnosticResponse = fixture.Facade.Diagnostics(
            new AgasAiDiagnosticsInput("connection-a"));

        AgasMcpAiProfilesDataDto profiles = Assert.IsType<AgasMcpAiProfilesDataDto>(profilesResponse.Data);
        Assert.Equal(new[] { "connection-a", "connection-b" }, profiles.Profiles.Select(row => row.ProfileId));
        Assert.All(profiles.Profiles, row =>
        {
            Assert.Equal(AgasAiProviderIds.AxonCatNewApi, row.ProviderId);
            Assert.Equal("gpt-image-2.5-flare", row.ModelName);
            Assert.True(row.ProviderAvailable);
            Assert.Equal(
                new[] { "textToImage", "referenceEdit", "regionEdit", "nativeAlphaMask" },
                row.Capabilities);
        });
        Assert.Equal("connection-b", profiles.EffectiveCurrent?.ProfileId);
        Assert.Equal(AgasAiProviderIds.AxonCatNewApi, profiles.EffectiveCurrent?.ProviderId);
        Assert.Equal(AgasAiUnifiedModelCatalog.ConnectionAdapterKind, profiles.EffectiveCurrent?.AdapterKind);
        Assert.Equal("gpt-image-2.5-flare", profiles.EffectiveCurrent?.ModelName);
        Assert.Equal("liveUi", profiles.EffectiveCurrent?.Source);

        AgasMcpAiModelsDataDto models = Assert.IsType<AgasMcpAiModelsDataDto>(modelsResponse.Data);
        AgasMcpAiProviderRowDto provider = Assert.Single(models.Providers);
        Assert.Equal(AgasAiProviderIds.AxonCatNewApi, provider.ProviderId);
        Assert.Equal(AgasAiUnifiedModelCatalog.ConnectionAdapterKind, provider.AdapterKind);
        Assert.False(provider.SupportsCustomModel);
        Assert.Equal(UnifiedModels, provider.Models.Select(model => model.ModelName));
        Assert.Equal(models.ProviderConfigurationErrorCount, profiles.ProviderConfigurationErrorCount);
        Assert.True(models.ProviderConfigurationErrorCount > 0);

        AgasMcpAiDiagnosticsDataDto diagnostic = Assert.IsType<AgasMcpAiDiagnosticsDataDto>(diagnosticResponse.Data);
        Assert.Equal("connection-a", diagnostic.ProfileId);
        Assert.Equal(AgasAiProviderIds.AxonCatNewApi, diagnostic.ProviderId);
        Assert.Equal("gpt-image-2.5-flare", diagnostic.ModelName);
        Assert.True(diagnostic.Ready);
        Assert.Empty(diagnostic.Issues);

        AssertValidOutput(AgasMcpToolNames.AiProfilesList, profilesResponse);
        AssertValidOutput(AgasMcpToolNames.AiModelsList, modelsResponse);
        AssertValidOutput(AgasMcpToolNames.AiDiagnostics, diagnosticResponse);
        AssertSafe(profilesResponse, fixture.FirstSecret, fixture.SecondSecret, fixture.FirstBaseUrl, fixture.SecondBaseUrl);
        AssertSafe(modelsResponse, fixture.FirstSecret, fixture.SecondSecret, fixture.FirstBaseUrl, fixture.SecondBaseUrl);
        AssertSafe(diagnosticResponse, fixture.FirstSecret, fixture.SecondSecret, fixture.FirstBaseUrl, fixture.SecondBaseUrl);
    }

    [Fact]
    public void ActivateAndModelSetMutateIndependentFieldsAndReturnTheEffectiveRoute()
    {
        using var fixture = new Fixture("gpt-image-2.5-sunburst");

        AgasMcpToolResponse activateResponse = fixture.Facade.Activate(
            new AgasAiProfileActivateInput("connection-b"));
        AgasMcpAiProfileActivatedDataDto activated =
            Assert.IsType<AgasMcpAiProfileActivatedDataDto>(activateResponse.Data);
        Assert.Equal("connection-b", activated.ProfileId);
        Assert.Equal(AgasAiProviderIds.AxonCatNewApi, activated.ProviderId);
        Assert.Equal("gpt-image-2.5-sunburst", activated.ModelName);
        Assert.True(activated.CredentialConfigured);

        AgasAiProfilesDocument afterActivate = fixture.Store.LoadProfiles();
        Assert.Equal("connection-b", afterActivate.ActiveProfileId);
        Assert.Equal("gpt-image-2.5-sunburst", afterActivate.ActiveModelName);
        Assert.Equal("legacy-a", afterActivate.Profiles.Single(row => row.Id == "connection-a").ModelName);
        Assert.Equal("legacy-b", afterActivate.Profiles.Single(row => row.Id == "connection-b").ModelName);
        Assert.Equal("legacy.provider-a", afterActivate.Profiles.Single(row => row.Id == "connection-a").ProviderId);
        Assert.Equal("legacy.provider-b", afterActivate.Profiles.Single(row => row.Id == "connection-b").ProviderId);

        AgasMcpToolResponse modelResponse = fixture.Facade.SetModel(new AgasAiModelSetInput("gemini-image"));
        AgasMcpAiModelSetDataDto model = Assert.IsType<AgasMcpAiModelSetDataDto>(modelResponse.Data);
        Assert.Equal("connection-b", model.ProfileId);
        Assert.Equal(AgasAiProviderIds.AxonCatNewApi, model.ProviderId);
        Assert.Equal("gemini-image", model.ModelName);
        Assert.True(model.KnownPreset);

        AgasAiProfilesDocument afterModel = fixture.Store.LoadProfiles();
        Assert.Equal("connection-b", afterModel.ActiveProfileId);
        Assert.Equal("gemini-image", afterModel.ActiveModelName);
        Assert.Equal(fixture.FirstBaseUrl, afterModel.Profiles.Single(row => row.Id == "connection-a").BaseUrl);
        Assert.Equal(fixture.SecondBaseUrl, afterModel.Profiles.Single(row => row.Id == "connection-b").BaseUrl);
        Assert.Equal("legacy.provider-a", afterModel.Profiles.Single(row => row.Id == "connection-a").ProviderId);
        Assert.Equal("legacy.provider-b", afterModel.Profiles.Single(row => row.Id == "connection-b").ProviderId);
        Assert.Equal(fixture.FirstSecret, fixture.Store.GetCredential("connection-a"));
        Assert.Equal(fixture.SecondSecret, fixture.Store.GetCredential("connection-b"));

        AssertValidOutput(AgasMcpToolNames.AiProfileActivate, activateResponse);
        AssertValidOutput(AgasMcpToolNames.AiModelSet, modelResponse);
        AssertSafe(activateResponse, fixture.FirstSecret, fixture.SecondSecret, fixture.FirstBaseUrl, fixture.SecondBaseUrl);
        AssertSafe(modelResponse, fixture.FirstSecret, fixture.SecondSecret, fixture.FirstBaseUrl, fixture.SecondBaseUrl);
    }

    [Fact]
    public void UnknownModelIsRejectedWithoutPersistingAndLegacyUnknownStateIsNotReady()
    {
        using var fixture = new Fixture("legacy-custom-model");
        byte[] before = File.ReadAllBytes(fixture.Store.ProfilesPath);

        AgasMcpToolResponse diagnosticResponse = fixture.Facade.Diagnostics(
            new AgasAiDiagnosticsInput("connection-a"));
        AgasMcpAiDiagnosticsDataDto diagnostic =
            Assert.IsType<AgasMcpAiDiagnosticsDataDto>(diagnosticResponse.Data);
        Assert.False(diagnostic.Ready);
        Assert.Empty(diagnostic.Capabilities);
        Assert.Equal(AgasMcpErrorCodes.AiModelInvalid, Assert.Single(diagnostic.Issues).Code);

        AgasMcpToolResponse setResponse = fixture.Facade.SetModel(
            new AgasAiModelSetInput("another-custom-model"));
        Assert.False(setResponse.Ok);
        Assert.Equal(AgasMcpErrorCodes.AiModelInvalid, setResponse.Error?.Code);
        Assert.Null(setResponse.Data);
        Assert.Equal(before, File.ReadAllBytes(fixture.Store.ProfilesPath));
        AssertSafe(diagnosticResponse, fixture.FirstSecret, fixture.SecondSecret, fixture.FirstBaseUrl, fixture.SecondBaseUrl);
        AssertSafe(setResponse, fixture.FirstSecret, fixture.SecondSecret, fixture.FirstBaseUrl, fixture.SecondBaseUrl);
    }

    private static void AssertValidOutput(string toolName, AgasMcpToolResponse response)
    {
        AgasMcpOutputValidationResult validation = AgasMcpOutputContractRegistry.Validate(
            AgasMcpToolDescriptorRegistry.Get(toolName).OutputContract!,
            response);
        Assert.True(validation.Success, validation.ContractFailure);
    }

    private static void AssertSafe(AgasMcpToolResponse response, params string[] forbidden)
    {
        string json = JsonSerializer.Serialize(response, AgasMcpOutputContractRegistry.SerializerOptions);
        Assert.All(forbidden, value => Assert.DoesNotContain(value, json, StringComparison.Ordinal));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly Bridge _bridge;

        public Fixture(string activeModelName)
        {
            _root = Path.Combine(Path.GetTempPath(), "agas-mcp-unified-settings", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            string providers = AgasAiSettingsPaths.ProvidersDirectory(_root);
            Directory.CreateDirectory(providers);
            File.WriteAllText(Path.Combine(providers, "broken.json"), "{ not-valid-json");

            Store = new AgasAiSettingsStore(_root);
            var first = new AgasAiProfile(
                "connection-a", "First", "legacy.provider-a", "legacy-a", FirstBaseUrl);
            var second = new AgasAiProfile(
                "connection-b", "Second", "legacy.provider-b", "legacy-b", SecondBaseUrl);
            Store.SaveProfiles(new AgasAiProfilesDocument(
                AgasAiProfilesDocument.CurrentResourceVersion,
                first.Id,
                activeModelName,
                new[] { second, first }));
            Store.SetCredential(first.Id, FirstSecret);
            Store.SetCredential(second.Id, SecondSecret);

            _bridge = new Bridge(first.Id);
            var service = new AgasAiProfileSettingsService(
                Store,
                () => AgasAiProviderCatalog.LoadFromDirectory(providers));
            Facade = new AgasMcpAiSettingsFacade(
                service,
                _bridge,
                new AgasMcpDirtyState(),
                () => new AgasAiEffectiveProfileSelection(
                    "connection-b", "legacy.live", "legacy-adapter", "legacy-live-model"));
        }

        public string FirstSecret { get; } = "synthetic-unified-secret-a";
        public string SecondSecret { get; } = "synthetic-unified-secret-b";
        public string FirstBaseUrl { get; } = "https://first-relay.example/v1/";
        public string SecondBaseUrl { get; } = "https://second-relay.example/v1/";
        public AgasAiSettingsStore Store { get; }
        public AgasMcpAiSettingsFacade Facade { get; }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }

        private sealed class Bridge(string? activeProfileId) : IAgasAiSettingsMutationLeaseBridge
        {
            private string? _activeProfileId = activeProfileId;

            public AgasAiSettingsDraftState DraftState => default;

            public bool TryAcquire(out AgasAiSettingsMutationLease? lease)
            {
                lease = new AgasAiSettingsMutationLease(
                    Guid.NewGuid().ToString("N"), 1, "mcp-unified-settings", _activeProfileId);
                return true;
            }

            public bool ValidateBeforeCommit(
                AgasAiSettingsMutationLease lease,
                AgasAiSettingsPersistedFingerprint persistedFingerprint) => true;

            public bool SynchronizeAfterCommit(
                AgasAiSettingsMutationLease lease,
                AgasAiProfilesDocument committedDocument)
            {
                _activeProfileId = committedDocument.ActiveProfileId;
                return true;
            }

            public void Release(AgasAiSettingsMutationLease lease)
            {
            }
        }
    }
}
