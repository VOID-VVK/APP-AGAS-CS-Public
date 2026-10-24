using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AGAS.UI;
using Agas.Features.Canvas;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Prompting;
using Godot;

public partial class LayerSwitchPerformanceAcceptance : Node
{
    private const string Prefix = "[AGAS-H2][LAYER-SWITCH-PERF]";
    private const int DirectoryCount = 80;
    private const int ImageFilesPerDirectory = 6;
    private const int LayerCount = 8;
    private const int SwitchWarmupCount = 2;
    private const int SwitchSampleCount = 14;

    private readonly List<string> _tempRoots = new();

    public override async void _Ready()
    {
        int exitCode = 0;
        try
        {
            await RunAll();
            GD.Print($"{Prefix}[PASS] Godot C# headless layer switch performance acceptance completed.");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {exception}");
        }
        finally
        {
            CleanupTempRoots();
            GetTree().Quit(exitCode);
        }
    }

    private async System.Threading.Tasks.Task RunAll()
    {
        string root = NewTempRoot("layer-switch-performance");
        CreateFileFixture(root);
        CreateProjectFixture(root);

        MainWorkspace workspace = new()
        {
            Name = "LayerSwitchPerformanceWorkspace",
            CustomMinimumSize = new Vector2(1280, 820)
        };
        AddChild(workspace);
        await ProcessFrame();

        workspace.OpenProject(root);
        await ProcessFrame();

        ProjectFilesTree filesTree = FindRequired<ProjectFilesTree>(workspace, "Files");
        LayerTabs layerTabs = FindRequired<LayerTabs>(workspace, "LayerTabs");
        Node layerActionBar = FindRequired<Node>(workspace, "LayerActionBar");
        Node layerButtons = FindRequired<Node>(workspace, "LayerTabButtons");
        VBoxContainer promptList = FindRequired<VBoxContainer>(workspace, "PromptFragmentList");
        TextEdit mergedPreview = FindRequired<TextEdit>(workspace, "PromptMergedPreview");

        Assert.Equal(LayerCount, LayerButtonCount(layerButtons), "Fixture should expose all layer buttons.");
        Assert.Equal("layer-001", workspace.CurrentScene?.ActiveLayerId, "Initial active layer should be layer-001.");
        Assert.True(ProjectFileRootInstanceId(filesTree) != 0, "Project files tree should have a root TreeItem.");
        await VerifyLayerUiContracts(workspace, layerTabs, layerButtons);
        await VerifySingleLayerDeleteBoundary();
        await VerifyNarrowLayerToolbarLayout();
        layerButtons = FindRequired<Node>(layerTabs, "LayerTabButtons");

        foreach (string layerId in BuildTargetLayerIds(workspace.CurrentScene?.ActiveLayerId ?? string.Empty, SwitchWarmupCount))
        {
            SwitchSample warmup = await ClickLayerAndMeasure(
                workspace,
                filesTree,
                layerActionBar,
                layerButtons,
                promptList,
                mergedPreview,
                layerId,
                expectActiveChange: true);
            GD.Print(warmup.ToLogLine(Prefix + "[WARMUP]"));
        }

        var samples = new List<SwitchSample>();
        foreach (string layerId in BuildTargetLayerIds(workspace.CurrentScene?.ActiveLayerId ?? string.Empty, SwitchSampleCount))
        {
            SwitchSample sample = await ClickLayerAndMeasure(
                workspace,
                filesTree,
                layerActionBar,
                layerButtons,
                promptList,
                mergedPreview,
                layerId,
                expectActiveChange: true);
            samples.Add(sample);
            GD.Print(sample.ToLogLine(Prefix));
        }

        SwitchSample duplicateSample = await ClickLayerAndMeasure(
            workspace,
            filesTree,
            layerActionBar,
            layerButtons,
            promptList,
            mergedPreview,
            workspace.CurrentScene?.ActiveLayerId ?? string.Empty,
            expectActiveChange: false);
        GD.Print(duplicateSample.ToLogLine(Prefix + "[DUPLICATE]"));

        PerfStats syncStats = PerfStats.From(samples.Select(sample => sample.SyncMicroseconds).ToArray());
        PerfStats frameStats = PerfStats.From(samples.Select(sample => sample.FrameMicroseconds).ToArray());
        int treeRebuilds = samples.Count(sample => sample.ProjectFilesTreeRebuilt);
        int duplicateTreeRebuilds = duplicateSample.ProjectFilesTreeRebuilt ? 1 : 0;
        int canvasChangedSamples = samples.Count(sample => sample.CanvasChangedObserved > 0);
        int maxActionBarStructuralChurnEstimate = samples.Select(sample => sample.ActionBarStructuralChurnEstimate).DefaultIfEmpty(0).Max();
        int maxTabStripStructuralChurnEstimate = samples.Select(sample => sample.TabStripStructuralChurnEstimate).DefaultIfEmpty(0).Max();
        int maxPromptPanelStructuralChurnEstimate = samples.Select(sample => sample.PromptPanelStructuralChurnEstimate).DefaultIfEmpty(0).Max();

        GD.Print($"{Prefix}[SUMMARY] fixture_directories={DirectoryCount} fixture_image_files_per_directory={ImageFilesPerDirectory} fixture_layers={LayerCount} warmup={SwitchWarmupCount} samples={samples.Count} sync_p50_us={syncStats.P50Us:F1} sync_p95_us={syncStats.P95Us:F1} sync_max_us={syncStats.MaxUs:F1} frame_p50_us={frameStats.P50Us:F1} frame_p95_us={frameStats.P95Us:F1} frame_max_us={frameStats.MaxUs:F1} tree_rebuilds={treeRebuilds} canvas_changed_samples={canvasChangedSamples} max_action_bar_structural_churn_estimate={maxActionBarStructuralChurnEstimate} max_tab_strip_structural_churn_estimate={maxTabStripStructuralChurnEstimate} max_prompt_panel_structural_churn_estimate={maxPromptPanelStructuralChurnEstimate} duplicate_tree_rebuilds={duplicateTreeRebuilds} duplicate_action_bar_structural_churn_estimate={duplicateSample.ActionBarStructuralChurnEstimate} duplicate_tab_strip_structural_churn_estimate={duplicateSample.TabStripStructuralChurnEstimate} duplicate_prompt_panel_structural_churn_estimate={duplicateSample.PromptPanelStructuralChurnEstimate} duplicate_canvas_changed={duplicateSample.CanvasChangedObserved}");

        Assert.Equal(0, treeRebuilds, "Switching layers must not rebuild ProjectFilesTree root TreeItem.");
        Assert.Equal(0, canvasChangedSamples, "Switching layers must not emit CanvasChanged side effects.");
        Assert.Equal(0, duplicateTreeRebuilds, "Clicking the current layer must not rebuild ProjectFilesTree root TreeItem.");
        Assert.Equal(0, maxActionBarStructuralChurnEstimate, "Selection-only layer switches must not structurally churn the layer action bar.");
        Assert.Equal(0, maxTabStripStructuralChurnEstimate, "Selection-only layer switches must not structurally churn the tab strip.");
        Assert.True(maxPromptPanelStructuralChurnEstimate <= 1, "Each layer switch should cause at most one PromptPanel structural churn estimate.");
        Assert.Equal(0, duplicateSample.ActionBarStructuralChurnEstimate, "Clicking the current layer must not structurally churn the layer action bar.");
        Assert.Equal(0, duplicateSample.TabStripStructuralChurnEstimate, "Clicking the current layer must not structurally churn the layer tab strip.");
        Assert.Equal(0, duplicateSample.PromptPanelStructuralChurnEstimate, "Clicking the current layer must not structurally churn PromptPanel.");
        Assert.Equal(0, duplicateSample.CanvasChangedObserved, "Clicking the current layer must not emit CanvasChanged side effects.");
        Assert.True(syncStats.MaxUs < 200_000.0, "Synchronous layer switch should remain below the loose 200ms acceptance ceiling.");

        workspace.QueueFree();
        await ProcessFrame();
    }

