using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AGAS.UI;
using Agas.Features.Canvas;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Features.Ai.UI;
using AppAgasCs.Features.Ai.Workspace;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using Godot;

public partial class McpAiGenerationAcceptance : Node
{
    private const string Prefix = "[AGAS][MCP-AI-GENERATION]";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private string? _root;
    [Export] public bool VerifyLayerScopesOnly { get; set; }

    public override async void _Ready()
    {
        int exitCode = 0;
        try
        {
            await RunAll();
            GD.Print($"{Prefix}[PASS]");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {exception}");
        }
        finally
        {
            if (_root != null)
            {
                try { Directory.Delete(_root, recursive: true); } catch { }
            }
            GetTree().Quit(exitCode);
        }
    }

    private async Task RunAll()
    {
        _root = Path.Combine(Path.GetTempPath(), "agas-mcp-ai-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Assert.True(new AgasProjectInitializer().EnsureInitialized(_root).Success, "Fixture project must initialize.");
        Directory.CreateDirectory(Path.Combine(_root, "assets", "generated"));
        string[] candidates =
        {
            "assets/generated/mcp-first.png",
            "assets/generated/mcp-stale.png",
            "assets/generated/mcp-replace.png",
            "assets/generated/mcp-boundary.png",
            "assets/generated/mcp-scope-default.png",
            "assets/generated/mcp-scope-all.png",
            "assets/generated/mcp-scope-background.png",
            "assets/generated/mcp-scope-hidden.png",
        };
        SaveImage(Path.Combine(_root, candidates[0]), Colors.Green);
        SaveImage(Path.Combine(_root, candidates[1]), Colors.Red);
        SaveImage(Path.Combine(_root, candidates[2]), Colors.Purple);
        SaveImage(Path.Combine(_root, candidates[3]), Colors.Cyan);
        foreach (string candidate in candidates.Skip(4))
            SaveImage(Path.Combine(_root, candidate), Colors.Green);

        string settingsHome = AgasAiSettingsPaths.ResolveHome();
        string providerDirectory = AgasAiSettingsPaths.ProvidersDirectory(settingsHome);
        Directory.CreateDirectory(providerDirectory);
        File.WriteAllText(Path.Combine(providerDirectory, "acceptance-vendor.json"), """
        {
          "schemaVersion":1,
          "id":"vendor.provider",
          "displayName":"Acceptance Vendor",
          "adapterKind":"openai-compatible",
          "defaultBaseUrl":"https://relay.example/v1/",
          "models":[{"modelName":"gpt-image-2","label":"Acceptance Model","recommended":true}],
          "capabilities":["textToImage","referenceEdit","regionEdit","nativeAlphaMask"],
          "credential":{"kind":"profileSettings","label":"API Key","settingsKey":"apiKey"}
        }
        """);
        var aiSettings = new AgasAiSettingsStore(settingsHome);
        var storedProfile = new AgasAiProfile(
            "acceptance-profile",
            "Acceptance Relay",
            "vendor.provider",
            "gpt-image-2",
            "https://relay.example/v1/");
        aiSettings.SaveProfilesAndCredential(
            new AgasAiProfilesDocument(3, storedProfile.Id, new[] { storedProfile }),
            storedProfile.Id,
            "acceptance-secret-key");

        var dirtyState = new AgasMcpDirtyState();
        var coordinator = new AgasAiGenerationCoordinator();
        var workspace = new MainWorkspace(coordinator) { CustomMinimumSize = new Vector2(1200, 760) };
        workspace.SetMcpDirtyState(dirtyState);
        AddChild(workspace);
        await Frame();
        workspace.OpenProject(_root);
        await Frame();
        Assert.True(workspace.TryBindMcpDirtyState(out string bindError), bindError);

        AgasMcpProjectContext live = workspace.GetCurrentMcpProjectContext()
            ?? throw new InvalidOperationException("Live MCP project context is unavailable.");
        IAgasAiGenerationWorkspaceBridge bridge = workspace.AiGenerationWorkspaceBridge;
        AgasMcpProjectContext context = live.WithAiGenerationWorkspace(bridge);
        var contextProvider = new AgasMcpProjectContextProvider();
        contextProvider.RegisterContext(context);
        var projectFacade = new AgasMcpProjectFacade(contextProvider, dirtyState, new AgasMcpPathValidator());
        using var store = new AgasMcpAiJobStore();
        var runner = new SequenceRunner(candidates);
        await using var scheduler = new AgasMcpAiJobScheduler(coordinator, store, AgasMcpAiFacade.MapRunnerFailure);
        var aiFacade = new AgasMcpAiFacade(contextProvider, dirtyState, store, scheduler, runner);
        var dispatcher = new AgasMcpMainThreadDispatcher { Name = "McpAiAcceptanceDispatcher" };
        AddChild(dispatcher);
        using var uiSynchronizer = new AgasMcpAiJobUiSynchronizer(scheduler, dispatcher, contextProvider);
        AiGenerationPanel aiPanel = FindRequired<AiGenerationPanel>(workspace, "AiGenerationPanel");
        Assert.True(aiPanel.SelectProfileById(storedProfile.Id),
            "The fixture must select its credentialed provider after first-run provider seeding.");
        Label aiStatusLabel = FindRequired<Label>(workspace, "AiGenerationStatus");
        Label aiTaskCountLabel = FindRequired<Label>(workspace, "AiGenerationTaskCountLabel");

        string sceneId = live.Project.ActiveSceneId;
        string layerId = live.CurrentScene?.ActiveLayerId
            ?? throw new InvalidOperationException("Active layer is unavailable.");
        if (VerifyLayerScopesOnly)
        {
            var scopeResult = new AgasLayerResource { Id = "layer-scope-result", Name = "Result", Order = 1 };
            AgasLayerResource initial = workspace.CurrentScene!.Layers.Single(item => item.Id == layerId);
            workspace.CurrentScene.Layers.Add(scopeResult);
            CanvasWorkspace scopeCanvas = FindRequired<CanvasWorkspace>(workspace, "CanvasWorkspaceHost");
            Assert.True(scopeCanvas.SetCurrentLayer(scopeResult), "Scope fixture must select its result layer.");
            await VerifyReferenceLayerScopes(workspace, projectFacade, aiFacade, store, scopeCanvas, initial, scopeResult, runner);
            await scheduler.StopAsync();
            workspace.QueueFree();
            await Frame();
            return;
        }
        AgasMcpToolResponse selection = projectFacade.SelectionSet(new AgasSelectionSetInput(
            "setRegions",
            sceneId,
            new AgasMcpGridRegionDto(0, 0, 2, 2),
            new AgasMcpGridRegionDto(0, 0, 1, 1)));
        Assert.True(selection.Ok, selection.Error?.Message ?? "selection.set failed.");

        AgasMcpToolResponse generated = aiFacade.Generate(new AgasAiGenerateInput(
            sceneId,
            layerId,
            "a green tile",
            ProfileId: null,
            Target: "NewImage"));
        Assert.True(generated.Ok, generated.Error?.Message ?? "ai.generate failed.");
        JsonElement generatedData = Json(generated.Data);
        string jobId = generatedData.GetProperty("jobId").GetString()
            ?? throw new InvalidOperationException("generate omitted jobId.");
        Assert.Equal(jobId, aiPanel.LatestExternalJobStatus?.JobId,
            "Submitting an MCP AI job must publish it to the shared AI panel.");
        Assert.True(aiPanel.LatestExternalJobStatus?.State is
                AiGenerationExternalJobState.Queued or AiGenerationExternalJobState.Running,
            "The no-poll fixture must still be active when generate returns.");
        Assert.True(aiStatusLabel.Text.Contains("MCP AI", StringComparison.Ordinal),
            "The shared AI panel must visibly identify MCP generation work.");
        Assert.Equal("MaskedEdit", generatedData.GetProperty("effectiveOperation").GetString(),
            "effective operation must come from the shared Q/W preparation bridge.");
        JsonElement execution = generatedData.GetProperty("execution");
        Assert.Equal("vendor.provider", execution.GetProperty("providerId").GetString(),
            "generate must preserve ProviderId independently from AdapterKind.");
        Assert.Equal("openai-compatible", execution.GetProperty("adapterKind").GetString(),
            "generate must expose the actual adapter implementation identity.");
        Assert.Equal("gpt-image-2", execution.GetProperty("modelName").GetString());
        new AgasAiSettingsFieldMergeService(aiSettings).UpdateActiveSelection(
            storedProfile.Id,
            modelName: "rotated-model");
        aiSettings.SetCredential(storedProfile.Id, "rotated-secret-key");
        AgasMcpAiJobSnapshot succeeded = await WaitForTerminal(store, jobId);
        Assert.Equal(AgasMcpAiJobState.Succeeded, succeeded.State, succeeded.Failure?.Message ?? "AI job did not succeed.");
        await WaitForExternalState(aiPanel, AiGenerationExternalJobState.Succeeded);
        await WaitForText(aiTaskCountLabel, $"任务 {coordinator.MaximumConcurrentGenerations}/0");
        Assert.True(aiStatusLabel.Text.Contains("生成完成", StringComparison.Ordinal),
            "A background scheduler terminal event must refresh the AI panel without an ai.status poll.");
        AgasMcpToolResponse succeededStatus = aiFacade.Status(new AgasAiStatusInput(jobId));
        Assert.True(succeededStatus.Ok, succeededStatus.Error?.Message ?? "ai.status failed.");
        Assert.True(succeededStatus.Data is AgasMcpAiJobSucceededDto,
            "Direct status must return the closed succeeded job root without a job wrapper.");
        JsonElement succeededStatusData = Json(succeededStatus.Data);
        Assert.Equal("gpt-image-2", succeededStatusData.GetProperty("execution").GetProperty("modelName").GetString(),
            "Old jobs must retain their submission-time model after settings change.");
        Assert.False(JsonSerializer.Serialize(succeededStatus.Data).Contains("rotated-secret-key", StringComparison.Ordinal),
            "Credential rotation must never enter old job status.");
        AgasMcpToolResponse listedStatus = aiFacade.Status(new AgasAiStatusInput(Limit: 10));
        Assert.True(listedStatus.Data is AgasMcpAiStatusListDataDto,
            "List status must return the typed jobs list variant.");
        Assert.Equal(AiGenerationExternalJobState.Succeeded, aiPanel.LatestExternalJobStatus?.State,
            "Polling a terminal MCP job must refresh the AI panel terminal state.");

        AgasLayerResource layer = workspace.CurrentScene!.Layers.Single(item => item.Id == layerId);
        int beforeApply = layer.Images.Count;
        AgasMcpToolResponse applied = aiFacade.Apply(new AgasAiApplyInput(jobId));
        Assert.True(applied.Ok, applied.Error?.Message ?? "ai.apply failed.");
        JsonElement appliedData = Json(applied.Data);
        Assert.False(appliedData.GetProperty("alreadyApplied").GetBoolean(), "First apply must mutate exactly once.");
        string appliedPath = appliedData.GetProperty("appliedRelativePath").GetString()
            ?? throw new InvalidOperationException("apply omitted relative path.");
        Assert.False(Path.IsPathRooted(appliedPath), "AI MCP output must expose only a project-relative path.");
        Assert.Equal(beforeApply + 1, layer.Images.Count, "First apply must add one image.");
        Assert.True(workspace.HasUnsavedChanges, "MarkDirtyOnly apply must make the shared workspace dirty.");
        Assert.True(dirtyState.IsDirty, "MarkDirtyOnly apply must be owned by MCP dirty state.");
        Assert.False(applied.Saved, "Apply must not auto-save the project.");

        AgasMcpToolResponse repeated = aiFacade.Apply(new AgasAiApplyInput(jobId));
        Assert.True(repeated.Ok, repeated.Error?.Message ?? "Repeated apply failed.");
        Assert.True(Json(repeated.Data).GetProperty("alreadyApplied").GetBoolean(), "Repeated apply must be idempotent.");
        Assert.Equal(beforeApply + 1, layer.Images.Count, "Repeated apply must not write another image.");
        Assert.False(store.TryGetApplicationState<AgasMcpAiApplicationState>(jobId, out _),
            "Successful apply must release the large frozen application state.");

        AgasMcpToolResponse statusAfterApply = aiFacade.Status(new AgasAiStatusInput(jobId));
        JsonElement statusAfterApplyData = Json(statusAfterApply.Data);
        Assert.True(statusAfterApply.Data is AgasMcpAiJobSucceededDto,
            "Applied status must remain the succeeded closed variant.");
        Assert.True(statusAfterApplyData.GetProperty("alreadyApplied").GetBoolean(),
            "Applied succeeded status must expose alreadyApplied=true.");
        Assert.Equal(candidates[0], statusAfterApplyData.GetProperty("resultRelativePath").GetString(),
            "status.resultRelativePath must remain the immutable provider result after apply.");
        Assert.Equal(appliedPath, statusAfterApplyData.GetProperty("appliedRelativePath").GetString(),
            "status must expose the applied artifact through its independent appliedRelativePath field.");
        Assert.Equal(AiGenerationExternalJobState.Applied, aiPanel.LatestExternalJobStatus?.State,
            "Polling an applied MCP job must keep the AI panel on the applied state.");
        string safeStatusJson = JsonSerializer.Serialize(statusAfterApply.Data);
        Assert.False(safeStatusJson.Contains("base64", StringComparison.OrdinalIgnoreCase), "Status must not expose Base64.");
        Assert.False(safeStatusJson.Contains("acceptance-secret-key", StringComparison.Ordinal), "Status must not expose credentials.");
        Assert.False(safeStatusJson.Contains(_root, StringComparison.Ordinal), "Status must not expose absolute project paths.");

        AgasMcpToolResponse saved = projectFacade.ProjectSave(new AgasProjectSaveInput());
        Assert.True(saved.Ok && saved.Saved && !saved.Dirty, saved.Error?.Message ?? "project.save failed.");
        Assert.False(workspace.HasUnsavedChanges, "Explicit project.save must clear workspace dirty state.");
        Assert.True(File.Exists(Path.Combine(_root, appliedPath.Replace('/', Path.DirectorySeparatorChar))),
            "Applied artifact must exist before reopen.");

        var reopened = new MainWorkspace { CustomMinimumSize = new Vector2(1200, 760) };
        AddChild(reopened);
        await Frame();
        reopened.OpenProject(_root);
        await Frame();
        AgasLayerResource reopenedLayer = reopened.CurrentScene!.Layers.Single(item => item.Id == layerId);
        Assert.True(reopenedLayer.Images.Any(item => item.ImagePath == appliedPath),
            "apply -> project.save -> reopen must persist the generated image reference.");
        reopened.QueueFree();
        await Frame();

        AgasMcpToolResponse staleGenerated = aiFacade.Generate(new AgasAiGenerateInput(sceneId, layerId, "red tile"));
        Assert.True(staleGenerated.Ok, staleGenerated.Error?.Message ?? "stale fixture generate failed.");
        string staleJobId = Json(staleGenerated.Data).GetProperty("jobId").GetString()!;
        Assert.Equal(AgasMcpAiJobState.Succeeded, (await WaitForTerminal(store, staleJobId)).State);
        CanvasWorkspace canvas = FindRequired<CanvasWorkspace>(workspace, "CanvasWorkspaceHost");
        Assert.True(canvas.TryAddImageFromProjectRelativePath(appliedPath, new Vector2(48, 48)),
            "Fixture must change captured canvas content.");
        AgasMcpToolResponse staleApply = aiFacade.Apply(new AgasAiApplyInput(staleJobId));
        Assert.False(staleApply.Ok, "A canvas-changed job must be rejected.");
        Assert.Equal(AgasMcpErrorCodes.AiContextStale, staleApply.Error?.Code,
            "Canvas fingerprint mismatch must map to AGAS_AI_CONTEXT_STALE.");

        ImageInstanceResource selected = (ImageInstanceResource?)canvas.GetSelectedImage()
            ?? throw new InvalidOperationException("Replace fixture image is not selected.");
        AgasMcpToolResponse replaceGenerated = aiFacade.Generate(new AgasAiGenerateInput(
            sceneId,
            layerId,
            "purple replacement",
            Target: "ReplaceSelected"));
        Assert.True(replaceGenerated.Ok, replaceGenerated.Error?.Message ?? "replace generate failed.");
        string replaceJobId = Json(replaceGenerated.Data).GetProperty("jobId").GetString()!;
        Assert.Equal(AgasMcpAiJobState.Succeeded, (await WaitForTerminal(store, replaceJobId)).State);
        Assert.True(canvas.DeleteSelected(), "Frozen replace target must be removable for the fail-closed fixture.");
        int afterTargetDelete = layer.Images.Count;
        AgasMcpToolResponse replaceApply = aiFacade.Apply(new AgasAiApplyInput(replaceJobId));
        Assert.False(replaceApply.Ok, "Missing frozen ReplaceSelected target must fail closed.");
        Assert.Equal(AgasMcpErrorCodes.AiContextStale, replaceApply.Error?.Code,
            "Missing frozen replacement target must report stale context.");
        Assert.Equal(afterTargetDelete, layer.Images.Count, "ReplaceSelected failure must never fall back to Add.");
        Assert.False(layer.Images.Any(item => item.Id == selected.Id), "Deleted target must remain deleted after rejected apply.");

        AgasMcpToolResponse boundaryGenerated = aiFacade.Generate(new AgasAiGenerateInput(
            sceneId,
            layerId,
            "cyan boundary fixture"));
        Assert.True(boundaryGenerated.Ok, boundaryGenerated.Error?.Message ?? "boundary generate failed.");
        string boundaryJobId = Json(boundaryGenerated.Data).GetProperty("jobId").GetString()!;
        Assert.Equal(AgasMcpAiJobState.Succeeded, (await WaitForTerminal(store, boundaryJobId)).State);
        var switchedLayer = new AgasLayerResource
        {
            Id = "scene-001-layer-boundary",
            Name = "Boundary Switch",
            Order = layer.Order + 1,
            Visible = true,
            Locked = false,
        };
        workspace.CurrentScene.Layers.Add(switchedLayer);
        Assert.True(canvas.SetCurrentLayer(switchedLayer), "Fixture must switch the active layer after job submission.");
        AgasMcpToolResponse boundaryApply = aiFacade.Apply(new AgasAiApplyInput(boundaryJobId));
        Assert.False(boundaryApply.Ok, "A job submitted for another active layer must be rejected.");
        Assert.Equal(AgasMcpErrorCodes.AiContextStale, boundaryApply.Error?.Code,
            "Scene/layer boundary changes must map to AGAS_AI_CONTEXT_STALE.");

        List<IAgasAiGenerationPermit> heldPermits = new();
        for (int index = 0; index < coordinator.MaximumConcurrentGenerations; index++)
        {
            Assert.True(coordinator.TryAcquire(out IAgasAiGenerationPermit? permit),
                "Cancellation UI fixture must reserve the shared generation budget.");
            heldPermits.Add(permit!);
        }
        AgasMcpAiProjectScope cancellationScope = AgasMcpAiProjectScope.Create(
            workspace.ProjectRoot,
            workspace.CurrentProjectId);
        AgasMcpAiJobSubmitResult queuedForCancel = scheduler.Submit(new AgasMcpAiJobSubmission(
            cancellationScope,
            "RegionEdit",
            token => throw new InvalidOperationException("Canceled queued runner must never start.")));
        Assert.True(queuedForCancel.Accepted && queuedForCancel.Job != null,
            "Cancellation UI fixture must queue an MCP job.");
        AgasMcpToolResponse queuedStatus = aiFacade.Status(new AgasAiStatusInput(queuedForCancel.Job!.JobId));
        Assert.True(queuedStatus.Ok, queuedStatus.Error?.Message ?? "Queued status failed.");
        Assert.Equal(AiGenerationExternalJobState.Queued, aiPanel.LatestExternalJobStatus?.State,
            "Polling a queued MCP job must refresh the AI panel queued state.");
        AgasMcpToolResponse canceledResponse = aiFacade.Cancel(new AgasAiCancelInput(queuedForCancel.Job.JobId));
        Assert.True(canceledResponse.Ok, canceledResponse.Error?.Message ?? "Queued cancel failed.");
        Assert.Equal(AiGenerationExternalJobState.Canceled, aiPanel.LatestExternalJobStatus?.State,
            "Canceling an MCP job must immediately refresh the AI panel canceled state.");
        Assert.True(aiStatusLabel.Text.Contains("已取消", StringComparison.Ordinal),
            "The AI panel must visibly show MCP cancellation.");
        foreach (IAgasAiGenerationPermit permit in heldPermits)
        {
            permit.Dispose();
        }

        scheduler.CancelProject(AgasMcpAiProjectScope.Create(workspace.ProjectRoot, workspace.CurrentProjectId));
        await scheduler.StopAsync();
        Assert.Equal(0, scheduler.ActiveCount, "MCP stop must quiesce all AI runners.");
        Assert.Equal(0, coordinator.ActiveCount, "MCP stop must release the shared UI/MCP permit.");
        workspace.QueueFree();
        await Frame();
    }

    private async Task VerifyReferenceLayerScopes(
        MainWorkspace workspace,
        AgasMcpProjectFacade projectFacade,
        AgasMcpAiFacade aiFacade,
        AgasMcpAiJobStore store,
        CanvasWorkspace canvas,
        AgasLayerResource previousLayer,
        AgasLayerResource resultLayer,
        SequenceRunner runner)
    {
        previousLayer.Visible = false;
        SaveImage(Path.Combine(_root!, "assets", "background.png"), Colors.Blue);
        SaveImage(Path.Combine(_root!, "assets", "foreground.png"), Colors.Red);
        using (Image foreground = Image.LoadFromFile(Path.Combine(_root!, "assets", "foreground.png")))
        {
            foreground.SetPixel(6, 1, Colors.Transparent);
            Assert.Equal(Error.Ok, foreground.SavePng(Path.Combine(_root!, "assets", "foreground.png")));
        }
        var background = new AgasLayerResource
        {
            Id = "layer-reference-background", Name = "Reference", Order = -1,
            Visible = true, Locked = true,
        };
        background.Images.Add(new ImageInstanceResource
        {
            Id = "image-reference-background", ImagePath = "assets/background.png",
        });
        workspace.CurrentScene!.Layers.Add(background);
        Assert.True(canvas.TryAddImageFromProjectRelativePath("assets/foreground.png", Vector2.Zero),
            "The current result layer must contain an old opaque result.");
        string sceneId = workspace.CurrentScene.Id;
        var q = new AgasMcpGridRegionDto(0, 0, 1, 1);
        Assert.True(projectFacade.SelectionSet(new AgasSelectionSetInput("clearRegions", sceneId)).Ok,
            "The missing-Q fixture must clear selection.");
        AgasMcpToolResponse missingQ = aiFacade.Generate(new AgasAiGenerateInput(
            sceneId, resultLayer.Id, "scope test", LayerScope: "VisibleLayersExceptCurrent"));
        Assert.Equal(AgasMcpErrorCodes.SelectionInvalid, missingQ.Error?.Code,
            "A background reference must not silently invent Q on another layer.");
        Assert.True(projectFacade.SelectionSet(new AgasSelectionSetInput("setRegions", sceneId, q)).Ok,
            "Cross-layer generation must accept an explicit Q.");
        foreach ((string scope, Color expected, bool visible) in new[]
        {
            ("CurrentLayer", Colors.Red, true),
            ("AllVisibleLayers", Colors.Red, true),
            ("VisibleLayersExceptCurrent", Colors.Blue, true),
            ("VisibleLayersExceptCurrent", Colors.Transparent, false),
        })
        {
            background.Visible = visible;
            AgasAiGenerateInput input = scope == "CurrentLayer"
                ? new AgasAiGenerateInput(sceneId, resultLayer.Id, "scope test")
                : new AgasAiGenerateInput(sceneId, resultLayer.Id, "scope test",
                    Target: scope == "VisibleLayersExceptCurrent" && visible ? "ReplaceSelected" : "NewImage",
                    LayerScope: scope);
            AgasMcpToolResponse generated = aiFacade.Generate(input);
            Assert.True(generated.Ok, generated.Error?.Message ?? $"Scope {scope} must generate.");
            string jobId = Json(generated.Data).GetProperty("jobId").GetString()!;
            Assert.Equal(AgasMcpAiJobState.Succeeded, (await WaitForTerminal(store, jobId)).State);
            Assert.True(store.TryGetApplicationState<AgasMcpAiApplicationState>(jobId, out var state),
                "The fake runner must preserve the actual prepared capture.");
            AiGeneratedImageApplication application = state!.Application;
            Assert.Equal(scope, application.LayerScope.ToString(), "The requested scope must survive submission.");
            Assert.Equal(resultLayer.Id, application.ExpectedLayerId, "Input scope must not redirect the output layer.");
            using var pixels = new Image();
            Assert.Equal(Error.Ok, pixels.LoadPngFromBuffer(application.PatchCapture!.ReplacementPatchPng));
            Assert.Equal(expected, pixels.GetPixel(1, 1),
                $"Scope {scope}, background visible={visible} must capture the expected source pixel.");
            Color gap = scope == "CurrentLayer" || !visible ? Colors.Transparent : Colors.Blue;
            Assert.Equal(gap, pixels.GetPixel(6, 1), "All-visible scope must include background through transparent result pixels.");
            AiGenerationRequest request = runner.LastRequest ?? throw new InvalidOperationException("Fake runner received no request.");
            Assert.Equal(resultLayer.Id, request.LayerId, "Provider request storage scope must remain the current result layer.");
            Assert.Equal(AiGenerationOperation.ReferenceEdit, request.Operation);
            using var providerReference = new Image();
            Assert.Equal(1, request.ReferenceImages.Count, "Provider must receive exactly the composed Q reference.");
            Assert.Equal(Error.Ok, providerReference.LoadPngFromBuffer(request.ReferenceImages[0].Bytes));
            Assert.Equal(expected, providerReference.GetPixel(1, 1), "Provider reference must use requested layer scope.");
            Assert.Equal(gap, providerReference.GetPixel(6, 1), "Provider must receive background through the foreground gap.");
            Assert.Equal(canvas.GetGridSizePx(), pixels.GetWidth(), "Capture dimensions must come from explicit Q.");
            if (scope == "VisibleLayersExceptCurrent" && visible)
            {
                int before = resultLayer.Images.Count;
                AgasMcpToolResponse applied = aiFacade.Apply(new AgasAiApplyInput(jobId));
                Assert.True(applied.Ok, applied.Error?.Message ?? "Cross-layer apply must succeed.");
                Assert.Equal(before + 1, resultLayer.Images.Count, "Generated image must append to the current result layer.");
                Assert.Equal(1, background.Images.Count, "Applying a result must preserve the locked background.");
            }
        }
        GD.Print($"{Prefix}[LAYER-SCOPE-PASS] default/current/all/background-only, hidden and locked background, explicit Q, output layer");
    }

    private static async Task<AgasMcpAiJobSnapshot> WaitForTerminal(AgasMcpAiJobStore store, string jobId)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (store.TryGet(jobId, out AgasMcpAiJobSnapshot? job)
                && job != null
                && job.State is AgasMcpAiJobState.Succeeded or AgasMcpAiJobState.Failed or AgasMcpAiJobState.Canceled)
            {
                return job;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException($"AI job {jobId} did not reach a terminal state.");
    }

    private async Task WaitForExternalState(
        AiGenerationPanel panel,
        AiGenerationExternalJobState expectedState)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (panel.LatestExternalJobStatus?.State == expectedState)
            {
                return;
            }
            await Frame();
        }
        throw new TimeoutException($"AI panel did not receive external state {expectedState}.");
    }

    private async Task WaitForText(Label label, string expectedText)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (string.Equals(label.Text, expectedText, StringComparison.Ordinal))
            {
                return;
            }
            await Frame();
        }
        throw new TimeoutException($"Label {label.Name} did not become '{expectedText}'. Actual='{label.Text}'.");
    }

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private static JsonElement Json(object? value) =>
        JsonSerializer.SerializeToElement(
            value ?? throw new InvalidOperationException("MCP response data is missing."),
            JsonOptions);

    private static T FindRequired<T>(Node root, string name) where T : Node
    {
        return root.FindChildren(name, recursive: true, owned: false).OfType<T>().FirstOrDefault()
            ?? throw new InvalidOperationException($"Missing node {name} ({typeof(T).Name}).");
    }

    private static void SaveImage(string path, Color color)
    {
        using Image image = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
        image.Fill(color);
        Error error = image.SavePng(path);
        if (error != Error.Ok)
        {
            throw new InvalidOperationException($"Failed to save fixture image: {error}");
        }
    }

    private sealed class SequenceRunner : IAiGenerationRunner
    {
        private readonly string[] _relativePaths;
        private int _next = -1;
        public AiGenerationRequest? LastRequest { get; private set; }

        public SequenceRunner(string[] relativePaths) => _relativePaths = relativePaths;

        public async Task<AiGenerationResult> GenerateAsync(
            AiGenerationProviderProfile profile,
            AiGenerationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            int index = Interlocked.Increment(ref _next);
            if (index >= _relativePaths.Length)
            {
                throw new InvalidOperationException("No fake AI candidate remains.");
            }
            await Task.Delay(40, cancellationToken);
            return new AiGenerationResult(
                _relativePaths[index],
                "image/png",
                256,
                AiGenerationEffectiveOperation.MaskedEdit,
                AiGenerationEditGuarantee.ProviderMasked,
                request.Width,
                request.Height);
        }
    }

    private static class Assert
    {
        public static void True(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        public static void False(bool value, string message) => True(!value, message);

        public static void Equal<T>(T expected, T actual, string? message = null)
        {
            if (!Equals(expected, actual))
            {
                throw new InvalidOperationException(message ?? $"Expected {expected}, got {actual}.");
            }
        }
    }
}
