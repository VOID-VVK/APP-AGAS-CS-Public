using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Runtime;
using Xunit;

namespace AppAgasCs.Tests.Core.Ai;

public sealed class AgasAiSettingsUiIntegrationTests
{
    [Fact]
    public void UiFieldMergeAfterMcpModelSetPreservesBothConcurrentWinners()
    {
        using var home = new Home();
        AgasAiProfile original = home.Profile with { Name = "Original", ModelName = "model-known" };
        home.Store.SaveProfiles(new AgasAiProfilesDocument(3, original.Id, new[] { original }));
        home.Store.SetCredential(original.Id, "fixture-ui-key");
        using var models = new ImageModelDiscoveryFixture();
        models.Prime(home.Store);
        var service = new AgasAiProfileSettingsService(home.Store, home.Catalog, models.Discovery);
        var bridge = new SimpleBridge(original.Id);

        const string mcpModel = "gpt-image-2.5-flare";
        AgasAiSettingsMutationResult mcp = service.SetActiveModel(mcpModel, bridge);
        Assert.True(mcp.Success);

        var staleUiDraft = original with { Name = "UI Name", ModelName = "stale-ui-model" };
        var merge = new AgasAiSettingsFieldMergeService(home.Store);
        merge.UpdateProfileAndCredential(
            staleUiDraft,
            new AgasAiSettingsDraftState(true, false, false, false, false),
            replacementCredential: null);

        AgasAiProfilesDocument persistedDocument = home.Store.LoadProfiles();
        AgasAiProfile persisted = Assert.Single(persistedDocument.Profiles);
        Assert.Equal("UI Name", persisted.Name);
        Assert.Equal("model-known", persisted.ModelName);
        Assert.Equal(mcpModel, persistedDocument.ActiveModelName);
    }

    [Fact]
    public void StagedBridgeKeepsUiDisabledUntilMainThreadPostcommitApply()
    {
        using var home = new Home(twoProfiles: true);
        AgasAiProfilesDocument? applied = null;
        var staged = new AgasAiSettingsUiMutationBridge(document => applied = document);
        staged.PublishMainThreadSnapshot(default, 10, "workspace-1", "profile-1");
        Assert.True(staged.StageAcquireOnMainThread());
        var facade = new AgasMcpAiSettingsFacade(
            new AgasAiProfileSettingsService(home.Store, home.Catalog),
            staged,
            new AgasMcpDirtyState());
        var execution = new AgasMcpCommitExecution(AgasMcpCommitPolicy.ShortAtomicCommit);

        AgasMcpToolResponse background = facade.Activate(
            new AgasAiProfileActivateInput("profile-2"),
            execution);

        Assert.True(background.Ok);
        Assert.False(Assert.IsType<AgasMcpAiProfileActivatedDataDto>(background.Data).UiSynchronized);
        Assert.True(staged.IsLeased);
        Assert.Null(applied);

        AgasMcpToolResponse finalized = facade.FinalizeStagedUiOnMainThread(background);

        Assert.True(Assert.IsType<AgasMcpAiProfileActivatedDataDto>(finalized.Data).UiSynchronized);
        Assert.Equal("profile-2", applied?.ActiveProfileId);
        Assert.False(staged.IsLeased);
    }

    [Fact]
    public void StagedBridgePostcommitGenerationDriftDoesNotOverwriteNewUi()
    {
        using var home = new Home(twoProfiles: true);
        int applyCount = 0;
        var staged = new AgasAiSettingsUiMutationBridge(_ => applyCount++);
        staged.PublishMainThreadSnapshot(default, 10, "workspace-1", "profile-1");
        Assert.True(staged.StageAcquireOnMainThread());
        var facade = new AgasMcpAiSettingsFacade(
            new AgasAiProfileSettingsService(home.Store, home.Catalog),
            staged,
            new AgasMcpDirtyState());
        AgasMcpToolResponse background = facade.Activate(
            new AgasAiProfileActivateInput("profile-2"),
            new AgasMcpCommitExecution(AgasMcpCommitPolicy.ShortAtomicCommit));
        Assert.True(background.Ok);
        staged.PublishMainThreadSnapshot(default, 11, "workspace-2", "profile-new-ui");

        AgasMcpToolResponse finalized = facade.FinalizeStagedUiOnMainThread(background);

        Assert.False(Assert.IsType<AgasMcpAiProfileActivatedDataDto>(finalized.Data).UiSynchronized);
        Assert.Equal(0, applyCount);
        Assert.False(staged.IsLeased);
        Assert.Equal("profile-2", home.Store.LoadProfiles().ActiveProfileId);
    }