    private async System.Threading.Tasks.Task<SwitchSample> ClickLayerAndMeasure(
        MainWorkspace workspace,
        ProjectFilesTree filesTree,
        Node layerActionBar,
        Node layerButtons,
        VBoxContainer promptList,
        TextEdit mergedPreview,
        string targetLayerId,
        bool expectActiveChange)
    {
        string beforeLayerId = workspace.CurrentScene?.ActiveLayerId ?? string.Empty;
        ulong beforeRootId = ProjectFileRootInstanceId(filesTree);
        int beforeActionChildren = layerActionBar.GetChildCount();
        int beforeLayerButtonChildren = layerButtons.GetChildCount();
        int beforePromptChildren = promptList.GetChildCount();

        using SignalCounter layerActionCounter = SignalCounter.ConnectChildOrder(layerActionBar);
        using SignalCounter layerButtonsCounter = SignalCounter.ConnectChildOrder(layerButtons);
        using SignalCounter promptCounter = SignalCounter.ConnectChildOrder(promptList);
        int canvasChangedCount = 0;
        CanvasWorkspace canvas = FindRequired<CanvasWorkspace>(workspace, "CanvasWorkspaceHost");
        CanvasWorkspace.CanvasChangedEventHandler canvasChangedHandler = () => canvasChangedCount++;
        canvas.CanvasChanged += canvasChangedHandler;

        Button targetButton = FindLayerButton(layerButtons, targetLayerId);
        Control targetTab = FindRequired<Control>(layerButtons, $"LayerTab-{targetLayerId}");
        ulong beforeTargetTabInstanceId = targetTab.GetInstanceId();
        Stopwatch stopwatch = Stopwatch.StartNew();
        double syncMicroseconds;
        double frameMicroseconds;
        try
        {
            targetButton.EmitSignal(BaseButton.SignalName.Pressed);
            syncMicroseconds = stopwatch.Elapsed.TotalMilliseconds * 1000.0;
            await ProcessFrame();
            frameMicroseconds = stopwatch.Elapsed.TotalMilliseconds * 1000.0;
        }
        finally
        {
            canvas.CanvasChanged -= canvasChangedHandler;
        }

        ulong afterRootId = ProjectFileRootInstanceId(filesTree);
        int afterActionChildren = layerActionBar.GetChildCount();
        int afterLayerButtonChildren = layerButtons.GetChildCount();
        int afterPromptChildren = promptList.GetChildCount();
        int actionBarChildOrderChanges = layerActionCounter.Count;
        int tabStripChildOrderChanges = layerButtonsCounter.Count;
        int promptChildOrderChanges = promptCounter.Count;
        int oneActionBarRefreshBudget = Math.Max(1, beforeActionChildren + afterActionChildren);
        int oneTabStripRefreshBudget = Math.Max(1, beforeLayerButtonChildren + afterLayerButtonChildren);
        int onePromptRefreshBudget = Math.Max(1, beforePromptChildren + afterPromptChildren);
        string afterLayerId = workspace.CurrentScene?.ActiveLayerId ?? string.Empty;
        string expectedPrompt = ExpectedPromptText(targetLayerId);
        AgasLayerResource? displayedLayer = workspace.CurrentScene?.Layers.FirstOrDefault(layer => layer.Id == afterLayerId);

        if (expectActiveChange)
        {
            Assert.Equal(targetLayerId, afterLayerId, "ActiveLayerId should follow the clicked layer.");
            Assert.Equal(expectedPrompt, mergedPreview.Text, "PromptPanel merged preview should follow the clicked layer.");
        }
        else
        {
            Assert.Equal(beforeLayerId, afterLayerId, "Clicking current layer should preserve ActiveLayerId.");
        }
        Assert.True(displayedLayer != null, "The active layer should remain available after selection.");
        Assert.Equal(beforeTargetTabInstanceId, FindRequired<Control>(layerButtons, $"LayerTab-{targetLayerId}").GetInstanceId(), "Selection-only switching must preserve the target compound-tab instance.");
        Assert.True(targetButton.ButtonPressed, "Selection-only switching should expose the active state on the selected tab button.");

        return new SwitchSample(
            beforeLayerId,
            targetLayerId,
            afterLayerId,
            syncMicroseconds,
            frameMicroseconds,
            beforeRootId,
            afterRootId,
            beforeRootId != afterRootId,
            actionBarChildOrderChanges,
            EstimateStructuralChurn(actionBarChildOrderChanges, oneActionBarRefreshBudget),
            tabStripChildOrderChanges,
            EstimateStructuralChurn(tabStripChildOrderChanges, oneTabStripRefreshBudget),
            promptChildOrderChanges,
            EstimateStructuralChurn(promptChildOrderChanges, onePromptRefreshBudget),
            canvasChangedCount);
    }

