using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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

public partial class PopupAppearanceAcceptance : Node
{
    private const string Prefix = "[AGAS][POPUP-APPEARANCE]";
    private readonly List<string> _failures = new();
    private string _artifacts = string.Empty;
    private int _checks;

    public override async void _Ready()
    {
        int exitCode = 0;
        try
        {
            string home = System.Environment.GetEnvironmentVariable("AGAS_HOME") ?? string.Empty;
            Require(Path.GetFullPath(home).Contains("agas-godot-acceptance."), "Disposable AGAS_HOME is mandatory.");
            _artifacts = System.Environment.GetEnvironmentVariable("AGAS_POPUP_ACCEPTANCE_ARTIFACTS")
                ?? Path.Combine(home, "popup-appearance-evidence");
            Directory.CreateDirectory(_artifacts);
            GetWindow().GuiEmbedSubwindows = false;
            GetWindow().Size = new Vector2I(1920, 1080);
            GD.Print($"{Prefix} display={DisplayServer.GetName()} native=true home={home}");
            await Frames(4);
            if (System.Environment.GetEnvironmentVariable("AGAS_POPUP_ACCEPTANCE_LANGUAGE_CLICK_ONLY") == "1")
                await VerifyNativeLanguageClick(home);
            else
            {
                await VerifySettings(home);
                if (System.Environment.GetEnvironmentVariable("AGAS_POPUP_ACCEPTANCE_CRITICAL_ONLY") != "1")
                    await VerifyWorkspaceSamples(home);
            }
            Require(_failures.Count == 0, string.Join(System.Environment.NewLine, _failures));
            GD.Print($"{Prefix}[GEOMETRY-PASS] checks={_checks}; nativePixels={DisplayServer.GetName() != "headless"}; artifacts={_artifacts}; visual review required");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] checks={_checks}: {exception}");
        }
        finally
        {
            foreach (Node child in GetChildren()) child.Free();
            GetTree().Quit(exitCode);
        }
    }

    private async Task VerifyNativeLanguageClick(string home)
    {
        Require(DisplayServer.GetName() != "headless", "Mouse acceptance requires the native display.");
        var store = new AgasUiAppearanceSettingsStore(home);
        store.Save(AgasUiAppearanceSettings.Default with { UiLocale = "en", FontSize = 35 });
        AppLocale.Apply("en");
        GetWindow().Size = new Vector2I(912, 592);
        var main = new Main(new RecentProjectsStore(Path.Combine(home, "recent-projects.tres")), disableMcpRuntimeForTesting: true);
        AddChild(main);
        await Frames(5);
        Check(main.McpDisposedForTesting, "Native click: MCP must remain disabled.");
        RequiredType<MenuBar>(main).PreferGlobalMenu = false;
        Required<PopupMenu>(main, "设置").EmitSignal(PopupMenu.SignalName.IdPressed, 2L);
        await Frames(5);
        SettingsDialog dialog = RequiredType<SettingsDialog>(main);
        ItemList categories = Required<ItemList>(dialog, "SettingsCategories");
        categories.Select(1);
        categories.EmitSignal(ItemList.SignalName.ItemSelected, 1L);
        Required<LineEdit>(dialog, "AiProfileName").Text = "isolated-unsaved-native-click-draft";
        await Frames(4);
        string DraftHash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
            Descendants(dialog).OfType<LineEdit>().OrderBy(editor => editor.Name.ToString()).Select(editor => editor.Name + ":" + editor.Text)))));
        string draftBefore = DraftHash();
        string profilesPath = Path.Combine(home, "image-generation-profiles.json");
        string profilesBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(profilesPath)));
        OptionButton language = Required<OptionButton>(dialog, "UiLanguage");
        language.ShowPopup();
        await Frames(5);
        PopupMenu popup = language.GetPopup();
        ScrollContainer scroll = Descendants(popup, true).OfType<ScrollContainer>().First();
        Control rows = scroll.GetChildren(includeInternal: true).OfType<Control>().First(control => control is not ScrollBar);
        Rect2 rowsRect = rows.GetGlobalRect();
        Rect2 lastRow = new(rowsRect.Position + new Vector2(0, rowsRect.Size.Y / 2), new Vector2(rowsRect.Size.X, rowsRect.Size.Y / 2));
        Vector2 point = lastRow.GetCenter();
        int effective = popup.GetThemeFontSize("font_size");
        Font face = popup.GetThemeFont("font");
        GD.Print($"{Prefix}[CLICK-METRICS] userFont=35 productionThemeFont={dialog.Theme.GetFontSize("font_size", "PopupMenu")} popupFont={effective} glyphHeight={face.GetHeight(effective)} ascent={face.GetAscent(effective)} descent={face.GetDescent(effective)} popupRect={popup.Position}/{popup.Size} contentRect={rowsRect} lastRowRect={lastRow} bottomInset={popup.Size.Y - lastRow.End.Y} itemRectAPI=unavailable; equal-height two-row rect verified by native hover hit");
        popup.PushInput(new InputEventMouseMotion { WindowId = popup.GetWindowId(), Position = point, GlobalPosition = point }, true);
        await Frames(4);
        Require(popup.GetFocusedItem() == 1, "Measured native mouse point must hit the Chinese row before clicking.");
        await Capture(popup, "native35-mouse-hover-chinese");
        popup.PushInput(new InputEventMouseButton { WindowId = popup.GetWindowId(), Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = true }, true);
        popup.PushInput(new InputEventMouseButton { WindowId = popup.GetWindowId(), Position = point, GlobalPosition = point, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        await Frames(6);
        Check(language.Selected == 1 && language.GetItemText(language.Selected) == "简体中文", "Native mouse click must select Chinese text.");
        Check(store.Load().UiLocale == "zh_CN" && AppLocale.Current == "zh_CN", "Native mouse click must persist and apply isolated Chinese locale.");
        Check(DraftHash() == draftBefore, "Native language click must preserve unrelated UI draft field text.");
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(profilesPath))) == profilesBefore,
            "Native language click must not write the isolated AI profile config.");
        language.ShowPopup();
        await Frames(5);
        Check(popup.IsItemChecked(1) && !popup.IsItemChecked(0), "Reopened native popup must check only the clicked Chinese radio.");
        VerifyPopup(popup, 35, "native35-after-mouse-chinese", false);
        VerifyAppearanceLabels(dialog, "native35-after-mouse-chinese");
        await Capture(popup, "native35-after-mouse-chinese-radio");
        await Capture(dialog, "native35-after-mouse-settings");
        await CaptureInterface(dialog, popup, "native35-after-mouse-interface");
        GD.Print($"{Prefix}[CLICK-RESULT] mouse=real-native-window-input selected={language.Selected} text={language.GetItemText(language.Selected)} locale={store.Load().UiLocale} radio={popup.IsItemChecked(1)} draftUnchanged={DraftHash() == draftBefore} aiConfigUnchanged={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(profilesPath))) == profilesBefore}");
        popup.Hide();
    }

    private async Task VerifySettings(string home)
    {
        foreach (string locale in new[] { "en", "zh_CN" })
        {
            var store = new AgasUiAppearanceSettingsStore(home);
            store.Save(AgasUiAppearanceSettings.Default with { UiLocale = locale, FontSize = 35 });
            AppLocale.Apply(locale);
            var main = new Main(new RecentProjectsStore(Path.Combine(home, "recent-projects.tres")), disableMcpRuntimeForTesting: true);
            AddChild(main);
            await Frames(5);
            Check(main.McpDisposedForTesting, $"{locale}: MCP must remain disabled.");
            RequiredType<MenuBar>(main).PreferGlobalMenu = false;
            Required<PopupMenu>(main, "设置").EmitSignal(PopupMenu.SignalName.IdPressed, 2L);
            await Frames(5);
            SettingsDialog dialog = RequiredType<SettingsDialog>(main);
            ItemList categories = Required<ItemList>(dialog, "SettingsCategories");
            categories.Select(1);
            categories.EmitSignal(ItemList.SignalName.ItemSelected, 1L);
            await Frames(5);
            foreach (Vector2I requested in new[] { new Vector2I(1792, 997), new Vector2I(912, 592) })
            {
                GetWindow().Size = requested;
                dialog.Hide();
                await Frames(4);
                dialog.Open(main);
                await Frames(7);
                string label = $"settings-{locale}-35-{requested.X}x{requested.Y}";
                ScrollContainer appearanceScroll = Required<ScrollContainer>(dialog, "AppearanceSettingsScroll");
                GD.Print($"{Prefix} {label} requestedRoot={requested} actualRoot={GetWindow().Size} actualDialog={dialog.Size} dialogMinimum={dialog.MinSize} scroll={appearanceScroll.Size}");
                Check(Math.Abs(GetWindow().Size.X - requested.X) <= 1 && Math.Abs(GetWindow().Size.Y - requested.Y) <= 1,
                    $"{label}: root viewport must match requested dimensions within one native pixel rounding.");
                Check(dialog.Size.X <= GetWindow().Size.X && dialog.Size.Y <= GetWindow().Size.Y,
                    $"{label}: production dialog overflows requested viewport ({dialog.Size} > {GetWindow().Size}); narrow-layout limitation.");
                VerifyAppearanceLabels(dialog, label);
                await Capture(dialog, label);
                OptionButton language = Required<OptionButton>(dialog, "UiLanguage");
                language.ShowPopup();
                await Frames(5);
                PopupMenu popup = language.GetPopup();
                Check(popup.ItemCount == 2 && popup.GetItemText(0) == "English" && popup.GetItemText(1) == "简体中文",
                    $"{label}: language must have exactly two complete choices.");
                Check(popup.IsItemRadioCheckable(0) && popup.IsItemRadioCheckable(1), $"{label}: both choices must retain radio affordances.");
                Check(popup.IsItemChecked(language.Selected), $"{label}: selected language radio must be checked.");
                VerifyPopup(popup, 35, label + "-language", false);
                await Capture(popup, label + "-language");
                await CaptureInterface(dialog, popup, label + "-interface");
                popup.SetFocusedItem(1);
                await Frames(3);
                Check(popup.GetFocusedItem() == 1 && popup.GetThemeStylebox("hover") is StyleBoxFlat hover && hover.BgColor.A > 0,
                    $"{label}: last-row focus/hover highlight must be visible.");
                await Capture(popup, label + "-language-hover");
                popup.Hide();
                GD.Print($"{Prefix}[CRITICAL-DONE] {label}");
            }
            main.QueueFree();
            await Frames(5);
        }
    }

    private void VerifyAppearanceLabels(SettingsDialog dialog, string caseName)
    {
        OptionButton language = Required<OptionButton>(dialog, "UiLanguage");
        SpinBox font = Required<SpinBox>(dialog, "UiFontSize");
        foreach (Control editor in new Control[] { language, font })
        {
            Label caption = editor.GetParent().GetChildren().OfType<Label>().First();
            float requiredHeight = caption.GetLineHeight() * caption.GetLineCount();
            Rect2 rect = caption.GetGlobalRect();
            Rect2 editorRect = editor.GetGlobalRect();
            GD.Print($"{Prefix} {caseName} field={editor.Name} text={caption.Tr(caption.Text)} rect={rect} editor={editorRect} font={caption.GetThemeFontSize("font_size")} lines={caption.GetLineCount()} visibleLines={caption.GetVisibleLineCount()} glyphHeight={requiredHeight} minimum={caption.GetCombinedMinimumSize()}");
            Check(caption.Size.Y + 1 >= requiredHeight && caption.GetVisibleLineCount() == caption.GetLineCount(),
                $"{caseName} {editor.Name}: caption clips glyphs ({caption.Size.Y} < {requiredHeight}).");
            Check(rect.End.Y <= editorRect.Position.Y + 1, $"{caseName} {editor.Name}: caption overlaps editor.");
            Check(caption.Size.X + 1 >= caption.GetCombinedMinimumSize().X, $"{caseName} {editor.Name}: caption clips horizontally.");
        }
    }

    private async Task VerifyWorkspaceSamples(string home)
    {
        GetWindow().Size = new Vector2I(1792, 997);
        AppLocale.Apply("zh_CN");
        var catalog = new ToolCatalog();
        string seed = catalog.ListTools()[0].Id;
        catalog.CreateChain("popup-appearance-english", "English sample tool chain", new[] { seed });
        catalog.CreateChain("popup-appearance-chinese", "中文示例工具链", new[] { seed });
        var workspace = new MainWorkspace();
        AddChild(workspace);
        await Frames(5);
        string project = Path.Combine(home, "fixture-project");
        Directory.CreateDirectory(Path.Combine(project, "assets"));
        workspace.OpenProject(project);
        await Frames(5);
        CanvasWorkspace canvas = Required<CanvasWorkspace>(workspace, "CanvasWorkspaceHost");
        using (Image fixture = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8))
        {
            fixture.Fill(Colors.White);
            Require(fixture.SavePng(Path.Combine(project, "assets", "selected.png")) == Error.Ok, "Fixture image must save.");
        }
        Require(canvas.TryAddImageFromProjectRelativePath("assets/selected.png", new Vector2(48, 96)), "Fixture image selection must exist.");
        foreach (int size in new[] { 35, 48 })
        {
            workspace.Theme = AgasSharedIdeTheme.Create(size);
            AgasSharedIdeTheme.ApplyScaledFontOverrides(workspace, size);
            await Frames(5);
            PopupMenu model = Required<OptionButton>(workspace, "AiGenerationProfileSelector").GetPopup();
            await SamplePopup(model, size, $"model-zh_CN-{size}", false);
            canvas._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = new Vector2(320, 240) });
            await Frames(3);
            PopupMenu context = Required<PopupMenu>(workspace, "CanvasAiContextMenu");
            context.Hide();
            await SamplePopup(context, size, $"context-zh_CN-{size}", true);
            PopupMenu submenu = Required<PopupMenu>(workspace, "CanvasToolMenu");
            Check(submenu.ItemCount > 1, "Real submenu must include the seeded English and Chinese chains.");
            await SamplePopup(submenu, size, $"submenu-zh_CN-{size}", true);
        }
        workspace.QueueFree();
        await Frames(4);
    }

    private async Task SamplePopup(PopupMenu popup, int size, string name, bool canvas)
    {
        popup.Popup(new Rect2I(GetWindow().Position.X + 80, GetWindow().Position.Y + 100, 0, 0));
        await Frames(5);
        VerifyPopup(popup, size, name, canvas);
        await Capture(popup, name);
        await CaptureInterface(null, popup, name + "-interface");
        popup.Hide();
        await Frames(3);
    }

    private void VerifyPopup(PopupMenu popup, int size, string name, bool canvas)
    {
        StyleBoxFlat panel = popup.GetThemeStylebox("panel") as StyleBoxFlat ?? throw new InvalidOperationException("Flat popup panel missing.");
        ScrollContainer scroll = Descendants(popup, true).OfType<ScrollContainer>().First();
        Control rows = scroll.GetChildren(includeInternal: true).OfType<Control>().First(control => control is not ScrollBar);
        float usable = scroll.Size.Y - scroll.GetThemeStylebox("panel").GetMinimumSize().Y;
        float needed = rows.GetCombinedMinimumSize().Y;
        GD.Print($"{Prefix} {name} native={!popup.IsEmbedded()} id={popup.GetWindowId()} rect={popup.Position}/{popup.Size} contentMinimum={popup.GetContentsMinimumSize()} rowsRect={rows.GetRect()} usable={usable} needed={needed} font={popup.GetThemeFontSize("font_size")} glyph={popup.GetThemeFont("font").GetHeight(popup.GetThemeFontSize("font_size"))} padding={panel.ContentMarginLeft}/{panel.ContentMarginRight}/{panel.ContentMarginTop}/{panel.ContentMarginBottom} inner={scroll.GetThemeStylebox("panel").GetMinimumSize()} separation={popup.GetThemeConstant("v_separation")} items={popup.ItemCount}");
        Check(popup.Visible && !popup.IsEmbedded(), $"{name}: a native child window is required.");
        Check(popup.GetThemeFontSize("font_size") == AgasSharedIdeTheme.ScaleFontSize(size, 11), $"{name}: compact font tier must follow production theme.");
        Check(panel.ContentMarginLeft == 12 && panel.ContentMarginRight == 12 && panel.ContentMarginTop == 10 && panel.ContentMarginBottom == 10,
            $"{name}: fixed LR12/TB10 permanent padding is required.");
        Check(panel.BorderWidthLeft == 2 && panel.BorderWidthTop == 2 && panel.BorderWidthRight == 2 && panel.BorderWidthBottom == 2,
            $"{name}: all borders must be exactly 2px.");
        Check(scroll.GetThemeStylebox("panel").GetMinimumSize() == Vector2.Zero, $"{name}: internal scroll must not double-pad.");
        Check(usable + 1 >= needed && !scroll.GetVScrollBar().Visible, $"{name}: last row clipped or unexpected scroll ({usable} < {needed}).");
        Check(rows.GetGlobalRect().End.Y <= popup.Size.Y - panel.ContentMarginBottom + 1, $"{name}: last row consumes permanent bottom padding.");
        Check(popup.Size.Y + 1 >= popup.GetContentsMinimumSize().Y, $"{name}: window must fit engine natural minimum.");
        if (canvas)
            Check(popup.GetThemeConstant("item_start_padding") == 8 && popup.GetThemeConstant("item_end_padding") == 8 && popup.GetThemeConstant("v_separation") == 12,
                $"{name}: canvas item padding8/vsep12 must remain intact.");
    }

    private async Task Capture(Window window, string name)
    {
        if (DisplayServer.GetName() == "headless") return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using Image image = window.GetTexture().GetImage();
        Require(!image.IsEmpty() && image.GetWidth() > 1 && image.GetHeight() > 1, $"{name}: real native pixels are mandatory.");
        Require(image.SavePng(Path.Combine(_artifacts, name + ".png")) == Error.Ok, $"{name}: native capture save failed.");
        GD.Print($"{Prefix}[CAPTURE] {name} pixels={image.GetSize()} window={window.Size}");
    }

    private async Task CaptureInterface(Window? dialog, PopupMenu popup, string name)
    {
        if (DisplayServer.GetName() == "headless") return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Window[] windows = dialog is null ? new Window[] { GetWindow(), popup } : new Window[] { GetWindow(), dialog, popup };
        Vector2I origin = GetWindow().Position;
        Vector2I end = origin + GetWindow().Size;
        foreach (Window window in windows)
        {
            origin = origin.Min(window.Position);
            end = end.Max(window.Position + window.Size);
        }
        using Image image = Image.CreateEmpty(end.X - origin.X, end.Y - origin.Y, false, Image.Format.Rgba8);
        image.Fill(Colors.Black);
        foreach (Window window in windows)
        {
            using Image child = window.GetTexture().GetImage();
            child.Convert(Image.Format.Rgba8);
            image.BlitRect(child, new Rect2I(Vector2I.Zero, child.GetSize()), window.Position - origin);
        }
        Require(image.SavePng(Path.Combine(_artifacts, name + ".png")) == Error.Ok, $"{name}: full-interface composite save failed.");
        GD.Print($"{Prefix}[CAPTURE] {name} composite=root+native-child pixels={image.GetSize()}");
    }

    private static IEnumerable<Node> Descendants(Node root, bool includeInternal = false)
    {
        foreach (Node child in root.GetChildren(includeInternal))
        {
            yield return child;
            foreach (Node descendant in Descendants(child, includeInternal)) yield return descendant;
        }
    }

    private static T Required<T>(Node root, string name) where T : Node =>
        Descendants(root).OfType<T>().FirstOrDefault(node => node.Name == name) ?? throw new InvalidOperationException($"Missing {name}.");
    private static T RequiredType<T>(Node root) where T : Node =>
        Descendants(root).OfType<T>().FirstOrDefault() ?? throw new InvalidOperationException($"Missing {typeof(T).Name}.");

    private async Task Frames(int count)
    {
        for (int frame = 0; frame < count; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private void Check(bool condition, string message)
    {
        _checks++;
        if (condition) return;
        _failures.Add(message);
        GD.Print($"{Prefix}[CHECK-FAIL] {message}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
