using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AGAS.UI;
using AGAS.UI.Dialogs;
using Agas.Features.Canvas;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Features.Tools.Storage;
using AppAgasCs.UI.Shared;
using AppAgasCs.UI.Theming;
using Godot;

public partial class PopupMenuFontLayoutAcceptance : Node
{
    private const string Prefix = "[AGAS][POPUP-FONT-LAYOUT]";
    private readonly List<string> _failures = new();
    private string _artifacts = string.Empty;

    public override async void _Ready()
    {
        string previousLocale = TranslationServer.GetLocale();
        int exitCode = 0;
        try
        {
            string home = System.Environment.GetEnvironmentVariable("AGAS_HOME") ?? string.Empty;
            Require(!string.IsNullOrEmpty(home) && Path.GetFullPath(home).Contains("agas-godot-acceptance."),
                "Run only through the disposable acceptance runner; real user settings are prohibited.");
            _artifacts = System.Environment.GetEnvironmentVariable("AGAS_POPUP_ACCEPTANCE_ARTIFACTS")
                ?? Path.Combine(home, "popup-font-layout-evidence");
            Directory.CreateDirectory(_artifacts);
            bool embedded = System.Environment.GetEnvironmentVariable("AGAS_POPUP_ACCEPTANCE_EMBEDDED") != "0";
            GetWindow().GuiEmbedSubwindows = embedded;
            GetWindow().Size = new Vector2I(1920, 1080);
            GD.Print($"{Prefix} display={DisplayServer.GetName()} embedded={embedded} home={home}");
            await Frames(3);
            foreach (string locale in new[] { "en", "zh_CN" })
            {
                var store = new AgasUiAppearanceSettingsStore(home);
                store.Save(AgasUiAppearanceSettings.Default with { UiLocale = locale });
                AppLocale.Apply(locale);
                var main = new Main(new RecentProjectsStore(Path.Combine(home, "recent-projects.tres")),
                    disableMcpRuntimeForTesting: true);
                AddChild(main);
                await Frames(4);
                RequiredType<MenuBar>(main).PreferGlobalMenu = false;
                Require(main.McpDisposedForTesting, "MCP runtime must stay disabled.");
                PopupMenu settingsMenu = Required<PopupMenu>(main, "设置");
                settingsMenu.EmitSignal(PopupMenu.SignalName.IdPressed, 2L);
                await Frames(5);
                SettingsDialog dialog = RequiredType<SettingsDialog>(main);
                ItemList categories = Required<ItemList>(dialog, "SettingsCategories");
                categories.Select(1);
                categories.EmitSignal(ItemList.SignalName.ItemSelected, 1L);
                if (DisplayServer.GetName() == "headless") dialog.Size = new Vector2I(1600, 1000);
                OptionButton language = Required<OptionButton>(dialog, "UiLanguage");
                SpinBox fontSize = Required<SpinBox>(dialog, "UiFontSize");
                foreach (int size in new[] { 11, 35, 48 })
                {
                    fontSize.Value = size;
                    await Frames(5);
                    string label = $"{locale}-{size}";
                    Check(store.Load().FontSize == size, $"{label}: font changes must persist in isolated settings.");
                    Check(language.Size.Y >= language.GetCombinedMinimumSize().Y - 1,
                        $"{label}: OptionButton itself must not clip.");
                    language.ShowPopup();
                    await Frames(5);
                    PopupMenu popup = language.GetPopup();
                    Check(popup.ItemCount == 2 && popup.GetItemText(0) == "English"
                        && popup.GetItemText(1) == "简体中文", $"{label}: both language choices must exist.");
                    VerifyPopup(popup, size, $"language-{label}", embedded);
                    await Capture(dialog, $"appearance-{label}");
                    await Capture(popup, $"language-{label}");
                    await VerifyKeyboardSelection(language, store, label);
                    AppLocale.Apply(locale);
                    language.Select(locale == "en" ? 0 : 1);
                    language.EmitSignal(OptionButton.SignalName.ItemSelected, (long)language.Selected);
                    dialog.Hide();
                    settingsMenu.Popup(new Rect2I(100, 100, 0, 0));
                    await Frames(4);
                    VerifyPopup(settingsMenu, size, $"settings-menu-{label}", embedded);
                    await Capture(settingsMenu, $"settings-menu-{label}");
                    settingsMenu.Hide();
                    await Frames(3);
                    dialog.Open(main);
                    categories.Select(1);
                    categories.EmitSignal(ItemList.SignalName.ItemSelected, 1L);
                    if (DisplayServer.GetName() == "headless") dialog.Size = new Vector2I(1600, 1000);
                    await Frames(4);
                }
                Required<Button>(dialog, "RestoreDefaultAppearance").EmitSignal(Button.SignalName.Pressed);
                await Frames(4);
                Check(store.Load().FontSize == 35, $"{locale}: restore default must persist 35px.");
                language.ShowPopup();
                await Frames(4);
                VerifyPopup(language.GetPopup(), 35, $"restore-{locale}-35", embedded);
                await Capture(language.GetPopup(), $"restore-{locale}-35");
                language.GetPopup().Hide();
                dialog.Hide();
                await VerifyWorkspaceMenus(locale);
                main.QueueFree();
                await Frames(5);
            }
            Require(_failures.Count == 0, string.Join(System.Environment.NewLine, _failures));
            GD.Print($"{Prefix}[PASS] artifacts={_artifacts}");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {exception}");
        }
        finally
        {
            foreach (Node child in GetChildren()) child.Free();
            TranslationServer.SetLocale(previousLocale);
            GetTree().Quit(exitCode);
        }
    }