    private static IEnumerable<string> BuildTargetLayerIds(string currentLayerId, int count)
    {
        int currentIndex = ParseLayerIndex(currentLayerId);
        for (int offset = 1; offset <= count; offset++)
        {
            int nextIndex = ((currentIndex - 1 + offset) % LayerCount) + 1;
            yield return $"layer-{nextIndex:000}";
        }
    }

    private static int ParseLayerIndex(string layerId)
    {
        return int.TryParse(layerId.Split('-').LastOrDefault(), out int parsed) && parsed >= 1 && parsed <= LayerCount
            ? parsed
            : 1;
    }

    private static int EstimateStructuralChurn(int childOrderChanges, int singleRefreshBudget)
    {
        if (childOrderChanges <= 0)
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Ceiling(childOrderChanges / (double)Math.Max(1, singleRefreshBudget)));
    }

    private async System.Threading.Tasks.Task VerifyLayerUiContracts(
        MainWorkspace workspace,
        LayerTabs layerTabs,
        Node layerButtons)
    {
        for (int index = 1; index <= LayerCount; index++)
        {
            string layerId = $"layer-{index:000}";
            PanelContainer compoundTab = FindRequired<PanelContainer>(layerButtons, $"LayerTab-{layerId}");
            Button select = FindRequired<Button>(compoundTab, $"LayerTabSelect-{layerId}");
            Button visibility = FindRequired<Button>(compoundTab, $"LayerVisible-{layerId}");
            Button locked = FindRequired<Button>(compoundTab, $"LayerLocked-{layerId}");
            Button shared = FindRequired<Button>(compoundTab, $"LayerShared-{layerId}");

            Assert.True(select.ToggleMode, $"{layerId} must expose a real toggle-style tab selection control.");
            Assert.Equal(index == 1, select.ButtonPressed, $"{layerId} selected state must match ActiveLayerId.");
            AssertIconControl(visibility, $"{layerId} visibility");
            AssertIconControl(locked, $"{layerId} lock");
            AssertIconControl(shared, $"{layerId} share");
        }

        Button add = FindRequired<Button>(layerTabs, "LayerAddButton");
        Button rename = FindRequired<Button>(layerTabs, "LayerRenameButton");
        Button delete = FindRequired<Button>(layerTabs, "LayerDeleteButton");
        Button moveUp = FindRequired<Button>(layerTabs, "LayerMoveUpButton");
        Button moveDown = FindRequired<Button>(layerTabs, "LayerMoveDownButton");
        Button prompt = FindRequired<Button>(layerTabs, "LayerPromptButton");
        AssertIconControl(rename, "rename tool");
        AssertIconControl(moveUp, "move-up tool");
        AssertIconControl(moveDown, "move-down tool");
        AssertIconControl(prompt, "prompt tool");
        AssertIconControl(delete, "delete tool");
        Assert.True(!add.Disabled && !rename.Disabled && !delete.Disabled && !prompt.Disabled, "Multi-layer action controls should be available for the active layer.");
        Assert.True(moveUp.Disabled, "The first ordered layer must not move farther up.");
        Assert.True(!moveDown.Disabled, "The first ordered layer should be movable down when multiple layers exist.");

        int visibilityEvents = 0;
        int lockEvents = 0;
        int shareEvents = 0;
        int moveEvents = 0;
        string lastMoveLayerId = string.Empty;
        int lastMoveDirection = 0;
        layerTabs.LayerVisibilityChanged += (_, _) => visibilityEvents++;
        layerTabs.LayerLockChanged += (_, _) => lockEvents++;
        layerTabs.LayerShareChanged += (_, _) => shareEvents++;
        layerTabs.LayerMoveRequested += (layer, direction) =>
        {
            moveEvents++;
            lastMoveLayerId = layer.Id;
            lastMoveDirection = direction;
        };

        FindRequired<Button>(layerTabs, "LayerVisible-layer-001").EmitSignal(BaseButton.SignalName.Pressed);
        await ProcessFrame();
        Assert.Equal(1, visibilityEvents, "Visibility icon should emit exactly one visibility event.");
        Assert.Equal(false, FindLayer(workspace, "layer-001").Visible, "Visibility icon should update the target layer.");
        FindRequired<Button>(layerTabs, "LayerVisible-layer-001").EmitSignal(BaseButton.SignalName.Pressed);
        await ProcessFrame();
        Assert.Equal(2, visibilityEvents, "Each visibility press should emit exactly one visibility event.");
        Assert.Equal(true, FindLayer(workspace, "layer-001").Visible, "Second visibility press should restore the fixture state.");

        FindRequired<Button>(layerTabs, "LayerLocked-layer-001").EmitSignal(BaseButton.SignalName.Pressed);
        await ProcessFrame();
        Assert.Equal(1, lockEvents, "Lock icon should emit exactly one lock event.");
        Assert.Equal(true, FindLayer(workspace, "layer-001").Locked, "Lock icon should update the target layer.");
        FindRequired<Button>(layerTabs, "LayerLocked-layer-001").EmitSignal(BaseButton.SignalName.Pressed);
        await ProcessFrame();
        Assert.Equal(2, lockEvents, "Each lock press should emit exactly one lock event.");
        Assert.Equal(false, FindLayer(workspace, "layer-001").Locked, "Second lock press should restore the fixture state.");

        FindRequired<Button>(layerTabs, "LayerShared-layer-001").EmitSignal(BaseButton.SignalName.Pressed);
        await ProcessFrame();
        Assert.Equal(1, shareEvents, "Share icon should emit exactly one share event.");
        Assert.Equal(true, FindLayer(workspace, "layer-001").Shared, "Share icon should update the target layer.");
        FindRequired<Button>(layerTabs, "LayerShared-layer-001").EmitSignal(BaseButton.SignalName.Pressed);
        await ProcessFrame();
        Assert.Equal(2, shareEvents, "Each share press should emit exactly one share event.");
        Assert.Equal(false, FindLayer(workspace, "layer-001").Shared, "Second share press should restore the fixture state.");

        FindLayerButton(layerTabs, "layer-008").EmitSignal(BaseButton.SignalName.Pressed);
        await ProcessFrame();
        await ProcessFrame();
        Assert.Equal("layer-008", workspace.CurrentScene?.ActiveLayerId, "Last tab should become active.");
        Assert.True(FindLayerButton(layerTabs, "layer-008").ButtonPressed, "Last tab should expose its selected state.");
        Assert.True(!FindRequired<Button>(layerTabs, "LayerMoveUpButton").Disabled, "The last ordered layer should be movable up.");
        Assert.True(FindRequired<Button>(layerTabs, "LayerMoveDownButton").Disabled, "The last ordered layer must not move farther down.");
        AssertControlVisibleInHorizontalScroll(
            FindRequired<ScrollContainer>(layerTabs, "LayerTabStrip"),
            FindRequired<Control>(layerTabs, "LayerTab-layer-008"),
            "Selecting an overflowed layer must scroll its compound tab into view.");

        FindLayerButton(layerTabs, "layer-001").EmitSignal(BaseButton.SignalName.Pressed);
        await ProcessFrame();
        FindRequired<Button>(layerTabs, "LayerMoveDownButton").EmitSignal(BaseButton.SignalName.Pressed);
        await ProcessFrame();
        Assert.Equal(1, moveEvents, "Move-down should emit exactly one move event.");
        Assert.Equal("layer-001", lastMoveLayerId, "Move-down should target the active layer.");
        Assert.Equal(1, lastMoveDirection, "Move-down should use direction +1.");
        FindRequired<Button>(layerTabs, "LayerMoveUpButton").EmitSignal(BaseButton.SignalName.Pressed);
        await ProcessFrame();
        Assert.Equal(2, moveEvents, "Move-up should emit exactly one additional move event.");
        Assert.Equal("layer-001", lastMoveLayerId, "Move-up should target the active layer.");
        Assert.Equal(-1, lastMoveDirection, "Move-up should use direction -1.");
        Assert.True(FindRequired<Button>(layerTabs, "LayerMoveUpButton").Disabled, "Restored first layer must again disable move-up.");
    }

    private async System.Threading.Tasks.Task VerifySingleLayerDeleteBoundary()
    {
        var scene = new AgasSceneResource { Id = "single-scene", Name = "Single", ActiveLayerId = "only-layer" };
        scene.Layers.Add(new AgasLayerResource
        {
            Id = "only-layer",
            Name = "Only Layer",
            Order = 1,
            Visible = true
        });

        var singleLayerTabs = new LayerTabs { Name = "SingleLayerTabsFixture" };
        AddChild(singleLayerTabs);
        await ProcessFrame();
        singleLayerTabs.SetScene(scene);
        await ProcessFrame();

        Assert.True(FindRequired<Button>(singleLayerTabs, "LayerDeleteButton").Disabled, "A scene with one layer must disable delete.");
        Assert.True(FindRequired<Button>(singleLayerTabs, "LayerMoveUpButton").Disabled, "A scene with one layer must disable move-up.");
        Assert.True(FindRequired<Button>(singleLayerTabs, "LayerMoveDownButton").Disabled, "A scene with one layer must disable move-down.");

        singleLayerTabs.QueueFree();
        await ProcessFrame();
    }

    private async System.Threading.Tasks.Task VerifyNarrowLayerToolbarLayout()
    {
        const float fixtureWidth = 720f;
        var scene = new AgasSceneResource
        {
            Id = "narrow-scene",
            Name = "Narrow",
            ActiveLayerId = "narrow-001"
        };
        for (int index = 1; index <= 4; index++)
        {
            scene.Layers.Add(new AgasLayerResource
            {
                Id = $"narrow-{index:000}",
                Name = $"Layer {index}",
                Order = index,
                Visible = true
            });
        }

        var narrowTabs = new LayerTabs
        {
            Name = "NarrowLayerTabsFixture",
            Position = new Vector2(0, 900),
            Size = new Vector2(fixtureWidth, 64)
        };
        AddChild(narrowTabs);
        await ProcessFrame();
        narrowTabs.SetScene(scene);
        await ProcessFrame();
        await ProcessFrame();

        Assert.True(Mathf.Abs(narrowTabs.Size.X - fixtureWidth) <= 1f, "Narrow fixture must actually remain at the requested 720px width.");
        Button add = FindRequired<Button>(narrowTabs, "LayerAddButton");
        ScrollContainer tabStrip = FindRequired<ScrollContainer>(narrowTabs, "LayerTabStrip");
        HBoxContainer tabButtons = FindRequired<HBoxContainer>(narrowTabs, "LayerTabButtons");
        HBoxContainer tools = FindRequired<HBoxContainer>(narrowTabs, "LayerCurrentTools");
        Button[] toolButtons =
        {
            FindRequired<Button>(narrowTabs, "LayerRenameButton"),
            FindRequired<Button>(narrowTabs, "LayerMoveUpButton"),
            FindRequired<Button>(narrowTabs, "LayerMoveDownButton"),
            FindRequired<Button>(narrowTabs, "LayerPromptButton"),
            FindRequired<Button>(narrowTabs, "LayerDeleteButton")
        };

        Rect2 fixtureRect = narrowTabs.GetGlobalRect();
        AssertRectContained(fixtureRect, add.GetGlobalRect(), "Narrow layout add button must stay visible.");
        AssertRectContained(fixtureRect, tabStrip.GetGlobalRect(), "Narrow layout tab strip must stay visible.");
        AssertRectContained(fixtureRect, tools.GetGlobalRect(), "Narrow layout tool group must stay visible.");
        AssertHorizontalOrder(add, tabStrip, "Add button and tab strip must not overlap.");
        AssertHorizontalOrder(tabStrip, tools, "Tab strip and current-layer tools must not overlap.");
        Assert.True(tabStrip.Size.X >= FindRequired<PanelContainer>(narrowTabs, "LayerTab-narrow-001").Size.X,
            "The 480px fixture must retain enough tab viewport width for one complete compound tab.");
        Assert.True(tabButtons.Size.X > tabStrip.Size.X + 1f, "Narrow fixture must exercise horizontally overflowing layer tabs.");

        for (int index = 0; index < toolButtons.Length; index++)
        {
            Button tool = toolButtons[index];
            AssertIconControl(tool, $"narrow tool {tool.Name}");
            Assert.True(tool.IsVisibleInTree(), $"Narrow tool {tool.Name} must remain visible in the tree.");
            Assert.True(tool.Size.X >= 48f && tool.Size.X <= 49f && tool.Size.Y >= 48f,
                $"Narrow tool {tool.Name} must keep its stable 48px interaction width.");
            AssertRectContained(fixtureRect, tool.GetGlobalRect(), $"Narrow tool {tool.Name} must remain inside the component bounds.");
            if (index > 0)
            {
                AssertHorizontalOrder(toolButtons[index - 1], tool, $"Narrow tools {toolButtons[index - 1].Name} and {tool.Name} must not overlap.");
            }
        }

        for (int index = 1; index <= 4; index++)
        {
            AssertIconControl(FindRequired<Button>(narrowTabs, $"LayerVisible-narrow-{index:000}"), $"narrow-{index:000} visibility");
            AssertIconControl(FindRequired<Button>(narrowTabs, $"LayerLocked-narrow-{index:000}"), $"narrow-{index:000} lock");
            AssertIconControl(FindRequired<Button>(narrowTabs, $"LayerShared-narrow-{index:000}"), $"narrow-{index:000} share");
        }

        FindLayerButton(narrowTabs, "narrow-004").EmitSignal(BaseButton.SignalName.Pressed);
        await ProcessFrame();
        await ProcessFrame();
        AssertControlVisibleInHorizontalScroll(
            tabStrip,
            FindRequired<PanelContainer>(narrowTabs, "LayerTab-narrow-004"),
            "Selecting the last narrow-layout layer must scroll the complete compound tab into view.");

        narrowTabs.QueueFree();
        await ProcessFrame();
    }

    private static void AssertIconControl(Button button, string label)
    {
        Assert.True(button.Icon != null, $"{label} control must use an icon.");
        Assert.Equal(32, button.Icon!.GetWidth(), $"{label} icon width must remain 32 pixels.");
        Assert.Equal(32, button.Icon.GetHeight(), $"{label} icon height must remain 32 pixels.");
        Assert.True(button.CustomMinimumSize.X == 48 && button.CustomMinimumSize.Y == 48, $"{label} icon control must expose a uniform 48x48 interaction target.");
        Assert.True(button.FocusMode == Control.FocusModeEnum.All, $"{label} icon control must remain keyboard focusable.");
        Assert.True(!string.IsNullOrWhiteSpace(button.TooltipText), $"{label} icon control must expose an accessible tooltip.");
        Assert.True(!string.IsNullOrWhiteSpace(button.AccessibilityName), $"{label} icon control must expose an accessibility name.");
    }

    private static void AssertControlVisibleInHorizontalScroll(ScrollContainer scroll, Control control, string message)
    {
        float scrollLeft = scroll.GlobalPosition.X;
        float scrollRight = scrollLeft + scroll.Size.X;
        float controlLeft = control.GlobalPosition.X;
        float controlRight = controlLeft + control.Size.X;
        Assert.True(controlLeft >= scrollLeft - 1f && controlRight <= scrollRight + 1f, message);
    }

    private static void AssertRectContained(Rect2 outer, Rect2 inner, string message)
    {
        float outerRight = outer.Position.X + outer.Size.X;
        float outerBottom = outer.Position.Y + outer.Size.Y;
        float innerRight = inner.Position.X + inner.Size.X;
        float innerBottom = inner.Position.Y + inner.Size.Y;
        Assert.True(
            inner.Position.X >= outer.Position.X - 1f &&
            inner.Position.Y >= outer.Position.Y - 1f &&
            innerRight <= outerRight + 1f &&
            innerBottom <= outerBottom + 1f,
            message);
    }

    private static void AssertHorizontalOrder(Control left, Control right, string message)
    {
        Assert.True(left.GlobalPosition.X + left.Size.X <= right.GlobalPosition.X + 1f, message);
    }

    private static AgasLayerResource FindLayer(MainWorkspace workspace, string layerId)
    {
        AgasLayerResource? layer = workspace.CurrentScene?.Layers.FirstOrDefault(candidate => candidate.Id == layerId);
        return layer ?? throw new InvalidOperationException($"Required fixture layer {layerId} was not found.");
    }

    private static Button FindLayerButton(Node layerButtons, string layerId)
    {
        return FindRequired<Button>(layerButtons, $"LayerTabSelect-{layerId}");
    }

    private static int LayerButtonCount(Node layerButtons)
    {
        int count = 0;
        CountNamedDescendants(layerButtons, "LayerTabSelect-", ref count);
        return count;
    }

    private static void CountNamedDescendants(Node node, string prefix, ref int count)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child.Name.ToString().StartsWith(prefix, StringComparison.Ordinal))
            {
                count++;
            }
            CountNamedDescendants(child, prefix, ref count);
        }
    }

    private static ulong ProjectFileRootInstanceId(ProjectFilesTree tree)
    {
        return tree.GetRoot()?.GetInstanceId() ?? 0;
    }

    private static string ExpectedPromptText(string layerId)
    {
        return $"{layerId} prompt alpha\n{layerId} prompt beta\n\n{PromptComposer.TaskHeader}\n{PromptComposer.ReferenceEditTask}";
    }

    private static string LayerName(string layerId)
    {
        int parsed = int.Parse(layerId.Split('-').Last());
        return $"Layer {parsed}";
    }

    private static T FindRequired<T>(Node root, string name) where T : Node
    {
        Node? found = FindByName(root, name);
        if (found is T typed)
        {
            return typed;
        }

        throw new InvalidOperationException($"Required node {name} of type {typeof(T).Name} was not found. Actual={found?.GetType().FullName ?? "null"}");
    }

    private static Node? FindByName(Node node, string name)
    {
        if (string.Equals(node.Name.ToString(), name, StringComparison.Ordinal))
        {
            return node;
        }

        foreach (Node child in node.GetChildren())
        {
            Node? found = FindByName(child, name);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private async System.Threading.Tasks.Task ProcessFrame()
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static void CreateProjectFixture(string root)
    {
        AgasResourceRepository repository = new();
        AgasProjectInitializer initializer = new(repository);
        AgasPersistenceResult init = initializer.EnsureInitialized(root);
        if (!init.Success)
        {
            throw new InvalidOperationException(init.ErrorMessage);
        }

        AgasLoadResult<AgasSceneResource> sceneLoad = repository.Load<AgasSceneResource>(AgasPaths.SceneFile(root, "scene-001"));
        if (!sceneLoad.Success || sceneLoad.Resource == null)
        {
            throw new InvalidOperationException(sceneLoad.ErrorMessage ?? "Failed to load fixture scene.");
        }

        AgasSceneResource scene = sceneLoad.Resource;
        scene.Layers.Clear();
        for (int index = 1; index <= LayerCount; index++)
        {
            string layerId = $"layer-{index:000}";
            AgasLayerResource layer = new()
            {
                Id = layerId,
                Name = LayerName(layerId),
                Order = index,
                Visible = true,
                Locked = false
            };
            layer.PromptFragments.Add(new PromptFragmentResource
            {
                Id = $"{layerId}-prompt-a",
                Text = $"{layerId} prompt alpha",
                Enabled = true,
                Order = 0
            });
            layer.PromptFragments.Add(new PromptFragmentResource
            {
                Id = $"{layerId}-prompt-b",
                Text = $"{layerId} prompt beta",
                Enabled = true,
                Order = 1
            });
            scene.Layers.Add(layer);
        }
        scene.ActiveLayerId = "layer-001";

        AgasPersistenceResult save = repository.Save(scene, AgasPaths.SceneFile(root, "scene-001"));
        if (!save.Success)
        {
            throw new InvalidOperationException(save.ErrorMessage);
        }
    }

    private static void CreateFileFixture(string root)
    {
        for (int directoryIndex = 0; directoryIndex < DirectoryCount; directoryIndex++)
        {
            string directory = Path.Combine(root, "library", $"folder-{directoryIndex:000}");
            Directory.CreateDirectory(directory);
            for (int fileIndex = 0; fileIndex < ImageFilesPerDirectory; fileIndex++)
            {
                File.WriteAllText(Path.Combine(directory, $"image-{fileIndex:000}.png"), "png marker");
            }
            File.WriteAllText(Path.Combine(directory, "notes.txt"), "ordinary file ignored by ProjectFilesTree");
        }
    }

    private string NewTempRoot(string name)
    {
        string root = Path.Combine(Path.GetTempPath(), "agas-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        return root;
    }

    private void CleanupTempRoots()
    {
        foreach (string root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception exception)
            {
                GD.PushWarning($"{Prefix}[CLEANUP] Failed to delete {root}: {exception.GetBaseException().Message}");
            }
        }
    }

    private sealed class SignalCounter : IDisposable
    {
        private readonly Node _node;
        private readonly Callable _callable;
        private bool _disposed;

        private SignalCounter(Node node)
        {
            _node = node;
            _callable = Callable.From(() => Count++);
        }

        public int Count { get; private set; }

        public static SignalCounter ConnectChildOrder(Node node)
        {
            SignalCounter counter = new(node);
            node.Connect(Node.SignalName.ChildOrderChanged, counter._callable);
            return counter;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (GodotObject.IsInstanceValid(_node) && _node.IsConnected(Node.SignalName.ChildOrderChanged, _callable))
            {
                _node.Disconnect(Node.SignalName.ChildOrderChanged, _callable);
            }
        }
    }

    private sealed record SwitchSample(
        string BeforeLayerId,
        string TargetLayerId,
        string AfterLayerId,
        double SyncMicroseconds,
        double FrameMicroseconds,
        ulong BeforeRootId,
        ulong AfterRootId,
        bool ProjectFilesTreeRebuilt,
        int ActionBarChildOrderChanges,
        int ActionBarStructuralChurnEstimate,
        int TabStripChildOrderChanges,
        int TabStripStructuralChurnEstimate,
        int PromptPanelChildOrderChanges,
        int PromptPanelStructuralChurnEstimate,
        int CanvasChangedObserved)
    {
        public string ToLogLine(string prefix)
        {
            return $"{prefix}[SAMPLE] before={BeforeLayerId} target={TargetLayerId} after={AfterLayerId} sync_us={SyncMicroseconds:F1} frame_us={FrameMicroseconds:F1} tree_root_before={BeforeRootId} tree_root_after={AfterRootId} tree_rebuilt={ProjectFilesTreeRebuilt} action_bar_child_order_changes={ActionBarChildOrderChanges} action_bar_structural_churn_estimate={ActionBarStructuralChurnEstimate} tab_strip_child_order_changes={TabStripChildOrderChanges} tab_strip_structural_churn_estimate={TabStripStructuralChurnEstimate} prompt_panel_child_order_changes={PromptPanelChildOrderChanges} prompt_panel_structural_churn_estimate={PromptPanelStructuralChurnEstimate} canvas_changed={CanvasChangedObserved}";
        }
    }

    private sealed record PerfStats(double P50Us, double P95Us, double MaxUs)
    {
        public static PerfStats From(double[] values)
        {
            if (values.Length == 0)
            {
                return new PerfStats(0, 0, 0);
            }

            Array.Sort(values);
            return new PerfStats(Percentile(values, 0.50), Percentile(values, 0.95), values[^1]);
        }

        private static double Percentile(double[] sortedValues, double percentile)
        {
            if (sortedValues.Length == 1)
            {
                return sortedValues[0];
            }

            double rawIndex = (sortedValues.Length - 1) * percentile;
            int lower = (int)Math.Floor(rawIndex);
            int upper = (int)Math.Ceiling(rawIndex);
            if (lower == upper)
            {
                return sortedValues[lower];
            }

            double weight = rawIndex - lower;
            return (sortedValues[lower] * (1.0 - weight)) + (sortedValues[upper] * weight);
        }
    }

    private static class Assert
    {
        public static void True(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(message + " Expected=" + expected + " Actual=" + actual);
            }
        }
    }
}
