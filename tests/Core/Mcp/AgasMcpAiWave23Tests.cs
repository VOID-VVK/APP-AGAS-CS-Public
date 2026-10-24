using System.Text.Json;
using System.Runtime.CompilerServices;
using AppAgasCs.Application;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Contracts.Runtime;
using AppAgasCs.Mcp.Runtime;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp;

public sealed class AgasMcpAiWave23Tests
{
    [Fact]
    public void SettingsFacade_AllFiveToolsReturnTypedSafeDeterministicData()
    {
        using var fixture = new SettingsFixture();
        AgasMcpToolResponse profilesResponse = fixture.Facade.Profiles(new AgasAiProfilesListInput());
        AgasMcpToolResponse modelsResponse = fixture.Facade.Models(new AgasAiModelsListInput());
        AgasMcpToolResponse diagnosticsResponse = fixture.Facade.Diagnostics(new AgasAiDiagnosticsInput());

        AgasMcpAiProfilesDataDto profiles = Assert.IsType<AgasMcpAiProfilesDataDto>(profilesResponse.Data);
        AgasMcpAiModelsDataDto models = Assert.IsType<AgasMcpAiModelsDataDto>(modelsResponse.Data);
        AgasMcpAiDiagnosticsDataDto diagnostics = Assert.IsType<AgasMcpAiDiagnosticsDataDto>(diagnosticsResponse.Data);
        Assert.Equal(new[] { "profile-a", "profile-b" }, profiles.Profiles.Select(row => row.ProfileId));
        Assert.Equal("profile-a", profiles.ActiveProfileId);
        Assert.Equal("liveUi", profiles.EffectiveCurrent?.Source);
        Assert.Equal(AgasAiProviderIds.AxonCatNewApi, profiles.EffectiveCurrent?.ProviderId);
        Assert.Equal(AgasAiUnifiedModelCatalog.ConnectionAdapterKind, profiles.EffectiveCurrent?.AdapterKind);
        Assert.Equal("[redacted]", profiles.EffectiveCurrent?.ModelName);
        Assert.Equal(AgasAiProviderIds.AxonCatNewApi, Assert.Single(models.Providers).ProviderId);
        Assert.Equal(
            new[]
            {
                "gpt-image-2",
                "gpt-image-2.5-flare",
                "gpt-image-2.5-sunburst",
                "grok-imagine-image-quality",
                "grok-imagine-image",
                "grok-imagine-image-2.0",
                "gemini-image",
            },
            Assert.Single(models.Providers).Models.Select(row => row.ModelName));
        Assert.Empty(diagnostics.Capabilities);
        Assert.False(diagnostics.NetworkChecked);
        Assert.Equal(AgasMcpErrorCodes.AiModelInvalid, Assert.Single(diagnostics.Issues).Code);

        var activateExecution = new AgasMcpCommitExecution(AgasMcpCommitPolicy.ShortAtomicCommit);
        AgasMcpToolResponse activationResponse = fixture.Facade.Activate(
            new AgasAiProfileActivateInput("profile-b"), activateExecution);
        AgasMcpAiProfileActivatedDataDto activation = Assert.IsType<AgasMcpAiProfileActivatedDataDto>(activationResponse.Data);
        Assert.Equal("profile-b", activation.ProfileId);
        Assert.True(activation.Persisted);
        Assert.False(activation.CredentialConfigured);
        Assert.Equal(AgasMcpCommitExecutionState.Completed, activateExecution.State);

        fixture.Bridge.ActiveProfileId = "profile-b";
        var modelExecution = new AgasMcpCommitExecution(AgasMcpCommitPolicy.ShortAtomicCommit);
        AgasMcpToolResponse setResponse = fixture.Facade.SetModel(
            new AgasAiModelSetInput("gemini-image"), modelExecution);
        AgasMcpAiModelSetDataDto set = Assert.IsType<AgasMcpAiModelSetDataDto>(setResponse.Data);
        Assert.Equal("gemini-image", set.ModelName);
        Assert.True(set.KnownPreset);
        Assert.True(set.Persisted);
        Assert.Equal(AgasMcpCommitExecutionState.Completed, modelExecution.State);
        Assert.Equal("gemini-image", fixture.Store.LoadProfiles().ActiveModelName);
        Assert.Equal("model-known", fixture.Store.LoadProfiles().Profiles.Single(item => item.Id == "profile-b").ModelName);
    }

