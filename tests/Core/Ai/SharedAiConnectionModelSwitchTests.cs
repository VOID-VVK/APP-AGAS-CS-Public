using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppAgasCs.Application;
using AppAgasCs.Application.Contracts;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Runtime;
using Godot;
using Xunit;
using NetHttpClient = System.Net.Http.HttpClient;

namespace AppAgasCs.Tests.Core.Ai;

public sealed class SharedAiConnectionModelSwitchTests
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

    private static readonly byte[] GeneratedPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==");

    [Fact]
    public async Task OneConnectionSwitchesAllUnifiedModelsWithoutChangingEndpointOrCredential()
    {
        using var home = new TemporaryHome();
        const string profileId = "shared-connection";
        const string credential = "synthetic-shared-credential";
        const string baseUrl = "https://relay.example/v1/";
        var store = new AgasAiSettingsStore(home.Path);
        var original = new AgasAiProfile(
            profileId,
            "Shared relay",
            "legacy.provider-that-must-not-route",
            "legacy-model-that-must-not-route",
            baseUrl);
        store.SaveProfiles(new AgasAiProfilesDocument(
            AgasAiProfilesDocument.CurrentResourceVersion,
            profileId,
            UnifiedModels[0],
            new[] { original }));
        store.SetCredential(profileId, credential);

        AgasAiProviderCatalog catalog = AgasAiProviderCatalog.Load(home.Path);
        AgasAiProfilesDocument ensured = AgasAiNewApiSetup.EnsureProfile(store, catalog);
        Assert.Equal(profileId, ensured.ActiveProfileId);
        using var models = new ImageModelDiscoveryFixture();
        models.Prime(store);
        Assert.All(UnifiedModels, model => Assert.True(models.Discovery
            .GetSnapshot(original with { ApiKey = credential }).TryGet(model, out _)));

        var bridge = new StableBridge(profileId);
        var facade = new AgasMcpAiSettingsFacade(
            new AgasAiProfileSettingsService(store, () => AgasAiProviderCatalog.Load(home.Path), models.Discovery),
            bridge,
            new AgasMcpDirtyState());
        var handler = new RecordingHandler(credential);
        using var client = new NetHttpClient(handler);
        var service = new AiGenerationService(client, models.Registry(), imageDownloadClient: client);

        foreach (string model in UnifiedModels)
        {
            AgasMcpToolResponse setResponse = facade.SetModel(new AgasAiModelSetInput(model));
            AgasMcpAiModelSetDataDto set = Assert.IsType<AgasMcpAiModelSetDataDto>(setResponse.Data);
            Assert.True(setResponse.Ok);
            Assert.Equal(profileId, set.ProfileId);
            Assert.Equal(AgasAiProviderIds.AxonCatNewApi, set.ProviderId);
            Assert.Equal(model, set.ModelName);
            Assert.True(set.KnownPreset);
            Assert.True(set.CredentialConfigured);

            AgasMcpToolResponse diagnosticResponse = facade.Diagnostics(new AgasAiDiagnosticsInput(profileId));
            AgasMcpAiDiagnosticsDataDto diagnostic =
                Assert.IsType<AgasMcpAiDiagnosticsDataDto>(diagnosticResponse.Data);
            Assert.True(diagnosticResponse.Ok);
            Assert.Equal(profileId, diagnostic.ProfileId);
            Assert.Equal(AgasAiProviderIds.AxonCatNewApi, diagnostic.ProviderId);
            Assert.Equal(model, diagnostic.ModelName);
            Assert.True(diagnostic.CredentialConfigured);
            Assert.True(diagnostic.Ready);
            Assert.Empty(diagnostic.Issues);

            AgasAiProfilesDocument persistedDocument = store.LoadProfiles();
            AgasAiProfile persisted = persistedDocument.Profiles.Single(item => item.Id == profileId);
            Assert.Equal(model, persistedDocument.ActiveModelName);
            Assert.Equal(baseUrl, persisted.BaseUrl);
            Assert.Equal("legacy.provider-that-must-not-route", persisted.ProviderId);
            Assert.Equal("legacy-model-that-must-not-route", persisted.ModelName);
            Assert.True(CredentialMatches(store.GetCredential(profileId), credential),
                "Switching models must retain the credential on the same connection.");
            AiGenerationProviderProfile runtimeProfile = AgasAiConnectionModelResolver.Resolve(
                persisted with { ApiKey = store.GetCredential(profileId) },
                persistedDocument.ActiveModelName, models.Discovery);
            Assert.Equal(profileId, runtimeProfile.Id);
            Assert.Equal(AgasAiProviderIds.AxonCatNewApi, runtimeProfile.ProviderId);
            Assert.Equal(AgasAiUnifiedModelCatalog.ConnectionAdapterKind, runtimeProfile.AdapterKind);
            Assert.Equal(model, runtimeProfile.ModelName);
            await service.GenerateAsync(
                runtimeProfile,
                new AiGenerationRequest(home.Path, "same prompt", 1024, 1024));
        }

        Assert.Equal(UnifiedModels.Length, handler.Requests.Count);
        Assert.Equal(UnifiedModels, handler.Requests.Select(request => request.Model));
        Assert.All(handler.Requests, request =>
        {
            string expectedEndpoint = request.Model == "gemini-image"
                ? "https://relay.example/v1/chat/completions"
                : "https://relay.example/v1/images/generations";
            Assert.Equal(expectedEndpoint, request.Endpoint.AbsoluteUri);
            Assert.True(request.CredentialMatched, "Every model request must use the connection credential.");
            Assert.True(request.UsesBearerScheme);
            Assert.False(request.PayloadContainsCredential);
        });
    }

    [Fact]
    public void Version4DocumentMigratesActiveProfileModelIntoIndependentActiveModel()
    {
        using var home = new TemporaryHome();
        const string credential = "synthetic-v4-credential";
        Directory.CreateDirectory(home.Path);
        string path = AgasAiSettingsPaths.ProfilesFile(home.Path);
        File.WriteAllText(path, $$"""
        {
          "resourceVersion": 4,
          "activeProfileId": "legacy-b",
          "profiles": [
            {
              "id": "legacy-a",
              "name": "Legacy A",
              "providerId": "gpt",
              "modelName": "gpt-image-2",
              "baseUrl": "https://a.example/v1"
            },
            {
              "id": "legacy-b",
              "name": "Legacy B",
              "providerId": "gemini-compatible",
              "modelName": "gpt-image-2.5-sunburst",
              "baseUrl": "https://b.example/v1",
              "apiKey": "{{credential}}"
            }
          ]
        }
        """);

        var store = new AgasAiSettingsStore(home.Path);
        AgasAiProfilesDocument migrated = store.LoadProfiles();

        Assert.Equal(AgasAiProfilesDocument.CurrentResourceVersion, migrated.ResourceVersion);
        Assert.Equal("legacy-b", migrated.ActiveProfileId);
        Assert.Equal("gpt-image-2.5-sunburst", migrated.ActiveModelName);
        Assert.Equal(new[] { "legacy-a", "legacy-b" }, migrated.Profiles.Select(profile => profile.Id));
        Assert.Equal("gemini-compatible", migrated.Profiles[1].ProviderId);
        Assert.Equal("gpt-image-2.5-sunburst", migrated.Profiles[1].ModelName);
        Assert.True(CredentialMatches(store.GetCredential("legacy-b"), credential));
        using JsonDocument rewritten = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(AgasAiProfilesDocument.CurrentResourceVersion,
            rewritten.RootElement.GetProperty("resourceVersion").GetInt32());
        Assert.Equal("gpt-image-2.5-sunburst",
            rewritten.RootElement.GetProperty("activeModelName").GetString());
    }

    [Fact]
    public void ActivatingConnectionAndSettingModelChangeOnlyTheirIndependentDocumentFields()
    {
        using var home = new TemporaryHome();
        var first = new AgasAiProfile("connection-a", "A", "old-gpt", "old-model-a", "https://a.example/v1");
        var second = new AgasAiProfile("connection-b", "B", "old-gemini", "old-model-b", "https://b.example/v1");
        var store = new AgasAiSettingsStore(home.Path);
        store.SaveProfiles(new AgasAiProfilesDocument(
            AgasAiProfilesDocument.CurrentResourceVersion,
            first.Id,
            "gpt-image-2.5-flare",
            new[] { first, second }));
        store.SetCredential(first.Id, "credential-a");
        store.SetCredential(second.Id, "credential-b");
        using var models = new ImageModelDiscoveryFixture();
        models.Prime(store);
        var bridge = new StableBridge(first.Id);
        var facade = new AgasMcpAiSettingsFacade(
            new AgasAiProfileSettingsService(store, discovery: models.Discovery), bridge, new AgasMcpDirtyState());

        AgasMcpToolResponse activate = facade.Activate(new AgasAiProfileActivateInput(second.Id));
        Assert.True(activate.Ok);
        AgasAiProfilesDocument afterActivate = store.LoadProfiles();
        Assert.Equal(second.Id, afterActivate.ActiveProfileId);
        Assert.Equal("gpt-image-2.5-flare", afterActivate.ActiveModelName);
        AssertConnectionFieldsUnchanged(afterActivate.Profiles[0], first);
        AssertConnectionFieldsUnchanged(afterActivate.Profiles[1], second);

        bridge.ActiveProfileId = second.Id;
        AgasMcpToolResponse setModel = facade.SetModel(new AgasAiModelSetInput("gemini-image"));
        Assert.True(setModel.Ok);
        AgasAiProfilesDocument afterModel = store.LoadProfiles();
        Assert.Equal(second.Id, afterModel.ActiveProfileId);
        Assert.Equal("gemini-image", afterModel.ActiveModelName);
        AssertConnectionFieldsUnchanged(afterModel.Profiles[0], first);
        AssertConnectionFieldsUnchanged(afterModel.Profiles[1], second);
        Assert.True(CredentialMatches(store.GetCredential(first.Id), "credential-a"));
        Assert.True(CredentialMatches(store.GetCredential(second.Id), "credential-b"));
    }

    [Fact]
    public async Task ConcurrentMcpModelSetThenProfileActivatePersistBothWinningFields()
    {
        using var home = new TemporaryHome();
        var first = new AgasAiProfile("connection-a", "A", "old-provider-a", "old-model-a", "https://a.example/v1");
        var second = new AgasAiProfile("connection-b", "B", "old-provider-b", "old-model-b", "https://b.example/v1");
        var seed = new AgasAiSettingsStore(home.Path);
        seed.SaveProfiles(new AgasAiProfilesDocument(
            AgasAiProfilesDocument.CurrentResourceVersion,
            first.Id,
            "gpt-image-2",
            new[] { first, second }));
        seed.SetCredential(first.Id, "fixture-concurrent-key");
        using var models = new ImageModelDiscoveryFixture();
        models.Prime(seed);
        using var modelBeforeReplace = new ManualResetEventSlim();
        using var allowModelReplace = new ManualResetEventSlim();
        using var activationAttempted = new ManualResetEventSlim();
        var modelStore = new AgasAiSettingsStore(home.Path, _ =>
        {
            modelBeforeReplace.Set();
            Assert.True(allowModelReplace.Wait(TimeSpan.FromSeconds(5)));
        });
        var activateStore = new AgasAiSettingsStore(home.Path);
        var modelFacade = new AgasMcpAiSettingsFacade(
            new AgasAiProfileSettingsService(modelStore, discovery: models.Discovery),
            new StableBridge(first.Id),
            new AgasMcpDirtyState());
        var activateFacade = new AgasMcpAiSettingsFacade(
            new AgasAiProfileSettingsService(activateStore, discovery: models.Discovery),
            new StableBridge(first.Id),
            new AgasMcpDirtyState());

        Task<AgasMcpToolResponse> modelTask = Task.Run(() =>
            modelFacade.SetModel(new AgasAiModelSetInput("gemini-image")));
        Assert.True(modelBeforeReplace.Wait(TimeSpan.FromSeconds(5)));
        Task<AgasMcpToolResponse> activateTask = Task.Run(() =>
        {
            activationAttempted.Set();
            return activateFacade.Activate(new AgasAiProfileActivateInput(second.Id));
        });
        Assert.True(activationAttempted.Wait(TimeSpan.FromSeconds(5)));
        allowModelReplace.Set();

        AgasMcpToolResponse[] responses = await Task.WhenAll(modelTask, activateTask);
        Assert.All(responses, response => Assert.True(response.Ok, response.Error?.Code));
        AgasAiProfilesDocument persisted = seed.LoadProfiles();
        Assert.Equal(second.Id, persisted.ActiveProfileId);
        Assert.Equal("gemini-image", persisted.ActiveModelName);
        Assert.Equal(new[] { first with { ApiKey = "fixture-concurrent-key" }, second }, persisted.Profiles);
        Assert.Empty(Directory.EnumerateFiles(home.Path, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData("grok-imagine-image-quality")]
    [InlineData("grok-imagine-image")]
    [InlineData("grok-imagine-image-2.0")]
    public async Task GrokFullRegionPreparationDowngradesToTextToImageAndUsesSharedConnection(
        string modelName)
    {
        using var home = new TemporaryHome();
        const string credential = "synthetic-grok-credential";
        var connection = new AgasAiProfile(
            "grok-connection",
            "Grok relay",
            "legacy-provider",
            "legacy-model",
            "https://relay.example/v1",
            credential);
        using var models = new ImageModelDiscoveryFixture();
        models.Prime(connection);
        AiGenerationProviderProfile runtime = AgasAiConnectionModelResolver.Resolve(connection, modelName, models.Discovery);
        AiGenerationPreparationResult prepared = new AiGenerationRequestPreparationService().Prepare(
            PreparationInput(runtime, home.Path),
            Capture(CanvasPatchGenerationMode.FullRegionGeneration));

        Assert.True(prepared.Success, prepared.Message);
        Assert.NotNull(prepared.Request);
        Assert.Equal(AiGenerationOperation.TextToImage, prepared.Request!.Operation);
        Assert.Empty(prepared.Request.ReferenceImages);
        Assert.Null(prepared.Request.EditInput);

        var handler = new RecordingHandler(credential);
        using var client = new NetHttpClient(handler);
        await new AiGenerationService(client, models.Registry(), imageDownloadClient: client).GenerateAsync(
            runtime,
            prepared.Request);

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal("https://relay.example/v1/images/generations", request.Endpoint.AbsoluteUri);
        Assert.Equal(modelName, request.Model);
        Assert.True(request.UsesBearerScheme);
        Assert.True(request.CredentialMatched);
    }

    [Theory]
    [InlineData("grok-imagine-image-quality")]
    [InlineData("grok-imagine-image")]
    [InlineData("grok-imagine-image-2.0")]
    public void GrokRegionReplacementFailsBeforeAnyHttpRequest(string modelName)
    {
        using var home = new TemporaryHome();
        const string credential = "synthetic-grok-credential";
        var connection = new AgasAiProfile(
                "grok-connection",
                "Grok relay",
                "legacy-provider",
                "legacy-model",
                "https://relay.example/v1",
                credential);
        using var models = new ImageModelDiscoveryFixture();
        models.Prime(connection);
        AiGenerationProviderProfile runtime = AgasAiConnectionModelResolver.Resolve(connection,
            modelName, models.Discovery);
        var handler = new RecordingHandler(credential);
        AiGenerationPreparationResult prepared = new AiGenerationRequestPreparationService().Prepare(
            PreparationInput(runtime, home.Path),
            Capture(CanvasPatchGenerationMode.RegionReplacement));

        Assert.False(prepared.Success);
        Assert.Null(prepared.Request);
        Assert.Null(prepared.PatchCapture);
        Assert.Contains("W", prepared.Message);
        Assert.Empty(handler.Requests);
    }

    private static AiGenerationPreparationInput PreparationInput(
        AiGenerationProviderProfile profile,
        string projectRoot) => new(
        profile,
        new AiGenerationCanvasContext(
            projectRoot,
            "project",
            "scene",
            "layer",
            HasSelectedImage: false,
            SelectedImageId: null,
            CanEditCurrentLayer: true,
            GridSizePx: 1,
            RegionMetadata: null),
        AiGenerationTarget.AddToCurrentLayer,
        "generate image");

    private static CanvasPatchCapture Capture(CanvasPatchGenerationMode mode) => new(
        new CanvasGridRegion(0, 0, 1, 1),
        new CanvasGridRegion(0, 0, 1, 1),
        1,
        new Rect2I(0, 0, 1, 1),
        new Rect2I(0, 0, 1, 1),
        new Rect2I(0, 0, 1, 1),
        GeneratedPng,
        GeneratedPng,
        GeneratedPng,
        "fingerprint",
        targetItem: null,
        mode);

    private static bool CredentialMatches(string? actual, string expected)
    {
        byte[] actualBytes = Encoding.UTF8.GetBytes(actual ?? string.Empty);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    private static void AssertConnectionFieldsUnchanged(AgasAiProfile actual, AgasAiProfile expected)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.ProviderId, actual.ProviderId);
        Assert.Equal(expected.ModelName, actual.ModelName);
        Assert.Equal(expected.BaseUrl, actual.BaseUrl);
    }

    private sealed class RecordingHandler(string credential) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string payloadText = await request.Content!.ReadAsStringAsync(cancellationToken);
            JsonObject payload = JsonNode.Parse(payloadText)?.AsObject()
                ?? throw new InvalidDataException("Expected an object request payload.");
            string model = payload["model"]?.GetValue<string>()
                ?? throw new InvalidDataException("Expected a model in the request payload.");
            payload["model"] = "<model>";
            Requests.Add(new RecordedRequest(
                request.RequestUri!,
                model,
                payload.ToJsonString(),
                string.Equals(request.Headers.Authorization?.Scheme, "Bearer", StringComparison.Ordinal),
                CredentialMatches(request.Headers.Authorization?.Parameter, credential),
                payloadText.Contains(credential, StringComparison.Ordinal)));
            object body = request.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal)
                ? new
                {
                    choices = new[]
                    {
                        new { message = new { content = "![result](data:image/png;base64," + Convert.ToBase64String(GeneratedPng) + ")" } },
                    },
                }
                : new
                {
                    data = new[] { new { b64_json = Convert.ToBase64String(GeneratedPng) } },
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(body),
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed record RecordedRequest(
        Uri Endpoint,
        string Model,
        string PayloadWithoutModel,
        bool UsesBearerScheme,
        bool CredentialMatched,
        bool PayloadContainsCredential);

    private sealed class StableBridge(string activeProfileId) : IAgasAiSettingsMutationLeaseBridge
    {
        public string ActiveProfileId { get; set; } = activeProfileId;
        public AgasAiSettingsDraftState DraftState => default;

        public bool TryAcquire(out AgasAiSettingsMutationLease? lease)
        {
            lease = new AgasAiSettingsMutationLease(
                Guid.NewGuid().ToString("N"),
                1,
                "shared-model-switch",
                ActiveProfileId);
            return true;
        }

        public bool ValidateBeforeCommit(
            AgasAiSettingsMutationLease lease,
            AgasAiSettingsPersistedFingerprint persistedFingerprint) => true;

        public bool SynchronizeAfterCommit(
            AgasAiSettingsMutationLease lease,
            AgasAiProfilesDocument committedDocument) => true;

        public void Release(AgasAiSettingsMutationLease lease)
        {
        }
    }

    private sealed class TemporaryHome : IDisposable
    {
        public TemporaryHome()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "agas-shared-connection-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
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