    [Fact]
    public void StagedBridgeCancellationBeforeCommitWritesNothingAndReleasesUiLease()
    {
        using var home = new Home(twoProfiles: true);
        byte[] before = File.ReadAllBytes(home.Store.ProfilesPath);
        var staged = new AgasAiSettingsUiMutationBridge(_ => throw new InvalidOperationException("No commit expected."));
        staged.PublishMainThreadSnapshot(default, 10, "workspace-1", "profile-1");
        Assert.True(staged.StageAcquireOnMainThread());
        var facade = new AgasMcpAiSettingsFacade(
            new AgasAiProfileSettingsService(home.Store, home.Catalog),
            staged,
            new AgasMcpDirtyState());
        var execution = new AgasMcpCommitExecution(AgasMcpCommitPolicy.ShortAtomicCommit);
        Assert.True(execution.TryCancel());

        AgasMcpToolResponse response = facade.Activate(
            new AgasAiProfileActivateInput("profile-2"),
            execution);
        AgasMcpToolResponse finalized = facade.FinalizeStagedUiOnMainThread(response);

        Assert.False(finalized.Ok);
        Assert.Equal(AgasMcpErrorCodes.Stopped, finalized.Error?.Code);
        Assert.False(staged.IsLeased);
        Assert.Equal(before, File.ReadAllBytes(home.Store.ProfilesPath));
    }

    [Fact]
    public void UnifiedModelKnownPresetIsUnaffectedByPostcommitLegacyCatalogChange()
    {
        using var home = new Home();
        home.Store.SetCredential(home.Profile.Id, "fixture-ui-key");
        using var models = new ImageModelDiscoveryFixture();
        models.Prime(home.Store);
        string providerFile = home.ProviderFile;
        var bridge = new SimpleBridge("profile-1")
        {
            Synchronize = _ =>
            {
                File.Delete(providerFile);
                return true;
            },
        };
        var facade = new AgasMcpAiSettingsFacade(
            new AgasAiProfileSettingsService(home.Store, home.Catalog, models.Discovery),
            bridge,
            new AgasMcpDirtyState());

        AgasMcpToolResponse response = facade.SetModel(new AgasAiModelSetInput("gpt-image-2"));

        AgasMcpAiModelSetDataDto data = Assert.IsType<AgasMcpAiModelSetDataDto>(response.Data);
        Assert.True(data.KnownPreset);
        Assert.False(File.Exists(providerFile));
    }

    private sealed class SimpleBridge : IAgasAiSettingsMutationLeaseBridge
    {
        private readonly string? _active;
        public SimpleBridge(string? active) => _active = active;
        public Func<AgasAiProfilesDocument, bool>? Synchronize { get; init; }
        public AgasAiSettingsDraftState DraftState => default;
        public bool TryAcquire(out AgasAiSettingsMutationLease? lease)
        {
            lease = new AgasAiSettingsMutationLease(Guid.NewGuid().ToString("N"), 1, "workspace", _active);
            return true;
        }
        public bool ValidateBeforeCommit(AgasAiSettingsMutationLease lease, AgasAiSettingsPersistedFingerprint persistedFingerprint) => true;
        public bool SynchronizeAfterCommit(AgasAiSettingsMutationLease lease, AgasAiProfilesDocument committedDocument) =>
            Synchronize?.Invoke(committedDocument) ?? true;
        public void Release(AgasAiSettingsMutationLease lease) { }
    }

    private sealed class Home : IDisposable
    {
        public Home(bool twoProfiles = false)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agas-ai-ui-integration", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            string providers = AgasAiSettingsPaths.ProvidersDirectory(Path);
            Directory.CreateDirectory(providers);
            ProviderFile = System.IO.Path.Combine(providers, "vendor.json");
            File.WriteAllText(ProviderFile, """
            {
              "schemaVersion":1,
              "id":"vendor.provider",
              "displayName":"Vendor",
              "adapterKind":"vendor.adapter-v2",
              "defaultBaseUrl":"https://relay.example/v1/",
              "models":[{"modelName":"model-known","label":"Known","recommended":true}],
              "capabilities":["textToImage"],
              "credential":{"kind":"profileSettings","label":"API Key","settingsKey":"apiKey"}
            }
            """);
            Store = new AgasAiSettingsStore(Path);
            Profile = new AgasAiProfile(
                "profile-1", "Profile 1", "vendor.provider", "model-known", "https://relay.example/v1/");
            AgasAiProfile[] profiles = twoProfiles
                ? new[] { Profile, Profile with { Id = "profile-2", Name = "Profile 2" } }
                : new[] { Profile };
            Store.SaveProfiles(new AgasAiProfilesDocument(3, Profile.Id, profiles));
        }

        public string Path { get; }
        public string ProviderFile { get; }
        public AgasAiSettingsStore Store { get; }
        public AgasAiProfile Profile { get; }
        public AgasAiProviderCatalog Catalog() => AgasAiProviderCatalog.LoadFromDirectory(
            AgasAiSettingsPaths.ProvidersDirectory(Path));
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch (DirectoryNotFoundException) { }
        }
    }
}
