using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AGAS.UI;
using AGAS.UI.Controllers;
using Agas.Features.Canvas;
using AppAgasCs.Application;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Features.Ai.Workspace;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Runtime;
using Godot;
using NetHttpClient = System.Net.Http.HttpClient;

public partial class McpHttpRuntimeAcceptance : Node
{
    private const string ProtocolVersion = "2025-11-25";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly List<string> _tempRoots = new();
    private readonly List<Exception> _asyncErrors = new();
    private int _nextDynamicRpcId = 1000;
    private string _testEndpoint = string.Empty;

    public override async void _Ready()
    {
        int exitCode = 0;
        UnhandledExceptionEventHandler unhandledHandler = (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                _asyncErrors.Add(ex);
            }
        };
        EventHandler<UnobservedTaskExceptionEventArgs> taskHandler = (_, args) =>
        {
            _asyncErrors.Add(args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += unhandledHandler;
        TaskScheduler.UnobservedTaskException += taskHandler;

        try
        {
            await RunAllAsync();
            if (_asyncErrors.Count > 0)
            {
                throw new InvalidOperationException("Unhandled async error was captured: " + _asyncErrors[0]);
            }
            GD.Print("[AGAS-H2][MCP-HTTP-RUNTIME][PASS] Godot headless real MCP HTTP E2E acceptance completed.");
        }
        catch (Exception ex)
        {
            exitCode = 1;
            GD.PushError("[AGAS-H2][MCP-HTTP-RUNTIME][FAIL] " + ex);
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= unhandledHandler;
            TaskScheduler.UnobservedTaskException -= taskHandler;
            CleanupTempRoots();
            GetTree().Quit(exitCode);
        }
    }

    private async Task RunAllAsync()
    {
        Assert.False(AgasMcpSettingsResource.DefaultEnabled, "MCP settings default constant should be disabled.");
        Assert.False(new AgasMcpSettingsResource().Enabled, "New MCP settings resource should default to Enabled=false.");

        RuntimeProject initializedProject = CreateInitializedProject("mcp-http-runtime");
        CreateTinyPngAsset(initializedProject.Root, "assets/http-runtime-test.png");
        CreatePngAsset(initializedProject.Root, "assets/http-runtime-override.png", 3, 2, new Color(0.8f, 0.1f, 0.3f, 1));
        const string generatedRelativePath = "assets/generated/http-mcp-generated.png";
        CreateSolidPngAsset(initializedProject.Root, generatedRelativePath);

        var dirtyState = new AgasMcpDirtyState();
        var coordinator = new AgasAiGenerationCoordinator();
        var workspace = new MainWorkspace(coordinator) { CustomMinimumSize = new Vector2(1200, 760) };
        workspace.SetMcpDirtyState(dirtyState);
        AddChild(workspace);
        await ProcessFramesAsync(2);
        workspace.OpenProject(initializedProject.Root);
        await ProcessFramesAsync(4);
        Assert.True(workspace.TryBindMcpDirtyState(out string bindError), "Live MCP dirty-state bind failed: " + bindError);
        AgasMcpMainThreadDispatcher dispatcher = new() { Name = "McpHttpRuntimeAcceptanceDispatcher" };
        AddChild(dispatcher);
        workspace.SetMcpMainThreadDispatcher(dispatcher);

        AgasMcpProjectContext live = workspace.GetCurrentMcpProjectContext()
            ?? throw new InvalidOperationException("Live MCP project context is unavailable.");
        var profile = new AiGenerationProviderProfile(
            "http-acceptance-profile",
            "HTTP Acceptance Relay",
            "openai-compatible",
            "gpt-image-2",
            "http-acceptance-secret-key",
            "https://relay.example/v1");
        var aiBridge = new ProfileOverrideBridge(workspace.AiGenerationWorkspaceBridge, profile);
        AgasMcpProjectContext liveContext = live.WithAiGenerationWorkspace(aiBridge);
        RuntimeProject runtimeProject = new(initializedProject.Root, liveContext, initializedProject.Facade);
        AgasMcpAiProjectScope initialScope = AgasMcpAiProjectScope.Create(
            runtimeProject.Root,
            runtimeProject.Context.ProjectId);
        AgasMcpProjectContextProvider provider = new();
        provider.RegisterContext(liveContext);
        AgasMcpProjectFacade facade = new(provider, dirtyState);
        using var aiStore = new AgasMcpAiJobStore();
        await using var aiScheduler = new AgasMcpAiJobScheduler(
            coordinator,
            aiStore,
            AgasMcpAiFacade.MapRunnerFailure);
        var fakeAiRunner = new ControllableAiRunner(generatedRelativePath);
        var aiFacade = new AgasMcpAiFacade(provider, dirtyState, aiStore, aiScheduler, fakeAiRunner);

        await using AgasMcpSerialToolExecutor executor = new(TimeSpan.FromSeconds(15));
        string agasHome = Path.Combine(initializedProject.Root, ".agas-mcp-http-home");
        var aiSettingsStore = new AgasAiSettingsStore(agasHome);
        var persistedProfile = new AgasAiProfile(
            profile.Id,
            profile.DisplayName,
            profile.ProviderId,
            profile.ModelName,
            profile.BaseUri.AbsoluteUri);
        aiSettingsStore.SaveProfilesAndCredential(
            new AgasAiProfilesDocument(
                AgasAiProfilesDocument.CurrentResourceVersion,
                persistedProfile.Id,
                new[] { persistedProfile }),
            persistedProfile.Id,
            profile.ApiKey);
        string? lifecycleProjectRoot = runtimeProject.Root;
        var lifecycleRecents = new List<ProjectLifecycleRecentEntry>
        {
            new(
                initializedProject.Root,
                liveContext.ProjectId,
                "HTTP Runtime Project",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                true),
        };
        var lifecycleBridge = new MainMcpProjectLifecycleBridgeAdapter(
            System.Environment.CurrentManagedThreadId,
            new MainMcpProjectLifecycleBridgePorts(
                () => lifecycleRecents.ToArray(),
                () => lifecycleProjectRoot,
                (project, execution, _) => new ValueTask<ProjectLifecycleOpenResult>(
                    OpenLifecycleWorkspace(project.ProjectRoot, project.Name, execution)),
                (name, execution, _) => new ValueTask<ProjectLifecycleOpenResult>(
                    CreateLifecycleWorkspace(name, execution))));
        var lifecycleMainThread = new MainMcpProjectLifecycleMainThreadPort(
            dispatcher,
            () => new ProjectLifecycleWorkspaceSnapshot(
                lifecycleProjectRoot != null,
                initialScope,
                1,
                dirtyState.IsDirty,
                false,
                false),
            workspace.SaveForMcpLifecycle,
            () =>
            {
                bool finalized = workspace.FinalizeMcpLifecycleClose();
                if (finalized)
                {
                    lifecycleProjectRoot = null;
                    provider.Clear();
                }
                return finalized;
            });
        var lifecycleFacade = new AgasMcpProjectLifecycleFacade(
            lifecycleBridge,
            new ProjectLifecycleCloseService(
                new ProjectLifecycleWorkspaceLeaseManager(),
                lifecycleMainThread,
                new ProjectLifecycleAiJobSchedulerQuiescencePort(aiScheduler, TimeSpan.FromSeconds(5))));
        var packageBridge = new MainMcpScenePackageBridgeAdapter(
            System.Environment.CurrentManagedThreadId,
            new MainMcpScenePackageBridgePorts(
                workspace.CaptureMcpScenePackageExport,
                workspace.PublishMcpPreparedScenePackageAsync));
        var importFacade = new AgasMcpImportFacade(provider, dirtyState, packageBridge, agasHome: () => agasHome);
        var settingsBridge = new AgasAiSettingsUiMutationBridge(_ => { });
        settingsBridge.PublishMainThreadSnapshot(default, 1, "mcp-http-runtime", persistedProfile.Id);
        var aiSettingsFacade = new AgasMcpAiSettingsFacade(
            new AgasAiProfileSettingsService(aiSettingsStore),
            settingsBridge,
            dirtyState,
            () => new AgasAiEffectiveProfileSelection(
                persistedProfile.Id,
                persistedProfile.ProviderId,
                profile.AdapterKind,
                persistedProfile.ModelName));
        IReadOnlyDictionary<string, AgasMcpPerToolHandler> handlers = AgasMcpDescriptorHandlerRegistry.Create(
            facade,
            lifecycleFacade,
            importFacade,
            aiSettingsFacade,
            aiFacade,
            settingsBridge.StageAcquireOnMainThread,
            settingsBridge.FinishWithoutCommitOnMainThread,
            () => { });
        AgasMcpToolRuntime runtime = new(executor, dispatcher, handlers);

        ProjectLifecycleOpenResult OpenLifecycleWorkspace(
            string projectRoot,
            string name,
            AgasMcpCommitExecution execution)
        {
            if (lifecycleProjectRoot != null)
                return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.ProjectAlreadyOpen);
            if (!execution.TryBeginMainThreadCompletion())
                return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.CanceledBeforeCommit);
            try
            {
                workspace.OpenProject(projectRoot);
                AgasMcpProjectContext? opened = workspace.GetCurrentMcpProjectContext();
                if (opened == null)
                    return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.OpenFailed);
                provider.RegisterContext(opened);
                lifecycleProjectRoot = projectRoot;
                return ProjectLifecycleOpenResult.Ok(projectRoot, name);
            }
            finally
            {
                execution.CompleteCommit();
            }
        }

        ProjectLifecycleOpenResult CreateLifecycleWorkspace(string name, AgasMcpCommitExecution execution)
        {
            if (lifecycleProjectRoot != null)
                return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.ProjectAlreadyOpen);
            string root = NewTempRoot("mcp-http-created");
            Directory.CreateDirectory(root);
            AgasPersistenceResult created = new AgasProjectInitializer().EnsureInitialized(root);
            if (!created.Success)
                return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.CreateFailed);
            lifecycleRecents.Add(new ProjectLifecycleRecentEntry(
                root,
                new FileSystemProjectAccessIdentityProvider().Capture(root).IdentityToken,
                name,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                true));
            return OpenLifecycleWorkspace(root, name, execution);
        }
        var hostFactory = AgasMcpHostFactory.CreateTestDynamicPort();
        await using AgasMcpLifecycleService lifecycle = new(
            hostFactory,
            AgasMcpToolCatalog.Create(runtime),
            executor,
            aiScheduler);
        Assert.Equal(AgasMcpRuntimeState.Disabled, lifecycle.Status.State,
            "Lifecycle should start Disabled before the dynamic test listener exists.");
        Assert.Equal(hostFactory.Endpoint, lifecycle.Status.Endpoint,
            "Initial Disabled status must use the owning HostFactory endpoint.");
        Assert.Equal("http://127.0.0.1:0/mcp", lifecycle.Status.Endpoint,
            "Dynamic test HostFactory should expose port 0 only before Kestrel atomically binds.");
        AgasMcpAiJobSnapshot failedJob = aiStore.CreateQueued(initialScope, "RegionEdit");
        Assert.True(aiStore.TryMarkRunning(failedJob.JobId, out _), "Failed status fixture must start running.");
        Assert.True(aiStore.TryFail(
            failedJob.JobId,
            new AgasMcpAiJobFailure(
                AgasMcpErrorCodes.AiProviderFailed,
                "The image provider request failed.",
                "Check the configured profile and retry."),
            out _),
            "Failed status fixture must reach Failed.");
        for (int index = 0; index < 11; index++)
        {
            AgasMcpAiJobSnapshot canceled = aiStore.CreateQueued(initialScope, "ReferenceEdit");
            Assert.True(aiStore.TryCancel(canceled.JobId, out _), "Recent status fixture must become terminal.");
        }
        PrintLoadedAssembly("Microsoft.AspNetCore");
        PrintLoadedAssembly("Microsoft.Extensions.DependencyInjection.Abstractions");

        try
        {
            await lifecycle.StartAsync(TestTimeout());
            PrintLoadedAssembly("Microsoft.AspNetCore");
            PrintLoadedAssembly("Microsoft.Extensions.Configuration");
            PrintLoadedAssembly("Microsoft.Extensions.DependencyInjection.Abstractions");
            PrintLoadedAssembly("Microsoft.Extensions.Hosting");
            PrintLoadedAssembly("ModelContextProtocol.AspNetCore");
            _testEndpoint = hostFactory.Endpoint;
            Assert.Equal(AgasMcpRuntimeState.Running, lifecycle.Status.State, "Lifecycle should enter Running after start.");
            Assert.Equal(hostFactory.Endpoint, lifecycle.Status.Endpoint, "Isolated lifecycle should report its actual test endpoint.");
            Assert.Equal($"http://127.0.0.1:{hostFactory.Port}/mcp", _testEndpoint,
                "Kestrel dynamic binding must update the HostFactory endpoint before StartAsync returns.");
            Assert.True(await CanConnectToTestPortAsync(), "MCP isolated test port should listen after lifecycle start.");

            using NetHttpClient client = CreateRawClient();
            await VerifySecurityAndBodyLimitAsync(client);

            string sessionId = await InitializeSessionWithoutOriginAsync(client);
            await SendInitializedNotificationAsync(client, sessionId);
            await VerifyToolsListAsync(client, sessionId);
            await VerifyStrictToolArgumentsAsync(client, sessionId);
            await VerifyAiHttpContractsAsync(client, sessionId, provider, runtimeProject, failedJob.JobId);
            await VerifyV2HttpWorkflowAsync(
                client,
                sessionId,
                provider,
                runtimeProject,
                fakeAiRunner,
                generatedRelativePath);
            await CallAllEightToolsAndVerifyPersistenceAsync(client, sessionId, runtimeProject.Root, provider);
            // The persistence checks deliberately swap in a detached reload context. Restore the
            // live workspace context before exercising handlers that publish through workspace ports.
            provider.RegisterContext(liveContext);
            await VerifyExpandedProductionHandlersAsync(client, sessionId);
            await VerifyImportPackageSuccessChainAsync(client, sessionId, agasHome, runtimeProject.Root);
            string lifecycleSessionId = await ReplaceClosedSessionAndVerifyIsolationAsync(client, sessionId);
            await VerifyLifecycleSuccessChainAsync(client, lifecycleSessionId);
            await VerifyDeleteInvalidatesOldSessionAsync(client, lifecycleSessionId);
        }
        finally
        {
            await lifecycle.ApplyEnabledAsync(false, TestTimeout());
            Assert.Equal(AgasMcpRuntimeState.Disabled, lifecycle.Status.State, "ApplyEnabledAsync(false) should stop and disable lifecycle.");
            Assert.Equal(hostFactory.Endpoint, lifecycle.Status.Endpoint,
                "ApplyEnabledAsync(false) Disabled status must retain the actual HostFactory endpoint.");
            await WaitForTestPortReleasedAsync();
            Assert.False(await CanConnectToTestPortAsync(), "MCP isolated test port should not listen after stop/disable.");
            executor.Stop();
            dispatcher.QueueFree();
            workspace.QueueFree();
            await ProcessFramesAsync(2);
        }
    }

    private async Task VerifySecurityAndBodyLimitAsync(NetHttpClient client)
    {
        Uri endpoint = new(_testEndpoint);
        using HttpRequestMessage allowed = CreateJsonRpcRequest("initialize", includeProtocolHeader: false);
        allowed.Headers.Host = $"127.0.0.1:{endpoint.Port}";
        allowed.Headers.Add("Origin", $"http://127.0.0.1:{endpoint.Port}");
        using HttpResponseMessage allowedResponse = await client.SendAsync(allowed, TestTimeout());
        Assert.True(allowedResponse.IsSuccessStatusCode,
            "Exact dynamic Host/Origin should be accepted. Body=" + await allowedResponse.Content.ReadAsStringAsync(TestTimeout()));

        using HttpRequestMessage badHost = CreateJsonRpcRequest("initialize", includeProtocolHeader: false);
        badHost.Headers.Host = "evil.example:54119";
        using HttpResponseMessage badHostResponse = await client.SendAsync(badHost, TestTimeout());
        Assert.Equal(HttpStatusCode.Forbidden, badHostResponse.StatusCode, "Invalid Host should be rejected.");
        Assert.Equal("AGAS_MCP_FORBIDDEN", await badHostResponse.Content.ReadAsStringAsync(TestTimeout()), "Invalid Host should use production forbidden body.");

        using HttpRequestMessage badOrigin = CreateJsonRpcRequest("initialize", includeProtocolHeader: false);
        badOrigin.Headers.Add("Origin", "http://evil.example");
        using HttpResponseMessage badOriginResponse = await client.SendAsync(badOrigin, TestTimeout());
        Assert.Equal(HttpStatusCode.Forbidden, badOriginResponse.StatusCode, "Invalid Origin should be rejected.");
        Assert.Equal("AGAS_MCP_FORBIDDEN", await badOriginResponse.Content.ReadAsStringAsync(TestTimeout()), "Invalid Origin should use production forbidden body.");

        foreach (string rejectedHost in new[]
        {
            $"localhost:{endpoint.Port}",
            $"0.0.0.0:{endpoint.Port}",
            $"127.0.0.1:{(endpoint.Port == 65535 ? 65534 : endpoint.Port + 1)}",
        })
        {
            using HttpRequestMessage request = CreateJsonRpcRequest("initialize", includeProtocolHeader: false);
            request.Headers.Host = rejectedHost;
            using HttpResponseMessage response = await client.SendAsync(request, TestTimeout());
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode,
                "Dynamic endpoint security must reject non-exact Host " + rejectedHost);
        }

        foreach (string rejectedOrigin in new[]
        {
            $"http://localhost:{endpoint.Port}",
            $"http://0.0.0.0:{endpoint.Port}",
            $"http://127.0.0.1:{(endpoint.Port == 65535 ? 65534 : endpoint.Port + 1)}",
        })
        {
            using HttpRequestMessage request = CreateJsonRpcRequest("initialize", includeProtocolHeader: false);
            request.Headers.Add("Origin", rejectedOrigin);
            using HttpResponseMessage response = await client.SendAsync(request, TestTimeout());
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode,
                "Dynamic endpoint security must reject non-exact Origin " + rejectedOrigin);
        }

        string padding = new('x', checked((int)AgasMcpHostFactory.MaxRequestBodyBytes + 1));
        string json = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 413,
            method = "initialize",
            @params = new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = "agas-godot-http-runtime", version = "1.0.0" },
            },
            padding,
        }, JsonOptions);
        using StringContent content = new(json, Encoding.UTF8, "application/json");
        content.Headers.ContentLength = Encoding.UTF8.GetByteCount(json);
        Assert.True(content.Headers.ContentLength > AgasMcpHostFactory.MaxRequestBodyBytes, "Oversized request should exceed production limit.");
        using HttpRequestMessage oversized = new(HttpMethod.Post, _testEndpoint) { Content = content };
        oversized.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        oversized.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using HttpResponseMessage oversizedResponse = await client.SendAsync(oversized, TestTimeout());
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResponse.StatusCode, "Body over 1MiB should return 413.");
    }

    private async Task<string> InitializeSessionWithoutOriginAsync(NetHttpClient client)
    {
        using HttpRequestMessage initialize = CreateJsonRpcRequest("initialize", includeProtocolHeader: false);
        Assert.False(initialize.Headers.Contains("Origin"), "Legal initialize request should omit Origin.");
        using HttpResponseMessage response = await client.SendAsync(initialize, TestTimeout());
        string body = await response.Content.ReadAsStringAsync(TestTimeout());
        Assert.True(response.IsSuccessStatusCode, "Initialize without Origin should be allowed. Body=" + body);
        Assert.True(response.Headers.TryGetValues(AgasMcpSessionDeleteMiddleware.SessionIdHeader, out IEnumerable<string>? values), "Initialize should return session header.");
        string sessionId = (values ?? throw new InvalidOperationException("Initialize did not return session header values.")).Single();
        Assert.False(string.IsNullOrWhiteSpace(sessionId), "Session id should not be empty.");
        using JsonDocument document = JsonDocument.Parse(ExtractJsonPayload(body));
        Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString(), "Initialize should return JSON-RPC response.");
        return sessionId;
    }

    private async Task SendInitializedNotificationAsync(NetHttpClient client, string sessionId)
    {
        using HttpRequestMessage request = CreateSessionRequest(HttpMethod.Post, sessionId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = JsonBody(new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized",
        });

        using HttpResponseMessage response = await client.SendAsync(request, TestTimeout());
        Assert.True(response.IsSuccessStatusCode, "notifications/initialized should succeed. Body=" + await response.Content.ReadAsStringAsync(TestTimeout()));
    }

    private async Task VerifyToolsListAsync(NetHttpClient client, string sessionId)
    {
        using JsonDocument document = await SendJsonRpcAsync(client, sessionId, new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/list",
        });

        JsonElement[] tools = document.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .ToArray();
        string[] names = tools
            .Select(tool => tool.GetProperty("name").GetString() ?? string.Empty)
            .ToArray();
        Assert.Equal(AgasMcpToolNames.All.Count, names.Length,
            "tools/list should expose exactly the current formal AGAS tools.");
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count(), "tools/list should not contain duplicate tool names.");
        Assert.Empty(AgasMcpToolNames.All.Except(names, StringComparer.Ordinal), "tools/list should include every formal AGAS tool.");
        Assert.Empty(names.Except(AgasMcpToolNames.All, StringComparer.Ordinal), "tools/list should not include extra tools.");
        Assert.Equal(16, LegacyWireCompatibilityToolNames.Count, "Legacy wire compatibility tool oracle should remain the old 16-tool baseline.");
        Assert.Empty(LegacyWireCompatibilityToolNames.Except(names, StringComparer.Ordinal),
            "tools/list should retain every legacy 16 wire-compatible tool while exposing the expanded formal listing.");
        foreach (JsonElement tool in tools)
        {
            AssertToolListWireShape(tool);
        }
        GD.Print($"[AGAS-H2][MCP-HTTP-RUNTIME][TOOLS-LIST] Count={names.Length} FormalCount={AgasMcpToolNames.All.Count} LegacyWireCompatibilityCount={LegacyWireCompatibilityToolNames.Count}");
        AssertSchemaProperties(tools, AgasMcpToolNames.LayerMergeVisible, "sceneId", "layerId");
        AssertSchemaProperties(tools, AgasMcpToolNames.AiGenerate, "sceneId", "layerId", "prompt", "profileId", "target");
        AssertSchemaProperties(tools, AgasMcpToolNames.AiStatus, "jobId", "limit");
        AssertSchemaProperties(tools, AgasMcpToolNames.AiCancel, "jobId");
        AssertSchemaProperties(tools, AgasMcpToolNames.AiApply, "jobId");
    }

    private static readonly IReadOnlyList<string> LegacyWireCompatibilityToolNames = Array.AsReadOnly(new[]
    {
        AgasMcpToolNames.ProjectSnapshot,
        AgasMcpToolNames.AssetsList,
        AgasMcpToolNames.SceneMutate,
        AgasMcpToolNames.LayerMutate,
        AgasMcpToolNames.ImageMutate,
        AgasMcpToolNames.PromptMutate,
        AgasMcpToolNames.ViewSet,
        AgasMcpToolNames.ProjectSave,
        AgasMcpToolNames.SelectionSet,
        AgasMcpToolNames.SelectionGet,
        AgasMcpToolNames.AiGenerate,
        AgasMcpToolNames.AiStatus,
        AgasMcpToolNames.AiCancel,
        AgasMcpToolNames.AiApply,
    });

    private static void AssertToolListWireShape(JsonElement tool)
    {
        string name = tool.GetProperty("name").GetString() ?? string.Empty;
        Assert.True(tool.TryGetProperty("description", out JsonElement description)
                    && description.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(description.GetString()),
            name + " tools/list entry should expose a non-empty standard description.");
        Assert.True(tool.TryGetProperty("inputSchema", out JsonElement inputSchema)
                    && inputSchema.ValueKind == JsonValueKind.Object,
            name + " tools/list entry should expose a standard object inputSchema.");
        Assert.True(tool.TryGetProperty("outputSchema", out JsonElement outputSchema)
                    && outputSchema.ValueKind == JsonValueKind.Object,
            name + " tools/list entry should expose a standard object outputSchema.");
        Assert.True(tool.TryGetProperty("annotations", out JsonElement annotations)
                    && annotations.ValueKind == JsonValueKind.Object,
            name + " tools/list entry should expose standard annotations.");
        foreach (string internalField in new[] { "family", "dispatch", "timeout", "commitPolicy" })
        {
            Assert.False(tool.TryGetProperty(internalField, out _),
                name + " tools/list entry should not publish internal " + internalField + " metadata.");
        }
    }

    private async Task VerifyStrictToolArgumentsAsync(NetHttpClient client, string sessionId)
    {
        const string validJobId = "aijob-00000000000000000000000000000000";
        (string Tool, Dictionary<string, object?> Arguments)[] validCases =
        {
            (AgasMcpToolNames.ProjectSnapshot, ToolArguments()),
            (AgasMcpToolNames.AssetsList, ToolArguments()),
            (AgasMcpToolNames.SceneMutate, ToolArguments(("op", "add"), ("name", "Strict Scene"))),
            (AgasMcpToolNames.LayerMutate, ToolArguments(("op", "add"), ("sceneId", "scene-001"), ("name", "Strict Layer"))),
            (AgasMcpToolNames.ImageMutate, ToolArguments(("op", "add"), ("sceneId", "scene-001"), ("layerId", "layer-001"), ("imagePath", "assets/http-runtime-test.png"))),
            (AgasMcpToolNames.PromptMutate, ToolArguments(("op", "add"), ("sceneId", "scene-001"), ("layerId", "layer-001"), ("text", "strict prompt"))),
            (AgasMcpToolNames.ViewSet, ToolArguments(("sceneId", "scene-001"), ("cameraX", 1), ("cameraY", 2), ("zoom", 1))),
            (AgasMcpToolNames.ProjectSave, ToolArguments()),
            (AgasMcpToolNames.SelectionSet, ToolArguments(("op", "clearRegions"), ("sceneId", "scene-001"))),
            (AgasMcpToolNames.SelectionGet, ToolArguments()),
            (AgasMcpToolNames.AiGenerate, ToolArguments(("sceneId", "scene-001"), ("layerId", "scene-001-layer-001"))),
            (AgasMcpToolNames.AiStatus, ToolArguments()),
            (AgasMcpToolNames.AiCancel, ToolArguments(("jobId", validJobId))),
            (AgasMcpToolNames.AiApply, ToolArguments(("jobId", validJobId))),
        };

        foreach ((string tool, Dictionary<string, object?> validArguments) in validCases)
        {
            var arguments = new Dictionary<string, object?>(validArguments, StringComparer.Ordinal)
            {
                ["unexpectedField"] = true,
            };
            JsonElement response = await CallToolAsync(client, sessionId, NextRpcId(), tool, arguments);
            AssertStrictToolShapeError(response,
                $"{tool} must reject unknown properties over real HTTP tools/call.");
        }

        (string Tool, Dictionary<string, object?> Arguments)[] caseAliasCases =
        {
            (AgasMcpToolNames.ProjectSnapshot, ToolArguments(("IncludeScenes", true))),
            (AgasMcpToolNames.AssetsList, ToolArguments(("Limit", 1))),
            (AgasMcpToolNames.SceneMutate, ToolArguments(("Op", "add"), ("name", "Strict Scene"))),
            (AgasMcpToolNames.LayerMutate, ToolArguments(("Op", "add"), ("sceneId", "scene-001"), ("name", "Strict Layer"))),
            (AgasMcpToolNames.ImageMutate, ToolArguments(("Op", "add"), ("sceneId", "scene-001"), ("layerId", "layer-001"), ("imagePath", "assets/http-runtime-test.png"))),
            (AgasMcpToolNames.PromptMutate, ToolArguments(("Op", "add"), ("sceneId", "scene-001"), ("layerId", "layer-001"), ("text", "strict prompt"))),
            (AgasMcpToolNames.ViewSet, ToolArguments(("SceneId", "scene-001"), ("cameraX", 1), ("cameraY", 2), ("zoom", 1))),
            (AgasMcpToolNames.SelectionSet, ToolArguments(("Op", "clearRegions"), ("sceneId", "scene-001"))),
            (AgasMcpToolNames.AiGenerate, ToolArguments(("sceneId", "scene-001"), ("layerId", "scene-001-layer-001"), ("Prompt", "tree"))),
            (AgasMcpToolNames.AiStatus, ToolArguments(("Limit", 1))),
            (AgasMcpToolNames.AiCancel, ToolArguments(("JobId", validJobId))),
            (AgasMcpToolNames.AiApply, ToolArguments(("JobId", validJobId))),
        };

        foreach ((string tool, Dictionary<string, object?> arguments) in caseAliasCases)
        {
            JsonElement response = await CallToolAsync(client, sessionId, NextRpcId(), tool, arguments);
            AssertStrictToolShapeError(response,
                $"{tool} must reject case-mismatched property aliases over real HTTP tools/call.");
        }

        Dictionary<string, object?>[] invalidRegions =
        {
            ToolArguments(("column", 0), ("row", 0), ("width", 1), ("height", 1), ("unexpected", true)),
            ToolArguments(("Column", 0), ("row", 0), ("width", 1), ("height", 1)),
        };
        foreach (string regionName in new[] { "selection", "replacement" })
        {
            foreach (Dictionary<string, object?> invalidRegion in invalidRegions)
            {
                Dictionary<string, object?> arguments = ToolArguments(
                    ("op", "setRegions"),
                    ("sceneId", "scene-001"),
                    ("selection", ToolArguments(("column", 0), ("row", 0), ("width", 1), ("height", 1))));
                arguments[regionName] = invalidRegion;
                JsonElement response = await CallToolAsync(
                    client,
                    sessionId,
                    NextRpcId(),
                    AgasMcpToolNames.SelectionSet,
                    arguments);
                AssertStrictToolShapeError(response,
                    $"selection.set must enforce nested {regionName} additionalProperties and exact property casing.");
            }
        }
    }

    private async Task VerifyExpandedProductionHandlersAsync(NetHttpClient client, string sessionId)
    {
        const string sourceRef = "src1-00000000000000000000000000000000-0000000000000000000000000000000000000000000000000000000000000000";
        (string Tool, object Arguments, bool Succeeds, bool FailureHasData)[] cases =
        {
            (AgasMcpToolNames.ProjectsRecent, new { }, true, false),
            (AgasMcpToolNames.ImportsList, new { }, true, false),
            (AgasMcpToolNames.AssetsImport, new { sourceRef }, false, false),
            (AgasMcpToolNames.SceneGet, new { sceneId = "scene-001" }, true, false),
            (AgasMcpToolNames.ScenePackageImport, new { sourceRef }, false, false),
            (AgasMcpToolNames.ScenePackageExport, new { sceneId = "scene-001" }, true, false),
            (AgasMcpToolNames.GridMutate, new { op = "setVisible", enabled = true }, true, false),
            (AgasMcpToolNames.AiProfilesList, new { }, true, false),
            (AgasMcpToolNames.AiModelsList, new { }, true, false),
            (AgasMcpToolNames.AiProfileActivate, new { profileId = "missing-profile" }, false, false),
            (AgasMcpToolNames.AiModelSet, new { modelName = "http-production-model" }, true, false),
            (AgasMcpToolNames.AiDiagnostics, new { }, true, false),
            (AgasMcpToolNames.AiDiagnostics, new { profileId = "missing-profile" }, false, false),
        };

        foreach ((string tool, object arguments, bool succeeds, bool failureHasData) in cases)
        {
            JsonElement structured = await CallToolAsync(client, sessionId, NextRpcId(), tool, arguments);
            Assert.True(structured.TryGetProperty("saved", out JsonElement saved) && saved.ValueKind is JsonValueKind.True or JsonValueKind.False,
                tool + " production handler must return saved:boolean.");
            Assert.True(structured.TryGetProperty("dirty", out JsonElement dirty) && dirty.ValueKind is JsonValueKind.True or JsonValueKind.False,
                tool + " production handler must return dirty:boolean.");
            Dictionary<string, JsonElement> invalidArguments = JsonSerializer.SerializeToElement(arguments, JsonOptions)
                .EnumerateObject()
                .ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
            invalidArguments["unexpectedField"] = JsonSerializer.SerializeToElement(true, JsonOptions);
            JsonElement invalid = await CallToolAsync(client, sessionId, NextRpcId(), tool, invalidArguments);
            AssertStrictToolShapeError(invalid,
                tool + " production handler must reject unknown properties over real HTTP tools/call.");
            if (tool == AgasMcpToolNames.ProjectClose)
            {
                Assert.True(invalid.TryGetProperty("data", out JsonElement invalidCloseData)
                            && invalidCloseData.TryGetProperty("projectStillOpen", out _)
                            && invalidCloseData.TryGetProperty("saved", out _)
                            && invalidCloseData.TryGetProperty("aiCancellationRequested", out _)
                            && invalidCloseData.TryGetProperty("saveState", out _),
                    "project.close fail-closed invalid-input response must retain its four-field receipt.");
            }
            if (succeeds)
            {
                Assert.True(structured.GetProperty("ok").GetBoolean(),
                    tool + " valid production handler call should succeed. Structured=" + structured.GetRawText());
                Assert.True(structured.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object,
                    tool + " successful production handler response must contain object data.");
                Assert.False(structured.TryGetProperty("error", out _),
                    tool + " successful production handler response must omit error.");
                continue;
            }

            Assert.False(structured.GetProperty("ok").GetBoolean(),
                tool + " controlled production failure should set ok=false. Structured=" + structured.GetRawText());
            Assert.True(structured.TryGetProperty("error", out JsonElement error)
                        && error.ValueKind == JsonValueKind.Object
                        && error.TryGetProperty("code", out JsonElement code)
                        && code.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(code.GetString()),
                tool + " controlled production failure must contain the standard error envelope.");
            Assert.Equal(failureHasData, structured.TryGetProperty("data", out JsonElement failureData),
                tool + " failure data presence differs from the declared output contract.");
            if (failureHasData)
            {
                Assert.Equal(JsonValueKind.Object, failureData.ValueKind,
                    tool + " typed failure data must be an object.");
                Assert.True(failureData.TryGetProperty("projectStillOpen", out _)
                            && failureData.TryGetProperty("saved", out _)
                            && failureData.TryGetProperty("aiCancellationRequested", out _)
                            && failureData.TryGetProperty("saveState", out _),
                    tool + " close failure must carry the immutable four-field receipt.");
            }

        }

        GD.Print($"[AGAS-H2][MCP-HTTP-RUNTIME][PRODUCTION-HANDLERS] Count={cases.Length} Success={cases.Count(item => item.Succeeds)} Failure={cases.Count(item => !item.Succeeds)}");
    }

    private async Task VerifyImportPackageSuccessChainAsync(
        NetHttpClient client,
        string sessionId,
        string agasHome,
        string projectRoot)
    {
        JsonElement preImportSave = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.ProjectSave, new { });
        AssertToolSuccess(preImportSave, saved: true, dirty: false,
            "project.save must clear prior HTTP mutations before the import/package success chain.");
        string assetInbox = Path.Combine(agasHome, "import-inbox", "assets");
        Directory.CreateDirectory(assetInbox);
        string inboxAsset = Path.Combine(assetInbox, "production-chain.png");
        File.Copy(Path.Combine(projectRoot, "assets", "http-runtime-test.png"), inboxAsset, overwrite: true);

        JsonElement listedAssets = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.ImportsList,
            new { kind = "asset" });
        AssertToolSuccess(listedAssets, saved: true, dirty: false, "imports.list asset discovery must succeed over production HTTP wiring.");
        JsonElement asset = listedAssets.GetProperty("data").GetProperty("items")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "production-chain.png");
        Assert.False(asset.TryGetProperty("path", out _) || asset.TryGetProperty("base64", out _) || asset.TryGetProperty("metadata", out _),
            "imports.list asset rows must not expose path, base64, or metadata.");
        string assetSourceRef = asset.GetProperty("sourceRef").GetString()
            ?? throw new InvalidOperationException("imports.list asset omitted sourceRef.");
        JsonElement importedAsset = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.AssetsImport,
            new { sourceRef = assetSourceRef });
        AssertToolSuccess(importedAsset, saved: true, dirty: false, "assets.import must consume an issued sourceRef successfully.");
        string relativePath = importedAsset.GetProperty("data").GetProperty("relativePath").GetString()
            ?? throw new InvalidOperationException("assets.import omitted relativePath.");
        Assert.True(relativePath.StartsWith("assets/imported/", StringComparison.Ordinal)
                    && File.Exists(Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))),
            "assets.import must publish the imported asset under assets/imported.");

        JsonElement exportedPackage = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.ScenePackageExport,
            new { sceneId = "scene-001" });
        Assert.True(exportedPackage.GetProperty("ok").GetBoolean(),
            "scene.package.export must publish a real package over production bridge wiring. Structured=" + exportedPackage.GetRawText());
        JsonElement exportData = exportedPackage.GetProperty("data");
        string packageRef = exportData.GetProperty("packageRef").GetString()
            ?? throw new InvalidOperationException("scene.package.export omitted packageRef.");
        string packageSourceRef = exportData.GetProperty("sourceRef").GetString()
            ?? throw new InvalidOperationException("scene.package.export omitted sourceRef.");
        Assert.False(Path.IsPathRooted(packageRef) || packageRef.Contains(projectRoot, StringComparison.Ordinal),
            "scene.package.export packageRef must be opaque rather than an absolute project path.");
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(packageSourceRef, "^src1-[a-f0-9]{32}-[a-f0-9]{64}$"),
            "scene.package.export must return an opaque issued sourceRef.");
        string exportedPackageDirectory = Path.Combine(
            agasHome, "import-exchange", "scene-packages", packageRef);
        ScenePackageDocument exportedDocument = ScenePackageCodec.Deserialize(
            File.ReadAllText(Path.Combine(exportedPackageDirectory, "scene.json")));
        ScenePackageCodec.ValidateForTransport(exportedDocument);
        Assert.Equal(ScenePackageCodec.CurrentSchemaVersion, exportedDocument.SchemaVersion,
            "MCP export must use the same schema-v2 transport contract as desktop export.");
        foreach (ScenePackageFileIntegrity integrity in
                 exportedDocument.Integrity ?? Array.Empty<ScenePackageFileIntegrity>())
        {
            string file = Path.Combine(
                exportedPackageDirectory,
                Path.Combine(integrity.Path.Split('/')));
            Assert.True(File.Exists(file), $"MCP export omitted typed artifact {integrity.Path}.");
            ScenePackageIntegrity.Verify(integrity, File.ReadAllBytes(file));
        }

        JsonElement listedPackages = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.ImportsList,
            new { kind = "scenePackage" });
        AssertToolSuccess(listedPackages, saved: true, dirty: false, "imports.list scene-package discovery must succeed.");
        JsonElement package = listedPackages.GetProperty("data").GetProperty("items")
            .EnumerateArray()
            .Single(item => item.GetProperty("kind").GetString() == "scenePackage"
                && item.GetProperty("name").GetString() == packageRef);
        Assert.Equal("scenePackage", package.GetProperty("kind").GetString(), "Package exchange row kind mismatch.");
        string packageImportSourceRef = package.GetProperty("sourceRef").GetString()
            ?? throw new InvalidOperationException("imports.list scene-package row omitted sourceRef.");
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(packageImportSourceRef, "^src1-[a-f0-9]{32}-[a-f0-9]{64}$"),
            "imports.list must issue an opaque sourceRef for the discovered package.");
        JsonElement importedPackage = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.ScenePackageImport,
            new { sourceRef = packageImportSourceRef });
        Assert.True(importedPackage.GetProperty("ok").GetBoolean(),
            "scene.package.import must complete real staging and workspace publish. Structured=" + importedPackage.GetRawText());
        string importedSceneId = importedPackage.GetProperty("data").GetProperty("sceneId").GetString()
            ?? throw new InvalidOperationException("scene.package.import omitted sceneId.");
        Assert.False(string.IsNullOrWhiteSpace(importedSceneId), "scene.package.import must return a scene id.");
        GD.Print("[AGAS-H2][MCP-HTTP-RUNTIME][IMPORT-PACKAGE] Asset=PASS Export=PASS Import=PASS");
    }

    private async Task VerifyLifecycleSuccessChainAsync(NetHttpClient client, string sessionId)
    {
        JsonElement saved = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.ProjectSave, new { });
        AssertToolSuccess(saved, saved: true, dirty: false,
            "project.save must persist the production package-import dirty state before close.");
        Assert.Equal("complete", saved.GetProperty("data").GetProperty("saveState").GetString(),
            "project.save must report a complete typed receipt.");

        JsonElement closed = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.ProjectClose,
            new { saveIfDirty = false, cancelAiJobs = false });
        AssertToolSuccess(closed, saved: true, dirty: false, "project.close success path must use the lifecycle bridge.");
        JsonElement closeData = closed.GetProperty("data");
        Assert.False(closeData.GetProperty("projectStillOpen").GetBoolean(), "project.close success receipt must report closed.");
        Assert.True(closeData.GetProperty("saved").GetBoolean(), "project.close success receipt must report saved.");
        Assert.Equal("none", closeData.GetProperty("saveState").GetString(),
            "project.close must preserve saveState=none when closing an already clean, durable project without a new save write.");

        JsonElement created = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.ProjectCreate,
            new { name = "HTTP Lifecycle Project" });
        AssertToolSuccess(created, saved: true, dirty: false, "project.create must create and open a controlled fixture project.");

        JsonElement closedCreated = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.ProjectClose,
            new { saveIfDirty = false, cancelAiJobs = false });
        AssertToolSuccess(closedCreated, saved: true, dirty: false, "project.close must close the created fixture project.");

        JsonElement recent = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.ProjectsRecent, new { });
        AssertToolSuccess(recent, saved: true, dirty: false, "projects.recent must issue project references after create/close.");
        JsonElement row = recent.GetProperty("data").GetProperty("projects")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "HTTP Lifecycle Project");
        string projectRef = row.GetProperty("projectRef").GetString()
            ?? throw new InvalidOperationException("projects.recent omitted projectRef for created project.");
        Assert.False(row.GetProperty("isOpen").GetBoolean(), "Created project should be closed before project.open.");

        JsonElement opened = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.ProjectOpen,
            new { projectRef });
        AssertToolSuccess(opened, saved: true, dirty: false, "project.open must reopen the recent controlled project.");
        Assert.Equal(projectRef, opened.GetProperty("data").GetProperty("projectRef").GetString(),
            "project.open must echo the issued opaque projectRef.");

        JsonElement invalidClose = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.ProjectClose,
            new { unexpectedField = true });
        AssertStrictToolShapeError(invalidClose,
            "project.close invalid input must retain the standard validation error.");
        JsonElement invalidCloseData = invalidClose.GetProperty("data");
        Assert.True(invalidCloseData.TryGetProperty("projectStillOpen", out _)
                    && invalidCloseData.TryGetProperty("saved", out _)
                    && invalidCloseData.TryGetProperty("aiCancellationRequested", out _)
                    && invalidCloseData.TryGetProperty("saveState", out _),
            "project.close handler-started invalid-input failure must retain its four-field typed receipt.");
        GD.Print("[AGAS-H2][MCP-HTTP-RUNTIME][LIFECYCLE] Save=PASS Close=PASS Create=PASS RecentOpen=PASS");
    }

    private static void AssertSchemaProperties(JsonElement[] tools, string toolName, params string[] expectedProperties)
    {
        JsonElement tool = tools.Single(item => string.Equals(
            item.GetProperty("name").GetString(),
            toolName,
            StringComparison.Ordinal));
        JsonElement schema = tool.GetProperty("inputSchema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean(), toolName + " must reject unknown properties.");
        string[] actual = schema.GetProperty("properties").EnumerateObject().Select(item => item.Name).ToArray();
        Assert.True(expectedProperties.SequenceEqual(actual, StringComparer.Ordinal),
            toolName + " schema properties differ from the frozen contract. Actual=" + string.Join(",", actual));
    }

    private async Task VerifyAiHttpContractsAsync(
        NetHttpClient client,
        string sessionId,
        AgasMcpProjectContextProvider provider,
        RuntimeProject initialProject,
        string failedJobId)
    {
        JsonElement generateExtra = await CallToolAsync(client, sessionId, 30, AgasMcpToolNames.AiGenerate, new
        {
            sceneId = "scene-001",
            layerId = "scene-001-layer-001",
            operation = "TextToImage",
        });
        AssertToolError(generateExtra, AgasMcpErrorCodes.InvalidOperation,
            "ai.generate must reject caller-supplied operation at runtime.");
        Assert.True(
            generateExtra.GetProperty("error").GetProperty("message").GetString()?.Contains("expected JSON shape", StringComparison.Ordinal) == true,
            "ai.generate forbidden-field rejection must come from strict runtime JSON parsing.");

        object[] invalidGenerateValues =
        {
            new { sceneId = "scene.bad", layerId = "scene-001-layer-001" },
            new { sceneId = " scene-001", layerId = "scene-001-layer-001" },
            new { sceneId = "scene-001", layerId = "layer.bad" },
            new { sceneId = "scene-001", layerId = "scene-001-layer-001 " },
            new { sceneId = "scene-001", layerId = "scene-001-layer-001", prompt = "   " },
            new { sceneId = "scene-001", layerId = "scene-001-layer-001", profileId = "profile.bad" },
            new { sceneId = "scene-001", layerId = "scene-001-layer-001", profileId = " profile-1 " },
            new { sceneId = "scene-001", layerId = "scene-001-layer-001", target = "Force" },
        };
        for (int index = 0; index < invalidGenerateValues.Length; index++)
        {
            JsonElement invalidGenerate = await CallToolAsync(
                client,
                sessionId,
                40 + index,
                AgasMcpToolNames.AiGenerate,
                invalidGenerateValues[index]);
            AssertToolError(invalidGenerate, AgasMcpErrorCodes.InvalidOperation,
                "ai.generate must enforce every schema value boundary at runtime.");
        }

        JsonElement applyExtra = await CallToolAsync(client, sessionId, 31, AgasMcpToolNames.AiApply, new
        {
            jobId = failedJobId,
            mode = "force",
        });
        AssertToolError(applyExtra, AgasMcpErrorCodes.InvalidOperation,
            "ai.apply must reject caller-supplied mode at runtime.");

        JsonElement invalidStatusJobId = await CallToolAsync(client, sessionId, 50, AgasMcpToolNames.AiStatus, new { jobId = "" });
        AssertToolError(invalidStatusJobId, AgasMcpErrorCodes.InvalidOperation,
            "ai.status must reject an explicitly empty jobId instead of treating it as omitted.");
        JsonElement invalidStatusLimitWithJob = await CallToolAsync(client, sessionId, 51, AgasMcpToolNames.AiStatus, new
        {
            jobId = failedJobId,
            limit = 51,
        });
        AssertToolError(invalidStatusLimitWithJob, AgasMcpErrorCodes.InvalidOperation,
            "ai.status must validate limit even when jobId is present.");
        JsonElement invalidCancelJobId = await CallToolAsync(client, sessionId, 52, AgasMcpToolNames.AiCancel, new { jobId = "bad" });
        AssertToolError(invalidCancelJobId, AgasMcpErrorCodes.InvalidOperation,
            "ai.cancel must enforce the frozen jobId format.");
        JsonElement invalidApplyJobId = await CallToolAsync(client, sessionId, 53, AgasMcpToolNames.AiApply, new { jobId = "bad" });
        AssertToolError(invalidApplyJobId, AgasMcpErrorCodes.InvalidOperation,
            "ai.apply must enforce the frozen jobId format.");

        JsonElement failedStatus = await CallToolAsync(client, sessionId, 32, AgasMcpToolNames.AiStatus, new
        {
            jobId = failedJobId,
        });
        AssertToolSuccess(failedStatus, saved: true, dirty: false, "Failed ai.status is a successful status query.");
        JsonElement failedData = failedStatus.GetProperty("data");
        Assert.Equal("Failed", failedData.GetProperty("state").GetString(), "Failed job state mismatch.");
        Assert.Equal(AgasMcpErrorCodes.AiProviderFailed, failedData.GetProperty("errorCode").GetString(), "Failed job errorCode mismatch.");
        Assert.Equal("The image provider request failed.", failedData.GetProperty("message").GetString(),
            "Failed job must expose the frozen message field.");
        Assert.False(failedData.TryGetProperty("errorMessage", out _), "Failed status must not emit the superseded errorMessage field.");
        Assert.Equal("Check the configured profile and retry.", failedData.GetProperty("hint").GetString(), "Failed job hint mismatch.");
        AssertSafeAiPayload(failedStatus, initialProject.Root, "failed status");

        JsonElement recent = await CallToolAsync(client, sessionId, 33, AgasMcpToolNames.AiStatus, new { limit = 1 });
        AssertToolSuccess(recent, saved: true, dirty: false, "ai.status limit=1 should succeed.");
        Assert.Equal(1, recent.GetProperty("data").GetProperty("jobs").GetArrayLength(), "ai.status limit=1 must return one recent job.");
        AssertSafeAiPayload(recent, initialProject.Root, "recent status list");

        JsonElement defaultRecent = await CallToolAsync(client, sessionId, 38, AgasMcpToolNames.AiStatus, new { });
        AssertToolSuccess(defaultRecent, saved: true, dirty: false, "ai.status default recent list should succeed.");
        Assert.Equal(10, defaultRecent.GetProperty("data").GetProperty("jobs").GetArrayLength(),
            "ai.status without jobId/limit must return the default 10 recent jobs.");
        AssertSafeAiPayload(defaultRecent, initialProject.Root, "default recent status list");

        JsonElement maximumRecent = await CallToolAsync(client, sessionId, 39, AgasMcpToolNames.AiStatus, new { limit = 50 });
        AssertToolSuccess(maximumRecent, saved: true, dirty: false, "ai.status limit=50 should succeed.");
        Assert.Equal(12, maximumRecent.GetProperty("data").GetProperty("jobs").GetArrayLength(),
            "ai.status limit=50 must return all 12 scoped fixture jobs.");
        AssertSafeAiPayload(maximumRecent, initialProject.Root, "maximum recent status list");

        JsonElement invalidLimit = await CallToolAsync(client, sessionId, 34, AgasMcpToolNames.AiStatus, new { limit = 51 });
        AssertToolError(invalidLimit, AgasMcpErrorCodes.InvalidOperation, "ai.status limit above 50 must fail.");

        RuntimeProject otherProject = CreateInitializedProject("mcp-http-ai-other-scope");
        provider.RegisterContext(otherProject.Context);
        JsonElement hiddenStatus = await CallToolAsync(client, sessionId, 35, AgasMcpToolNames.AiStatus, new { jobId = failedJobId });
        AssertToolError(hiddenStatus, AgasMcpErrorCodes.AiJobNotFound,
            "A job from another normalized project scope must not be visible.");
        JsonElement hiddenCancel = await CallToolAsync(client, sessionId, 36, AgasMcpToolNames.AiCancel, new { jobId = failedJobId });
        AssertToolError(hiddenCancel, AgasMcpErrorCodes.AiJobNotFound,
            "A job from another normalized project scope must not be cancelable.");
        JsonElement otherRecent = await CallToolAsync(client, sessionId, 37, AgasMcpToolNames.AiStatus, new { });
        AssertToolSuccess(otherRecent, saved: true, dirty: false, "Other project recent status should succeed.");
        Assert.Equal(0, otherRecent.GetProperty("data").GetProperty("jobs").GetArrayLength(),
            "Other project recent status must not include old project jobs.");
        provider.RegisterContext(initialProject.Context);
    }

    private static void AssertToolError(JsonElement structured, string expectedCode, string message)
    {
        Assert.False(structured.GetProperty("ok").GetBoolean(), message + " Structured=" + structured.GetRawText());
        Assert.Equal(expectedCode, structured.GetProperty("error").GetProperty("code").GetString(), message + " code mismatch.");
    }

    private static void AssertStrictToolShapeError(JsonElement structured, string message)
    {
        AssertToolError(structured, AgasMcpErrorCodes.InvalidOperation, message);
        Assert.Equal(
            "Tool arguments did not match the expected JSON shape.",
            structured.GetProperty("error").GetProperty("message").GetString(),
            message + " error message mismatch.");
    }

    private static void AssertSafeAiPayload(JsonElement structured, string projectRoot, string label)
    {
        string json = structured.GetRawText();
        string[] forbidden =
        {
            "data:image",
            "base64",
            "b64json",
            "b64_json",
            "apiKey",
            "api_key",
            "secret-api-key",
            "metadata",
            "manifest",
            projectRoot,
        };
        foreach (string token in forbidden)
        {
            Assert.False(json.Contains(token, StringComparison.OrdinalIgnoreCase),
                label + " leaked forbidden token: " + token + " Payload=" + json);
        }
        Assert.True(Encoding.UTF8.GetByteCount(json) < 64 * 1024, label + " response must remain lightweight.");
    }

    private async Task VerifyV2HttpWorkflowAsync(
        NetHttpClient client,
        string sessionId,
        AgasMcpProjectContextProvider provider,
        RuntimeProject project,
        ControllableAiRunner runner,
        string generatedRelativePath)
    {
        JsonElement snapshot = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.ProjectSnapshot, new
        {
            includeScenes = true,
            includeLayers = true,
            includeImages = true,
            includePrompts = false,
        });
        AssertToolSuccess(snapshot, saved: true, dirty: false, "v2 HTTP workflow must start from a clean snapshot.");
        AssertSafeV2Payload(snapshot, project.Root, "initial v2 snapshot");
        JsonElement snapshotData = snapshot.GetProperty("data");
        string sceneId = snapshotData.GetProperty("activeSceneId").GetString()
            ?? throw new InvalidOperationException("v2 snapshot omitted activeSceneId.");
        string layerId = snapshotData.GetProperty("layers")[0].GetProperty("layerId").GetString()
            ?? throw new InvalidOperationException("v2 snapshot omitted active layerId.");

        JsonElement selectionSet = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.SelectionSet, new
        {
            op = "setRegions",
            sceneId,
            selection = new { column = 0, row = 0, width = 2, height = 2 },
            replacement = new { column = 0, row = 0, width = 1, height = 1 },
        });
        AssertToolSuccess(selectionSet, saved: true, dirty: false, "selection.set must succeed over Streamable HTTP.");
        AssertSafeV2Payload(selectionSet, project.Root, "selection.set");

        JsonElement selectionGet = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.SelectionGet, new { });
        AssertToolSuccess(selectionGet, saved: true, dirty: false, "selection.get must succeed over Streamable HTTP.");
        AssertSafeV2Payload(selectionGet, project.Root, "selection.get");
        JsonElement selectionData = selectionGet.GetProperty("data");
        Assert.Equal(sceneId, selectionData.GetProperty("sceneId").GetString(), "selection.get scene mismatch.");
        AssertRegion(selectionData.GetProperty("selection"), 0, 0, 2, 2, "selection.get Q mismatch.");
        AssertRegion(selectionData.GetProperty("replacement"), 0, 0, 1, 1, "selection.get W mismatch.");

        JsonElement generate = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.AiGenerate, new
        {
            sceneId,
            layerId,
            prompt = "Generate a green HTTP acceptance tile",
            target = "NewImage",
        });
        AssertToolSuccess(generate, saved: true, dirty: false, "ai.generate must submit over Streamable HTTP.");
        AssertSafeV2Payload(generate, project.Root, "ai.generate");
        JsonElement generateData = generate.GetProperty("data");
        string succeededJobId = generateData.GetProperty("jobId").GetString()
            ?? throw new InvalidOperationException("ai.generate omitted jobId.");
        Assert.Equal("Queued", generateData.GetProperty("state").GetString(), "ai.generate must return immediately with a queued job.");
        Assert.Equal("MaskedEdit", generateData.GetProperty("effectiveOperation").GetString(), "AI operation must be derived from live Q/W state and adapter capability.");

        await WaitForAiStateAsync(client, sessionId, project.Root, succeededJobId, "Running", expectedDirty: false);
        runner.ReleaseSuccessfulJob();
        JsonElement succeeded = await WaitForAiStateAsync(client, sessionId, project.Root, succeededJobId, "Succeeded", expectedDirty: false);
        Assert.Equal(generatedRelativePath, succeeded.GetProperty("data").GetProperty("resultRelativePath").GetString(),
            "Succeeded status must expose only the fake artifact's project-relative path.");

        int beforeApplyCount = snapshotData.GetProperty("images").GetArrayLength();
        JsonElement apply = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.AiApply, new { jobId = succeededJobId });
        AssertToolSuccess(apply, saved: false, dirty: true, "First ai.apply must mutate without auto-saving.");
        AssertSafeV2Payload(apply, project.Root, "ai.apply");
        Assert.False(apply.GetProperty("data").GetProperty("alreadyApplied").GetBoolean(), "First ai.apply must not report alreadyApplied.");
        string appliedRelativePath = apply.GetProperty("data").GetProperty("appliedRelativePath").GetString()
            ?? throw new InvalidOperationException("ai.apply omitted appliedRelativePath.");

        JsonElement afterApplySnapshot = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.ProjectSnapshot, new
        {
            includeScenes = true,
            includeLayers = true,
            includeImages = true,
            includePrompts = false,
        });
        AssertToolSuccess(afterApplySnapshot, saved: false, dirty: true, "Snapshot after apply must remain dirty.");
        AssertSafeV2Payload(afterApplySnapshot, project.Root, "snapshot after apply");
        JsonElement imagesAfterApply = afterApplySnapshot.GetProperty("data").GetProperty("images");
        Assert.Equal(beforeApplyCount + 1, imagesAfterApply.GetArrayLength(), "First apply must add exactly one image.");
        Assert.JsonArrayContainsObjectWithString(imagesAfterApply, "imagePath", appliedRelativePath,
            "Snapshot after apply must contain the generated image reference.");

        JsonElement repeatedApply = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.AiApply, new { jobId = succeededJobId });
        AssertToolSuccess(repeatedApply, saved: false, dirty: true, "Repeated ai.apply must succeed idempotently.");
        AssertSafeV2Payload(repeatedApply, project.Root, "repeated ai.apply");
        Assert.True(repeatedApply.GetProperty("data").GetProperty("alreadyApplied").GetBoolean(), "Repeated ai.apply must report alreadyApplied.");
        JsonElement afterRepeatedApply = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.ProjectSnapshot, new
        {
            includeScenes = false,
            includeLayers = false,
            includeImages = true,
            includePrompts = false,
        });
        AssertToolSuccess(afterRepeatedApply, saved: false, dirty: true, "Snapshot after repeated apply must remain dirty.");
        AssertSafeV2Payload(afterRepeatedApply, project.Root, "snapshot after repeated apply");
        Assert.Equal(beforeApplyCount + 1, afterRepeatedApply.GetProperty("data").GetProperty("images").GetArrayLength(),
            "Repeated apply must not add another image.");

        JsonElement cancelGenerate = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.AiGenerate, new
        {
            sceneId,
            layerId,
            prompt = "Generate a job that will be canceled",
            target = "NewImage",
        });
        AssertToolSuccess(cancelGenerate, saved: false, dirty: true, "Second ai.generate must submit over Streamable HTTP.");
        AssertSafeV2Payload(cancelGenerate, project.Root, "cancel fixture generate");
        string canceledJobId = cancelGenerate.GetProperty("data").GetProperty("jobId").GetString()
            ?? throw new InvalidOperationException("Cancel fixture generate omitted jobId.");
        await WaitForAiStateAsync(client, sessionId, project.Root, canceledJobId, "Running", expectedDirty: true);
        JsonElement cancel = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.AiCancel, new { jobId = canceledJobId });
        AssertToolSuccess(cancel, saved: false, dirty: true, "ai.cancel must succeed over Streamable HTTP.");
        AssertSafeV2Payload(cancel, project.Root, "ai.cancel");
        Assert.True(cancel.GetProperty("data").GetProperty("canceled").GetBoolean(), "First ai.cancel must report canceled=true.");
        await WaitForAiStateAsync(client, sessionId, project.Root, canceledJobId, "Canceled", expectedDirty: true);

        JsonElement save = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.ProjectSave, new { });
        AssertToolSuccess(save, saved: true, dirty: false, "project.save must persist the HTTP-applied AI image.");
        AssertSafeV2Payload(save, project.Root, "v2 project.save");

        RuntimeProject reloaded = OpenProject(project.Root);
        provider.RegisterContext(reloaded.Context);
        JsonElement reloadedSnapshot = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.ProjectSnapshot, new
        {
            includeScenes = true,
            includeLayers = true,
            includeImages = true,
            includePrompts = false,
        });
        AssertToolSuccess(reloadedSnapshot, saved: true, dirty: false, "Reloaded HTTP snapshot must be clean.");
        AssertSafeV2Payload(reloadedSnapshot, project.Root, "reloaded v2 snapshot");
        JsonElement reloadedImages = reloadedSnapshot.GetProperty("data").GetProperty("images");
        Assert.JsonArrayContainsObjectWithString(reloadedImages, "imagePath", appliedRelativePath,
            "Reloaded HTTP snapshot must contain the persisted AI image reference.");
        AgasSceneResource reloadedScene = LoadFresh<AgasSceneResource>(AgasPaths.SceneFile(project.Root, sceneId));
        Assert.True(reloadedScene.Layers.SelectMany(item => item.Images).Any(item => item.ImagePath == appliedRelativePath),
            "Fresh .tres reload must contain the persisted AI image reference.");
    }

    private async Task<JsonElement> WaitForAiStateAsync(
        NetHttpClient client,
        string sessionId,
        string projectRoot,
        string jobId,
        string expectedState,
        bool expectedDirty)
    {
        string lastState = string.Empty;
        for (int attempt = 0; attempt < 200; attempt++)
        {
            JsonElement status = await CallToolAsync(client, sessionId, NextRpcId(), AgasMcpToolNames.AiStatus, new { jobId });
            AssertToolSuccess(status, saved: !expectedDirty, dirty: expectedDirty,
                "ai.status polling must succeed.");
            AssertSafeV2Payload(status, projectRoot, "ai.status " + expectedState);
            lastState = status.GetProperty("data").GetProperty("state").GetString() ?? string.Empty;
            if (string.Equals(lastState, expectedState, StringComparison.Ordinal))
            {
                return status;
            }
            if (lastState is "Failed" or "Canceled" or "Succeeded")
            {
                break;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException($"AI job {jobId} did not reach {expectedState}; last state was {lastState}.");
    }

    private static void AssertRegion(JsonElement region, int column, int row, int width, int height, string message)
    {
        Assert.Equal(column, region.GetProperty("column").GetInt32(), message + " column");
        Assert.Equal(row, region.GetProperty("row").GetInt32(), message + " row");
        Assert.Equal(width, region.GetProperty("width").GetInt32(), message + " width");
        Assert.Equal(height, region.GetProperty("height").GetInt32(), message + " height");
    }

    private static void AssertSafeV2Payload(JsonElement structured, string projectRoot, string label)
    {
        AssertSafeAiPayload(structured, projectRoot, label);
        string json = structured.GetRawText();
        Assert.False(json.Contains("http-acceptance-secret-key", StringComparison.Ordinal), label + " leaked the fake profile secret.");
    }

    private int NextRpcId() => Interlocked.Increment(ref _nextDynamicRpcId);

    private async Task CallAllEightToolsAndVerifyPersistenceAsync(
        NetHttpClient client,
        string sessionId,
        string root,
        AgasMcpProjectContextProvider provider)
    {
        JsonElement snapshot = await CallToolAsync(client, sessionId, 10, AgasMcpToolNames.ProjectSnapshot, new
        {
            includeScenes = true,
            includeLayers = true,
            includeImages = true,
            includePrompts = true,
        });
        AssertToolSuccess(snapshot, saved: true, dirty: false, "Initial project.snapshot should be clean.");
        string sceneId = snapshot.GetProperty("data").GetProperty("activeSceneId").GetString() ?? throw new InvalidOperationException("Missing activeSceneId.");
        string initialLayerId = snapshot.GetProperty("data").GetProperty("layers")[0].GetProperty("layerId").GetString() ?? throw new InvalidOperationException("Missing initial layerId.");
        Assert.Equal("scene-001", sceneId, "Initialized project should start on scene-001.");

        JsonElement assets = await CallToolAsync(client, sessionId, 11, AgasMcpToolNames.AssetsList, new
        {
            query = "http-runtime-test",
            extensions = new[] { "png" },
            limit = 10,
            offset = 0,
        });
        AssertToolSuccess(assets, saved: true, dirty: false, "assets.list should be clean.");
        Assert.JsonArrayContainsObjectWithString(
            assets.GetProperty("data").GetProperty("items"),
            "relativePath",
            "assets/http-runtime-test.png",
            "assets.list should find the test PNG by project-relative path.");

        JsonElement sceneRename = await CallToolAsync(client, sessionId, 12, AgasMcpToolNames.SceneMutate, new
        {
            op = "rename",
            sceneId,
            name = "HTTP Runtime Scene Renamed",
        });
        AssertToolSuccess(sceneRename, saved: false, dirty: true, "scene.mutate rename should dirty the project.");
        Assert.Equal(sceneId, sceneRename.GetProperty("data").GetProperty("changedSceneId").GetString(), "scene.mutate should report changed scene.");

        JsonElement layerAdd = await CallToolAsync(client, sessionId, 13, AgasMcpToolNames.LayerMutate, new
        {
            op = "add",
            sceneId,
            name = "HTTP Runtime Layer",
        });
        AssertToolSuccess(layerAdd, saved: false, dirty: true, "layer.mutate add should keep dirty state.");
        string layerId = layerAdd.GetProperty("data").GetProperty("changedLayerId").GetString() ?? throw new InvalidOperationException("Missing changedLayerId.");
        Assert.False(string.Equals(initialLayerId, layerId, StringComparison.Ordinal), "Added layer should be distinct from initial layer.");

        JsonElement imageAdd = await CallToolAsync(client, sessionId, 14, AgasMcpToolNames.ImageMutate, new
        {
            op = "add",
            sceneId,
            layerId,
            imagePath = "assets/http-runtime-test.png",
            x = 123.5,
            y = -45.25,
            scaleX = 1.5,
            scaleY = 0.75,
            rotationDegrees = 12.0,
            opacity = 0.8,
        });
        AssertToolSuccess(imageAdd, saved: false, dirty: true, "image.mutate add should keep dirty state.");
        string imageId = imageAdd.GetProperty("data").GetProperty("image").GetProperty("imageId").GetString() ?? throw new InvalidOperationException("Missing imageId.");
        Assert.True(imageId.StartsWith("image-", StringComparison.Ordinal), "image.mutate should create formal image id.");

        JsonElement promptAdd = await CallToolAsync(client, sessionId, 15, AgasMcpToolNames.PromptMutate, new
        {
            op = "add",
            sceneId,
            layerId,
            text = "HTTP runtime prompt persisted",
            enabled = true,
        });
        AssertToolSuccess(promptAdd, saved: false, dirty: true, "prompt.mutate add should keep dirty state.");
        string promptId = promptAdd.GetProperty("data").GetProperty("changedFragmentId").GetString() ?? throw new InvalidOperationException("Missing changedFragmentId.");
        Assert.True(promptId.StartsWith("prompt-", StringComparison.Ordinal), "prompt.mutate should create formal prompt id.");

        JsonElement viewSet = await CallToolAsync(client, sessionId, 16, AgasMcpToolNames.ViewSet, new
        {
            sceneId,
            cameraX = 42.25,
            cameraY = -17.5,
            zoom = 2.5,
        });
        AssertToolSuccess(viewSet, saved: false, dirty: true, "view.set should keep dirty state.");
        Assert.Equal(2.5, viewSet.GetProperty("data").GetProperty("zoom").GetDouble(), "view.set should echo zoom.");

        JsonElement save = await CallToolAsync(client, sessionId, 17, AgasMcpToolNames.ProjectSave, new { });
        AssertToolSuccess(save, saved: true, dirty: false, "project.save should persist all dirty resources and clear dirty state.");
        JsonElement saveData = save.GetProperty("data");
        Assert.JsonArrayContains(saveData.GetProperty("savedFiles"), ".agas/project.tres", "project.save should save project.tres after scene rename.");
        Assert.JsonArrayContains(saveData.GetProperty("savedFiles"), ".agas/scenes/scene-001.tres", "project.save should save dirty scene.");
        Assert.Equal(0, saveData.GetProperty("failedFiles").GetArrayLength(), "project.save should not report failed files.");
        Assert.Equal(0, saveData.GetProperty("pendingDirtyFiles").GetArrayLength(), "project.save should not leave pending dirty files.");

        RuntimeProject reloaded = OpenProject(root);
        provider.RegisterContext(reloaded.Context);
        VerifyReloadedTresState(root, sceneId, layerId, imageId, promptId);

        JsonElement reloadedSnapshot = await CallToolAsync(client, sessionId, 18, AgasMcpToolNames.ProjectSnapshot, new
        {
            includeScenes = true,
            includeLayers = true,
            includeImages = true,
            includePrompts = true,
        });
        AssertToolSuccess(reloadedSnapshot, saved: true, dirty: false, "project.snapshot after reload/new context should remain saved=true dirty=false.");
        JsonElement reloadedData = reloadedSnapshot.GetProperty("data");
        Assert.False(reloadedData.GetProperty("dirtyProject").GetBoolean(), "Reloaded snapshot should report dirtyProject=false.");
        Assert.False(reloadedData.GetProperty("dirtyLayout").GetBoolean(), "Reloaded snapshot should report dirtyLayout=false.");
        Assert.Equal(0, reloadedData.GetProperty("dirtySceneIds").GetArrayLength(), "Reloaded snapshot should report no dirty scenes.");
        Assert.JsonArrayContainsObjectWithString(reloadedData.GetProperty("images"), "imageId", imageId, "Reloaded snapshot should include persisted image.");
        Assert.JsonArrayContainsObjectWithString(reloadedData.GetProperty("prompts"), "fragmentId", promptId, "Reloaded snapshot should include persisted prompt.");
    }

    private async Task<string> ReplaceClosedSessionAndVerifyIsolationAsync(NetHttpClient client, string closedSessionId)
    {
        using HttpRequestMessage delete = CreateSessionRequest(HttpMethod.Delete, closedSessionId);
        using HttpResponseMessage deleteResponse = await client.SendAsync(delete, TestTimeout());
        Assert.True(deleteResponse.IsSuccessStatusCode,
            "DELETE /mcp should succeed for the import/package session. Body="
            + await deleteResponse.Content.ReadAsStringAsync(TestTimeout()));

        using HttpRequestMessage oldPost = CreateJsonRpcRequest("tools/list", includeProtocolHeader: true, closedSessionId);
        using HttpResponseMessage oldPostResponse = await client.SendAsync(oldPost, TestTimeout());
        Assert.Equal(HttpStatusCode.NotFound, oldPostResponse.StatusCode,
            "POST with the deleted import/package session should return 404.");

        string lifecycleSessionId = await InitializeSessionWithoutOriginAsync(client);
        await SendInitializedNotificationAsync(client, lifecycleSessionId);
        Assert.False(string.Equals(closedSessionId, lifecycleSessionId, StringComparison.Ordinal),
            "A newly initialized HTTP MCP session must receive a distinct session id.");

        JsonElement recent = await CallToolAsync(client, lifecycleSessionId, NextRpcId(), AgasMcpToolNames.ProjectsRecent, new { });
        AssertToolSuccess(recent, saved: true, dirty: false,
            "A new session must remain able to make tools/call after another session is deleted.");
        GD.Print("[AGAS-H2][MCP-HTTP-RUNTIME][SESSION-ISOLATION] DeletedA=404 SessionBToolsCall=PASS");
        return lifecycleSessionId;
    }

    private async Task VerifyDeleteInvalidatesOldSessionAsync(NetHttpClient client, string sessionId)
    {
        using HttpRequestMessage delete = CreateSessionRequest(HttpMethod.Delete, sessionId);
        using HttpResponseMessage deleteResponse = await client.SendAsync(delete, TestTimeout());
        Assert.True(deleteResponse.IsSuccessStatusCode, "DELETE /mcp should succeed for active session. Body=" + await deleteResponse.Content.ReadAsStringAsync(TestTimeout()));

        using HttpRequestMessage oldPost = CreateJsonRpcRequest("tools/list", includeProtocolHeader: true, sessionId);
        using HttpResponseMessage oldPostResponse = await client.SendAsync(oldPost, TestTimeout());
        Assert.Equal(HttpStatusCode.NotFound, oldPostResponse.StatusCode, "POST with deleted session should return 404.");
    }

    private static void VerifyReloadedTresState(string root, string sceneId, string layerId, string imageId, string promptId)
    {
        AgasProjectResource project = LoadFresh<AgasProjectResource>(AgasPaths.ProjectFile(root));
        AgasSceneResource scene = LoadFresh<AgasSceneResource>(AgasPaths.SceneFile(root, sceneId));
        Assert.Equal("HTTP Runtime Scene Renamed", project.Scenes.First(item => item.Id == sceneId).Name, "Reloaded project index should preserve scene rename.");
        Assert.Equal("HTTP Runtime Scene Renamed", scene.Name, "Reloaded scene should preserve scene rename.");
        AgasLayerResource layer = scene.Layers.First(item => item.Id == layerId);
        Assert.Equal("HTTP Runtime Layer", layer.Name, "Reloaded scene should preserve added layer.");
        ImageInstanceResource image = layer.Images.First(item => item.Id == imageId);
        Assert.Equal("assets/http-runtime-test.png", image.ImagePath, "Reloaded image should preserve project-relative image path.");
        Assert.Equal(123.5, image.X, "Reloaded image should preserve X.");
        Assert.Equal(-45.25, image.Y, "Reloaded image should preserve Y.");
        Assert.Equal(1.5, image.ScaleX, "Reloaded image should preserve ScaleX.");
        Assert.Equal(0.75, image.ScaleY, "Reloaded image should preserve ScaleY.");
        Assert.Equal(12.0, image.RotationDegrees, "Reloaded image should preserve rotation.");
        Assert.Equal(0.8, image.Opacity, "Reloaded image should preserve opacity.");
        PromptFragmentResource prompt = layer.PromptFragments.First(item => item.Id == promptId);
        Assert.Equal("HTTP runtime prompt persisted", prompt.Text, "Reloaded prompt should preserve text.");
        Assert.True(prompt.Enabled, "Reloaded prompt should preserve enabled=true.");
        Assert.Equal(42.25, scene.ViewState.CameraX, "Reloaded view should preserve camera X.");
        Assert.Equal(-17.5, scene.ViewState.CameraY, "Reloaded view should preserve camera Y.");
        Assert.Equal(2.5, scene.ViewState.Zoom, "Reloaded view should preserve zoom.");
    }

    private async Task<JsonElement> CallToolAsync(NetHttpClient client, string sessionId, int id, string toolName, object arguments)
    {
        using JsonDocument document = await SendJsonRpcAsync(client, sessionId, new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new
            {
                name = toolName,
                arguments,
            },
        });

        JsonElement result = document.RootElement.GetProperty("result");
        JsonElement structured = result.GetProperty("structuredContent").Clone();
        bool isError = result.TryGetProperty("isError", out JsonElement isErrorElement)
            && isErrorElement.GetBoolean();
        Assert.Equal(
            !structured.GetProperty("ok").GetBoolean(),
            isError,
            toolName + " HTTP isError must match the structured AGAS response.");
        return structured;
    }

    private static Dictionary<string, object?> ToolArguments(params (string Name, object? Value)[] entries) =>
        entries.ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.Ordinal);

    private static void AssertToolSuccess(JsonElement structured, bool saved, bool dirty, string message)
    {
        Assert.True(structured.GetProperty("ok").GetBoolean(), message + " Structured=" + structured.GetRawText());
        Assert.Equal(saved, structured.GetProperty("saved").GetBoolean(), message + " saved mismatch.");
        Assert.Equal(dirty, structured.GetProperty("dirty").GetBoolean(), message + " dirty mismatch.");
        Assert.True(structured.TryGetProperty("data", out _), message + " should include data.");
    }

    private async Task<JsonDocument> SendJsonRpcAsync(NetHttpClient client, string sessionId, object payload)
    {
        using HttpRequestMessage request = CreateSessionRequest(HttpMethod.Post, sessionId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = JsonBody(payload);
        using HttpResponseMessage response = await client.SendAsync(request, TestTimeout());
        string body = await response.Content.ReadAsStringAsync(TestTimeout());
        Assert.True(response.IsSuccessStatusCode, "JSON-RPC request should succeed. Status=" + response.StatusCode + " Body=" + body);
        return JsonDocument.Parse(ExtractJsonPayload(body));
    }

    private RuntimeProject CreateInitializedProject(string name)
    {
        string root = NewTempRoot(name);
        Directory.CreateDirectory(root);
        AgasPersistenceResult result = new AgasProjectInitializer().EnsureInitialized(root);
        Assert.True(result.Success, "Initialization should succeed for " + root + ": " + result.ErrorMessage);
        Assert.True(File.Exists(AgasPaths.ProjectFile(root)), "Initialized project should create project.tres.");
        Assert.True(File.Exists(AgasPaths.SceneFile(root, "scene-001")), "Initialized project should create scene-001.tres.");
        return OpenProject(root);
    }

    private static RuntimeProject OpenProject(string root)
    {
        AgasProjectResource project = LoadFresh<AgasProjectResource>(AgasPaths.ProjectFile(root));
        WorkspaceLayoutResource layout = LoadFresh<WorkspaceLayoutResource>(AgasPaths.WorkspaceLayoutFile(root));
        Dictionary<string, AgasSceneResource> cache = new(StringComparer.Ordinal);
        foreach (SceneIndexResource sceneIndex in project.Scenes)
        {
            cache[sceneIndex.Id] = LoadFresh<AgasSceneResource>(AgasPaths.SceneFile(root, sceneIndex.Id));
        }

        ProjectSession session = new();
        session.Reset(root);
        session.Configure(loadScene: sceneId =>
            {
                string path = AgasPaths.SceneFile(root, sceneId);
                return File.Exists(path) ? LoadFresh<AgasSceneResource>(path) : null;
            });
        session.AcceptProject(project);
        foreach (AgasSceneResource scene in cache.Values)
        {
            session.CacheScene(scene);
        }
        session.SetCurrentScene(cache[project.ActiveSceneId], notify: false);

        AgasMcpProjectContext context = new(
            session,
            layout);
        AgasMcpProjectFacade facade = new(new AgasMcpProjectContextProvider());
        return new RuntimeProject(root, context, facade);
    }

    private HttpRequestMessage CreateJsonRpcRequest(string method, bool includeProtocolHeader, string? sessionId = null)
    {
        HttpRequestMessage request = new(HttpMethod.Post, _testEndpoint)
        {
            Content = JsonBody(new
            {
                jsonrpc = "2.0",
                id = 1,
                method,
                @params = method == "initialize" ? new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { },
                    clientInfo = new { name = "agas-godot-http-runtime", version = "1.0.0" },
                } : null,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (includeProtocolHeader)
        {
            request.Headers.Add(AgasMcpProtocolHeaderMiddleware.ProtocolVersionHeader, ProtocolVersion);
        }
        if (sessionId != null)
        {
            request.Headers.Add(AgasMcpSessionDeleteMiddleware.SessionIdHeader, sessionId);
        }
        return request;
    }

    private HttpRequestMessage CreateSessionRequest(HttpMethod method, string? sessionId)
    {
        HttpRequestMessage request = new(method, _testEndpoint);
        request.Headers.Add(AgasMcpProtocolHeaderMiddleware.ProtocolVersionHeader, ProtocolVersion);
        if (sessionId != null)
        {
            request.Headers.Add(AgasMcpSessionDeleteMiddleware.SessionIdHeader, sessionId);
        }
        return request;
    }

    private static StringContent JsonBody(object payload) => new(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

    private static void PrintLoadedAssembly(string assemblyName)
    {
        Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(item => string.Equals(item.GetName().Name, assemblyName, StringComparison.Ordinal));
        string path;
        if (assembly == null)
        {
            path = "<not loaded>";
        }
        else if (!string.IsNullOrWhiteSpace(assembly.Location))
        {
            path = assembly.Location;
        }
        else if (!string.Equals(assembly.ManifestModule.FullyQualifiedName, "<Unknown>", StringComparison.Ordinal))
        {
            path = assembly.ManifestModule.FullyQualifiedName;
        }
        else
        {
            string outputPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
            path = File.Exists(outputPath) ? outputPath + " (Assembly.Location=<Unknown>)" : "<Unknown>";
        }
        GD.Print("[AGAS-H2][MCP-HTTP-RUNTIME][ASM] " + assemblyName + " => " + path);
    }

    private static string ExtractJsonPayload(string body)
    {
        if (body.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return body;
        }

        string? data = body.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => line["data:".Length..].TrimStart())
            .FirstOrDefault(line => line.StartsWith("{", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(data), "Expected JSON or SSE data JSON body. Body=" + body);
        return data!;
    }

    private static void CreateTinyPngAsset(string root, string relativePath)
    {
        CreatePngAsset(root, relativePath, 1, 1, Colors.White);
    }

    private static void CreatePngAsset(string root, string relativePath, int width, int height, Color color)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using Image image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        image.Fill(color);
        Error error = image.SavePng(path);
        Assert.Equal(Error.Ok, error, "Failed to create PNG fixture.");
    }

    private static void CreateSolidPngAsset(string root, string relativePath)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using Image image = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
        image.Fill(Colors.Green);
        Error error = image.SavePng(path);
        Assert.Equal(Error.Ok, error, "Failed to create generated PNG fixture.");
    }

    private string NewTempRoot(string name)
    {
        string root = Path.Combine(Path.GetTempPath(), "agas-h2-godot-mcp-http", name + "-" + Guid.NewGuid().ToString("N"));
        _tempRoots.Add(root);
        return root;
    }

    private void CleanupTempRoots()
    {
        foreach (string root in _tempRoots.AsEnumerable().Reverse())
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr("[AGAS-H2][MCP-HTTP-RUNTIME][WARN] Failed to cleanup temp root ", root, ": ", ex.Message);
            }
        }
    }

    private static T LoadFresh<T>(string absolutePath) where T : Resource
    {
        Resource? loaded = ResourceLoader.Load(absolutePath, string.Empty, ResourceLoader.CacheMode.Ignore);
        Assert.True(loaded is T, "Expected to load " + typeof(T).Name + " from " + absolutePath + ", actual " + (loaded?.GetType().FullName ?? "null"));
        return (T)loaded!;
    }

    private static NetHttpClient CreateRawClient() => new() { Timeout = TimeSpan.FromSeconds(10) };

    private async Task<bool> CanConnectToTestPortAsync()
    {
        using TcpClient client = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(500));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, new Uri(_testEndpoint).Port, timeout.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private bool CanBindTestPort()
    {
        TcpListener listener = new(IPAddress.Loopback, new Uri(_testEndpoint).Port);
        try
        {
            listener.Start();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task WaitForTestPortReleasedAsync()
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (CanBindTestPort())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.True(CanBindTestPort(), "MCP isolated test port should be released after lifecycle stop/disable.");
    }

    private static CancellationToken TestTimeout() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    private async Task ProcessFramesAsync(int count)
    {
        for (int index = 0; index < count; index++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private sealed record RuntimeProject(string Root, AgasMcpProjectContext Context, AgasMcpProjectFacade Facade);

    private sealed class ControllableAiRunner : IAiGenerationRunner
    {
        private readonly string _successfulRelativePath;
        private readonly TaskCompletionSource<bool> _successfulRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public ControllableAiRunner(string successfulRelativePath) => _successfulRelativePath = successfulRelativePath;

        public void ReleaseSuccessfulJob() => _successfulRelease.TrySetResult(true);

        public async Task<AiGenerationResult> GenerateAsync(
            AiGenerationProviderProfile profile,
            AiGenerationRequest request,
            CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                await _successfulRelease.Task.WaitAsync(cancellationToken);
                return new AiGenerationResult(
                    _successfulRelativePath,
                    "image/png",
                    68,
                    AiGenerationEffectiveOperation.MaskedEdit,
                    AiGenerationEditGuarantee.ProviderMasked,
                    request.Width,
                    request.Height);
            }

            if (call == 2)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Canceled fake generation unexpectedly resumed.");
            }

            throw new InvalidOperationException("Unexpected fake AI generation call.");
        }
    }

    private sealed class ProfileOverrideBridge : IAgasAiGenerationWorkspaceBridge
    {
        private readonly IAgasAiGenerationWorkspaceBridge _inner;
        private readonly AiGenerationProviderProfile _profile;

        public ProfileOverrideBridge(IAgasAiGenerationWorkspaceBridge inner, AiGenerationProviderProfile profile)
        {
            _inner = inner;
            _profile = profile;
        }

        public AiGenerationCanvasContext? CaptureCurrentContext(CanvasPatchLayerScope scope) =>
            _inner.CaptureCurrentContext(scope);

        public AiGenerationProfileResolution ResolveProfile(string? profileId = null) =>
            string.IsNullOrWhiteSpace(profileId) || string.Equals(profileId, _profile.Id, StringComparison.Ordinal)
                ? AiGenerationProfileResolution.Ok(_profile)
                : AiGenerationProfileResolution.Fail("Unknown HTTP acceptance profile.");

        public string ResolvePrompt(string? promptOverride = null) => _inner.ResolvePrompt(promptOverride);

        public void ReflectMcpPrompt(string prompt) => _inner.ReflectMcpPrompt(prompt);

        public AiGenerationPreparationResult Prepare(AiGenerationPreparationInput input) => _inner.Prepare(input);

        public AiGeneratedImageApplyResult Apply(
            AiGeneratedImageApplication application,
            AiGenerationApplyPersistence persistence,
            AiGenerationApplyContextPolicy contextPolicy = AiGenerationApplyContextPolicy.FrozenBoundaryOnly) =>
            _inner.Apply(application, persistence, contextPolicy);

        public void Discard(AiGeneratedImageApplication application) => _inner.Discard(application);

        public void ReportExternalJobStatus(AiGenerationExternalJobStatus status) =>
            _inner.ReportExternalJobStatus(status);
    }

    private static class Assert
    {
        public static void True(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        public static void False(bool condition, string message) => True(!condition, message);

        public static void Empty<T>(IEnumerable<T> values, string message)
        {
            T[] array = values.ToArray();
            if (array.Length != 0)
            {
                throw new InvalidOperationException(message + " Actual=" + string.Join(", ", array));
            }
        }

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(message + " Expected=" + expected + " Actual=" + actual);
            }
        }

        public static void JsonArrayContains(JsonElement array, string expected, string message)
        {
            foreach (JsonElement item in array.EnumerateArray())
            {
                if (string.Equals(item.GetString(), expected, StringComparison.Ordinal))
                {
                    return;
                }
            }

            throw new InvalidOperationException(message + " Missing=" + expected + " Actual=" + array.GetRawText());
        }

        public static void JsonArrayContainsObjectWithString(JsonElement array, string propertyName, string expected, string message)
        {
            foreach (JsonElement item in array.EnumerateArray())
            {
                if (item.TryGetProperty(propertyName, out JsonElement value)
                    && string.Equals(value.GetString(), expected, StringComparison.Ordinal))
                {
                    return;
                }
            }

            throw new InvalidOperationException(message + " Missing " + propertyName + "=" + expected + " Actual=" + array.GetRawText());
        }
    }
}