    [Fact]
    public void SettingsMutation_CancellationWinsAtAtomicReplaceBoundaryAndLeavesBytesUnchanged()
    {
        using var fixture = new SettingsFixture();
        byte[] before = File.ReadAllBytes(fixture.Store.ProfilesPath);
        var execution = new AgasMcpCommitExecution(AgasMcpCommitPolicy.ShortAtomicCommit);
        Assert.True(execution.TryCancel());

        AgasMcpToolResponse response = fixture.Facade.Activate(
            new AgasAiProfileActivateInput("profile-b"),
            execution);

        Assert.False(response.Ok);
        Assert.Equal(AgasMcpErrorCodes.Stopped, response.Error?.Code);
        Assert.Equal(AgasMcpCommitExecutionState.Canceled, execution.State);
        Assert.Equal(before, File.ReadAllBytes(fixture.Store.ProfilesPath));
        Assert.Empty(Directory.EnumerateFiles(fixture.Store.HomeDirectory, "*.tmp"));
    }

    [Fact]
    public void SettingsFacade_MutationAndDiagnosticFailuresUseFrozenCodesWithoutWrites()
    {
        using var fixture = new SettingsFixture();
        byte[] before = File.ReadAllBytes(fixture.Store.ProfilesPath);

        AgasMcpToolResponse missing = fixture.Facade.Activate(new AgasAiProfileActivateInput("missing"));
        AgasMcpToolResponse invalidModel = fixture.Facade.SetModel(
            new AgasAiModelSetInput("alias-secret-api-key"));
        AgasMcpToolResponse missingDiagnostic = fixture.Facade.Diagnostics(
            new AgasAiDiagnosticsInput("missing"));

        Assert.Equal(AgasMcpErrorCodes.AiProfileNotFound, missing.Error?.Code);
        Assert.Equal(AgasMcpErrorCodes.AiModelInvalid, invalidModel.Error?.Code);
        Assert.Equal(AgasMcpErrorCodes.AiProfileNotFound, missingDiagnostic.Error?.Code);
        Assert.All(new[] { missing, invalidModel, missingDiagnostic }, response => Assert.Null(response.Data));
        Assert.Equal(before, File.ReadAllBytes(fixture.Store.ProfilesPath));

        fixture.Bridge.Drafts = new AgasAiSettingsDraftState(true, false, false, false, false);
        AgasMcpToolResponse conflict = fixture.Facade.Activate(new AgasAiProfileActivateInput("profile-b"));
        Assert.Equal(AgasMcpErrorCodes.AiSettingsConflict, conflict.Error?.Code);
        Assert.Equal(before, File.ReadAllBytes(fixture.Store.ProfilesPath));
    }

