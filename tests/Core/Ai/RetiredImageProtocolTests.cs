using System.Net;
using System.Text.Json;
using AppAgasCs.Features.Ai.Settings;
using Xunit;

namespace AppAgasCs.Tests.Core.Ai;

public sealed class RetiredImageProtocolTests
{
    [Theory]
    [InlineData("gemini-compatible")]
    [InlineData("renamed-private-relay")]
    public async Task RetiredGoogleDescriptor_BlocksDiscoveryAndMcpMutationWithoutSendingOldCredential(string providerId)
    {
        using var home = new Home(providerId);
        int calls = 0;
        using var client = new HttpClient(new Handler(() => calls++));
        var discovery = new AgasAiModelDiscovery(client);
        var access = new AgasAiConnectionModelAccess(home.Store, discovery);
        AgasAiModelSnapshot snapshot = await access.RefreshAsync();
        Assert.False(snapshot.IsReady);
        Assert.Contains("Google", snapshot.Message);
        Assert.DoesNotContain("old-private-key", snapshot.Message);
        Assert.Equal(0, calls);
        var mutations = new AgasAiSettingsRmwCoordinator(home.Store, discovery: discovery);
        AgasAiSettingsMutationResult activation = mutations.ActivateProfile(home.Profile.Id, new Bridge(home.Profile.Id));
        Assert.False(activation.Success);
        Assert.Equal(AgasAiSettingsMutationFailure.ProviderUnavailable, activation.Failure);
        Assert.Equal("old-private-key", home.Store.GetCredential(home.Profile.Id));
        Assert.Equal(providerId, Assert.Single(home.Store.LoadProfiles().Profiles).ProviderId);
    }

    [Fact]
    public async Task CustomRetiredDescriptor_RequiresExplicitNewUrlAndKeyBeforeConvertingConnection()
    {
        using var home = new Home("renamed-private-relay");
        var merge = new AgasAiSettingsFieldMergeService(home.Store);
        AgasAiProfile converted = home.Profile with
        {
            ProviderId = AgasAiProviderIds.AxonCatNewApi,
            BaseUrl = "https://unified.example/custom/v1beta",
        };
        byte[] before = File.ReadAllBytes(home.Store.ProfilesPath);
        Assert.Throws<InvalidDataException>(() => merge.UpdateProfileAndCredential(converted, replacementCredential: null));
        Assert.Equal(before, File.ReadAllBytes(home.Store.ProfilesPath));
        Assert.Throws<InvalidDataException>(() => merge.UpdateProfileAndCredential(converted,
            new AgasAiSettingsDraftState(true, false, false, false, true), "new-unified-key"));
        merge.UpdateProfileAndCredential(converted, "new-unified-key");
        AgasAiProfile saved = Assert.Single(home.Store.LoadProfiles().Profiles);
        Assert.Equal(AgasAiProviderIds.AxonCatNewApi, saved.ProviderId);
        Assert.Equal(converted.BaseUrl, saved.BaseUrl);
        Assert.Equal("new-unified-key", home.Store.GetCredential(saved.Id));
        Assert.True(File.Exists(home.DescriptorPath));
        using var models = new ImageModelDiscoveryFixture();
        AgasAiModelSnapshot live = await new AgasAiConnectionModelAccess(home.Store, models.Discovery).RefreshAsync();
        Assert.True(live.TryGet("gpt-image-2", out _));
    }

    private sealed class Home : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "agas-retired-image-protocol", Guid.NewGuid().ToString("N"));
        public AgasAiSettingsStore Store { get; }
        public AgasAiProfile Profile { get; }
        public string DescriptorPath { get; }
        public Home(string providerId)
        {
            string providers = AgasAiSettingsPaths.ProvidersDirectory(_path);
            Directory.CreateDirectory(providers);
            DescriptorPath = Path.Combine(providers, "user-legacy-relay.json");
            File.WriteAllText(DescriptorPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, id = providerId, displayName = "Old relay", adapterKind = "gemini-compatible",
                defaultBaseUrl = "https://old-relay.example/private/v1beta",
                models = new[] { new { modelName = "old-model", label = "Old", recommended = true } },
                capabilities = new[] { "textToImage" },
                credential = new { kind = "profileSettings", label = "API Key", settingsKey = "apiKey" },
            }));
            Store = new AgasAiSettingsStore(_path);
            Profile = new AgasAiProfile("old-connection", "Old relay", providerId, "old-model",
                "https://old-relay.example/private/v1beta", "old-private-key");
            Store.SaveProfiles(new AgasAiProfilesDocument(AgasAiProfilesDocument.CurrentResourceVersion,
                Profile.Id, "gpt-image-2", new[] { Profile }));
        }
        public void Dispose() => Directory.Delete(_path, true);
    }

    private sealed class Handler(Action called) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            called();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class Bridge(string id) : IAgasAiSettingsMutationLeaseBridge
    {
        public AgasAiSettingsDraftState DraftState => default;
        public bool TryAcquire(out AgasAiSettingsMutationLease? lease)
        {
            lease = new AgasAiSettingsMutationLease(Guid.NewGuid().ToString("N"), 1, "test", id);
            return true;
        }
        public bool ValidateBeforeCommit(AgasAiSettingsMutationLease lease, AgasAiSettingsPersistedFingerprint fingerprint) => true;
        public bool SynchronizeAfterCommit(AgasAiSettingsMutationLease lease, AgasAiProfilesDocument document) => true;
        public void Release(AgasAiSettingsMutationLease lease) { }
    }
}
