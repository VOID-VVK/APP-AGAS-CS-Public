#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AGAS.UI;
using AppAgasCs.Features.Tools.Model;
using AppAgasCs.Features.Tools.Storage;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class ToolboxPanelAcceptance : Node
{
    private const string Prefix = "[AGAS][TOOLBOX-PANEL]";

    public override async void _Ready()
    {
        string home = Path.Combine(Path.GetTempPath(), "agas-toolbox-panel-" + Guid.NewGuid().ToString("N"));
        string previousLocale = TranslationServer.GetLocale();
        int exitCode = 0;
        try
        {
            TranslationServer.SetLocale("zh_CN");
            Directory.CreateDirectory(home);
            await VerifyAsync(home);
            GD.Print($"{Prefix}[PASS]");
        }
        catch (Exception error)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {error}");
        }
        finally
        {
            TranslationServer.SetLocale(previousLocale);
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
            GetTree().Quit(exitCode);
        }
    }

    private async Task VerifyAsync(string home)
    {
        var catalog = new ToolCatalog(home);
        var panel = new ToolboxPanel(catalog);
        var tabs = new TabContainer { Size = new Vector2(1200, 320) };
        AddChild(tabs);
        tabs.AddChild(panel);
        await Frames(3);

        var split = Find<HSplitContainer>(panel, "ToolboxSplit");
        var directory = Find<Control>(panel, "ToolboxDirectory");
        var editor = Find<Control>(panel, "ToolboxEditor");
        Require(Math.Abs(split.Size.X - panel.Size.X) <= 2 && Math.Abs(split.Size.Y - panel.Size.Y) <= 2,
            $"The toolbox split must fill its tab (panel={panel.Size}, split={split.Size}).");
        Require(editor.GlobalPosition.X > directory.GlobalPosition.X,
            "The tool list and editor must be side by side.");
        Require(directory.Size.X >= 280 && directory.Size.X <= 320,
            $"The default tool list should stay near 300px (width={directory.Size.X}).");
        tabs.Size = new Vector2(1600, 360);
        await Frames(3);
        Require(split.Size.X > 1500 && Math.Abs(split.Size.X - panel.Size.X) <= 2,
            $"The toolbox split did not expand with its tab (panel={panel.Size}, split={split.Size}).");
        Require(directory.Size.X >= 280 && directory.Size.X <= 320,
            $"Resizing the tab must leave the tool list near 300px (width={directory.Size.X}).");
        float directoryWidth = directory.Size.X;
        split.EmitSignal(SplitContainer.SignalName.DragStarted);
        split.SplitOffsets = new[] { split.SplitOffsets[0] + 80 };
        await Frames(2);
        split.EmitSignal(SplitContainer.SignalName.DragEnded);
        await Frames(2);
        Require(directory.Size.X > directoryWidth + 20,
            $"Dragging the toolbox divider did not widen the list (before={directoryWidth}, after={directory.Size.X}).");
        float draggedWidth = directory.Size.X;
        tabs.Size = new Vector2(1400, 360);
        await Frames(3);
        Require(Math.Abs(directory.Size.X - draggedWidth) <= 2,
            $"The toolbox did not retain its dragged list width (dragged={draggedWidth}, resized={directory.Size.X}).");

        var items = Find<ItemList>(panel, "ToolboxItems");
        var chains = Find<ItemList>(panel, "ToolboxChainItems");
        var categoryTabs = Find<TabContainer>(panel, "ToolboxCategoryTabs");
        var createDialog = Find<AcceptDialog>(panel, "ToolboxCreateDialog");
        var createName = Find<LineEdit>(panel, "ToolboxCreateName");
        var code = Find<CodeEdit>(panel, "ToolboxCodeEditor");
        var editorName = Find<LineEdit>(panel, "ToolboxItemName");
        var save = Find<Button>(panel, "ToolboxSave");
        Require(categoryTabs.GetTabCount() == 2
                && categoryTabs.GetTabTitle(0) == "工具"
                && categoryTabs.GetTabTitle(1) == "工具链",
            "Tool management must expose separate tools and chains tabs.");
        Require(items.ItemCount == 8, "The six built-in and two editable Tile47 tools were not listed.");
        Require(chains.ItemCount == 0 && categoryTabs.CurrentTab == 0,
            "The initial tools tab must not contain chains.");
        Require(panel.FindChild("ToolboxRun", true, false) == null,
            "The management panel must not expose a run command.");
        categoryTabs.CurrentTab = 1;
        await Frame();
        Require(panel.SelectedItemId == null && !code.Visible && save.Disabled
                && Find<Button>(panel, "ToolboxCreateChain").Visible
                && !Find<Button>(panel, "ToolboxCreateTool").Visible,
            "An empty chains tab must clear the tool editor and show chain creation.");
        Require(panel.SelectItem("resize-256"), "The built-in resize tool was not listed.");
        Require(categoryTabs.CurrentTab == 0,
            "Selecting a tool must activate its tools tab.");
        Require(code.Visible && !code.Editable
                && code.Text.Contains("256×256", StringComparison.Ordinal)
                && code.Text.Contains("没有可编辑的 GDScript 源码", StringComparison.Ordinal)
                && save.Disabled,
            "A built-in tool should show a read-only implementation summary, not an empty editor.");
        Require(panel.SelectItem("tile47-standard-reverse"), "The Tile47 reverse tool was not listed.");
        Require(code.Visible && code.Editable && !save.Disabled
                && code.Text.Contains("func process(image", StringComparison.Ordinal),
            "The Tile47 reverse preset must be an editable single-image tool.");
        code.Text += "\n# Tile47 editing accepted\n";
        save.EmitSignal(BaseButton.SignalName.Pressed);
        await Frame();
        Require(catalog.LoadTool("tile47-standard-reverse").Source!.Contains("# Tile47 editing accepted", StringComparison.Ordinal),
            "Editing the Tile47 preset did not persist.");

        Create(panel, "ToolboxCreateTool", createDialog, createName, "第一步");
        await Frame();
        string firstId = panel.SelectedItemId ?? throw new Exception("First tool not selected.");
        Require(code.Visible && code.Editable, "A custom tool should open its editable code pane.");
        code.Text += "\n# changed\n";
        save.EmitSignal(BaseButton.SignalName.Pressed);
        await Frame();
        Require(catalog.LoadTool(firstId).Source!.Contains("# changed", StringComparison.Ordinal),
            "Tool code did not persist.");

        Create(panel, "ToolboxCreateTool", createDialog, createName, "第二步");
        await Frame();
        string secondId = panel.SelectedItemId ?? throw new Exception("Second tool not selected.");
        Require(firstId != secondId, "Tool IDs collided.");
        editorName.Text = "第二步（已改名）";
        save.EmitSignal(BaseButton.SignalName.Pressed);
        await Frame();
        Require(catalog.LoadTool(secondId).Name == "第二步（已改名）",
            "Renaming a custom tool did not persist.");

        Create(panel, "ToolboxCreateChain", createDialog, createName, "组合工具");
        await Frame();
        string draftChainId = panel.SelectedItemId ?? throw new Exception("Draft chain not selected.");
        Require(categoryTabs.CurrentTab == 1 && !code.Visible,
            "Creating a chain must activate the chain editor in its own tab.");
        save.EmitSignal(BaseButton.SignalName.Pressed);
        Require(catalog.ListChains().Count == 0 && panel.SelectedItemId == draftChainId,
            "An empty chain must not be persisted.");
        var available = Find<OptionButton>(panel, "ToolboxAvailableTools");
        var addStep = Find<Button>(panel, "ToolboxAddStep");
        available.Select(FindOption(available, firstId));
        addStep.EmitSignal(BaseButton.SignalName.Pressed);
        save.EmitSignal(BaseButton.SignalName.Pressed);
        await Frame();
        Require(catalog.LoadChain(draftChainId).ToolIds.SequenceEqual([firstId]),
            "A single-step chain must be saved without requiring an unrelated second tool.");
        Require(items.ItemCount == 10 && chains.ItemCount == 1
                && chains.GetItemMetadata(0).AsString() == draftChainId,
            "A saved chain must appear only in the chains list.");
        categoryTabs.CurrentTab = 0;
        await Frame();
        Require(panel.SelectedItemId == secondId && code.Visible && code.Editable,
            "Switching back to tools must restore the selected tool editor.");
        categoryTabs.CurrentTab = 1;
        await Frame();
        Require(panel.SelectedItemId == draftChainId && !code.Visible,
            "Switching to chains must restore the selected chain editor.");
        available.Select(FindOption(available, secondId));
        addStep.EmitSignal(BaseButton.SignalName.Pressed);
        save.EmitSignal(BaseButton.SignalName.Pressed);
        await Frame();
        string chainId = panel.SelectedItemId ?? throw new Exception("Chain not selected.");
        ToolChainDefinition chain = catalog.LoadChain(chainId);
        Require(chain.ToolIds.SequenceEqual([firstId, secondId]),
            "The saved chain did not retain ordered tools.");

        var steps = Find<ItemList>(panel, "ToolboxChainSteps");
        steps.Select(1);
        steps.EmitSignal(ItemList.SignalName.ItemSelected, 1L);
        Find<Button>(panel, "ToolboxMoveStepUp").EmitSignal(BaseButton.SignalName.Pressed);
        save.EmitSignal(BaseButton.SignalName.Pressed);
        await Frame();
        chain = catalog.LoadChain(chainId);
        Require(chain.ToolIds.SequenceEqual([secondId, firstId]),
            "Moving a chain step did not persist its new order.");

        Find<Button>(panel, "ToolboxDelete").EmitSignal(BaseButton.SignalName.Pressed);
        Find<ConfirmationDialog>(panel, "ToolboxDeleteDialog")
            .EmitSignal(AcceptDialog.SignalName.Confirmed);
        await Frame();
        Require(catalog.ListChains().All(item => item.Id != chainId), "Deleting a chain did not remove it.");
        Require(categoryTabs.CurrentTab == 1 && chains.ItemCount == 0
                && panel.SelectedItemId == null && !code.Visible && save.Disabled,
            "Deleting the last chain must leave its tab empty without selecting a tool.");

        Require(panel.SelectItem(firstId), "The custom tool disappeared after deleting its chain.");
        Find<Button>(panel, "ToolboxDelete").EmitSignal(BaseButton.SignalName.Pressed);
        Find<ConfirmationDialog>(panel, "ToolboxDeleteDialog")
            .EmitSignal(AcceptDialog.SignalName.Confirmed);
        await Frame();
        Require(catalog.ListTools().All(item => item.Id != firstId),
            "Deleting a custom tool did not remove it.");
        Require(categoryTabs.CurrentTab == 0 && chains.ItemCount == 0 && items.ItemCount == 9,
            "Deleting a tool must keep the tool and chain lists separate.");
    }

    private static void Create(
        ToolboxPanel panel, string buttonName, AcceptDialog dialog, LineEdit name, string value)
    {
        Find<Button>(panel, buttonName).EmitSignal(BaseButton.SignalName.Pressed);
        name.Text = value;
        dialog.Hide();
        dialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
    }

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private async Task Frames(int count)
    {
        for (int index = 0; index < count; index++) await Frame();
    }

    private static int FindOption(OptionButton options, string id)
    {
        for (int index = 0; index < options.ItemCount; index++)
        {
            if (options.GetItemMetadata(index).AsString() == id) return index;
        }
        throw new Exception($"Missing tool option: {id}");
    }

    private static T Find<T>(Node root, string name) where T : Node =>
        root.FindChild(name, recursive: true, owned: false) as T
        ?? throw new Exception($"Missing node: {name}");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
