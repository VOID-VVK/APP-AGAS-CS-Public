using System;
using System.IO;
using System.Threading.Tasks;
using AGAS.UI;
using AGAS.UI.Dialogs;
using AppAgasCs.Features.Ai.Settings;
using Godot;

public partial class UiAppearanceSettingsAcceptance : Node
{
    private const string Prefix = "[AGAS][UI-APPEARANCE]";
    private string? _home;
    private string? _previousHome;

    public override async void _Ready()
    {
        int exitCode = 0;
        try
        {
            await RunAllAsync();
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
            if (_home != null)
            {
                try { Directory.Delete(_home, recursive: true); } catch { }
            }
            GetTree().Quit(exitCode);
        }
    }

    private async Task RunAllAsync()
    {
        _home = Path.Combine(Path.GetTempPath(), "agas-ui-appearance", Guid.NewGuid().ToString("N"));
        _previousHome = System.Environment.GetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable);
        System.Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, _home);

        VerifyActiveTheme();

        var firstMain = new Main();
        AddChild(firstMain);
        await Frames(3);
        Button firstProjectButton = RequireNode<Button>(firstMain, "NewProjectButton");
        Require(firstProjectButton.GetThemeFontSize("font_size") == AgasIdeTheme.ScaleFontSize(35, 12),
            "Primary buttons must use the control tier below the 35px title/body baseline.");

        SettingsDialog firstDialog = await OpenSettings(firstMain);
        SpinBox firstFontSize = RequireNode<SpinBox>(firstDialog, "UiFontSize");
        Require(firstFontSize.MinValue == 11 && !firstFontSize.AllowLesser,
            "Font size editor must allow 11px but reject smaller values.");
        Require(firstFontSize.AllowGreater && firstFontSize.MaxValue > 24, "Font size editor must not impose the old 24px maximum.");
        firstFontSize.Value = 11;
        await Frames(2);
        Require(firstProjectButton.GetThemeFontSize("font_size") == AgasIdeTheme.ScaleFontSize(11, 12),
            "11px must apply immediately through the control font tier.");
        Require(new AgasUiAppearanceSettingsStore(_home).Load().FontSize == 11, "11px must persist in ui-appearance.json.");

        firstMain.QueueFree();
        await Frames(3);

        var secondMain = new Main();
        AddChild(secondMain);
        await Frames(3);
        Button secondProjectButton = RequireNode<Button>(secondMain, "NewProjectButton");
        Require(secondProjectButton.GetThemeFontSize("font_size") == AgasIdeTheme.ScaleFontSize(11, 12),
            "A recreated Main must load the saved minimum 11px setting into the control tier.");

        SettingsDialog secondDialog = await OpenSettings(secondMain);
        SpinBox secondFontSize = RequireNode<SpinBox>(secondDialog, "UiFontSize");
        secondFontSize.Value = 48;
        await Frames(2);
        Require(secondProjectButton.GetThemeFontSize("font_size") == AgasIdeTheme.ScaleFontSize(48, 12),
            "48px must apply immediately through the control font tier without an artificial maximum.");
        Require(new AgasUiAppearanceSettingsStore(_home).Load().FontSize == 48, "48px must persist in ui-appearance.json.");

        RequireNode<Button>(secondDialog, "RestoreDefaultAppearance").EmitSignal(Button.SignalName.Pressed);
        await Frames(2);
        Require(secondProjectButton.GetThemeFontSize("font_size") == AgasIdeTheme.ScaleFontSize(35, 12),
            "Restore default must immediately reset real main-screen controls to their tiered size.");
        Require(new AgasUiAppearanceSettingsStore(_home).Load().FontSize == 35, "Restore default must persist 35px.");

        secondMain.QueueFree();
        await Frames(2);
    }

    private static void VerifyActiveTheme()
    {
        Theme normalTheme = AgasIdeTheme.Create(AgasUiAppearanceSettings.DefaultFontSize);
        Require(normalTheme.GetFontSize("font_size", "Label") == 35,
            "Labels must retain the primary 35px title/body tier.");
        Require(normalTheme.GetFontSize("font_size", "Button") == AgasIdeTheme.ScaleFontSize(35, 12),
            "Buttons must use the smaller control tier.");
        Require(normalTheme.GetFontSize("font_size", "Tree") == AgasIdeTheme.ScaleFontSize(35, 11),
            "Trees must use the compact information tier.");
        Require(!normalTheme.HasStylebox("panel", "GraphEdit"),
            "Removed graph styles must not be registered by the workspace theme.");
    }

    private async Task<SettingsDialog> OpenSettings(Main main)
    {
        PopupMenu menu = RequireNode<PopupMenu>(main, "设置");
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 2L);
        await Frames(2);
        SettingsDialog dialog = RequireNodeOfType<SettingsDialog>(main);
        ItemList categories = RequireNode<ItemList>(dialog, "SettingsCategories");
        categories.Select(1);
        categories.EmitSignal(ItemList.SignalName.ItemSelected, 1L);
        return dialog;
    }

    private async Task Frames(int count)
    {
        for (int index = 0; index < count; index++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static T RequireNode<T>(Node root, string name) where T : Node
    {
        return root.FindChild(name, recursive: true, owned: false) as T
            ?? throw new InvalidOperationException($"Required node is missing: {name}");
    }

    private static T RequireNodeOfType<T>(Node root) where T : Node
    {
        if (root is T match)
        {
            return match;
        }
        foreach (Node child in root.GetChildren())
        {
            try
            {
                return RequireNodeOfType<T>(child);
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
