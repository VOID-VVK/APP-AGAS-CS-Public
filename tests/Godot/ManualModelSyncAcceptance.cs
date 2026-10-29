using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AGAS.UI;
using AGAS.UI.Dialogs;
using Agas.Features.Canvas;
using AppAgasCs.Application.Contracts;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Features.Ai.UI;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class ManualModelSyncAcceptance : Node
{
    public override async void _Ready()
    {
        int code = 0;
        MainWorkspace? workspace = null;
        try
        {
            string home = System.Environment.GetEnvironmentVariable("AGAS_HOME") ?? string.Empty;
            Require(home.Contains("agas-godot-acceptance."), "Run only in the disposable acceptance runner.");
            using var upstream = new ModelUpstream();
            var store = new AgasAiSettingsStore(home);
            var profile = new AgasAiProfile("manual-sync", "Local test", AgasAiProviderIds.AxonCatNewApi,
                "ignored", upstream.Url, "synthetic-key");
            store.SaveProfiles(new AgasAiProfilesDocument(AgasAiProfilesDocument.CurrentResourceVersion,
                profile.Id, "gpt-image-2.5-flare", new[] { profile }));
            workspace = new MainWorkspace();
            AddChild(workspace);
            var selector = Required<OptionButton>(workspace, "AiGenerationProfileSelector");
            var sync = Required<Button>(workspace, "AiModelSyncButton");
            var status = Required<Label>(workspace, "SaveStatusLabel");
            await Until(() => selector.ItemCount == 2 && !sync.Disabled);
            Require(sync.Text == "↻ 同步模型", "A permanent manual sync button must sit beside the selector.");
            Require(SelectedModel(selector) == "gpt-image-2.5-flare", "Initial choice must be preserved.");

            int requests = upstream.RequestCount;
            selector.ShowPopup();
            for (int frame = 0; frame < 5; frame++) await Frame();
            Require(upstream.RequestCount == requests, "Opening the dropdown must not fetch models.");
            selector.GetPopup().Hide();

            upstream.Models = new[] { "gpt-image-2", "gpt-image-2.5-flare", "future-manual-model" };
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            upstream.Gate = gate;
            sync.EmitSignal(Button.SignalName.Pressed);
            Require(sync.Disabled && sync.Text == "同步中…", "An in-flight sync must disable the button and show progress.");
            Require(status.Text.Contains("正在同步最新上游模型"), "Manual sync must report progress.");
            sync.EmitSignal(Button.SignalName.Pressed);
            sync.EmitSignal(Button.SignalName.Pressed);
            await Until(() => upstream.RequestCount == requests + 1);
            for (int frame = 0; frame < 5; frame++) await Frame();
            Require(upstream.RequestCount == requests + 1, "Repeated clicks must share the active request.");
            gate.SetResult(true);
            await Until(() => !sync.Disabled && selector.ItemCount == 3);
            Require(SelectedModel(selector) == "gpt-image-2.5-flare"
                && store.LoadProfiles().ActiveModelName == "gpt-image-2.5-flare", "Sync must retain the selected model.");
            Require(Enumerable.Range(0, selector.ItemCount).Any(index =>
                selector.GetItemMetadata(index).AsString() == "future-manual-model"), "The toolbar must show a newly added upstream model.");
            Require(status.Text.Contains("已同步最新上游模型"), "Successful sync must report completion.");

            upstream.Gate = null;
            upstream.Status = 503;
            requests = upstream.RequestCount;
            sync.EmitSignal(Button.SignalName.Pressed);
            await Until(() => upstream.RequestCount == requests + 1 && !sync.Disabled);
            Require(selector.ItemCount == 3 && SelectedModel(selector) == "gpt-image-2.5-flare",
                "Transient upstream failure must keep the cached list and selection.");
            Require(status.Text.Contains("最近的模型列表"), "Fallback must be visible to the user.");

            upstream.Status = 403;
            requests = upstream.RequestCount;
            sync.EmitSignal(Button.SignalName.Pressed);
            await Until(() => upstream.RequestCount == requests + 1 && !sync.Disabled);
            Require(selector.ItemCount == 0 && selector.Disabled && status.Text.Contains("403"),
                "Permission denial must invalidate models while leaving manual retry available.");
            upstream.Status = 200;
            sync.EmitSignal(Button.SignalName.Pressed);
            await Until(() => selector.ItemCount == 3 && !sync.Disabled);
            Require(SelectedModel(selector) == "gpt-image-2.5-flare", "Retry must recover the selected model.");

            gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            upstream.Gate = gate;
            requests = upstream.RequestCount;
            sync.EmitSignal(Button.SignalName.Pressed);
            await Until(() => upstream.RequestCount == requests + 1);
            upstream.Gate = null;
            upstream.Models = new[] { "gpt-image-2.5-flare" };
            workspace.ReloadAiProfiles();
            await Until(() => upstream.RequestCount == requests + 2 && !sync.Disabled && selector.ItemCount == 1);
            gate.SetResult(true);
            for (int frame = 0; frame < 5; frame++) await Frame();
            Require(selector.ItemCount == 1 && SelectedModel(selector) == "gpt-image-2.5-flare",
                "Superseded sync must not overwrite the latest reload.");
            await VerifyContextGeneration(workspace, upstream, sync, selector, home);
            GD.Print("[AGAS][MANUAL-MODEL-SYNC][PASS] toolbar, no popup refresh, latest models, selection, deduplication, fallback, denial, retry, superseded request, two synchronized models across all five send commands");
        }
        catch (Exception exception)
        {
            code = 1;
            GD.PushError($"[AGAS][MANUAL-MODEL-SYNC][FAIL] {exception}");
        }
        finally
        {
            workspace?.QueueFree();
            await Frame();
        }
        GetTree().Quit(code);
    }

    private async Task VerifyContextGeneration(MainWorkspace workspace, ModelUpstream upstream,
        Button sync, OptionButton selector, string home)
    {
        upstream.Models = new[] { "gpt-image-2.5-flare", "gemini-image" };
        string projectRoot = Path.Combine(home, "right-click-project");
        Directory.CreateDirectory(projectRoot);
        workspace.OpenProject(projectRoot);
        Require(workspace.CurrentScene != null, "The disposable canvas project must open successfully.");
        workspace.CurrentScene!.Layers.Add(new AgasLayerResource
        {
            Id = "reference-layer",
            Name = "Reference layer",
            Order = workspace.CurrentScene.Layers.Count,
        });
        sync.EmitSignal(Button.SignalName.Pressed);
        await Until(() => !sync.Disabled && selector.ItemCount == 2);
        var panel = Required<AiGenerationPanel>(workspace, "AiGenerationPanel");
        var runner = new CapturingRunner();
        typeof(AiGenerationPanel).GetField("_runner", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(panel, runner);
        var canvas = Required<CanvasWorkspace>(workspace, "CanvasWorkspaceHost");
        foreach (string modelName in new[] { "gemini-image", "gpt-image-2.5-flare" })
        {
            int selected = Enumerable.Range(0, selector.ItemCount).Single(index =>
                selector.GetItemMetadata(index).AsString() == modelName);
            selector.Select(selected);
            selector.EmitSignal(OptionButton.SignalName.ItemSelected, (long)selected);
            upstream.Gate = null;
            int beforeSync = upstream.RequestCount;
            sync.EmitSignal(Button.SignalName.Pressed);
            await Until(() => !sync.Disabled && upstream.RequestCount == beforeSync + 1);
            Require(panel.ResolveProviderProfile().Success && SelectedModel(selector) == modelName,
                "The selected model must be ready after explicit synchronization finishes.");

            canvas.SetActiveRegionTool(CanvasRegionTool.Selection);
            Vector2 start = canvas.WorldToScreen(new Vector2(1, 1));
            Vector2 end = canvas.WorldToScreen(new Vector2(47, 47));
            canvas._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = start });
            canvas._GuiInput(new InputEventMouseMotion { Position = end, Relative = end - start, ButtonMask = MouseButtonMask.Left });
            canvas._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = end });
            Require(canvas.SelectionRegion.HasValue, "The canvas input sequence must select a Q region.");
            foreach ((long command, CanvasPatchLayerScope scope, bool custom) in new[]
            {
                (1L, CanvasPatchLayerScope.CurrentLayer, false),
                (6L, CanvasPatchLayerScope.VisibleLayersExceptCurrent, false),
                (2L, CanvasPatchLayerScope.AllVisibleLayers, false),
                (3L, CanvasPatchLayerScope.CurrentLayer, true),
                (4L, CanvasPatchLayerScope.AllVisibleLayers, true),
            })
            {
                canvas._GuiInput(new InputEventMouseButton
                    { ButtonIndex = MouseButton.Right, Pressed = true, Position = end });
                var menu = Required<PopupMenu>(workspace, "CanvasAiContextMenu");
                Require(menu.Visible && !menu.IsItemDisabled(menu.GetItemIndex((int)command)),
                    $"Right-click must expose enabled generation command {command}.");

                // Any accidental discovery request remains pending, reproducing real network latency.
                upstream.Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                int beforeGenerate = upstream.RequestCount;
                int previousRuns = runner.RequestCount;
                menu.EmitSignal(PopupMenu.SignalName.IdPressed, command);
                if (custom)
                {
                    var dialog = Required<TextInputDialog>(workspace, "CanvasAiCustomPromptDialog");
                    Require(dialog.Visible, "A custom send must request its prompt before generation.");
                    Required<LineEdit>(dialog, "TextInput").Text = "synchronized custom detail";
                    dialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
                }
                for (int frame = 0; frame < 30 && runner.RequestCount == previousRuns; frame++) await Frame();
                Require(!panel.IsRefreshingModels && upstream.RequestCount == beforeGenerate,
                    $"Command {command} must use the synchronized model without another discovery request.");
                Require(runner.RequestCount == previousRuns + 1 && runner.ModelName == modelName,
                    $"Command {command} must reach the runner with the exact synchronized model.");
                Require(panel.GenerationOverlayStates.Single(state =>
                    state.Phase == AiGenerationOverlayPhase.Generating).Context.LayerScope == scope,
                    $"Command {command} must retain its selected layer scope.");
                Require(!custom || runner.Prompt.Contains("synchronized custom detail"),
                    "Custom sends must include the confirmed prompt in the submitted request.");
                Required<Button>(panel, "AiCancelButton").EmitSignal(Button.SignalName.Pressed);
                await Until(() => panel.ActiveGenerationCount == 0 && !panel.IsPreparingGeneration);
                upstream.Gate.SetResult(true);
                upstream.Gate = null;
                await Frame();
            }
        }
    }

    private sealed class CapturingRunner : IAiGenerationRunner
    {
        public int RequestCount { get; private set; }
        public string ModelName { get; private set; } = string.Empty;
        public string Prompt { get; private set; } = string.Empty;

        public Task<AiGenerationResult> GenerateAsync(AiGenerationProviderProfile profile,
            AiGenerationRequest request, CancellationToken cancellationToken)
        {
            RequestCount++;
            ModelName = profile.ModelName;
            Prompt = request.Prompt;
            return Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith<AiGenerationResult>(
                _ => throw new OperationCanceledException(cancellationToken), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    private async Task Until(Func<bool> condition)
    {
        for (int frame = 0; frame < 600; frame++)
        {
            if (condition()) return;
            await Frame();
        }
        throw new InvalidOperationException("Model sync did not settle within 600 frames.");
    }
    private static string SelectedModel(OptionButton selector) => selector.GetItemMetadata(selector.Selected).AsString();
    private static T Required<T>(Node root, string name) where T : Node =>
        root.FindChild(name, true, false) as T ?? throw new InvalidOperationException($"Missing control: {name}");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ModelUpstream : IDisposable
    {
        private readonly HttpListener _listener = new();
        private int _requests;
        public string Url { get; }
        public string[] Models { get; set; } = { "gpt-image-2", "gpt-image-2.5-flare" };
        public int Status { get; set; } = 200;
        public TaskCompletionSource<bool>? Gate { get; set; }
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

        private async Task Listen()
        {
            try
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context = await _listener.GetContextAsync();
                    _ = Respond(context);
                }
            }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task Respond(HttpListenerContext context)
        {
            int status = Status;
            string[] names = Models;
            Task? gate = Gate?.Task;
            Interlocked.Increment(ref _requests);
            try
            {
                if (gate != null) await gate;
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    data = names.Select(id => new { id, image_generation = new
                    { family = id == "gemini-image" ? "gemini" : "gpt",
                        protocol = id == "gemini-image" ? "chat-completions" : "openai-images",
                        capabilities = new[] { "text-to-image", "image-to-image", "image-edit" } } }),
                }));
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            catch (HttpListenerException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally { context.Response.Close(); }
        }

        public void Dispose() => _listener.Close();
    }
}
