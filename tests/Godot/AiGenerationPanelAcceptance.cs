using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Application.Contracts;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Features.Ai.UI;
using AppAgasCs.Features.Ai.Workspace;
using Agas.Features.Canvas;
using Godot;

public partial class AiGenerationPanelAcceptance : Node
{
    private const string Prefix = "[AGAS][AI-GENERATION-PANEL]";
    private string? _tempHome;

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
            if (_tempHome != null)
            {
                try { Directory.Delete(_tempHome, recursive: true); } catch { }
            }
            GetTree().Quit(exitCode);
        }
    }

    private async Task RunAll()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), "agas-ai-panel-" + Guid.NewGuid().ToString("N"));
        var store = new AgasAiSettingsStore(_tempHome);
        string providerDirectory = AgasAiSettingsPaths.ProvidersDirectory(_tempHome);
        Directory.CreateDirectory(providerDirectory);
        File.WriteAllText(Path.Combine(providerDirectory, "30-panel-provider.json"), """
        {
          "schemaVersion": 1,
          "id": "panel-provider",
          "displayName": "Panel Third Party",
          "adapterKind": "vendor.panel-v1",
          "defaultBaseUrl": "https://panel-provider.example/v1/",
          "models": [
            { "modelName": "panel-model", "label": "Panel Model", "recommended": true }
          ],
          "capabilities": ["textToImage"],
          "credential": { "kind": "profileSettings", "label": "API Key", "settingsKey": "apiKey" },
          "baseUrlHint": "Panel test provider",
          "baseUrlPlaceholder": "https://panel-provider.example/v1/",
          "order": 30
        }
        """);
        var profile = new AgasAiProfile(
            "profile-1",
            "测试中转站",
            AgasAiProviderIds.OpenAiCompatible,
            "gpt-image-2",
            "https://example.test/v1/");
        var alternateProfile = new AgasAiProfile(
            "profile-2",
            "备用中转站",
            AgasAiProviderIds.GeminiCompatible,
            "gemini-3-pro-image-preview",
            "https://alternate.example.test/v1/");
        var thirdPartyProfile = new AgasAiProfile(
            "profile-3",
            "第三方目录中转站",
            "panel-provider",
            "panel-model",
            "https://panel-provider.example/v1/");
        store.SaveProfiles(new AgasAiProfilesDocument(
            AgasAiProfilesDocument.CurrentResourceVersion,
            profile.Id,
            "gpt-image-2",
            new[] { profile, alternateProfile, thirdPartyProfile }));
        store.SetCredential(profile.Id, "secret-for-test");
        store.SetCredential(alternateProfile.Id, "alternate-secret-for-test");
        store.SetCredential(thirdPartyProfile.Id, "third-party-secret-for-test");

        var runner = new FakeRunner();
        var coordinator = new AgasAiGenerationCoordinator();
        var panel = new AiGenerationPanel(store, runner, coordinator);
        var applications = new List<AiGeneratedImageApplication>();
        int discarded = 0;
        string latestStatus = string.Empty;
        bool latestStatusError = false;
        AiGeneratedImageApplyResult configuredApplyResult = AiGeneratedImageApplyResult.Ok("已写回冻结区域");
        var overlaySnapshots = new List<IReadOnlyList<AiGenerationOverlayState>>();
        panel.GenerationOverlaysChanged = states => overlaySnapshots.Add(states);
        panel.StatusChanged += (message, error) =>
        {
            latestStatus = message;
            latestStatusError = error;
        };
        panel.PrepareGenerationRequest = PrepareRegionRequest;
        panel.ApplyGeneratedImage = application =>
        {
            applications.Add(application);
            return configuredApplyResult;
        };
        panel.DiscardGeneratedImage = _ => discarded++;
        AddChild(panel);
        await Frame();

        Assert.True(panel.SelectProfileById(thirdPartyProfile.Id),
            "Every saved connection must remain selectable regardless of legacy provider metadata.");
        AiGenerationProfileResolution thirdPartyResolution = panel.ResolveProviderProfile();
        Assert.True(thirdPartyResolution.Success && thirdPartyResolution.Profile != null,
            "A saved connection must resolve into an executable generation profile.");
        Assert.Equal(AgasAiUnifiedModelCatalog.ConnectionAdapterKind, thirdPartyResolution.Profile!.AdapterKind,
            "Legacy provider metadata must not change the unified runtime adapter.");
        Assert.Equal("gpt-image-2", thirdPartyResolution.Profile.ModelName,
            "Every connection must use the global model.");
        Assert.True(panel.SelectProfileById(profile.Id), "The initial built-in profile must remain selectable.");
        OptionButton initialModelPresets = FindRequired<OptionButton>(panel, "AiGenerationModelPreset");
        Assert.Equal(7, initialModelPresets.ItemCount, "The generation selector must expose exactly seven unified models.");
        Assert.True(AgasAiUnifiedModelCatalog.Models.All(model => FindPresetIndex(initialModelPresets, model.ModelName) >= 0),
            "The generation selector must expose GPT, GPT 2.5, Grok, and Gemini models.");
        AiGenerationProfileResolution explicitPersistedProfile = panel.ResolveProviderProfile(profile.Id);
        Assert.True(explicitPersistedProfile.Success && explicitPersistedProfile.Profile != null,
            "An explicit connection id must resolve independently of the selected connection.");
        Assert.Equal(AgasAiUnifiedModelCatalog.ConnectionAdapterKind, explicitPersistedProfile.Profile!.AdapterKind,
            "An explicit connection id must use the unified adapter.");
        Assert.Equal(new Uri(profile.BaseUrl), explicitPersistedProfile.Profile.BaseUri,
            "An explicit profile id must retain its persisted endpoint.");
        Assert.True(panel.SelectProfileById(profile.Id), "Reselecting the profile must restore its persisted provider defaults.");

        var firstContext = ContextFor(1, "image-1");
        panel.BeginCanvasGeneration(firstContext, "first", submit: true, target: AiGenerationTarget.AddToCurrentLayer);
        Button generateButton = FindRequired<Button>(panel, "AiGenerateButton");
        Assert.True(panel.IsPreparingGeneration, "The generate button must enter a loading state before the Q/W composite freezes the UI.");
        Assert.True(generateButton.Disabled, "The generate button must stay disabled while the request is being prepared.");
        Assert.Equal("准备中…", generateButton.Text, "The generate button must show an explicit loading label during prepare.");
        Assert.True(generateButton.Icon != null, "The generate button must show a loading icon during prepare.");
        Assert.True(
            panel.GenerationOverlayStates.Any(state =>
                state.Phase == AiGenerationOverlayPhase.Pending
                && state.Text.Contains("准备中", StringComparison.Ordinal)),
            "A pending overlay must appear as soon as the user starts a generation.");
        await WaitUntil(() => runner.RequestCount == 1 && panel.ActiveGenerationCount == 1, "First task must begin after the loading frame.");
        Assert.True(!panel.IsPreparingGeneration, "The loading state must clear once the provider request starts.");
        Assert.True(
            !panel.GenerationOverlayStates.Any(state => state.Phase == AiGenerationOverlayPhase.Pending),
            "The pending overlay must be replaced by the running overlay.");
        Assert.Equal(1, coordinator.ActiveCount, "The UI task must hold one shared generation permit.");
        Label generationStatus = FindRequired<Label>(panel, "AiGenerationStatus");
        string manualRunningStatus = generationStatus.Text;
        panel.ReportExternalJobStatus(new AiGenerationExternalJobStatus(
            "aijob-00000000000000000000000000000000",
            AiGenerationExternalJobState.Succeeded));
        Assert.Equal(manualRunningStatus, generationStatus.Text,
            "An MCP terminal update must not replace the status of a running manual generation task.");
        Assert.Equal(AiGenerationExternalJobState.Succeeded, panel.LatestExternalJobStatus?.State,
            "The panel must still retain the latest MCP state while preserving manual status ownership.");
        Assert.Equal(profile.Id, runner.ProfileIds[0], "The first task must freeze the initially selected complete profile.");
        Assert.Equal(AgasAiUnifiedModelCatalog.ConnectionAdapterKind, runner.Profiles[0].AdapterKind,
            "The frozen first task must use the unified interface type.");
        Assert.Equal(profile.ModelName, runner.Profiles[0].ModelName,
            "The frozen first task must keep the original model name.");
        Assert.Equal(new Uri(profile.BaseUrl), runner.Profiles[0].BaseUri,
            "The frozen first task must keep the original endpoint.");
        Assert.Equal("secret-for-test", runner.Profiles[0].ApiKey,
            "The frozen first task must keep the original credential.");
        Assert.Equal(1, panel.GenerationOverlayStates.Count, "First task must publish one frozen overlay.");
        Rect2 firstRect = panel.GenerationOverlayStates[0].WorldRect;
        Assert.Equal(new Rect2(24, 48, 96, 96), firstRect,
            "Generation overlay must cover the complete frozen Q, not only W.");

        AiGenerationCanvasContext reselectedContext = ContextFor(20, "image-2");
        panel.SetCanvasContext(reselectedContext);
        Assert.Equal(1, panel.ActiveGenerationCount, "Changing Q/W or selection must not cancel an active task.");
        Assert.Equal(firstRect, panel.GenerationOverlayStates[0].WorldRect, "The first task overlay must retain its original region.");
        Assert.True(panel.SelectProfileById(alternateProfile.Id), "A second configured profile must be selectable by id.");
        Assert.Equal(alternateProfile.Id, panel.SelectedProfileId, "Connection switching must update the connection used by future tasks.");
        Assert.Equal(alternateProfile.Id, store.LoadProfiles().ActiveProfileId,
            "Selecting a connection from the generation UI must persist ActiveProfileId for the next launch.");
        Assert.Equal(profile.Id, runner.ProfileIds[0], "Switching profile must not mutate a running task's frozen profile.");

        // The model is global and must remain independent of the active connection.
        OptionButton modelPresets = FindRequired<OptionButton>(panel, "AiGenerationModelPreset");
        const string selectedGlobalModel = "gpt-image-2.5-sunburst";
        int alternatePresetIndex = FindPresetIndex(modelPresets, selectedGlobalModel);
        Assert.True(alternatePresetIndex >= 0, "The shared model list must include GPT Image 2.5 Sunburst.");
        modelPresets.Select(alternatePresetIndex);
        modelPresets.EmitSignal(OptionButton.SignalName.ItemSelected, (long)alternatePresetIndex);
        AgasAiProfilesDocument afterModelPick = store.LoadProfiles();
        Assert.Equal(alternateProfile.Id, afterModelPick.ActiveProfileId,
            "Model preset selection must keep the active profile id.");
        Assert.Equal(selectedGlobalModel, afterModelPick.ActiveModelName,
            "Model preset selection must persist the global model name.");
        AgasAiProfile persistedAlternate = afterModelPick.Profiles.Single(item => item.Id == alternateProfile.Id);
        Assert.Equal(alternateProfile.ModelName, persistedAlternate.ModelName,
            "Model preset selection must not rewrite legacy per-connection model metadata.");
        panel.ReloadProfiles();
        Assert.Equal(alternateProfile.Id, panel.SelectedProfileId,
            "Reload without an explicit preferred id must restore the persisted active profile.");
        Assert.Equal(selectedGlobalModel, FindRequired<LineEdit>(panel, "AiGenerationModelName").Text,
            "Reload must restore the persisted global model name as the default.");

        for (int index = 2; index <= 5; index++)
        {
            panel.BeginCanvasGeneration(ContextFor(index * 10, $"image-{index}"), $"task-{index}", submit: true,
                target: AiGenerationTarget.AddToCurrentLayer);
        }
        await WaitUntil(() => runner.RequestCount == 5 && panel.ActiveGenerationCount == 5, "Five tasks must run concurrently.");
        Assert.Equal(5, coordinator.ActiveCount, "All UI tasks must consume the shared active budget.");
        Assert.Equal(coordinator.MaximumConcurrentGenerations, panel.ActiveGenerationCount,
            "UI simultaneous generation count must match the shared concurrent capacity.");
        Assert.True(runner.ProfileIds.Skip(1).All(id => id == alternateProfile.Id),
            "Every task started after switching must use the alternate endpoint/key/type/model profile.");
        Assert.True(runner.Profiles.Skip(1).All(captured =>
                captured.AdapterKind == AgasAiUnifiedModelCatalog.ConnectionAdapterKind
                && captured.ModelName == selectedGlobalModel
                && captured.BaseUri == new Uri(alternateProfile.BaseUrl)
                && captured.ApiKey == "alternate-secret-for-test"),
            "Tasks started after switching must use the alternate connection and global model.");
        Assert.Equal(5, panel.GenerationOverlayStates.Count, "Every active task must retain an independent overlay.");
        Assert.True(panel.GenerationOverlayStates.Select(state => state.RunId).Distinct().Count() == 5,
            "Concurrent overlays must have unique run identifiers.");
        generateButton = FindRequired<Button>(panel, "AiGenerateButton");
        Assert.True(generateButton.Disabled, "The sixth task must be blocked at the configured limit.");
        Assert.Equal($"已满 (5/{coordinator.MaximumConcurrentGenerations})", generateButton.Text,
            "The generate button must show full concurrent capacity when no more simultaneous boxes can start.");

        panel.BeginCanvasGeneration(ContextFor(60, "image-6"), "blocked", submit: true, target: AiGenerationTarget.AddToCurrentLayer);
        Assert.Equal(5, runner.RequestCount, "The sixth request must not reach the provider.");
        Assert.True(latestStatus.Contains("最多同时生成 5 个任务", StringComparison.Ordinal), "The limit must be explained in the UI.");

        runner.CompleteNext();
        await WaitUntil(() => applications.Count == 1 && panel.ActiveGenerationCount == 4, "A completed task must auto-apply and leave four running tasks.");
        Assert.Equal(4, coordinator.ActiveCount, "Completion must release exactly one shared permit.");
        // Permit must be released before overlay/UI consumers re-read ActiveCount,
        // otherwise the toolbar counter sticks at N after the last task finishes.
        await WaitUntil(() => coordinator.ActiveCount == panel.ActiveGenerationCount,
            "Shared ActiveCount must match panel active tasks immediately after completion.");
        Assert.Equal(4, RunningCount(overlaySnapshots.Last()),
            "A real completion event must update compact consumers from 5 to 4 running tasks.");
        Assert.Equal(4, panel.GenerationOverlayStates.Count, "A completed task overlay must be removed automatically.");
        Assert.Equal(1, discarded, "The raw candidate must be released after automatic apply.");
        Assert.True(FindOptional<Button>(panel, "AiApplyPendingButton") == null, "Manual apply must not remain in the UI.");

        panel.BeginCanvasGeneration(ContextFor(60, "image-6"), "task-6", submit: true, target: AiGenerationTarget.AddToCurrentLayer);
        await WaitUntil(() => runner.RequestCount == 6 && panel.ActiveGenerationCount == 5, "A freed slot must accept the next task.");
        runner.CompleteAll();
        await WaitUntil(() => applications.Count == 6 && panel.ActiveGenerationCount == 0, "All completed tasks must auto-apply and leave no active work.");
        Assert.Equal(0, coordinator.ActiveCount, "All completed UI work must release the shared budget.");

        int requestCountBeforeCanceledPrepare = runner.RequestCount;
        panel.BeginCanvasGeneration(ContextFor(62, "image-prepare-cancel"), "prepare-cancel", submit: true,
            target: AiGenerationTarget.AddToCurrentLayer);
        Assert.True(panel.IsPreparingGeneration, "A new send must show loading before the canvas composite runs.");
        Assert.True(!FindRequired<Button>(panel, "AiCancelButton").Disabled,
            "Cancel must be available while the generate button is in the loading state.");
        FindRequired<Button>(panel, "AiCancelButton").EmitSignal(BaseButton.SignalName.Pressed);
        Assert.True(!panel.IsPreparingGeneration, "Cancel during prepare must clear the loading state immediately.");
        await Frame();
        Assert.Equal(requestCountBeforeCanceledPrepare, runner.RequestCount,
            "Cancel during prepare must not send a provider request.");
        Assert.Equal(0, panel.ActiveGenerationCount, "Cancel during prepare must not leave an active task.");
        Assert.Equal(0, RunningCount(overlaySnapshots.Last()),
            "Completing all real tasks must publish zero running overlays.");
        Assert.Equal(0, panel.GenerationOverlayStates.Count, "All overlays must clear after their tasks finish.");
        Assert.Equal(6, discarded, "Every auto-applied candidate must be cleaned up.");

        configuredApplyResult = AiGeneratedImageApplyResult.AppliedButUnsaved(
            "图片已应用，但自动保存失败，请按 Ctrl/Cmd+S 重试。");
        panel.BeginCanvasGeneration(ContextFor(65, "image-unsaved"), "autosave-failure", submit: true,
            target: AiGenerationTarget.AddToCurrentLayer);
        await WaitUntil(() => panel.ActiveGenerationCount == 1, "Autosave failure fixture must start a task.");
        runner.CompleteNext();
        await WaitUntil(() => applications.Count == 7 && panel.ActiveGenerationCount == 0,
            "An applied-but-unsaved result must release its concurrent generation slot.");
        Assert.True(latestStatus.Contains("已自动应用，但自动保存失败", StringComparison.Ordinal),
            "Applied-but-unsaved work must not be mislabeled as an application failure.");
        Assert.True(latestStatus.Contains("Ctrl/Cmd+S", StringComparison.Ordinal),
            "Applied-but-unsaved status must explain the manual retry shortcut.");
        Assert.True(latestStatusError, "Applied-but-unsaved status must use the error appearance.");
        Assert.True(panel.GenerationOverlayStates.Any(state =>
                state.Phase == AiGenerationOverlayPhase.Failure
                && state.Text.Contains("自动保存失败", StringComparison.Ordinal)),
            "Applied-but-unsaved work must retain an explicit autosave failure overlay.");
        Assert.Equal(0, RunningCount(overlaySnapshots.Last()),
            "An autosave failure terminal overlay must not remain in the running count.");
        Assert.Equal(7, discarded, "Applied-but-unsaved work must release its raw candidate without repeating apply.");
        Assert.True(!FindRequired<Button>(panel, "AiGenerateButton").Disabled,
            "Applied-but-unsaved work must release capacity for the next task.");

        configuredApplyResult = AiGeneratedImageApplyResult.Fail("画布未修改");
        panel.BeginCanvasGeneration(ContextFor(66, "image-apply-failure"), "apply-failure", submit: true,
            target: AiGenerationTarget.AddToCurrentLayer);
        await WaitUntil(() => panel.ActiveGenerationCount == 1, "Apply failure fixture must start a task.");
        runner.CompleteNext();
        await WaitUntil(() => applications.Count == 8 && panel.ActiveGenerationCount == 0,
            "A true application failure must also release its concurrent generation slot.");
        Assert.True(latestStatus.Contains("自动应用失败", StringComparison.Ordinal),
            "A true application failure must remain distinct from an autosave failure.");
        Assert.Equal(0, RunningCount(overlaySnapshots.Last()),
            "An application failure must publish zero running overlays.");
        Assert.True(!latestStatus.Contains("已自动应用，但", StringComparison.Ordinal),
            "A true application failure must not claim that the canvas was modified.");
        Assert.Equal(8, discarded, "A failed application must release its provider candidate.");
        configuredApplyResult = AiGeneratedImageApplyResult.Ok("已写回冻结区域");

        panel.BeginCanvasGeneration(ContextFor(70, "image-7"), "failure", submit: true, target: AiGenerationTarget.AddToCurrentLayer);
        await WaitUntil(() => panel.ActiveGenerationCount == 1, "Failure fixture must start a task.");
        runner.FailNext();
        await WaitUntil(
            () => panel.ActiveGenerationCount == 0
                && panel.GenerationOverlayStates.Any(state => state.Phase == AiGenerationOverlayPhase.Failure),
            "A failed task must release its active slot while retaining a terminal failure overlay.");
        AiGenerationOverlayState failureOverlay = panel.GenerationOverlayStates.Single(state =>
            state.Phase == AiGenerationOverlayPhase.Failure
            && state.Text.Contains("响应提前中断", StringComparison.Ordinal));
        Assert.True(failureOverlay.Text.Contains("响应提前中断", StringComparison.Ordinal),
            "The failure overlay must show the safe transport reason.");
        Assert.Equal(0, RunningCount(overlaySnapshots.Last()),
            "A provider failure must publish zero running overlays.");
        Assert.True(!FindRequired<Button>(panel, "AiGenerateButton").Disabled,
            "A terminal failure overlay must not consume a concurrent generation slot.");

        panel.BeginCanvasGeneration(ContextFor(80, "image-8"), "cancel", submit: true, target: AiGenerationTarget.AddToCurrentLayer);
        await WaitUntil(() => panel.ActiveGenerationCount == 1, "Cancellation fixture must start a task while the failure overlay remains visible.");
        FindRequired<Button>(panel, "AiCancelButton").EmitSignal(BaseButton.SignalName.Pressed);
        await WaitUntil(() => runner.CancellationCount > 0 && panel.ActiveGenerationCount == 0, "Cancel all must cancel running tasks.");
        Assert.True(panel.GenerationOverlayStates.Any(state => state.Phase == AiGenerationOverlayPhase.Canceled),
            "Canceled work must leave a short-lived canceled overlay.");
        Assert.Equal(0, RunningCount(overlaySnapshots.Last()),
            "Cancellation must publish zero running overlays while retaining its terminal overlay.");

        panel.BeginCanvasGeneration(ContextFor(90, "image-9"), "boundary", submit: true, target: AiGenerationTarget.AddToCurrentLayer);
        await WaitUntil(() => panel.ActiveGenerationCount == 1, "Boundary fixture must start a task.");
        string applySceneId = "scene-1";
        panel.ApplyGeneratedImage = application =>
        {
            applications.Add(application);
            if (!string.Equals(application.ExpectedSceneId, applySceneId, StringComparison.Ordinal))
            {
                return AiGeneratedImageApplyResult.Fail(
                    "请求期间项目、场景或图层已切换，未应用生成结果。",
                    AiGeneratedImageApplyFailureReason.ContextStale);
            }

            return AiGeneratedImageApplyResult.Ok("已写回冻结区域");
        };
        AiGenerationCanvasContext otherScene = new(
            "/tmp/agas-project",
            "project-1",
            "scene-parked",
            "layer-1",
            HasSelectedImage: true,
            SelectedImageId: "image-parked",
            CanEditCurrentLayer: true,
            GridSizePx: 24,
            RegionMetadata: ContextFor(90, "image-9").RegionMetadata,
            LayerScope: CanvasPatchLayerScope.CurrentLayer);
        panel.SetCanvasContext(otherScene);
        await WaitUntil(() => panel.ActiveGenerationCount == 1, "Switching scenes in the same project must keep the running task.");
        runner.CompleteNext();
        await WaitUntil(
            () => panel.ActiveGenerationCount == 0 && panel.ParkedWriteBackCount == 1,
            "A finished task for another scene must park its write-back instead of discarding it.");
        Assert.Equal(1, applications.Count, "The first apply attempt happens while the other scene is active.");
        Assert.Equal(0, discarded, "A parked write-back must keep the generated artifact.");
        applySceneId = "scene-1";
        panel.SetCanvasContext(ContextFor(90, "image-9"));
        await WaitUntil(
            () => panel.ParkedWriteBackCount == 0 && applications.Count == 2,
            "Switching back to the original scene must apply the parked write-back.");
        Assert.Equal(1, discarded, "The parked artifact is discarded only after a successful write-back.");

        panel.BeginCanvasGeneration(ContextFor(95, "image-95"), "project-boundary", submit: true, target: AiGenerationTarget.AddToCurrentLayer);
        await WaitUntil(() => panel.ActiveGenerationCount == 1, "Project-boundary fixture must start a task.");
        panel.SetCanvasContext(new AiGenerationCanvasContext(
            "/tmp/another-project",
            "project-other",
            "scene-other",
            "layer-other",
            HasSelectedImage: true,
            SelectedImageId: "image-other",
            CanEditCurrentLayer: true,
            GridSizePx: 24,
            RegionMetadata: ContextFor(95, "image-95").RegionMetadata,
            LayerScope: CanvasPatchLayerScope.CurrentLayer));
        await WaitUntil(() => panel.ActiveGenerationCount == 0, "Changing project must cancel unsafe work.");
        Assert.Equal(0, panel.GenerationOverlayStates.Count, "Changing the project boundary must clear prior overlays.");
        Assert.True(overlaySnapshots.Any(snapshot => snapshot.Count == 5), "The overlay event must publish all concurrent tasks together.");
    }

    private static AiGenerationCanvasContext ContextFor(int column, string selectedImageId) => new(
        "/tmp/agas-project",
        "project-1",
        "scene-1",
        "layer-1",
        HasSelectedImage: true,
        SelectedImageId: selectedImageId,
        CanEditCurrentLayer: true,
        GridSizePx: 24,
        RegionMetadata: new AiGenerationRegionMetadata(
            new AiGenerationGridRegion(column, 2, 4, 4),
            new AiGenerationGridRegion(column + 1, 3, 2, 2)),
        LayerScope: CanvasPatchLayerScope.CurrentLayer);

    private static int RunningCount(IReadOnlyList<AiGenerationOverlayState> states) =>
        states.Count(state => state.Phase == AiGenerationOverlayPhase.Generating);

    private static AiGenerationPreparationResult PrepareRegionRequest(AiGenerationPreparationInput input)
    {
        byte[] png = CreatePng();
        var item = new CanvasCompositeItem(
            input.Context.SelectedImageId ?? "layer-item",
            0,
            0,
            true,
            Vector2.Zero,
            Vector2.One,
            0,
            1,
            png);
        AiGenerationGridRegion q = input.Context.RegionMetadata!.SelectionRegion!.Value;
        AiGenerationGridRegion w = input.Context.RegionMetadata.ReplacementRegion!.Value;
        var reference = new CanvasGridRegion(q.Column, q.Row, q.Width, q.Height);
        var replacement = new CanvasGridRegion(w.Column, w.Row, w.Width, w.Height);
        CanvasPatchCapture capture = CanvasCompositeRenderer.Capture(new CanvasPatchCaptureRequest(
            reference,
            replacement,
            input.Context.GridSizePx,
            new[] { item },
            item.Id));
        Rect2I edit = capture.EditRegionPixels;
        var editInput = new AiGenerationEditInput(
            AiGenerationReferenceImage.Create(capture.SourceCanvasPng, "image/png"),
            AiGenerationReferenceImage.Create(capture.MaskPng, "image/png"),
            new AiGenerationPixelRegion(edit.Position.X, edit.Position.Y, edit.Size.X, edit.Size.Y));
        var request = new AiGenerationRequest(
            input.Context.ProjectRoot,
            input.Prompt,
            capture.ReferenceWorldPixels.Size.X,
            capture.ReferenceWorldPixels.Size.Y,
            regionMetadata: input.Context.RegionMetadata,
            operation: AiGenerationOperation.RegionEdit,
            editInput: editInput);
        return AiGenerationPreparationResult.Ok(request, capture);
    }

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private async Task WaitUntil(Func<bool> condition, string message)
    {
        for (int frame = 0; frame < 240; frame++)
        {
            if (condition())
            {
                return;
            }
            await Frame();
        }
        throw new InvalidOperationException(message);
    }

    private static byte[] CreatePng()
    {
        using Image image = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
        image.Fill(Colors.Green);
        return image.SavePngToBuffer();
    }

    private static T FindRequired<T>(Node root, string name) where T : Node =>
        FindOptional<T>(root, name) ?? throw new InvalidOperationException($"Missing node: {name}");

    private static T? FindOptional<T>(Node root, string name) where T : Node =>
        root.FindChild(name, recursive: true, owned: false) as T;

    private static int FindPresetIndex(OptionButton options, string modelName)
    {
        for (int index = 0; index < options.ItemCount; index++)
        {
            string packed = options.GetItemMetadata(index).AsString();
            int separator = packed.IndexOf('\n');
            string actual = separator >= 0 ? packed[(separator + 1)..] : packed;
            if (string.Equals(actual, modelName, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindOptionIndex(OptionButton options, string providerId)
    {
        for (int index = 0; index < options.ItemCount; index++)
        {
            if (options.GetItemMetadata(index).AsString() == providerId)
            {
                return index;
            }
        }
        return -1;
    }

    private sealed class FakeRunner : IAiGenerationRunner
    {
        private readonly List<TaskCompletionSource<AiGenerationResult>> _pending = new();
        public List<string> ProfileIds { get; } = new();
        public List<AiGenerationProviderProfile> Profiles { get; } = new();

        public int RequestCount { get; private set; }
        public int CancellationCount { get; private set; }

        public Task<AiGenerationResult> GenerateAsync(
            AiGenerationProviderProfile profile,
            AiGenerationRequest request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            ProfileIds.Add(profile.Id);
            Profiles.Add(profile);
            var completion = new TaskCompletionSource<AiGenerationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(completion);
            cancellationToken.Register(() =>
            {
                CancellationCount++;
                completion.TrySetCanceled(cancellationToken);
            });
            return completion.Task;
        }

        public void FailNext()
        {
            TaskCompletionSource<AiGenerationResult>? next = _pending.FirstOrDefault(item => !item.Task.IsCompleted);
            if (next == null)
            {
                return;
            }
            var diagnostic = new AiGenerationSafeDiagnostic(
                Guid.NewGuid().ToString("N"),
                AiGenerationErrorCode.ProviderRequestFailed,
                AiGenerationFailurePhase.ProviderRequest,
                AiGenerationFailureCategory.InvalidResponse,
                "profile-1",
                "GptImage",
                "gpt-image-2",
                "cpa.axoncat.com",
                null,
                "ResponseEnded");
            next.TrySetException(new AiGenerationException(
                AiGenerationErrorCode.ProviderRequestFailed,
                "中转站在响应完成前关闭了连接。",
                diagnostic));
        }

        public void CompleteNext()
        {
            TaskCompletionSource<AiGenerationResult>? next = _pending.FirstOrDefault(item => !item.Task.IsCompleted);
            next?.TrySetResult(Result());
        }

        public void CompleteAll()
        {
            foreach (TaskCompletionSource<AiGenerationResult> completion in _pending)
            {
                completion.TrySetResult(Result());
            }
        }

        private static AiGenerationResult Result() => new(
            "assets/generated/fake.png",
            "image/png",
            32,
            AiGenerationEffectiveOperation.GuidedRegionEdit,
            AiGenerationEditGuarantee.GuidedReference,
            2,
            2);
    }

    private static class Assert
    {
        public static void True(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
        }

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException($"{message} Expected={expected} Actual={actual}");
            }
        }
    }
}