    [Fact]
    public void JobStoreFreezesActualIdentityAndStatusUsesFiveClosedTypedVariants()
    {
        using var store = new AgasMcpAiJobStore();
        AgasMcpAiProjectScope scope = AgasMcpAiProjectScope.Create(
            Path.Combine(Path.GetTempPath(), "agas-wave23-ai", "identity"),
            "project-1");
        var execution = new AgasMcpAiExecutionIdentity(
            "profile-1", "vendor.provider", "vendor.adapter-v2", "model-original");

        AgasMcpAiJobSnapshot queued = store.CreateQueued(scope, "GuidedRegionEdit", execution);
        Assert.Equal(execution, queued.Execution);
        Assert.IsType<AgasMcpAiJobQueuedDto>(AgasMcpAiFacade.JobData(queued));
        Assert.True(store.TryMarkRunning(queued.JobId, out AgasMcpAiJobSnapshot? running));
        Assert.IsType<AgasMcpAiJobRunningDto>(AgasMcpAiFacade.JobData(running!));
        Assert.True(store.TrySucceed(
            queued.JobId,
            new AgasMcpAiJobResultMetadata(".agas/generated/result.png", null, "Generated."),
            new object(),
            "MaskedEdit",
            out AgasMcpAiJobSnapshot? succeeded));
        AgasMcpAiJobSucceededDto succeededDto = Assert.IsType<AgasMcpAiJobSucceededDto>(AgasMcpAiFacade.JobData(succeeded!));
        Assert.Equal("vendor.provider", succeededDto.Execution.ProviderId);
        Assert.Equal("vendor.adapter-v2", succeededDto.Execution.AdapterKind);
        Assert.Equal("model-original", succeededDto.Execution.ModelName);
        Assert.Equal("MaskedEdit", succeededDto.EffectiveOperation);
        Assert.False(succeededDto.AlreadyApplied);

        AgasMcpOutputContract contract = AgasMcpOutputSchemaBuilder.Build(new[]
        {
            typeof(AgasMcpAiJobDto),
            typeof(AgasMcpAiStatusListDataDto),
        });
        Assert.True(AgasMcpOutputContractRegistry.Validate(
            contract,
            AgasMcpToolResponse.Success(false, false, succeededDto)).Success);
        Assert.True(AgasMcpOutputContractRegistry.Validate(
            contract,
            AgasMcpToolResponse.Success(false, false, new AgasMcpAiStatusListDataDto(new AgasMcpAiJobDto[] { succeededDto }))).Success);
        string json = JsonSerializer.Serialize(
            new AgasMcpAiStatusListDataDto(new AgasMcpAiJobDto[] { succeededDto }),
            AgasMcpOutputContractRegistry.SerializerOptions);
        Assert.DoesNotContain("$type", json, StringComparison.Ordinal);
        Assert.Contains("\"resultRelativePath\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedStatus_WithLegacyRegionEditOperation_IsNormalizedToAuthoritativeGuidedVariant()
    {
        using var store = new AgasMcpAiJobStore();
        AgasMcpAiProjectScope scope = AgasMcpAiProjectScope.Create(
            Path.Combine(Path.GetTempPath(), "agas-wave23-ai", "failed-region-edit"),
            "project-1");
        AgasMcpAiJobSnapshot queued = store.CreateQueued(scope, "RegionEdit", new AgasMcpAiExecutionIdentity(
            "profile-1", "vendor.provider", "vendor.adapter-v2", "model-1"));
        Assert.Equal("GuidedRegionEdit", queued.EffectiveOperation);
        Assert.True(store.TryMarkRunning(queued.JobId, out _));
        Assert.True(store.TryFail(
            queued.JobId,
            new AgasMcpAiJobFailure(
                AgasMcpErrorCodes.AiProviderFailed,
                "The image provider request failed.",
                "Check the configured profile and retry."),
            out AgasMcpAiJobSnapshot? failed));

        AgasMcpAiJobFailedDto failedDto = Assert.IsType<AgasMcpAiJobFailedDto>(AgasMcpAiFacade.JobData(failed!));
        Assert.Equal("GuidedRegionEdit", failedDto.EffectiveOperation);
        Assert.Equal("Check the configured profile and retry.", failedDto.Hint);
        AgasMcpToolDescriptor status = AgasMcpToolDescriptorRegistry.Get(AgasMcpToolNames.AiStatus);
        AgasMcpOutputValidationResult validation = AgasMcpOutputContractRegistry.Validate(
            status.OutputContract!,
            AgasMcpToolResponse.Success(saved: true, dirty: false, failedDto));
        Assert.True(validation.Success, validation.ContractFailure);
    }

    [Fact]
    public void RunnerDiagnosticProjectionKeepsAllowListAndNeverReturnsRawExceptionFields()
    {
        var diagnostic = new AiGenerationSafeDiagnostic(
            "diag-safe",
            AiGenerationErrorCode.ProviderRequestFailed,
            AiGenerationFailurePhase.ProviderResponse,
            AiGenerationFailureCategory.ProviderRejected,
            "profile-secret",
            "adapter-secret",
            "model-secret",
            "private.example",
            401,
            "UNAUTHENTICATED",
            "Raw.Exception",
            "Raw.Inner");
        var exception = new AiGenerationException(
            AiGenerationErrorCode.ProviderRequestFailed,
            "safe",
            diagnostic);

        AgasMcpAiJobFailure failure = AgasMcpAiFacade.MapRunnerFailure(exception);

        Assert.Equal("diag-safe", failure.Diagnostic?.DiagnosticId);
        Assert.Equal(401, failure.Diagnostic?.HttpStatus);
        Assert.Equal("UNAUTHENTICATED", failure.Diagnostic?.UpstreamCode);
        string text = JsonSerializer.Serialize(failure);
        Assert.DoesNotContain("private.example", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Raw.Exception", text, StringComparison.Ordinal);
        Assert.DoesNotContain("profile-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("adapter-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("model-secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SucceededDtoConstructorEnforcesAppliedConditionalFields()
    {
        var execution = new AgasMcpAiExecutionIdentityDto(
            "profile-1", "vendor.provider", "adapter", "model");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => new AgasMcpAiJobSucceededDto(
            "aijob-00000000000000000000000000000000",
            "Succeeded",
            "TextToImage",
            execution,
            now,
            alreadyApplied: false,
            now,
            now,
            "assets/result.png",
            "done",
            appliedRelativePath: "assets/applied.png",
            appliedAtUtc: now));
        Assert.Throws<ArgumentException>(() => new AgasMcpAiJobSucceededDto(
            "aijob-00000000000000000000000000000000",
            "Succeeded",
            "TextToImage",
            execution,
            now,
            alreadyApplied: true,
            now,
            now,
            "assets/result.png",
            "done"));
    }

    [Fact]
    public void StatusHandlerReturnsDirectListAndSucceededAppliedVariantsWithFrozenIdentity()
    {
        string root = Path.Combine(Path.GetTempPath(), "agas-ai-status-handler", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = (AgasProjectResource)RuntimeHelpers.GetUninitializedObject(typeof(AgasProjectResource));
            project.ProjectId = "project-status";
            project.ResourceVersion = AgasProjectResource.CurrentResourceVersion;
            var layout = (WorkspaceLayoutResource)RuntimeHelpers.GetUninitializedObject(typeof(WorkspaceLayoutResource));
            var session = new ProjectSession();
            session.Reset(root);
            session.AcceptProject(project);
            var contexts = new AgasMcpProjectContextProvider();
            contexts.RegisterContext(new AgasMcpProjectContext(session, layout));
            using var store = new AgasMcpAiJobStore();
            var coordinator = new AgasAiGenerationCoordinator();
            using var scheduler = new AgasMcpAiJobScheduler(coordinator, store);
            var facade = new AgasMcpAiFacade(
                contexts,
                new AgasMcpDirtyState(),
                store,
                scheduler,
                new NeverRunner());
            AgasMcpAiProjectScope scope = AgasMcpAiProjectScope.Create(root, project.ProjectId);
            var identity = new AgasMcpAiExecutionIdentity(
                "profile-1", "vendor.provider", "vendor.adapter-v2", "frozen-model");
            AgasMcpAiJobSnapshot job = store.CreateQueued(scope, "TextToImage", identity);
            Assert.True(store.TryMarkRunning(job.JobId, out _));
            Assert.True(store.TrySucceed(
                job.JobId,
                new AgasMcpAiJobResultMetadata("assets/generated/result.png", null, "done"),
                applicationState: null,
                effectiveOperation: "TextToImage",
                snapshot: out _));

            AgasMcpToolResponse direct = facade.Status(new AgasAiStatusInput(job.JobId));
            AgasMcpToolResponse list = facade.Status(new AgasAiStatusInput(Limit: 10));
            Assert.IsType<AgasMcpAiJobSucceededDto>(direct.Data);
            AgasMcpAiStatusListDataDto listData = Assert.IsType<AgasMcpAiStatusListDataDto>(list.Data);
            Assert.IsType<AgasMcpAiJobSucceededDto>(Assert.Single(listData.Jobs));
            Assert.Equal("vendor.provider", Assert.IsType<AgasMcpAiJobSucceededDto>(direct.Data).Execution.ProviderId);

            Assert.True(store.TryMarkApplied(job.JobId, "assets/generated/applied.png", "applied", out _));
            AgasMcpAiJobSucceededDto applied = Assert.IsType<AgasMcpAiJobSucceededDto>(
                facade.Status(new AgasAiStatusInput(job.JobId)).Data);
            Assert.True(applied.AlreadyApplied);
            Assert.Equal("assets/generated/applied.png", applied.AppliedRelativePath);
            Assert.NotNull(applied.AppliedAtUtc);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class SettingsFixture : IDisposable
    {
        private readonly string _root;

        public SettingsFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "agas-mcp-ai-wave23", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            string providers = AgasAiSettingsPaths.ProvidersDirectory(_root);
            Directory.CreateDirectory(providers);
            File.WriteAllText(Path.Combine(providers, "vendor.json"), """
            {
              "schemaVersion": 1,
              "id": "vendor.provider",
              "displayName": "Vendor",
              "adapterKind": "vendor.adapter-v2",
              "defaultBaseUrl": "https://relay.example/v1/",
              "models": [
                {"modelName":"model-other","label":"Other","recommended":false},
                {"modelName":"model-known","label":"Known","recommended":true}
              ],
              "capabilities": ["guidedRegionEdit","textToImage","regionEdit"],
              "credential": {"kind":"profileSettings","label":"API Key","settingsKey":"apiKey"}
            }
            """);
            Store = new AgasAiSettingsStore(_root);
            var first = new AgasAiProfile("profile-a", "First", "vendor.provider", "alias-secret-api-key", "https://relay.example/v1/");
            var second = new AgasAiProfile("profile-b", "Second", "vendor.provider", "model-known", "https://relay.example/v1/");
            Store.SaveProfiles(new AgasAiProfilesDocument(3, first.Id, new[] { second, first }));
            Store.SetCredential(first.Id, "secret-api-key");
            Bridge = new Bridge(first.Id);
            var service = new AgasAiProfileSettingsService(
                Store,
                () => AgasAiProviderCatalog.LoadFromDirectory(providers));
            Facade = new AgasMcpAiSettingsFacade(
                service,
                Bridge,
                new AgasMcpDirtyState(),
                () => new AgasAiEffectiveProfileSelection(
                    "profile-a", "vendor.provider", "vendor.adapter-v2", "alias-secret-api-key"));
        }

        public AgasAiSettingsStore Store { get; }
        public Bridge Bridge { get; }
        public AgasMcpAiSettingsFacade Facade { get; }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    private sealed class NeverRunner : IAiGenerationRunner
    {
        public Task<AiGenerationResult> GenerateAsync(
            AiGenerationProviderProfile profile,
            AiGenerationRequest request,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Runner is not used.");
    }

    private sealed class Bridge : IAgasAiSettingsMutationLeaseBridge
    {
        public Bridge(string? activeProfileId) => ActiveProfileId = activeProfileId;

        public string? ActiveProfileId { get; set; }
        public AgasAiSettingsDraftState Drafts { get; set; }
        public AgasAiSettingsDraftState DraftState => Drafts;

        public bool TryAcquire(out AgasAiSettingsMutationLease? lease)
        {
            lease = new AgasAiSettingsMutationLease(
                Guid.NewGuid().ToString("N"), 1, "workspace", ActiveProfileId);
            return true;
        }

        public bool ValidateBeforeCommit(
            AgasAiSettingsMutationLease lease,
            AgasAiSettingsPersistedFingerprint persistedFingerprint) => true;

        public bool SynchronizeAfterCommit(
            AgasAiSettingsMutationLease lease,
            AgasAiProfilesDocument committedDocument) => true;

        public void Release(AgasAiSettingsMutationLease lease) { }
    }
}