    private async Task VerifyWorkspaceMenus(string locale)
    {
        var catalog = new ToolCatalog();
        string seedTool = catalog.ListTools()[0].Id;
        catalog.CreateChain("popup-" + locale.ToLowerInvariant() + "-english", "English sample tool chain", new[] { seedTool });
        catalog.CreateChain("popup-" + locale.ToLowerInvariant() + "-chinese", "中文示例工具链", new[] { seedTool });
        var workspace = new MainWorkspace();
        AddChild(workspace);
        await Frames(5);
        string projectRoot = Path.Combine(System.Environment.GetEnvironmentVariable("AGAS_HOME")!, "fixture-project-" + locale);
        Directory.CreateDirectory(Path.Combine(projectRoot, "assets"));
        workspace.OpenProject(projectRoot);
        await Frames(5);
        CanvasWorkspace canvas = Required<CanvasWorkspace>(workspace, "CanvasWorkspaceHost");
        using (Image fixture = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8))
        {
            fixture.Fill(Colors.White);
            Require(fixture.SavePng(Path.Combine(projectRoot, "assets", "selected.png")) == Error.Ok,
                "The isolated image fixture must save successfully.");
        }
        Require(canvas.TryAddImageFromProjectRelativePath("assets/selected.png", new Vector2(48, 96)),
            "The toolbox fixture must have an image selection.");
        OptionButton model = Required<OptionButton>(workspace, "AiGenerationProfileSelector");
        foreach (int size in new[] { 11, 35, 48 })
        {
            workspace.Theme = AgasSharedIdeTheme.Create(size);
            AgasSharedIdeTheme.ApplyScaledFontOverrides(workspace, size);
            await Frames(4);
            PopupMenu modelPopup = model.GetPopup();
            modelPopup.Popup(new Rect2I(100, 100, 0, 0));
            await Frames(4);
            Check(modelPopup.ItemCount >= 2, "Model selector must contain real catalog choices.");
            VerifyPopup(modelPopup, size, $"model-{locale}-{size}", GetWindow().GuiEmbedSubwindows);
            await Capture(modelPopup, $"model-{locale}-{size}");
            modelPopup.Hide();
            await Frames(3);
            canvas._GuiInput(new InputEventMouseButton
                { ButtonIndex = MouseButton.Right, Pressed = true, Position = new Vector2(320, 240) });
            await Frames(4);
            PopupMenu canvasMenu = Required<PopupMenu>(workspace, "CanvasAiContextMenu");
            canvasMenu.Popup(new Rect2I(100, 100, 0, 0));
            await Frames(4);
            VerifyPopup(canvasMenu, size, $"canvas-{locale}-{size}", GetWindow().GuiEmbedSubwindows);
            await Capture(canvasMenu, $"canvas-{locale}-{size}");
            canvasMenu.Hide();
            await Frames(3);
            PopupMenu tools = Required<PopupMenu>(workspace, "CanvasToolMenu");
            Check(tools.GetThemeFontSize("font_size") == canvasMenu.GetThemeFontSize("font_size"),
                $"tools-{locale}-{size}: toolbox submenu must share the popup font tier.");
            tools.Popup(new Rect2I(100, 100, 0, 0));
            await Frames(4);
            Check(tools.ItemCount > 1, "Toolbox submenu must contain real tool entries, not only an empty-state row.");
            VerifyPopup(tools, size, $"tools-{locale}-{size}", GetWindow().GuiEmbedSubwindows);
            await Capture(tools, $"tools-{locale}-{size}");
            tools.Hide();
            await Frames(3);
        }
        workspace.QueueFree();
        await Frames(4);
    }

    private void VerifyPopup(PopupMenu popup, int size, string label, bool embedded)
    {
        int effectiveFontSize = popup.GetThemeFontSize("font_size");
        int separation = popup.GetThemeConstant("v_separation");
        StyleBox panel = popup.GetThemeStylebox("panel");
        float glyphHeight = popup.GetThemeFont("font").GetHeight(effectiveFontSize);
        float minimumContentHeight = panel.GetMinimumSize().Y;
        for (int index = 0; index < popup.ItemCount; index++)
        {
            minimumContentHeight += popup.IsItemSeparator(index)
                ? popup.GetThemeStylebox("separator").GetMinimumSize().Y + separation
                : glyphHeight + separation;
        }
        Check(popup.Visible && popup.IsEmbedded() == embedded, $"{label}: popup must use the requested window mode.");
        Check(effectiveFontSize == AgasSharedIdeTheme.ScaleFontSize(size, 11),
            $"{label}: popup font must follow the compact tier after live changes.");
        Check(separation >= Math.Max(2, effectiveFontSize / 10), $"{label}: row spacing must scale with the font.");
        Check(panel.ContentMarginTop >= Math.Max(2, effectiveFontSize / 8)
            && panel.ContentMarginBottom >= Math.Max(2, effectiveFontSize / 8),
            $"{label}: first/last glyphs need scaled panel padding.");
        Check(popup.Size.Y + 1 >= minimumContentHeight, $"{label}: popup clips rows ({popup.Size.Y} < {minimumContentHeight}).");
        Check(popup.Size.Y + 1 >= popup.GetContentsMinimumSize().Y, $"{label}: popup is below its content minimum.");
        foreach (Node child in popup.GetChildren(includeInternal: true))
        {
            foreach (Node panelChild in child.GetChildren(includeInternal: true))
            {
                if (panelChild is not ScrollContainer scroll) continue;
                float available = scroll.Size.Y - scroll.GetThemeStylebox("panel").GetMinimumSize().Y;
                foreach (Node content in scroll.GetChildren(includeInternal: true))
                {
                    if (content is not Control control || content is ScrollBar) continue;
                    Check(available + 1 >= control.GetCombinedMinimumSize().Y,
                        $"{label}: internal scroll area clips rows ({available} < {control.GetCombinedMinimumSize().Y}).");
                }
                Check(!scroll.GetVScrollBar().Visible, $"{label}: all menu rows must fit without unexpected scrolling.");
            }
        }
        GD.Print($"{Prefix} {label} size={popup.Size} minimum={popup.GetContentsMinimumSize()}"
            + $" font={effectiveFontSize} glyph={glyphHeight} separation={separation}"
            + $" margins={panel.ContentMarginTop}/{panel.ContentMarginBottom} rows={popup.ItemCount}");
    }

    private async Task VerifyKeyboardSelection(OptionButton language, AgasUiAppearanceSettingsStore store, string label)
    {
        PopupMenu popup = language.GetPopup();
        popup.SetFocusedItem(0);
        SendKey(popup, Key.Down);
        await Frames(2);
        Check(popup.GetFocusedItem() == 1, $"{label}: Down must focus the second language.");
        SendKey(popup, Key.Enter);
        await Frames(3);
        Check(language.Selected == 1 && store.Load().UiLocale == "zh_CN",
            $"{label}: keyboard selection must apply and persist Chinese.");
        popup.Hide();
        language.ShowPopup();
        await Frames(3);
        Check(language.Selected == 1 && popup.Visible, $"{label}: selection must survive reopening.");
        popup.SetFocusedItem(1);
        SendKey(popup, Key.Up);
        await Frames(2);
        SendKey(popup, Key.Enter);
        await Frames(3);
        Check(language.Selected == 0 && store.Load().UiLocale == "en",
            $"{label}: keyboard selection must apply and persist English.");
        popup.Hide();
        await Frames(3);
    }

    private void SendKey(PopupMenu popup, Key key)
    {
        int windowId = popup.IsEmbedded() ? GetWindow().GetWindowId() : popup.GetWindowId();
        Input.ParseInputEvent(new InputEventKey { WindowId = windowId, Keycode = key, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { WindowId = windowId, Keycode = key, Pressed = false });
    }

    private async Task Capture(Window window, string name)
    {
        if (DisplayServer.GetName() == "headless") return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image image = window.GetTexture().GetImage()
            ?? throw new InvalidOperationException($"{name}: screenshot is unavailable.");
        Require(!image.IsEmpty(), $"{name}: real rendered screenshot is required.");
        Require(image.SavePng(Path.Combine(_artifacts, name + ".png")) == Error.Ok, $"{name}: screenshot save failed.");
    }

    private async Task Frames(int count)
    {
        for (int frame = 0; frame < count; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private void Check(bool condition, string message)
    {
        if (!condition) { _failures.Add(message); GD.Print($"{Prefix}[CHECK-FAIL] {message}"); }
    }

    private static T Required<T>(Node root, string name) where T : Node =>
        root.FindChild(name, true, false) as T ?? throw new InvalidOperationException($"Missing {typeof(T).Name}: {name}");

    private static T RequiredType<T>(Node root) where T : Node
    {
        if (root is T match) return match;
        foreach (Node child in root.GetChildren())
        {
            try { return RequiredType<T>(child); } catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException($"Missing {typeof(T).Name}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
