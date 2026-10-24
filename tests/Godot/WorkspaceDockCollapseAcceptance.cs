using System;
using System.IO;
using System.Reflection;
using AGAS.UI;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using Agas.Features.Canvas;
using Godot;

public partial class WorkspaceDockCollapseAcceptance : Node
{
    private const string Prefix = "[AGAS][WORKSPACE-DOCK-COLLAPSE]";
    private string? _projectRoot;

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
            if (_projectRoot != null)
            {
                try { Directory.Delete(_projectRoot, recursive: true); } catch { }
            }
            GetTree().Quit(exitCode);
        }
    }

    private async System.Threading.Tasks.Task RunAll()
    {
        await VerifyGeometryRegression();
        await VerifySavedBottomHeightFitsCanvas();
        await VerifyProjectSwitchPreservesBottomTab();

        _projectRoot = Path.Combine(Path.GetTempPath(), "agas-workspace-docks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_projectRoot);

        var workspace = new MainWorkspace { CustomMinimumSize = new Vector2(1200, 760) };
        AddChild(workspace);
        await Frames(2);
        workspace.OpenProject(_projectRoot);
        await Frames(4);

        Control canvas = FindRequired<CanvasWorkspace>(workspace, "CanvasWorkspaceHost");
        Control leftBody = FindRequiredType<FilesScenesPanel>(workspace);
        Control rightBody = FindRequiredType<PropertiesPanel>(workspace);
        TabContainer bottomBody = FindRequired<TabContainer>(workspace, "BottomDockTabs");
        Control leftDock = FindRequired<Control>(workspace, "LeftWorkspaceDock");
        Control rightDock = FindRequired<Control>(workspace, "RightWorkspaceDock");
        Control bottomDock = FindRequired<Control>(workspace, "BottomWorkspaceDock");
        Button leftToggle = FindRequired<Button>(workspace, "LeftDockToggleButton");
        Button rightToggle = FindRequired<Button>(workspace, "RightDockToggleButton");
        Button bottomToggle = FindRequired<Button>(workspace, "BottomDockToggleButton");
        Control bottomHeader = FindRequired<Control>(workspace, "BottomWorkspaceDockHeader");
        MenuButton viewMenu = FindRequired<MenuButton>(workspace, "WorkspaceViewMenu");

        Require(leftBody.Visible && rightBody.Visible, "Left and right docks should start expanded.");
        Require(viewMenu.TooltipText == "最小化或恢复工作区面板", "View menu tooltip must use the exact minimize/restore wording.");
        string[] expectedViewItems =
        {
            "最小化/恢复 文件/场景面板",
            "最小化/恢复 属性面板",
            "最小化/恢复 提示词/工具面板"
        };
        Require(viewMenu.GetPopup().ItemCount == expectedViewItems.Length, "View menu must expose exactly the three workspace docks.");
        for (int item = 0; item < viewMenu.GetPopup().ItemCount; item++)
        {
            string menuText = viewMenu.GetPopup().GetItemText(item);
            Require(menuText == expectedViewItems[item], $"View menu item must use the exact minimize/restore wording: {menuText}");
        }
        Require(!bottomBody.Visible && bottomDock.Visible, "Collapsed bottom dock must retain a visible restore bar.");
        Require(bottomHeader.Visible && bottomToggle.GetParent() == bottomHeader,
            "Collapsed bottom dock must keep its restore button in the compact header.");
        Require(bottomToggle.Text == "+" && bottomToggle.TooltipText.Contains("恢复", StringComparison.Ordinal), "Collapsed dock must expose a consistent restore affordance.");

        Press(bottomToggle);
        await Frames(3);
        Require(bottomBody.Visible, "Bottom restore button must restore the dock body.");
        Require(!bottomHeader.Visible && bottomToggle.GetParent() == bottomBody.GetTabBar(),
            "Expanded bottom dock must remove the duplicate header and place the toggle in the tab bar.");
        Require(Math.Abs(bottomToggle.Position.X + bottomToggle.Size.X - bottomBody.GetTabBar().Size.X) <= 4,
            "Expanded bottom toggle must stay at the right edge of the tab bar.");
        Require(bottomToggle.Text == "−" && bottomToggle.TooltipText.Contains("最小化", StringComparison.Ordinal), "Expanded dock must expose a consistent minimize affordance.");
        Require(bottomBody.GetTabCount() == 2
                && bottomBody.GetTabTitle(0) == "提示词"
                && bottomBody.GetTabTitle(1) == "工具",
            "Bottom dock must expose Prompt and Tools tabs.");
        bottomBody.CurrentTab = 1;
        Require(workspace.Layout?.ActiveBottomTab == "Tools", "Tool tab selection must update persisted layout state.");

        // Dragging the prompt dock taller must stick. A previous 260px hard max made
        // the splitter snap back after DragEnded re-applied layout.
        WorkspaceLayoutResource? layout = workspace.Layout;
        Require(layout != null, "Workspace layout must exist after open.");
        Press(bottomToggle); // collapse (captures current height)
        await Frames(2);
        layout!.BottomDockHeight = 420; // taller than the old 260px hard max
        workspace.CustomMinimumSize = new Vector2(1200, 1300);
        await Frames(2);
        Press(bottomToggle); // expand and apply layout offsets
        await Frames(4);
        Require(Math.Abs(bottomDock.Size.Y - 420) <= 1,
            $"Prompt dock must reach the requested 420px when space allows (actual {bottomDock.Size.Y}).");
        Require(Math.Abs(layout.BottomDockHeight - 420) <= 1,
            $"Persisted BottomDockHeight must retain the requested 420px (actual {layout.BottomDockHeight}).");

        Vector2 allExpandedCanvas = canvas.Size;

        Press(leftToggle);
        await Frames(3);
        Require(!leftBody.Visible && leftDock.Visible, "Left dock body should minimize while its restore handle stays visible.");
        Require(leftDock.Size.X <= 48, $"Left restore handle should remain narrow, actual width {leftDock.Size.X}.");
        Require(canvas.Size.X > allExpandedCanvas.X + 100, "Minimizing the left dock must release horizontal space to the canvas.");
        Press(leftToggle);
        await Frames(3);
        Require(leftBody.Visible, "Left restore handle must restore the left dock.");

        Vector2 beforeRightCollapse = canvas.Size;
        Press(rightToggle);
        await Frames(3);
        Require(!rightBody.Visible && rightDock.Visible, "Right dock body should minimize while its restore handle stays visible.");
        Require(rightDock.Size.X <= 48, $"Right restore handle should remain narrow, actual width {rightDock.Size.X}.");
        Require(canvas.Size.X > beforeRightCollapse.X + 100, "Minimizing the right dock must release horizontal space to the canvas.");
        Press(rightToggle);
        await Frames(3);

        Vector2 beforeBottomCollapse = canvas.Size;
        Press(bottomToggle);
        await Frames(3);
        Require(!bottomBody.Visible && bottomDock.Visible, "Bottom dock body should minimize while its restore bar stays visible.");
        Require(bottomDock.Size.Y <= 48, $"Bottom restore bar should remain narrow, actual height {bottomDock.Size.Y}.");
        Require(canvas.Size.Y > beforeBottomCollapse.Y + 100, "Minimizing the bottom dock must release vertical space to the canvas.");

        workspace.CustomMinimumSize = new Vector2(1200, 760);
        await Frames(3);

        Press(leftToggle);
        Press(rightToggle);
        await Frames(3);
        Require(workspace.Layout?.LeftPanelCollapsed == true, "Left collapsed state must be captured in workspace layout state.");
        Require(workspace.Layout?.RightPanelCollapsed == true, "Right collapsed state must be captured in workspace layout state.");
        Require(workspace.Layout?.BottomDockCollapsed == true, "Bottom collapsed state must be captured in workspace layout state.");

        Vector2 collapsedCanvasBeforeResize = canvas.Size;
        workspace.CustomMinimumSize = new Vector2(1450, 900);
        await Frames(4);
        Require(canvas.Size.X > collapsedCanvasBeforeResize.X && canvas.Size.Y > collapsedCanvasBeforeResize.Y, "The canvas should absorb window growth while docks are minimized.");
        Require(leftDock.Size.X <= 64 && rightDock.Size.X <= 64 && bottomDock.Size.Y <= 48,
            $"Minimized restore handles must stay narrow after resize (left={leftDock.Size.X}, right={rightDock.Size.X}, bottom={bottomDock.Size.Y}).");

        Require(workspace.TrySaveForApplicationExit(), "Workspace layout should save for persistence verification.");
        AgasLoadResult<WorkspaceLayoutResource> savedLayout = new AgasResourceRepository()
            .LoadFresh<WorkspaceLayoutResource>(AgasPaths.WorkspaceLayoutFile(_projectRoot));
        Require(savedLayout.Success
                && savedLayout.Resource!.ActiveBottomTab == "Tools"
                && savedLayout.Resource.BottomDockCollapsed,
            "Tools tab and collapsed state must be persisted to disk, not only retained in ResourceLoader cache.");
        workspace.QueueFree();
        await Frames(3);

        var reopened = new MainWorkspace { CustomMinimumSize = new Vector2(1200, 760) };
        AddChild(reopened);
        await Frames(2);
        reopened.OpenProject(_projectRoot);
        await Frames(4);

        Require(!FindRequiredType<FilesScenesPanel>(reopened).Visible, "Left minimized state must survive reopening.");
        Require(!FindRequiredType<PropertiesPanel>(reopened).Visible, "Right minimized state must survive reopening.");
        TabContainer reopenedBottomTabs = FindRequired<TabContainer>(reopened, "BottomDockTabs");
        Require(!reopenedBottomTabs.Visible, "Bottom minimized state must survive reopening.");
        Require(reopenedBottomTabs.CurrentTab == 1 && reopened.Layout?.ActiveBottomTab == "Tools",
            "Tool tab selection must survive reopening even when the dock is minimized.");
        Require(FindRequired<Control>(reopened, "LeftWorkspaceDock").Visible
            && FindRequired<Control>(reopened, "RightWorkspaceDock").Visible
            && FindRequired<Control>(reopened, "BottomWorkspaceDock").Visible,
            "All minimized docks must retain visible restore handles after reopening.");

        Press(FindRequired<Button>(reopened, "LeftDockToggleButton"));
        Press(FindRequired<Button>(reopened, "RightDockToggleButton"));
        Press(FindRequired<Button>(reopened, "BottomDockToggleButton"));
        await Frames(4);
        Require(FindRequiredType<FilesScenesPanel>(reopened).Visible
            && FindRequiredType<PropertiesPanel>(reopened).Visible
            && reopenedBottomTabs.Visible && reopenedBottomTabs.CurrentTab == 1,
            "Every persisted restore handle must restore its dock.");

        reopened.QueueFree();
        await Frames(2);
    }

    private async System.Threading.Tasks.Task VerifyGeometryRegression()
    {
        string root = Path.Combine(Path.GetTempPath(), "agas-workspace-geometry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new AgasResourceRepository();
            Require(new AgasProjectInitializer(repository).EnsureInitialized(root).Success,
                "Geometry regression fixture project must initialize.");
            WorkspaceLayoutResource oversizedLayout = ResourceLoader.Load<WorkspaceLayoutResource>(AgasPaths.WorkspaceLayoutFile(root));
            oversizedLayout.LeftPanelWidth = 280;
            oversizedLayout.RightPanelWidth = 5000;
            oversizedLayout.ActiveBottomTab = "Blueprints";
            Require(repository.Save(oversizedLayout, AgasPaths.WorkspaceLayoutFile(root)).Success,
                "Geometry regression fixture layout must save.");

            var workspace = new MainWorkspace { CustomMinimumSize = new Vector2(1200, 760) };
            AddChild(workspace);
            await Frames(2);
            workspace.OpenProject(root);
            await Frames(4);

            Require(FindRequired<TabContainer>(workspace, "BottomDockTabs").CurrentTab == 0,
                "Removed bottom-tab keys must safely fall back to Prompt.");

            Control rightDock = FindRequired<Control>(workspace, "RightWorkspaceDock");
            AssertRightDockLeavesCanvas(workspace, "initial layout");
            HSplitContainer outerSplit = FindRequired<HSplitContainer>(workspace, "LeftWorkspaceSplitter");
            HSplitContainer centerSplit = FindRequired<HSplitContainer>(workspace, "CanvasPropertiesSplitter");
            float expectedRightWidth = workspace.Size.X - 220 - 320
                - outerSplit.GetThemeConstant("separation")
                - centerSplit.GetThemeConstant("separation");
            Require(Math.Abs(rightDock.Size.X - expectedRightWidth) <= 1,
                $"Loaded 5000px right width must use the available maximum, actual {rightDock.Size.X}, expected {expectedRightWidth}.");
            Require(workspace.Layout?.RightPanelWidth < 5000,
                $"Oversized persisted right width must be clamped to available space, actual {workspace.Layout?.RightPanelWidth}.");

            float stableRightWidth = rightDock.Size.X;
            await Frames(4);
            Require(Math.Abs(rightDock.Size.X - stableRightWidth) <= 1,
                $"Repeated deferred layout passes must be idempotent (before={stableRightWidth}, after={rightDock.Size.X}).");

            workspace.CustomMinimumSize = new Vector2(1450, 760);
            await Frames(4);
            AssertRightDockLeavesCanvas(workspace, "resize");

            workspace.CustomMinimumSize = new Vector2(1200, 760);
            await Frames(4);
            AssertRightDockLeavesCanvas(workspace, "resize restore");
            float persistedRightWidth = (float)workspace.Layout!.RightPanelWidth;
            Require(InvokeSaveProject(workspace), "Clamped geometry fixture must save explicitly.");
            AgasLoadResult<WorkspaceLayoutResource> diskLayout = repository.LoadFresh<WorkspaceLayoutResource>(
                AgasPaths.WorkspaceLayoutFile(root));
            Require(diskLayout.Success && Math.Abs(diskLayout.Resource!.RightPanelWidth - persistedRightWidth) <= 1,
                $"Clamped right dock width must be persisted to disk (expected {persistedRightWidth}, "
                + $"disk {diskLayout.Resource?.RightPanelWidth}, error {diskLayout.ErrorMessage}).");

            workspace.QueueFree();
            await Frames(3);
            var reopened = new MainWorkspace { CustomMinimumSize = new Vector2(1200, 760) };
            AddChild(reopened);
            await Frames(2);
            reopened.OpenProject(root);
            await Frames(4);
            AssertRightDockLeavesCanvas(reopened, "save and reopen");
            Require(Math.Abs(reopened.Layout!.RightPanelWidth - persistedRightWidth) <= 1,
                "Clamped right dock width must survive save and reopen.");
            reopened.QueueFree();
            await Frames(2);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private async System.Threading.Tasks.Task VerifyProjectSwitchPreservesBottomTab()
    {
        string firstRoot = Path.Combine(Path.GetTempPath(), "agas-tab-first-" + Guid.NewGuid().ToString("N"));
        string secondRoot = Path.Combine(Path.GetTempPath(), "agas-tab-second-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        try
        {
            var repository = new AgasResourceRepository();
            var initializer = new AgasProjectInitializer(repository);
            Require(initializer.EnsureInitialized(firstRoot).Success && initializer.EnsureInitialized(secondRoot).Success,
                "Project-switch fixtures must initialize.");
            string secondLayoutPath = AgasPaths.WorkspaceLayoutFile(secondRoot);
            WorkspaceLayoutResource secondLayout = repository.LoadFresh<WorkspaceLayoutResource>(secondLayoutPath).Resource
                ?? throw new InvalidOperationException("Second project layout is missing.");
            secondLayout.ActiveBottomTab = "Tools";
            secondLayout.BottomDockCollapsed = true;
            Require(repository.Save(secondLayout, secondLayoutPath).Success,
                "Second project tool-tab layout must save.");

            var workspace = new MainWorkspace { CustomMinimumSize = new Vector2(1200, 760) };
            AddChild(workspace);
            await Frames(2);
            workspace.OpenProject(firstRoot);
            await Frames(4);
            Press(FindRequired<Button>(workspace, "BottomDockToggleButton"));
            await Frames(3);
            Require(!workspace.Layout!.BottomDockCollapsed
                    && FindRequired<TabContainer>(workspace, "BottomDockTabs").CurrentTab == 0,
                "First project must have an expanded Prompt dock before switching.");

            workspace.OpenProject(secondRoot);
            await Frames(4);
            TabContainer tabs = FindRequired<TabContainer>(workspace, "BottomDockTabs");
            Require(workspace.Layout?.ActiveBottomTab == "Tools"
                    && workspace.Layout.BottomDockCollapsed
                    && tabs.CurrentTab == 1,
                "Applying a second project must not replace its saved Tools tab with the previous project's Prompt tab.");
            workspace.QueueFree();
            await Frames(2);
        }
        finally
        {
            try { Directory.Delete(firstRoot, recursive: true); } catch { }
            try { Directory.Delete(secondRoot, recursive: true); } catch { }
        }
    }

    private async System.Threading.Tasks.Task VerifySavedBottomHeightFitsCanvas()
    {
        foreach (int preferredHeight in new[] { 617, 5000 })
        {
            string root = Path.Combine(Path.GetTempPath(), "agas-bottom-height-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var repository = new AgasResourceRepository();
                Require(new AgasProjectInitializer(repository).EnsureInitialized(root).Success,
                    "Bottom-height fixture project must initialize.");
                string layoutPath = AgasPaths.WorkspaceLayoutFile(root);
                WorkspaceLayoutResource saved = repository.LoadFresh<WorkspaceLayoutResource>(layoutPath).Resource
                    ?? throw new InvalidOperationException("Bottom-height fixture layout is missing.");
                saved.BottomDockHeight = preferredHeight;
                saved.BottomDockCollapsed = false;
                Require(repository.Save(saved, layoutPath).Success,
                    "Bottom-height fixture layout must save.");

                var workspace = new MainWorkspace { CustomMinimumSize = new Vector2(1600, 900) };
                AddChild(workspace);
                await Frames(2);
                workspace.OpenProject(root);
                await Frames(5);
                Control canvas = FindRequired<CanvasWorkspace>(workspace, "CanvasWorkspaceHost");
                Control bottomDock = FindRequired<Control>(workspace, "BottomWorkspaceDock");
                Require(canvas.Size.Y >= 260,
                    $"Saved {preferredHeight}px dock must leave a usable canvas at 900px (canvas {canvas.Size.Y}, dock {bottomDock.Size.Y}).");
                Require(bottomDock.Size.Y < preferredHeight,
                    $"Saved {preferredHeight}px dock must be clipped in a 900px window (actual {bottomDock.Size.Y}).");
                Require(Math.Abs(workspace.Layout!.BottomDockHeight - preferredHeight) <= 1,
                    "Viewport clipping must not replace the saved dock-height preference in memory.");

                workspace.CustomMinimumSize = new Vector2(1600, 1300);
                await Frames(5);
                Require(canvas.Size.Y >= 260,
                    "The canvas must remain usable after growing the window.");
                if (preferredHeight == 617)
                {
                    Require(Math.Abs(bottomDock.Size.Y - preferredHeight) <= 1,
                        $"A larger window must restore the preferred dock height (actual {bottomDock.Size.Y}).");
                }
                Require(Math.Abs(workspace.Layout.BottomDockHeight - preferredHeight) <= 1,
                    "Resizing must not overwrite the saved dock-height preference.");

                workspace.CustomMinimumSize = new Vector2(1600, 900);
                await Frames(5);
                Require(canvas.Size.Y >= 260 && Math.Abs(workspace.Layout.BottomDockHeight - preferredHeight) <= 1,
                    "Shrinking the window again must reapply the temporary dock clamp.");
                Require(InvokeSaveProject(workspace), "Bottom-height fixture must save explicitly.");
                AgasLoadResult<WorkspaceLayoutResource> disk = repository.LoadFresh<WorkspaceLayoutResource>(layoutPath);
                Require(disk.Success && Math.Abs(disk.Resource!.BottomDockHeight - preferredHeight) <= 1,
                    "Saving a clipped window must retain the user's preferred dock height on disk.");
                workspace.QueueFree();
                await Frames(3);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }

    private async System.Threading.Tasks.Task Frames(int count)
    {
        for (int index = 0; index < count; index++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static void Press(Button button) => button.EmitSignal(Button.SignalName.Pressed);

    private static bool InvokeSaveProject(MainWorkspace workspace) =>
        typeof(MainWorkspace).GetMethod("SaveProject", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(workspace, null) is true;

    private static void AssertRightDockLeavesCanvas(MainWorkspace workspace, string phase)
    {
        Control canvas = FindRequired<CanvasWorkspace>(workspace, "CanvasWorkspaceHost");
        Control rightDock = FindRequired<Control>(workspace, "RightWorkspaceDock");
        HSplitContainer split = FindRequired<HSplitContainer>(workspace, "CanvasPropertiesSplitter");
        float maxRight = split.Size.X - split.GetThemeConstant("separation") - 320;
        Require(canvas.Size.X >= 320,
            $"Canvas center must retain at least 320px after {phase}, actual {canvas.Size.X}.");
        Require(rightDock.Size.X <= maxRight + 1,
            $"Right dock must leave 320px for the center after {phase}, actual {rightDock.Size.X}, maximum {maxRight}.");
        Require(workspace.Layout != null && Math.Abs(workspace.Layout.RightPanelWidth - rightDock.Size.X) <= 1,
            $"Right dock and saved layout must agree after {phase}.");
    }

    private static T FindRequired<T>(Node root, string name) where T : Node =>
        root.FindChild(name, recursive: true, owned: false) as T
        ?? throw new InvalidOperationException($"Required node is missing: {name}");

    private static T FindRequiredType<T>(Node root) where T : Node
    {
        if (root is T match)
        {
            return match;
        }
        foreach (Node child in root.GetChildren())
        {
            try
            {
                return FindRequiredType<T>(child);
            }
            catch (InvalidOperationException)
            {
            }
        }
        throw new InvalidOperationException($"Required node type is missing: {typeof(T).Name}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
