using System;
using System.IO;
using System.Linq;
using AGAS.UI;
using Agas.Features.Canvas;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Features.Ai.UI;
using AppAgasCs.Features.Ai.Workspace;
using AppAgasCs.Features.Ai.Generation;
using Godot;

public partial class CanvasToolStatusAcceptance : Node
{
    private const string Prefix = "[AGAS][CANVAS-TOOL-STATUS]";
    private string? _root;
    private string? _home;
    private string? _previousHome;

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
            System.Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, _previousHome);
            if (_root != null)
            {
                try { Directory.Delete(_root, recursive: true); } catch { }
            }
            if (_home != null)
            {
                try { Directory.Delete(_home, recursive: true); } catch { }
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GetTree().Quit(exitCode);
        }
    }

    private async System.Threading.Tasks.Task RunAll()
    {
        _root = Path.Combine(Path.GetTempPath(), "agas-canvas-tool-status-" + Guid.NewGuid().ToString("N"));
        _home = Path.Combine(Path.GetTempPath(), "agas-canvas-tool-status-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        SaveImage(Path.Combine(_root, "assets", "selected.png"));
        _previousHome = System.Environment.GetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable);
        System.Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, _home);
        var profileA = new AgasAiProfile("profile-a", "配置 A", AgasAiProviderIds.OpenAiCompatible, "gpt-image-2", "https://a.test/v1/");
        var profileB = new AgasAiProfile("profile-b", "配置 B", AgasAiProviderIds.GeminiCompatible, "gemini-3-pro-image-preview", "https://b.test/v1/");
        new AgasAiSettingsStore(_home).SaveProfiles(new AgasAiProfilesDocument(
            AgasAiProfilesDocument.CurrentResourceVersion,
            profileA.Id,
            new[] { profileA, profileB }));

        var coordinator = new AgasAiGenerationCoordinator();
        var workspace = new MainWorkspace(coordinator) { CustomMinimumSize = new Vector2(1200, 760) };
        AddChild(workspace);
        await Frames(2);
        workspace.OpenProject(_root);
        await Frames(4);

        CanvasWorkspace canvas = FindRequired<CanvasWorkspace>(workspace, "CanvasWorkspaceHost");
        Control statusBar = FindRequired<Control>(workspace, "CanvasToolStatusBar");
        Button q = FindRequired<Button>(workspace, "CanvasSelectionToolButton");
        Button w = FindRequired<Button>(workspace, "CanvasReplacementToolButton");
        Button e = FindRequired<Button>(workspace, "CanvasEditToolButton");
        Button zoomOut = FindRequired<Button>(workspace, "CanvasZoomOutButton");
        Button zoomPercent = FindRequired<Button>(workspace, "CanvasZoomPercentButton");
        Button zoomIn = FindRequired<Button>(workspace, "CanvasZoomInButton");
        Button zoomFit = FindRequired<Button>(workspace, "CanvasZoomFitButton");
        Label imageStatus = FindRequired<Label>(workspace, "CanvasSelectionStatusLabel");
        Label contextStatus = FindRequired<Label>(workspace, "CanvasContextStatusLabel");
        Label taskCount = FindRequired<Label>(workspace, "AiGenerationTaskCountLabel");
        OptionButton profileSelector = FindRequired<OptionButton>(workspace, "AiGenerationProfileSelector");
        AiGenerationPanel aiController = FindRequired<AiGenerationPanel>(workspace, "AiGenerationPanel");

        Require(statusBar.Visible && statusBar.CustomMinimumSize.Y <= 32, "Canvas tool status bar must remain visible and compact.");
        Require(q.Text.Contains("Q", StringComparison.Ordinal)
            && w.Text.Contains("W", StringComparison.Ordinal)
            && e.Text.Contains("E", StringComparison.Ordinal),
            "Every tool item must display its shortcut.");
        Require(q.FocusMode == Control.FocusModeEnum.None
            && w.FocusMode == Control.FocusModeEnum.None
            && e.FocusMode == Control.FocusModeEnum.None
            && zoomOut.FocusMode == Control.FocusModeEnum.None
            && zoomPercent.FocusMode == Control.FocusModeEnum.None
            && zoomIn.FocusMode == Control.FocusModeEnum.None
            && zoomFit.FocusMode == Control.FocusModeEnum.None,
            "Tool items must not retain keyboard focus.");
        Require(canvas.ActiveRegionTool == CanvasRegionTool.Edit && e.ButtonPressed && !q.ButtonPressed && !w.ButtonPressed,
            "E edit mode must be active by default.");
        Require(imageStatus.Text.Contains("未选中", StringComparison.Ordinal), "Status must expose the empty image selection.");
        Require(contextStatus.Text.Contains("图层：Layer 1", StringComparison.Ordinal), "Status must expose the active layer.");
        Require(taskCount.Text == "任务 5/0", "Compact task count must start at limit/active = 5/0.");
        Require(profileSelector.ItemCount == 7
                && AgasAiUnifiedModelCatalog.Models.All(model =>
                    Enumerable.Range(0, profileSelector.ItemCount).Any(index =>
                        profileSelector.GetItemMetadata(index).AsString() == model.ModelName))
                && Enumerable.Range(0, profileSelector.ItemCount).All(index =>
                    !profileSelector.GetItemText(index).Contains(profileA.Name, StringComparison.Ordinal)
                    && !profileSelector.GetItemText(index).Contains(profileB.Name, StringComparison.Ordinal)),
            "The compact selector must list exactly the seven unified models without connection names.");
        var overlayContext = new AiGenerationCanvasContext(
            workspace.ProjectRoot,
            workspace.CurrentProjectId,
            workspace.CurrentScene!.Id,
            workspace.CurrentScene.ActiveLayerId,
            false,
            null,
            true,
            48,
            new AppAgasCs.Features.Ai.Generation.AiGenerationRegionMetadata(
                new AppAgasCs.Features.Ai.Generation.AiGenerationGridRegion(0, 0, 1, 1),
                null));
        int modelIndex = Enumerable.Range(0, profileSelector.ItemCount).Single(index =>
            profileSelector.GetItemMetadata(index).AsString() == "gpt-image-2.5-flare");
        profileSelector.Select(modelIndex);
        profileSelector.EmitSignal(OptionButton.SignalName.ItemSelected, (long)modelIndex);
        AgasAiProfilesDocument afterModelSwitch = new AgasAiSettingsStore(_home).LoadProfiles();
        Require(afterModelSwitch.ActiveModelName == "gpt-image-2.5-flare"
                && aiController.SelectedProfileId == profileA.Id,
            "Selecting the compact model control must change only the global model.");
        IAgasAiGenerationPermit[] permits = Enumerable.Range(0, 3)
            .Select(_ => coordinator.TryAcquire(out IAgasAiGenerationPermit? permit)
                ? permit!
                : throw new InvalidOperationException("Fixture could not acquire an AI generation permit."))
            .ToArray();
        aiController.GenerationOverlaysChanged?.Invoke(Enumerable.Range(1, 3)
            .Select(id => new AiGenerationOverlayState(
                id,
                overlayContext,
                new Rect2(id * 48, 0, 48, 48),
                AiGenerationOverlayPhase.Generating,
                0,
                "AI 生成中"))
            .ToArray());
        Require(taskCount.Text == "任务 5/3", "Concurrent task updates must render limit/active = 5/3.");
        foreach (IAgasAiGenerationPermit permit in permits) permit.Dispose();
        aiController.GenerationOverlaysChanged?.Invoke(new[]
        {
            new AiGenerationOverlayState(1, overlayContext, new Rect2(48, 0, 48, 48), AiGenerationOverlayPhase.Failure, 1, "失败"),
            new AiGenerationOverlayState(2, overlayContext, new Rect2(96, 0, 48, 48), AiGenerationOverlayPhase.Canceled, 1, "取消"),
        });
        Require(taskCount.Text == "任务 5/0", "Completed, failed, or canceled tasks must not remain active in the count.");

        Require(canvas.TryAddImageFromProjectRelativePath("assets/selected.png", new Vector2(48, 96)), "Fixture image should be added.");
        Require(imageStatus.Text.Contains("selected.png", StringComparison.Ordinal), "Selection signal must update the image status immediately.");
        Require(FindRequired<Control>(workspace, "WorkspaceToolbar").GetChildCount() > 0, "Workspace toolbar must be available for shortcut actions.");

        Press(zoomIn);
        Require(Mathf.IsEqualApprox(canvas.GetZoom(), 1.1f) && zoomPercent.Text == "110%",
            "Canvas zoom-in control did not update the viewport and percentage.");
        Press(zoomOut);
        Require(Mathf.IsEqualApprox(canvas.GetZoom(), 1.0f) && zoomPercent.Text == "100%",
            "Canvas zoom-out control did not update the viewport and percentage.");
        Press(zoomIn);
        Press(zoomPercent);
        Require(Mathf.IsEqualApprox(canvas.GetZoom(), 1.0f) && zoomPercent.Text == "100%",
            "Canvas percentage control did not reset the viewport to 100%.");
        ImageInstanceResource selectedForFit = canvas.GetSelectedImage() as ImageInstanceResource
            ?? throw new InvalidOperationException("Selected image fixture was not retained for fit testing.");
        selectedForFit.ScaleX = 200;
        selectedForFit.ScaleY = 200;
        Press(zoomFit);
        Require(canvas.GetZoom() < 1.0f && zoomPercent.Text != "100%",
            "Fit-visible control did not fit oversized visible content.");
        selectedForFit.ScaleX = 1;
        selectedForFit.ScaleY = 1;
        Press(zoomPercent);

        workspace._UnhandledKeyInput(new InputEventKey { Keycode = Key.Z, Pressed = true, CtrlPressed = true });
        Require(canvas.CanRedo(), "Ctrl/Cmd+Z must undo the latest canvas command through the workspace.");
        workspace._UnhandledKeyInput(new InputEventKey { Keycode = Key.Y, Pressed = true, CtrlPressed = true });
        Require(canvas.CanUndo(), "Ctrl/Cmd+Y must redo the latest canvas command through the workspace.");
        workspace._UnhandledKeyInput(new InputEventKey { Keycode = Key.S, Pressed = true, CtrlPressed = true });
        Require(!workspace.HasUnsavedChanges, "Ctrl/Cmd+S must save the project through the workspace.");

        canvas.GrabFocus();
        Press(q);
        Require(canvas.ActiveRegionTool == CanvasRegionTool.Selection && q.ButtonPressed && !w.ButtonPressed && !e.ButtonPressed,
            "Clicking Q must activate and highlight only the selection tool.");
        Require(canvas.HasFocus(), "Clicking a status-bar tool must return keyboard focus to the canvas.");
        Drag(canvas, new Vector2(350, 250), new Vector2(450, 350));
        Require(canvas.SelectionRegion.HasValue, "Q drag must create a selection region.");
        CanvasGridRegion selection = canvas.SelectionRegion.GetValueOrDefault();
        Require(contextStatus.Text.Contains($"Q 发送/写回：({selection.Column},{selection.Row}) {selection.Width}×{selection.Height}", StringComparison.Ordinal),
            "Region signal must update the Q summary immediately.");

        Press(w);
        Require(canvas.ActiveRegionTool == CanvasRegionTool.Replacement && w.ButtonPressed && !q.ButtonPressed && !e.ButtonPressed,
            "Clicking W must activate and highlight only the replacement tool.");
        Drag(canvas, new Vector2(400, 300), new Vector2(440, 340));
        Require(canvas.ReplacementRegion.HasValue && contextStatus.Text.Contains("W 透明洞：(", StringComparison.Ordinal),
            "Region signal must update the W summary immediately.");

        workspace._UnhandledKeyInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        Require(canvas.ActiveRegionTool == CanvasRegionTool.Edit && e.ButtonPressed,
            "Escape must return to E and update the active highlight.");
        workspace._UnhandledKeyInput(new InputEventKey { Keycode = Key.Q, Pressed = true });
        workspace._UnhandledKeyInput(new InputEventKey { Keycode = Key.E, Pressed = true });
        Require(canvas.ActiveRegionTool == CanvasRegionTool.Edit && e.ButtonPressed,
            "Workspace-level Q/W/E shortcuts must update tool state even outside direct canvas dispatch.");

        Press(FindRequired<Button>(workspace, "LeftDockToggleButton"));
        Press(FindRequired<Button>(workspace, "RightDockToggleButton"));
        Press(FindRequired<Button>(workspace, "BottomDockToggleButton"));
        await Frames(3);
        Require(statusBar.Visible && statusBar.IsVisibleInTree(), "Dock minimization must not hide the canvas tool status bar.");

        workspace.QueueFree();
        await Frames(6);
    }

    private async System.Threading.Tasks.Task Frames(int count)
    {
        for (int index = 0; index < count; index++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static void Drag(CanvasWorkspace canvas, Vector2 start, Vector2 end)
    {
        canvas._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = start });
        canvas._GuiInput(new InputEventMouseMotion { Position = end, Relative = end - start, ButtonMask = MouseButtonMask.Left });
        canvas._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = end });
    }

    private static void Press(Button button) => button.EmitSignal(Button.SignalName.Pressed);

    private static void SaveImage(string path)
    {
        Image image = Image.CreateEmpty(16, 16, false, Image.Format.Rgba8);
        image.Fill(Colors.CornflowerBlue);
        Error error = image.SavePng(path);
        if (error != Error.Ok)
        {
            throw new InvalidOperationException($"Failed to save fixture image: {error}");
        }
    }

    private static T FindRequired<T>(Node root, string name) where T : Node =>
        root.FindChild(name, recursive: true, owned: false) as T
        ?? throw new InvalidOperationException($"Required node is missing: {name}");

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
