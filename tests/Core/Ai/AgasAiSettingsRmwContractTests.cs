using AppAgasCs.Features.Ai.Settings;
using Xunit;

namespace AppAgasCs.Tests.Core.Ai;

public sealed class AgasAiSettingsRmwContractTests
{
    [Fact]
    public async Task SharedGate_ConcurrentModelSetAndDifferentProfileActivationPersistBothWinningFields()
    {
        using var home = new TemporaryHome();
        AgasAiProfile first = Profile("profile-a", "model-a");
        AgasAiProfile second = Profile("profile-b", "model-b");
        string initialActiveProfileId = second.Id;
        string activationTargetProfileId = first.Id;
        Assert.NotEqual(initialActiveProfileId, activationTargetProfileId);
        var seed = new AgasAiSettingsStore(home.Path);
        seed.SaveProfiles(new AgasAiProfilesDocument(3, initialActiveProfileId, new[] { first, second }));
        Assert.Equal(initialActiveProfileId, seed.LoadProfiles().ActiveProfileId);
        Func<AgasAiProviderCatalog> providers = home.ProviderCatalog;
        using var modelBeforeReplace = new ManualResetEventSlim();
        using var releaseModelReplace = new ManualResetEventSlim();
        using var activationAttempted = new ManualResetEventSlim();
        var modelStore = new AgasAiSettingsStore(home.AliasedPath, _ =>
        {
            modelBeforeReplace.Set();
            Assert.True(releaseModelReplace.Wait(TimeSpan.FromSeconds(5)), "Timed out releasing model replace.");
        });
        var activateStore = new AgasAiSettingsStore(home.Path);
        Assert.Same(modelStore.SharedGate, activateStore.SharedGate);
        var activate = new AgasAiSettingsRmwCoordinator(activateStore, providers);
        var model = new AgasAiSettingsRmwCoordinator(modelStore, providers);

        Task<AgasAiSettingsMutationResult> modelMutation = Task.Run(() =>
            model.SetActiveModel("model-updated", new Bridge(second.Id)));
        Assert.True(modelBeforeReplace.Wait(TimeSpan.FromSeconds(5)), "Model mutation did not reach atomic replace.");
        Task<AgasAiSettingsMutationResult> activateMutation = Task.Run(() =>
        {
            activationAttempted.Set();
            return activate.ActivateProfile(activationTargetProfileId, new Bridge(initialActiveProfileId));
        });
        Assert.True(activationAttempted.Wait(TimeSpan.FromSeconds(5)), "Activation mutation did not start.");
        releaseModelReplace.Set();

        AgasAiSettingsMutationResult[] results = await Task.WhenAll(modelMutation, activateMutation);
        AgasAiSettingsMutationResult modelResult = results[0];
        AgasAiSettingsMutationResult activationResult = results[1];
        Assert.True(modelResult.Success);
        Assert.True(activationResult.Success);
        Assert.Equal(activationTargetProfileId, activationResult.ProfileId);
        Assert.True(activationResult.Persisted);
        AgasAiProfilesDocument persisted = seed.LoadProfiles();
        Assert.Equal(activationTargetProfileId, persisted.ActiveProfileId);
        Assert.Equal("model-updated", persisted.ActiveModelName);
        Assert.Equal("model-a", persisted.Profiles.Single(item => item.Id == first.Id).ModelName);
        Assert.Equal("model-b", persisted.Profiles.Single(item => item.Id == second.Id).ModelName);
        Assert.Empty(Directory.EnumerateFiles(home.Path, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData("mo\0del")]
    [InlineData("mo\tdel")]
    [InlineData("mo\ndel")]
    [InlineData("mo\u001fdel")]
    [InlineData("model\u007f")]
    public void ModelSet_RejectsControlCharactersWithoutWriting(string invalidModel)
    {
        using var home = new TemporaryHome();
        var store = new AgasAiSettingsStore(home.Path);
        AgasAiProfile profile = Profile("profile-a", "model-a");
        store.SaveProfiles(new AgasAiProfilesDocument(3, profile.Id, new[] { profile }));
        byte[] before = File.ReadAllBytes(store.ProfilesPath);
        var coordinator = new AgasAiSettingsRmwCoordinator(store, home.ProviderCatalog);

        AgasAiSettingsMutationResult result = coordinator.SetActiveModel(invalidModel, new Bridge(profile.Id));

        Assert.False(result.Success);
        Assert.Equal(AgasAiSettingsMutationFailure.ModelInvalid, result.Failure);
        Assert.Equal(before, File.ReadAllBytes(store.ProfilesPath));
    }

    [Fact]
    public void ModelSet_TrimsOuterWhitespaceWithoutChangingConnectionFields()
    {
        using var home = new TemporaryHome();
        var store = new AgasAiSettingsStore(home.Path);
        AgasAiProfile profile = Profile("profile-a", "legacy-model") with
        {
            ApiKey = "credential-value",
        };
        store.SaveProfiles(new AgasAiProfilesDocument(5, profile.Id, "before-model", new[] { profile }));
        var coordinator = new AgasAiSettingsRmwCoordinator(store, home.ProviderCatalog);

        AgasAiSettingsMutationResult result = coordinator.SetActiveModel(
            "  gpt-image-2.5-flare  ",
            new Bridge(profile.Id));

        Assert.True(result.Success);
        Assert.Equal("gpt-image-2.5-flare", result.ModelName);
        Assert.Equal(AgasAiProviderIds.AxonCatNewApi, result.ProviderId);
        Assert.True(result.KnownPreset);
        AgasAiProfilesDocument persisted = store.LoadProfiles();
        Assert.Equal("gpt-image-2.5-flare", persisted.ActiveModelName);
        Assert.Equal(profile, Assert.Single(persisted.Profiles));
    }

    [Fact]
    public async Task SharedGate_ResolvesRealSymlinkAncestorBeforeSerializingConcurrentRmw()
    {
        using var home = new SymlinkAncestorHome();
        AgasAiProfile first = Profile("profile-a", "model-a");
        AgasAiProfile second = Profile("profile-b", "model-b");
        var seed = new AgasAiSettingsStore(home.RealHome);
        seed.SaveProfiles(new AgasAiProfilesDocument(3, second.Id, new[] { first, second }));

        using var firstBeforeReplace = new ManualResetEventSlim();
        using var allowFirstReplace = new ManualResetEventSlim();
        using var secondAttemptStarted = new ManualResetEventSlim();
        using var secondBeforeReplace = new ManualResetEventSlim();
        var firstStore = new AgasAiSettingsStore(home.RealHome, _ =>
        {
            firstBeforeReplace.Set();
            Assert.True(allowFirstReplace.Wait(TimeSpan.FromSeconds(5)), "Timed out releasing the first atomic replace.");
        });
        var secondStore = new AgasAiSettingsStore(home.AliasHome, _ => secondBeforeReplace.Set());
        Assert.Same(firstStore.SharedGate, secondStore.SharedGate);
        var model = new AgasAiSettingsRmwCoordinator(firstStore, home.ProviderCatalog);
        var activate = new AgasAiSettingsRmwCoordinator(secondStore, home.ProviderCatalog);

        Task<AgasAiSettingsMutationResult> modelMutation = Task.Run(() =>
            model.SetActiveModel("model-updated", new Bridge(second.Id)));
        Assert.True(firstBeforeReplace.Wait(TimeSpan.FromSeconds(5)), "First mutation did not reach atomic replace.");
        Task<AgasAiSettingsMutationResult> activateMutation = Task.Run(() =>
        {
            secondAttemptStarted.Set();
            return activate.ActivateProfile(first.Id, new Bridge(second.Id));
        });
        Assert.True(secondAttemptStarted.Wait(TimeSpan.FromSeconds(5)), "Second mutation did not start.");
        bool overlapped;
        try
        {
            overlapped = secondBeforeReplace.Wait(TimeSpan.FromSeconds(1));
        }
        finally
        {
            allowFirstReplace.Set();
        }

        AgasAiSettingsMutationResult[] results = await Task.WhenAll(modelMutation, activateMutation);
        Assert.False(overlapped, "A symlinked ancestor produced a second process-local settings gate.");
        Assert.All(results, result => Assert.True(result.Success));
        AgasAiProfilesDocument persisted = seed.LoadProfiles();
        Assert.Equal(first.Id, persisted.ActiveProfileId);
        Assert.Equal("model-updated", persisted.ActiveModelName);
        Assert.Equal("model-b", persisted.Profiles.Single(item => item.Id == second.Id).ModelName);
    }

    [Fact]
    public void LegacyProviderIdentifier_DoesNotAffectCanonicalMutationResult()
    {
        using var home = new TemporaryHome();
        var store = new AgasAiSettingsStore(home.Path);
        AgasAiProfile profile = Profile("profile-a", "model-a", "vendor.provider");
        store.SaveProfiles(new AgasAiProfilesDocument(3, profile.Id, new[] { profile }));
        var coordinator = new AgasAiSettingsRmwCoordinator(store, home.ProviderCatalog);

        AgasAiSettingsMutationResult result = coordinator.SetActiveModel("model-updated", new Bridge(profile.Id));

        Assert.True(result.Success);
        Assert.Equal(AgasAiProviderIds.AxonCatNewApi, result.ProviderId);
    }

    [Fact]
    public async Task StoreHome_IsFrozenToCanonicalTargetWhenSymlinkAncestorIsRetargeted()
    {
        using var home = new SymlinkAncestorHome();
        var firstStore = new AgasAiSettingsStore(home.AliasHome);
        Assert.Equal(AgasAiSettingsSharedGate.CanonicalizePath(home.RealHome), firstStore.HomeDirectory);

        home.RetargetAliasToSecondParent();
        var secondStore = new AgasAiSettingsStore(home.AliasHome);
        Assert.Equal(AgasAiSettingsSharedGate.CanonicalizePath(home.SecondRealHome), secondStore.HomeDirectory);
        Assert.NotSame(firstStore.SharedGate, secondStore.SharedGate);

        AgasAiProfile first = Profile("profile-a", "model-a");
        AgasAiProfile second = Profile("profile-b", "model-b");
        await Task.WhenAll(
            Task.Run(() => firstStore.SaveProfiles(new AgasAiProfilesDocument(3, first.Id, new[] { first }))),
            Task.Run(() => secondStore.SaveProfiles(new AgasAiProfilesDocument(3, second.Id, new[] { second }))));

        Assert.Equal(first.Id, firstStore.LoadProfiles().ActiveProfileId);
        Assert.Equal(second.Id, secondStore.LoadProfiles().ActiveProfileId);
    }

    [Fact]
    public void PostcommitUiSynchronization_RunsAfterSharedGateIsReleased()
    {
        using var home = new TemporaryHome();
        var store = new AgasAiSettingsStore(home.Path);
        AgasAiProfile profile = Profile("profile-a", "model-a");
        store.SaveProfiles(new AgasAiProfilesDocument(3, profile.Id, new[] { profile }));
        var bridge = new Bridge(profile.Id)
        {
            Synchronize = _ => Task.Run(store.LoadProfiles).Wait(TimeSpan.FromSeconds(2)),
        };
        var coordinator = new AgasAiSettingsRmwCoordinator(store, home.ProviderCatalog);

        AgasAiSettingsMutationResult result = coordinator.SetActiveModel("model-updated", bridge);

        Assert.True(result.Success);
        Assert.True(result.UiSynchronized);
    }

    public static IEnumerable<object[]> DirtyDrafts()
    {
        yield return new object[] { new AgasAiSettingsDraftState(true, false, false, false, false) };
        yield return new object[] { new AgasAiSettingsDraftState(false, true, false, false, false) };
        yield return new object[] { new AgasAiSettingsDraftState(false, false, true, false, false) };
        yield return new object[] { new AgasAiSettingsDraftState(false, false, false, true, false) };
        yield return new object[] { new AgasAiSettingsDraftState(false, false, false, false, true) };
    }

    [Theory]
    [MemberData(nameof(DirtyDrafts))]
    public void EachDraftClass_RejectsMutationBeforeDiskWrite(AgasAiSettingsDraftState drafts)
    {
        using var home = new TemporaryHome();
        var store = new AgasAiSettingsStore(home.Path);
        AgasAiProfile profile = Profile("profile-a", "model-a");
        store.SaveProfiles(new AgasAiProfilesDocument(3, profile.Id, new[] { profile }));
        byte[] before = File.ReadAllBytes(store.ProfilesPath);
        var coordinator = new AgasAiSettingsRmwCoordinator(store, home.ProviderCatalog);
        var bridge = new Bridge(profile.Id) { Drafts = drafts };

        AgasAiSettingsMutationResult result = coordinator.SetActiveModel("new-model", bridge);

        Assert.False(result.Success);
        Assert.Equal(AgasAiSettingsMutationFailure.Conflict, result.Failure);
        Assert.Equal(before, File.ReadAllBytes(store.ProfilesPath));
        Assert.Equal(0, bridge.AcquireCount);
    }

    [Fact]
    public void PrecommitGenerationDrift_LeavesPersistedBytesUnchangedAndDeletesTemp()
    {
        using var home = new TemporaryHome();
        var store = new AgasAiSettingsStore(home.Path);
        AgasAiProfile profile = Profile("profile-a", "model-a");
        store.SaveProfiles(new AgasAiProfilesDocument(3, profile.Id, new[] { profile }));
        byte[] before = File.ReadAllBytes(store.ProfilesPath);
        var bridge = new Bridge(profile.Id) { PreCommitValid = false };
        var coordinator = new AgasAiSettingsRmwCoordinator(store, home.ProviderCatalog);

        AgasAiSettingsMutationResult result = coordinator.SetActiveModel("new-model", bridge);

        Assert.False(result.Success);
        Assert.Equal(AgasAiSettingsMutationFailure.Conflict, result.Failure);
        Assert.Equal(before, File.ReadAllBytes(store.ProfilesPath));
        Assert.Empty(Directory.EnumerateFiles(home.Path, "*.tmp", SearchOption.TopDirectoryOnly));
        Assert.Equal(1, bridge.ReleaseCount);
    }

    [Fact]
    public void PrecommitActiveModelDrift_IsDetectedWithoutOverwritingExternalWinner()
    {
        using var home = new TemporaryHome();
        AgasAiProfile profile = Profile("profile-a", "legacy-model");
        var seed = new AgasAiSettingsStore(home.Path);
        seed.SaveProfiles(new AgasAiProfilesDocument(5, profile.Id, "initial-model", new[] { profile }));
        var externalWinner = new AgasAiProfilesDocument(5, profile.Id, "external-model", new[] { profile });
        var mutatingStore = new AgasAiSettingsStore(home.Path, _ => seed.SaveProfiles(externalWinner));
        var coordinator = new AgasAiSettingsRmwCoordinator(mutatingStore, home.ProviderCatalog);

        AgasAiSettingsMutationResult result = coordinator.SetActiveModel(
            "requested-model",
            new Bridge(profile.Id));

        Assert.False(result.Success);
        Assert.Equal(AgasAiSettingsMutationFailure.Conflict, result.Failure);
        Assert.Equal("external-model", seed.LoadProfiles().ActiveModelName);
        Assert.Equal(profile, Assert.Single(seed.LoadProfiles().Profiles));
        Assert.Empty(Directory.EnumerateFiles(home.Path, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void PostcommitGenerationDrift_KeepsPersistedChangeWithoutSynchronizingUi()
    {
        using var home = new TemporaryHome();
        var store = new AgasAiSettingsStore(home.Path);
        AgasAiProfile profile = Profile("profile-a", "model-a");
        store.SaveProfiles(new AgasAiProfilesDocument(3, profile.Id, new[] { profile }));
        var bridge = new Bridge(profile.Id) { PostCommitValid = false };
        var coordinator = new AgasAiSettingsRmwCoordinator(store, home.ProviderCatalog);

        AgasAiSettingsMutationResult result = coordinator.SetActiveModel("persisted-model", bridge);

        Assert.True(result.Success);
        Assert.True(result.Persisted);
        Assert.False(result.UiSynchronized);
        AgasAiProfilesDocument persisted = store.LoadProfiles();
        Assert.Equal("persisted-model", persisted.ActiveModelName);
        Assert.Equal("model-a", Assert.Single(persisted.Profiles).ModelName);
        Assert.Equal(1, bridge.ReleaseCount);
    }

    [Fact]
    public void ModelAliasContainingAnyStoredCredential_IsRejectedWithoutCredentialOrProfileWrite()
    {
        using var home = new TemporaryHome();
        var store = new AgasAiSettingsStore(home.Path);
        AgasAiProfile profile = Profile("profile-a", "model-a");
        store.SaveProfiles(new AgasAiProfilesDocument(3, profile.Id, new[] { profile }));
        store.SetCredential(profile.Id, "credential-value");
        byte[] profilesBefore = File.ReadAllBytes(store.ProfilesPath);
        var coordinator = new AgasAiSettingsRmwCoordinator(store, home.ProviderCatalog);

        AgasAiSettingsMutationResult result = coordinator.SetActiveModel(
            "relay-credential-value-alias",
            new Bridge(profile.Id));

        Assert.False(result.Success);
        Assert.Equal(AgasAiSettingsMutationFailure.ModelInvalid, result.Failure);
        Assert.Equal(profilesBefore, File.ReadAllBytes(store.ProfilesPath));
    }

    [Fact]
    public void CallerCancellationBeforeShortCommitStart_LeavesBytesUnchangedAndReleasesLease()
    {
        using var home = new TemporaryHome();
        var store = new AgasAiSettingsStore(home.Path);
        AgasAiProfile profile = Profile("profile-a", "model-a");
        store.SaveProfiles(new AgasAiProfilesDocument(3, profile.Id, new[] { profile }));
        byte[] before = File.ReadAllBytes(store.ProfilesPath);
        var bridge = new Bridge(profile.Id);
        var coordinator = new AgasAiSettingsRmwCoordinator(store, home.ProviderCatalog);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        AgasAiSettingsMutationResult result = coordinator.SetActiveModel(
            "new-model",
            bridge,
            commitGate: null,
            cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal(AgasAiSettingsMutationFailure.Canceled, result.Failure);
        Assert.Equal(before, File.ReadAllBytes(store.ProfilesPath));
        Assert.Empty(Directory.EnumerateFiles(home.Path, "*.tmp", SearchOption.TopDirectoryOnly));
        Assert.Equal(1, bridge.AcquireCount);
        Assert.Equal(1, bridge.ReleaseCount);
    }

    [Fact]
    public void TimeoutCancellationAfterShortCommitStart_CannotRewritePersistedResult()
    {
        using var home = new TemporaryHome();
        var store = new AgasAiSettingsStore(home.Path);
        AgasAiProfile profile = Profile("profile-a", "model-a");
        store.SaveProfiles(new AgasAiProfilesDocument(3, profile.Id, new[] { profile }));
        var bridge = new Bridge(profile.Id);
        using var cancellation = new CancellationTokenSource();
        var commitGate = new CancelAfterCommitStartGate(cancellation);
        var coordinator = new AgasAiSettingsRmwCoordinator(store, home.ProviderCatalog);

        AgasAiSettingsMutationResult result = coordinator.SetActiveModel(
            "committed-model",
            bridge,
            commitGate,
            cancellation.Token);

        Assert.True(result.Success);
        Assert.True(result.Persisted);
        Assert.True(result.UiSynchronized);
        Assert.Equal(1, commitGate.BeginCount);
        AgasAiProfilesDocument persisted = store.LoadProfiles();
        Assert.Equal("committed-model", persisted.ActiveModelName);
        Assert.Equal("model-a", Assert.Single(persisted.Profiles).ModelName);
        Assert.Empty(Directory.EnumerateFiles(home.Path, "*.tmp", SearchOption.TopDirectoryOnly));
        Assert.Equal(1, bridge.ReleaseCount);
    }

    private static AgasAiProfile Profile(
        string id,
        string model,
        string providerId = "vendor-provider") => new(
        id, id, providerId, model, "https://relay.example/v1/");

    private sealed class Bridge : IAgasAiSettingsMutationLeaseBridge
    {
        private readonly string? _activeProfileId;

        public Bridge(string? activeProfileId) => _activeProfileId = activeProfileId;

        public AgasAiSettingsDraftState Drafts { get; set; }
        public bool PreCommitValid { get; set; } = true;
        public bool PostCommitValid { get; set; } = true;
        public Func<AgasAiProfilesDocument, bool>? Synchronize { get; set; }
        public int AcquireCount { get; private set; }
        public int ReleaseCount { get; private set; }
        public AgasAiSettingsDraftState DraftState => Drafts;

        public bool TryAcquire(out AgasAiSettingsMutationLease? lease)
        {
            AcquireCount++;
            lease = new AgasAiSettingsMutationLease(
                Guid.NewGuid().ToString("N"), 1, "workspace", _activeProfileId);
            return true;
        }

        public bool ValidateBeforeCommit(
            AgasAiSettingsMutationLease lease,
            AgasAiSettingsPersistedFingerprint persistedFingerprint) => PreCommitValid;

        public bool SynchronizeAfterCommit(
            AgasAiSettingsMutationLease lease,
            AgasAiProfilesDocument committedDocument) =>
            Synchronize?.Invoke(committedDocument) ?? PostCommitValid;

        public void Release(AgasAiSettingsMutationLease lease) => ReleaseCount++;
    }

    private sealed class CancelAfterCommitStartGate : IAgasAiSettingsCommitGate
    {
        private readonly CancellationTokenSource _cancellation;

        public CancelAfterCommitStartGate(CancellationTokenSource cancellation) =>
            _cancellation = cancellation;

        public int BeginCount { get; private set; }

        public bool TryBeginAtomicReplace()
        {
            BeginCount++;
            _cancellation.Cancel();
            return true;
        }
    }

    private sealed class TemporaryHome : IDisposable
    {
        public TemporaryHome()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agas-settings-rmw", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            string providerDirectory = AgasAiSettingsPaths.ProvidersDirectory(Path);
            Directory.CreateDirectory(providerDirectory);
            File.WriteAllText(System.IO.Path.Combine(providerDirectory, "vendor.json"), """
            {
              "schemaVersion": 1,
              "id": "vendor-provider",
              "displayName": "Vendor",
              "adapterKind": "vendor-adapter",
              "defaultBaseUrl": "https://relay.example/v1/",
              "models": [{"modelName":"model-a","label":"Model A","recommended":true}],
              "capabilities": ["guidedRegionEdit","textToImage","regionEdit"],
              "credential": {"kind":"profileSettings","label":"API Key","settingsKey":"apiKey"}
            }
            """);
            File.WriteAllText(System.IO.Path.Combine(providerDirectory, "vendor-dot.json"), """
            {
              "schemaVersion": 1,
              "id": "vendor.provider",
              "displayName": "Vendor Dot",
              "adapterKind": "vendor-adapter",
              "defaultBaseUrl": "https://relay.example/v1/",
              "models": [{"modelName":"model-a","label":"Model A","recommended":true}],
              "capabilities": ["guidedRegionEdit","textToImage","regionEdit"],
              "credential": {"kind":"profileSettings","label":"API Key","settingsKey":"apiKey"}
            }
            """);
        }

        public string Path { get; }
        public string AliasedPath => System.IO.Path.Combine(Path, ".");
        public AgasAiProviderCatalog ProviderCatalog() =>
            AgasAiProviderCatalog.LoadFromDirectory(AgasAiSettingsPaths.ProvidersDirectory(Path));

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    private sealed class SymlinkAncestorHome : IDisposable
    {
        public SymlinkAncestorHome()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agas-settings-symlink", Guid.NewGuid().ToString("N"));
            string realParent = System.IO.Path.Combine(Root, "real-parent");
            AliasParent = System.IO.Path.Combine(Root, "alias-parent");
            RealHome = System.IO.Path.Combine(realParent, "settings-home");
            AliasHome = System.IO.Path.Combine(AliasParent, "settings-home");
            SecondRealHome = System.IO.Path.Combine(Root, "second-parent", "settings-home");
            Directory.CreateDirectory(RealHome);
            Directory.CreateDirectory(SecondRealHome);
            try
            {
                Directory.CreateSymbolicLink(AliasParent, realParent);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                throw Xunit.Sdk.SkipException.ForSkip($"Directory symlink creation is unavailable: {exception.Message}");
            }

            string providerDirectory = AgasAiSettingsPaths.ProvidersDirectory(RealHome);
            Directory.CreateDirectory(providerDirectory);
            File.WriteAllText(System.IO.Path.Combine(providerDirectory, "vendor.json"), """
            {
              "schemaVersion": 1,
              "id": "vendor-provider",
              "displayName": "Vendor",
              "adapterKind": "vendor-adapter",
              "defaultBaseUrl": "https://relay.example/v1/",
              "models": [{"modelName":"model-a","label":"Model A","recommended":true}],
              "capabilities": ["guidedRegionEdit","textToImage","regionEdit"],
              "credential": {"kind":"profileSettings","label":"API Key","settingsKey":"apiKey"}
            }
            """);
        }

        public string Root { get; }
        public string AliasParent { get; }
        public string RealHome { get; }
        public string AliasHome { get; }
        public string SecondRealHome { get; }
        public AgasAiProviderCatalog ProviderCatalog() =>
            AgasAiProviderCatalog.LoadFromDirectory(AgasAiSettingsPaths.ProvidersDirectory(RealHome));

        public void RetargetAliasToSecondParent()
        {
            Directory.Delete(AliasParent);
            Directory.CreateSymbolicLink(AliasParent, System.IO.Path.GetDirectoryName(SecondRealHome)!);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(AliasParent) || new DirectoryInfo(AliasParent).LinkTarget != null)
                {
                    Directory.Delete(AliasParent);
                }
                Directory.Delete(Root, recursive: true);
            }
            catch (DirectoryNotFoundException) { }
        }
    }
}
