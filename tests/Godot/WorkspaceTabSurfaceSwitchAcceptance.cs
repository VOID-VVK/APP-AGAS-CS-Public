#nullable enable

using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using AGAS.UI;
using AppAgasCs.Core.Persistence;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class WorkspaceTabSurfaceSwitchAcceptance : Node
{
    private const string Prefix = "[AGAS][WORKSPACE-TOOL-TAB]";
    private string? _root;

    public override async void _Ready()
    {
        int exitCode = 0;
        try
        {
            await RunAsync();
            GD.Print($"{Prefix}[PASS]");
        }
        catch (Exception error)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {error}");
        }
        finally
        {
            if (_root != null) { try { Directory.Delete(_root, true); } catch { } }
            GetTree().Quit(exitCode);
        }
    }

    private async Task RunAsync()
    {
        GetTree().Root.Size = new Vector2I(1280, 760);
        _root = Path.Combine(Path.GetTempPath(), "agas-workspace-tool-tab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Require(new AgasProjectInitializer().EnsureInitialized(_root).Success, "Project initialization failed.");

        var workspace = CreateWorkspace();
        workspace.OpenProject(_root);
        await Frames(5);
        TabContainer bottom = FindRequired<TabContainer>(workspace, "BottomDockTabs");
        TabContainer left = FindRequired<TabContainer>(workspace, "FilesScenesTabs");
        Require(bottom.GetTabCount() == 2 && left.GetTabCount() == 2,
            "Expected prompt/tools and files/scenes tabs only.");
        Require(bottom.GetTabTitle(0) == "提示词" && bottom.GetTabTitle(1) == "工具",
            "Bottom tabs have unexpected labels.");
        Require(workspace.FindChild("BlueprintDocumentWorkspace", true, false) == null
                && workspace.FindChild("CustomModuleEditorPanel", true, false) == null,
            "Retired center editor is still mounted.");

        Button bottomToggle = FindRequired<Button>(workspace, "BottomDockToggleButton");
        Require(workspace.Layout?.BottomDockCollapsed == true && !bottom.Visible,
            "A new project's bottom dock should start collapsed.");
        bottomToggle.EmitSignal(Button.SignalName.Pressed);
        await Frames(3);
        Require(bottom.Visible && workspace.Layout?.BottomDockCollapsed == false,
            "The restore button must expand the bottom dock before selecting a tab.");
        Require(!FindRequired<Control>(workspace, "BottomWorkspaceDockHeader").Visible
                && bottomToggle.GetParent() == bottom.GetTabBar(),
            "Expanded prompt/tools tabs must share a row with the dock toggle.");
        bottom.CurrentTab = 1;
        await Frames(2);
        Require(workspace.Layout?.ActiveBottomTab == "Tools"
                && FindRequired<Control>(workspace, "CanvasDocumentHost").Visible
                && FindRequired<ToolboxPanel>(workspace, "ToolboxPanel").Visible,
            "Switching to tools hid the canvas or did not update layout state.");
        bottomToggle.EmitSignal(Button.SignalName.Pressed);
        await Frames(2);
        Require(workspace.Layout?.BottomDockCollapsed == true
                && workspace.Layout.ActiveBottomTab == "Tools"
                && bottom.CurrentTab == 1 && !bottom.Visible,
            "Collapsing the dock must retain the selected tools tab.");
        Require(bottomToggle.GetParent() == FindRequired<Control>(workspace, "BottomWorkspaceDockHeader"),
            "Collapsed bottom dock must restore the toggle to its compact header.");
        Require(InvokeSaveProject(workspace), "Could not save the tools tab layout.");
        workspace.QueueFree();
        await Frames(3);

        var reopened = CreateWorkspace();
        reopened.OpenProject(_root);
        await Frames(5);
        TabContainer restored = FindRequired<TabContainer>(reopened, "BottomDockTabs");
        Require(restored.CurrentTab == 1 && reopened.Layout?.ActiveBottomTab == "Tools"
                && reopened.Layout.BottomDockCollapsed && !restored.Visible
                && FindRequired<Control>(reopened, "CanvasDocumentHost").Visible,
            "Saved tools tab did not restore with the canvas.");
        FindRequired<Button>(reopened, "BottomDockToggleButton").EmitSignal(Button.SignalName.Pressed);
        await Frames(3);
        Require(restored.Visible && restored.CurrentTab == 1
                && FindRequired<ToolboxPanel>(reopened, "ToolboxPanel").Visible,
            "Restoring the dock must show the persisted tools tab.");
        restored.CurrentTab = 0;
        await Frames(2);
        Require(reopened.Layout?.ActiveBottomTab == "Prompt"
                && FindRequired<Control>(reopened, "CanvasDocumentHost").Visible,
            "Switching back to prompts did not preserve the canvas.");
        reopened.QueueFree();
        await Frames(2);
    }

    private MainWorkspace CreateWorkspace()
    {
        var workspace = new MainWorkspace { CustomMinimumSize = new Vector2(1280, 760) };
        AddChild(workspace);
        return workspace;
    }

    private async Task Frames(int count)
    {
        for (int index = 0; index < count; index++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static bool InvokeSaveProject(MainWorkspace workspace) =>
        typeof(MainWorkspace).GetMethod("SaveProject", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(workspace, null) is true;

    private static T FindRequired<T>(Node root, string name) where T : Node =>
        root.FindChild(name, true, false) as T
        ?? throw new InvalidOperationException("Missing node: " + name);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
