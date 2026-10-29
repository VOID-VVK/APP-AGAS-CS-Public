using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AGAS.UI;
using AGAS.UI.Dialogs;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Features.Ai.UI;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class McpAiModelSelectionAcceptance : Node
{
    public override async void _Ready()
    {
        int code = 0;
        Main? main = null;
        using var upstream = new ModelUpstream();
        try
        {
            string home = System.Environment.GetEnvironmentVariable("AGAS_HOME") ?? string.Empty;
            Require(home.Contains("agas-godot-acceptance."), "Use the isolated acceptance runner.");
            var store = new AgasAiSettingsStore(home);
            var first = new AgasAiProfile("primary", "Primary", AgasAiProviderIds.AxonCatNewApi,
                "ignored", upstream.Url, "synthetic-primary-key");
            var second = first with { Id = "backup", Name = "Backup", BaseUrl = upstream.Url + "backup/", ApiKey = "synthetic-backup-key" };
            store.SaveProfiles(new AgasAiProfilesDocument(AgasAiProfilesDocument.CurrentResourceVersion,
                first.Id, "gpt-image-2", new[] { first, second }));
            main = new Main(new RecentProjectsStore(Path.Combine(home, "recent-projects.tres")), disableMcpRuntimeForTesting: true);
            AddChild(main);
            string projectRoot = Path.Combine(home, "mcp-model-project");
            Directory.CreateDirectory(projectRoot);
            Invoke(main, "OpenOrCreateWorkspace", projectRoot);
            MainWorkspace workspace = Required<MainWorkspace>(main, "MainWorkspace");
            AiGenerationPanel panel = Required<AiGenerationPanel>(workspace, "AiGenerationPanel");
            await Until(() => !panel.IsRefreshingModels && panel.ResolveProviderProfile().Success);
            Invoke(main, "EnsureSettingsDialog");
            SettingsDialog dialog = Field<SettingsDialog>(main, "_settingsDialog");
            dialog.Open(main);

            var dirty = new AgasMcpDirtyState();
            workspace.SetMcpDirtyState(dirty);
            Require(workspace.TryBindMcpDirtyState(out string error), error);
            AgasMcpProjectContext context = workspace.GetCurrentMcpProjectContext()
                ?? throw new InvalidOperationException("Missing live project context.");
            var contexts = new AgasMcpProjectContextProvider();
            contexts.RegisterContext(context.WithAiGenerationWorkspace(workspace.AiGenerationWorkspaceBridge));
            var projectFacade = new AgasMcpProjectFacade(contexts, dirty, new AgasMcpPathValidator());
            string sceneId = context.Project.ActiveSceneId;
            string layerId = context.CurrentScene!.ActiveLayerId;
            Require(projectFacade.SelectionSet(new AgasSelectionSetInput("setRegions", sceneId,
                new AgasMcpGridRegionDto(0, 0, 1, 1))).Ok, "The live canvas must accept a Q selection.");
            using var jobs = new AgasMcpAiJobStore();
            var runner = new CapturingRunner();
            await using var scheduler = new AgasMcpAiJobScheduler(new AgasAiGenerationCoordinator(), jobs, AgasMcpAiFacade.MapRunnerFailure);
            var generation = new AgasMcpAiFacade(contexts, dirty, jobs, scheduler, runner);
            var settings = new AgasMcpAiSettingsFacade(new AgasAiProfileSettingsService(store),
                dialog.McpAiMutationBridge, dirty, dialog.CaptureEffectiveAiSelection);

            // A new model request cannot complete while the normal MCP sequence runs.
            upstream.Block();
            int beforeSet = upstream.RequestCount;
            Require(dialog.StageMcpAiMutation(context.ProjectId), "The model change must acquire its UI lease.");
            AgasMcpToolResponse committed = settings.SetModel(new AgasAiModelSetInput("gpt-image-2.5-flare"));
            Require(committed.Ok, committed.Error?.Message ?? "Model commit failed.");
            AgasMcpToolResponse finalized = settings.FinalizeStagedUiOnMainThread(committed);
            dialog.FinishMcpAiMutation();
            Require(finalized.Data is AgasMcpAiModelSetDataDto { UiSynchronized: true }, "The real Main settings event must finish UI synchronization.");
            AgasMcpToolResponse generated = generation.Generate(new AgasAiGenerateInput(sceneId, layerId, "A blue tile", Target: "NewImage"));
            Require(generated.Ok, generated.Error?.Message ?? "Generate immediately after model.set must be accepted.");
            await Until(() => runner.Started.Task.IsCompleted);
            AiGenerationProviderProfile execution = await runner.Started.Task;
            Require(execution.ModelName == "gpt-image-2.5-flare" && execution.Id == first.Id,
                "The generation runner must receive the newly committed model and existing connection.");
            Require(!panel.IsRefreshingModels && upstream.RequestCount == beforeSet,
                "A model-only MCP commit must not invalidate the ready model catalog or query it again.");
            var selector = Required<OptionButton>(workspace, "AiGenerationProfileSelector");
            Require(selector.GetItemMetadata(selector.Selected).AsString() == execution.ModelName,
                "The toolbar must reflect the MCP-selected model before generation starts.");
            await scheduler.StopAsync();

            // Saving a different key must never reuse the previous key's permissions.
            LineEdit key = Required<LineEdit>(dialog, "AiApiKey");
            key.Text = "synthetic-rotated-key";
            key.EmitSignal(LineEdit.SignalName.TextChanged, key.Text);
            Required<Button>(dialog, "SaveAiProfile").EmitSignal(Button.SignalName.Pressed);
            Require(panel.IsRefreshingModels && !panel.ResolveProviderProfile().Success,
                "Saving a new key must require discovery before the connection can generate.");
            await Until(() => upstream.RequestCount > beforeSet);
            upstream.Release();
            await Until(() => !panel.IsRefreshingModels && panel.ResolveProviderProfile().Success);
            Require(panel.ResolveProviderProfile().Profile!.ApiKey == "synthetic-rotated-key",
                "Resolved generation must use the newly saved key.");
            Require(panel.ProfileOptions.Count == 1 && !panel.SelectModelByName("gpt-image-2"),
                "The new key's narrower catalog must remove the previous key's model permissions.");

            upstream.Block();
            int beforeActivate = upstream.RequestCount;
            Require(dialog.StageMcpAiMutation(context.ProjectId), "Connection activation must acquire its UI lease.");
            committed = settings.Activate(new AgasAiProfileActivateInput(second.Id));
            Require(committed.Ok, committed.Error?.Message ?? "Connection activation failed.");
            finalized = settings.FinalizeStagedUiOnMainThread(committed);
            dialog.FinishMcpAiMutation();
            Require(finalized.Data is AgasMcpAiProfileActivatedDataDto { UiSynchronized: true }, "Connection activation must synchronize the actual workspace.");
            Require(panel.IsRefreshingModels && !panel.ResolveProviderProfile().Success,
                "A different profile endpoint must not inherit the previous connection's ready catalog.");
            await Until(() => upstream.RequestCount > beforeActivate);
            upstream.Release();
            await Until(() => !panel.IsRefreshingModels && panel.ResolveProviderProfile().Success);
            execution = panel.ResolveProviderProfile().Profile!;
            Require(execution.Id == second.Id && execution.BaseUri == new Uri(second.BaseUrl)
                && execution.ApiKey == second.ApiKey && execution.ModelName == "gpt-image-2",
                "After discovery, the new connection must use its own endpoint, key, and available model.");
            Require(panel.ProfileOptions.Count == 1 && !panel.SelectModelByName("gpt-image-2.5-flare"),
                "The new endpoint's catalog must not inherit models from the previous connection.");
            GD.Print("[AGAS][MCP-AI-MODEL-SELECTION][PASS] actual Main events, immediate generation, selected model, no model-only refresh, key rotation, endpoint/profile switch");
        }
        catch (Exception exception)
        {
            code = 1;
            GD.PushError($"[AGAS][MCP-AI-MODEL-SELECTION][FAIL] {exception}");
        }
        finally
        {
            upstream.Release();
            main?.QueueFree();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        GetTree().Quit(code);
    }

    private async Task Until(Func<bool> condition)
    {
        for (int frame = 0; frame < 600; frame++)
        {
            if (condition()) return;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        throw new InvalidOperationException("The acceptance operation did not settle in 600 frames.");
    }

    private static T Required<T>(Node root, string name) where T : Node =>
        root.FindChild(name, true, false) as T ?? throw new InvalidOperationException($"Missing node: {name}");
    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Invoke(object owner, string name, params object[] arguments) =>
        owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, arguments);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CapturingRunner : IAiGenerationRunner
    {
        public TaskCompletionSource<AiGenerationProviderProfile> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AiGenerationResult> GenerateAsync(AiGenerationProviderProfile profile,
            AiGenerationRequest request, CancellationToken cancellationToken)
        {
            Started.TrySetResult(profile);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("The fixture runner must only stop through cancellation.");
        }
    }

    private sealed class ModelUpstream : IDisposable
    {
        private readonly HttpListener _listener = new();
        private TaskCompletionSource<bool>? _gate;
        private int _requests;
        public string Url { get; }
        public int RequestCount => Volatile.Read(ref _requests);

        public ModelUpstream()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            int port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Listen();
        }

        public void Block() => _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() { _gate?.TrySetResult(true); _gate = null; }

        private async Task Listen()
        {
            try
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext request = await _listener.GetContextAsync();
                    _ = Respond(request);
                }
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException) { }
        }

        private async Task Respond(HttpListenerContext request)
        {
            try
            {
                Interlocked.Increment(ref _requests);
                TaskCompletionSource<bool>? gate = _gate;
                if (gate != null) await gate.Task;
                string models = request.Request.Url!.AbsolutePath.StartsWith("/backup/", StringComparison.Ordinal)
                    ? "{\"data\":[{\"id\":\"gpt-image-2\"}]}"
                    : request.Request.Headers["Authorization"] == "Bearer synthetic-rotated-key"
                        ? "{\"data\":[{\"id\":\"gpt-image-2.5-flare\"}]}"
                        : "{\"data\":[{\"id\":\"gpt-image-2\"},{\"id\":\"gpt-image-2.5-flare\"}]}";
                byte[] payload = Encoding.UTF8.GetBytes(models);
                request.Response.ContentType = "application/json";
                request.Response.ContentLength64 = payload.Length;
                await request.Response.OutputStream.WriteAsync(payload);
            }
            catch (Exception exception) when (exception is IOException or HttpListenerException or ObjectDisposedException) { }
            finally { request.Response.Close(); }
        }

        public void Dispose() { Release(); _listener.Close(); }
    }
}
