using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AGAS.UI;
using AGAS.UI.Dialogs;
using Agas.Features.Canvas;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Features.Ai.UI;
using AppAgasCs.Features.Tools.Storage;
using AppAgasCs.UI.Shared;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class LocalizedLayoutAcceptance : Node
{
    private const string Prefix = "[AGAS][LOCALIZED-LAYOUT]";
    private const string RawName = "设置 Long user layer / 文件 / {name} abcdefghijklmnopqrstuvwxyz";
    private const string RawPrompt = "设置 保存 user prompt {0} {unfinished\nKeep this exact text. 中文原文不能翻译。";
    private readonly List<string> _failures = new();
    private string _root = string.Empty;
    private int _checks;
    private int _captures;

    public override async void _Ready()
    {
        string? previousHome = System.Environment.GetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable);
        string previousLocale = TranslationServer.GetLocale();
        int exitCode = 0;
        try
        {
            Require(!string.IsNullOrWhiteSpace(previousHome), "Use the isolated acceptance runner.");
            _root = Path.GetFullPath(Path.Combine(previousHome!, "localized-layout-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(_root);
            GetWindow().GuiEmbedSubwindows = true;
            bool productionOnly = System.Environment.GetEnvironmentVariable("AGAS_LAYOUT_CASE") == "production";
            bool widthsOnly = System.Environment.GetEnvironmentVariable("AGAS_LAYOUT_CASE") == "widths";
            int userFont = int.TryParse(System.Environment.GetEnvironmentVariable("AGAS_LAYOUT_USER_FONT"), out int selectedFont) ? selectedFont : 35;
            string? selectedFonts = System.Environment.GetEnvironmentVariable("AGAS_LAYOUT_FONTS");
            int[] fonts = selectedFonts != null ? selectedFonts.Split(',').Select(int.Parse).Distinct().ToArray()
                : productionOnly || widthsOnly ? new[] { 35 } : new[] { 35, 16, 24, userFont, 48, 64 }.Distinct().ToArray();
            GD.Print($"{Prefix} real production Main/theme; requested fonts={string.Join(",", fonts)}; font setting has no finite supported maximum");
            foreach (int fontSize in fonts)
            {
                Require(fontSize >= AgasUiAppearanceSettings.MinimumFontSize && fontSize <= 96,
                    "This bounded rendering acceptance supports requested font stress cases through 96, not int.MaxValue.");
                await VerifyWorkspaceAsync(fontSize, extreme: true, inspectPanels: !productionOnly && !widthsOnly, verifyWidths: widthsOnly);
                if (widthsOnly)
                    await VerifyWorkspaceAsync(fontSize, extreme: false, inspectPanels: false, verifyWidths: true, newDefault: true);
                else if (!productionOnly)
                    await VerifyWorkspaceAsync(fontSize, extreme: false, inspectPanels: true);
            }
            if (_failures.Count > 0) throw new InvalidOperationException($"{_failures.Count} layout failures; first: {_failures[0]}");
            GD.Print($"{Prefix}[PASS] {_checks} checks; real populated Main, persisted split layouts, native locales, constrained viewports, no MCP host or generation");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {_checks} checks: {exception}");
        }
        finally
        {
            foreach (Node child in GetChildren()) child.Free();
            TranslationServer.SetLocale(previousLocale);
            System.Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, previousHome);
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            GetTree().Quit(exitCode);
        }
    }

    private async Task VerifyWorkspaceAsync(int fontSize, bool extreme, bool inspectPanels, bool verifyWidths = false, bool newDefault = false)
    {
        string caseName = $"font{fontSize}-{(newDefault ? "new-default" : extreme ? "production-saved" : "left48percent")}";
        string home = Path.Combine(_root, caseName, "home");
        string projectRoot = Path.Combine(_root, caseName, "project");
        Directory.CreateDirectory(projectRoot);
        System.Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, home);
        var appearance = new AgasUiAppearanceSettingsStore(home);
        appearance.Save(AgasUiAppearanceSettings.Default with { FontSize = fontSize, UiLocale = "en" });
        if (fontSize == 35 && extreme)
            File.WriteAllText(appearance.SettingsPath, "{\"resourceVersion\":3,\"fontSize\":35}");
        var profile = AgasAiProfile.CreateDefault() with { Name = RawName };
        string longestModel = AgasAiUnifiedModelCatalog.Models.OrderByDescending(model => model.DisplayName.Length).First().ModelName;
        new AgasAiSettingsStore(home).SaveProfiles(new AgasAiProfilesDocument(AgasAiProfilesDocument.CurrentResourceVersion, profile.Id, longestModel, new[] { profile }));
        SeedProject(projectRoot, extreme);
        string layoutPath = AgasPaths.WorkspaceLayoutFile(projectRoot);
        if (newDefault) File.Delete(layoutPath);
        byte[]? savedLayout = File.Exists(layoutPath) ? File.ReadAllBytes(layoutPath) : null;
        var main = new Main(new RecentProjectsStore(Path.Combine(home, "recent.tres")), disableMcpRuntimeForTesting: true);
        GetWindow().Size = new Vector2I(1792, 997);
        AddChild(main);
        await Frames(6);
        Invoke(main, "OpenWorkspace", projectRoot, false);
        await Frames(24);
        MainWorkspace workspace = Required<MainWorkspace>(main, "MainWorkspace");
        CanvasWorkspace canvas = Required<CanvasWorkspace>(workspace, "CanvasWorkspaceHost");
        ulong workspaceId = workspace.GetInstanceId();
        ulong canvasId = canvas.GetInstanceId();
        if (savedLayout != null)
            Check(savedLayout.SequenceEqual(File.ReadAllBytes(layoutPath)), $"{caseName}: startup runtime clamping must not rewrite the existing saved layout");
        string[] files = new[] { AgasPaths.ProjectFile(projectRoot), layoutPath, AgasPaths.SceneFile(projectRoot, "scene-001") }.Where(File.Exists).ToArray();
        byte[][] before = files.Select(File.ReadAllBytes).ToArray();
        float initialLeft = Required<Control>(workspace, "LeftWorkspaceDock").Size.X;
        if (!extreme && !verifyWidths)
            Check(initialLeft >= GetWindow().GetVisibleRect().Size.X * 0.45f - 4, $"{caseName}: the populated saved-left-48% case must actually retain a wide left pane at the larger viewport");
        foreach (Vector2I requested in new[] { new Vector2I(1792, 997), new Vector2I(1024, 570), new Vector2I(1792, 997) })
        {
            GetWindow().Size = requested;
            await Frames(24);
            foreach (string locale in new[] { "en", "zh_CN" })
            {
                appearance.Save(appearance.Load() with { UiLocale = locale });
                AppLocale.Apply(locale);
                await Frames(12);
                string context = $"{caseName}/{locale}/requested{requested}";
                await VerifyWorkspaceGeometryAsync(main, workspace, context);
                if (verifyWidths) VerifyStartupWidths(workspace, context);
                Check(workspace.GetInstanceId() == workspaceId && canvas.GetInstanceId() == canvasId,
                    $"{context}: resize/locale must not rebuild the existing workspace/canvas");
                Check(workspace.CurrentScene?.LayersSnapshot[0].Name == RawName
                    && workspace.CurrentScene.LayersSnapshot[0].PromptFragmentsSnapshot[0].Text == RawPrompt,
                    $"{context}: populated user layer and raw prompt must stay exact");
                for (int index = 0; index < files.Length; index++)
                    Check(before[index].SequenceEqual(File.ReadAllBytes(files[index])), $"{context}: locale/resize must not rewrite {Path.GetFileName(files[index])}");
                await CaptureAsync($"{caseName}-{locale}-{requested.X}x{requested.Y}");
                if (inspectPanels && requested.X == 1024) await VerifyOwnedPanelsAsync(main, workspace, locale, context);
            }
        }
        if (verifyWidths)
        {
            await VerifyManualDragAsync(main, workspace, caseName);
            for (int index = 0; index < files.Length; index++)
                Check(before[index].SequenceEqual(File.ReadAllBytes(files[index])), $"{caseName}: manual drag before an explicit save must not rewrite {Path.GetFileName(files[index])}");
        }
        else
            Check(Required<Control>(workspace, "LeftWorkspaceDock").Size.X >= initialLeft - 4,
                $"{caseName}: resizing back must restore the larger saved left-pane intent, not permanently reset it to compact width");
        main.Free();
        await Frames(4);
    }

    private void VerifyStartupWidths(MainWorkspace workspace, string context)
    {
        Control left = Required<Control>(workspace, "LeftWorkspaceDock");
        Control right = Required<Control>(workspace, "RightWorkspaceDock");
        Control center = Required<Control>(workspace, "CenterWorkspace");
        float available = left.Size.X + center.Size.X + right.Size.X;
        float leftMinimum = left.GetCombinedMinimumSize().X;
        float rightMinimum = right.GetCombinedMinimumSize().X;
        float targetCenter = Math.Min(available * 0.55f, available - leftMinimum - rightMinimum);
        GD.Print($"{Prefix}[WIDTHS] {context}: available={available} left={left.Size.X} minLeft={leftMinimum} right={right.Size.X} minRight={rightMinimum} center={center.Size.X} fraction={center.Size.X / available:0.000} target={targetCenter}");
        Check(center.Size.X > left.Size.X && center.Size.X > right.Size.X,
            $"{context}: startup center must be larger than either sidebar, actual center={center.Size.X} left={left.Size.X} right={right.Size.X}");
        Check(center.Size.X >= targetCenter - 4,
            $"{context}: center must retain at least 55% of available width unless actual sidebar font minima prevent it, actual={center.Size.X} required={targetCenter}");
        Check(left.Size.X <= Math.Max(available * 0.25f, leftMinimum) + 4,
            $"{context}: startup Files/Scenes sidebar must stay near the requested smaller proportion, actual={left.Size.X}/{available}");
        Check(right.Size.X <= Math.Max(available * 0.22f, rightMinimum) + 4,
            $"{context}: startup Properties sidebar must stay bounded near the requested smaller proportion, actual={right.Size.X}/{available}");
    }

    private async Task VerifyManualDragAsync(Main main, MainWorkspace workspace, string context)
    {
        foreach ((string splitterName, string dockName, int change) in new[]
        {
            ("LeftWorkspaceSplitter", "LeftWorkspaceDock", 160),
            ("CanvasPropertiesSplitter", "RightWorkspaceDock", -64),
        })
        {
            HSplitContainer split = Required<HSplitContainer>(workspace, splitterName);
            Control dock = Required<Control>(workspace, dockName);
            float beforeDrag = dock.Size.X;
            split.EmitSignal(SplitContainer.SignalName.DragStarted);
            await Frames(4);
            int leading = change > 0 ? (int)Math.Round(beforeDrag) + change : -(int)Math.Round(beforeDrag) + change;
            split.SplitOffsets = new[] { leading };
            await Frames(4);
            float draggedWidth = dock.Size.X;
            split.EmitSignal(SplitContainer.SignalName.DragEnded);
            await Frames(24);
            GD.Print($"{Prefix}[MANUAL-DRAG] {context}/{dockName}: before={beforeDrag} dragged={draggedWidth} after={dock.Size.X}");
            Check(draggedWidth >= beforeDrag + 40, $"{context}: real splitter interaction must allow enlarging {dockName}");
            Check(Math.Abs(dock.Size.X - draggedWidth) <= 4, $"{context}: manual {dockName} width must not snap back to the startup default");
            foreach (string locale in new[] { "en", "zh_CN" })
            {
                AppLocale.Apply(locale);
                await Frames(16);
                Check(Math.Abs(dock.Size.X - draggedWidth) <= 4, $"{context}/{locale}: locale refresh must retain the user's manually dragged {dockName} width");
                await VerifyWorkspaceGeometryAsync(main, workspace, context + "/manual-" + dockName + "/" + locale);
            }
        }
        Control left = Required<Control>(workspace, "LeftWorkspaceDock");
        Control right = Required<Control>(workspace, "RightWorkspaceDock");
        float manualLeft = left.Size.X;
        float manualRight = right.Size.X;
        GetWindow().Size = new Vector2I(1024, 570);
        await Frames(24);
        GetWindow().Size = new Vector2I(1792, 997);
        await Frames(24);
        Check(Math.Abs(left.Size.X - manualLeft) <= 4 && Math.Abs(right.Size.X - manualRight) <= 4,
            $"{context}: resize roundtrip must restore both manually dragged sidebar widths, expected={manualLeft}/{manualRight} actual={left.Size.X}/{right.Size.X}");
        await CaptureAsync(context + "-manual-drag");
    }

    private static void SeedProject(string projectRoot, bool extreme)
    {
        var repository = new AgasResourceRepository();
        Require(new AgasProjectInitializer(repository).EnsureInitialized(projectRoot).Success, "Populate a real project before layout testing.");
        AgasSceneResource scene = repository.Load<AgasSceneResource>(AgasPaths.SceneFile(projectRoot, "scene-001")).Resource
            ?? throw new InvalidOperationException("Missing seeded scene.");
        scene.Name = RawName;
        scene.Readme = RawPrompt;
        AgasLayerResource layer = scene.LayersSnapshot[0];
        layer.Name = RawName;
        layer.AddPromptFragment(new PromptFragmentResource { Id = "layout-fragment", Text = RawPrompt, Enabled = true });
        layer.AddImage(new ImageInstanceResource
        {
            Id = "layout-image",
            ImagePath = "assets/设置-user-long-file-abcdefghijklmnopqrstuvwxyz.png",
        });
        Require(repository.Save(scene, AgasPaths.SceneFile(projectRoot, scene.Id)).Success, "Save populated user scene.");
        AgasProjectResource project = repository.Load<AgasProjectResource>(AgasPaths.ProjectFile(projectRoot)).Resource
            ?? throw new InvalidOperationException("Missing seeded project.");
        project.ScenesSnapshot[0].Name = RawName;
        Require(repository.Save(project, AgasPaths.ProjectFile(projectRoot)).Success, "Save a long raw scene-list name.");
        var layout = new WorkspaceLayoutResource
        {
            LeftPanelWidth = extreme ? 1582 : 860,
            RightPanelWidth = extreme ? 572 : 280,
            PreviewHeight = extreme ? 1058 : 180,
            BottomDockHeight = extreme ? 1005 : 240,
            BottomDockCollapsed = false,
            ActiveLeftTab = "Scenes",
            ActiveBottomTab = "Tools",
        };
        Require(repository.Save(layout, AgasPaths.WorkspaceLayoutFile(projectRoot)).Success, "Save production-sized split fixture.");
        Directory.CreateDirectory(Path.Combine(projectRoot, "assets"));
        using Image image = Image.CreateEmpty(16, 16, false, Image.Format.Rgba8);
        image.Fill(Colors.CornflowerBlue);
        Require(image.SavePng(Path.Combine(projectRoot, "assets", "设置-user-long-file-abcdefghijklmnopqrstuvwxyz.png")) == Error.Ok, "Seed a real long-name image file.");
        var catalog = new ToolCatalog();
        catalog.CreateTool("layout-tool", RawName, "extends RefCounted\nfunc process(image: Image) -> Dictionary:\n\treturn {\"image\": image}\n");
    }

    private async Task VerifyWorkspaceGeometryAsync(Main main, MainWorkspace workspace, string context)
    {
        Rect2 viewport = GetWindow().GetVisibleRect();
        Control center = Required<Control>(workspace, "CenterWorkspace");
        Control toolbar = Required<Control>(workspace, "CanvasToolStatusBar");
        Control canvas = Required<Control>(workspace, "CanvasWorkspaceHost");
        Label layerInfo = Required<Label>(workspace, "CanvasContextStatusLabel");
        Label selection = Required<Label>(workspace, "CanvasSelectionStatusLabel");
        Control topToolbar = Required<Control>(workspace, "WorkspaceToolbar");
        Control root = Required<Control>(workspace, "WorkspaceRoot");
        GD.Print($"{Prefix}[METRICS] {context}: window={GetWindow().Size} visible={viewport.Size} main={main.Size} workspace={workspace.Size} root={root.Size} rootMin={root.GetCombinedMinimumSize()} center={center.Size} canvasToolbar={toolbar.Size} layerInfo={layerInfo.Size} selection={selection.Size} canvas={canvas.Size} left={Required<Control>(workspace, "LeftWorkspaceDock").Size} right={Required<Control>(workspace, "RightWorkspaceDock").Size} bottom={Required<Control>(workspace, "BottomWorkspaceDock").Size} top={topToolbar.Size} font={layerInfo.GetThemeFontSize("font_size")}");
        Check(Fits(workspace.GetGlobalRect(), viewport), $"{context}: production workspace must fit the actual visible viewport");
        Check(Fits(Required<Control>(workspace, "WorkspaceRoot").GetGlobalRect(), viewport), $"{context}: root minimum widths/heights must not push the real UI beyond the visible viewport");
        Check(Fits(center.GetGlobalRect(), viewport), $"{context}: constrained center pane must remain inside viewport");
        Check(toolbar.Size.Y <= Math.Max(96, center.Size.Y * 0.4f), $"{context}: canvas toolbar must not consume the center pane height, actual {toolbar.Size.Y}/{center.Size.Y}");
        Check(canvas.Size.Y >= Math.Max(60, center.Size.Y * 0.3f), $"{context}: real populated canvas must retain at least 30% of center height, actual {canvas.Size.Y}/{center.Size.Y}");
        Check(canvas.Size.X >= 100, $"{context}: actual canvas must retain a usable display width");
        foreach (Label label in new[] { layerInfo, selection })
        {
            Check(label.AutowrapMode == TextServer.AutowrapMode.Off || label.ClipText || label.Size.X >= Math.Max(70, label.GetThemeFontSize("font_size") * 3),
                $"{context}: {label.Name} must not word-wrap into a one-glyph column, width={label.Size.X}");
        }
        VerifyGlyphs(layerInfo, context);
        Check(topToolbar.Size.Y <= viewport.Size.Y * 0.4f, $"{context}: top actions/status must leave room for workspace content");
        foreach (BaseButton button in Descendants(topToolbar).OfType<BaseButton>().Where(button => button.IsVisibleInTree()))
            await CheckReachableAsync(button, topToolbar.GetGlobalRect(), context + "/top-toolbar");
        foreach (BaseButton button in Descendants(toolbar).OfType<BaseButton>().Where(button => button.IsVisibleInTree()))
        {
            Check(button.Size.Y <= button.GetCombinedMinimumSize().Y * 1.35f + 8,
                $"{context}: {button.Name} must keep natural height, actual={button.Size.Y} natural={button.GetCombinedMinimumSize().Y}");
            await CheckReachableAsync(button, toolbar.GetGlobalRect(), context);
        }
        foreach (string name in new[] { "LeftDockToggleButton", "RightDockToggleButton", "BottomDockToggleButton" })
            await CheckReachableAsync(Required<BaseButton>(workspace, name), viewport, context);
        Control sidebar = Required<Control>(workspace, "LeftWorkspaceDock");
        foreach (BaseButton button in Descendants(sidebar).OfType<BaseButton>().Where(button => button.IsVisibleInTree()))
            await CheckReachableAsync(button, sidebar.GetGlobalRect(), context + "/sidebar");
        OptionButton models = Required<OptionButton>(workspace, "AiGenerationProfileSelector");
        if (models.ItemCount > 0)
        {
            int longest = Enumerable.Range(0, models.ItemCount).OrderByDescending(index => models.GetItemText(index).Length).First();
            models.Select(longest);
            Check(models.AutoTranslateMode == AutoTranslateModeEnum.Disabled, $"{context}: model names remain raw user/provider values");
            await CheckReachableAsync(models, viewport, context + "/longest-model");
        }
    }

    private async Task VerifyOwnedPanelsAsync(Main main, MainWorkspace workspace, string locale, string context)
    {
        TabContainer bottom = Required<TabContainer>(workspace, "BottomDockTabs");
        bottom.CurrentTab = 0;
        await Frames(8);
        PromptPanel prompt = Required<PromptPanel>(workspace, "PromptPanel");
        TextEdit preview = Required<TextEdit>(prompt, "PromptMergedPreview");
        Check(preview.Text.Contains(RawPrompt, StringComparison.Ordinal), $"{context}: long raw prompt preview must remain original");
        foreach (BaseButton button in Descendants(prompt).OfType<BaseButton>().Where(button => button.IsVisibleInTree()))
            await CheckReachableAsync(button, prompt.GetGlobalRect(), context + "/prompt");
        bottom.CurrentTab = 1;
        await Frames(4);

        Invoke(main, "OpenSettingsDialog");
        await Frames(12);
        SettingsDialog settings = Descendants(main).OfType<SettingsDialog>().Single();
        ItemList categories = Required<ItemList>(settings, "SettingsCategories");
        Check(settings.Size.X <= GetWindow().GetVisibleRect().Size.X && settings.Size.Y <= GetWindow().GetVisibleRect().Size.Y,
            $"{context}: real settings window must fit the constrained viewport");
        foreach (int category in new[] { 0, 1, 2 })
        {
            categories.Select(category);
            categories.EmitSignal(ItemList.SignalName.ItemSelected, (long)category);
            await Frames(6);
            foreach (BaseButton button in Descendants(settings).OfType<BaseButton>().Where(button => button.IsVisibleInTree()))
                await CheckReachableAsync(button, new Rect2(Vector2.Zero, settings.Size), context + $"/settings{category}");
        }
        await CaptureAsync(context.Replace('/', '-') + "-settings");
        settings.Hide();

        AiGenerationPanel ai = Required<AiGenerationPanel>(workspace, "AiGenerationPanel");
        ai.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        ai.Show();
        await Frames(8);
        TextEdit input = Required<TextEdit>(ai, "AiGenerationPrompt");
        input.Text = RawPrompt;
        await Frames(4);
        int changes = 0;
        void Changed() => changes++;
        input.TextChanged += Changed;
        AppLocale.Apply(locale == "en" ? "zh_CN" : "en");
        AppLocale.Apply(locale);
        await Frames(8);
        Check(input.Text == RawPrompt && changes == 0, $"{context}: AI locale refresh must not mutate raw prompt or emit input changes");
        input.TextChanged -= Changed;
        foreach (string name in new[] { "AiGenerateButton", "AiCancelButton", "RefreshAiProfiles" })
            await CheckReachableAsync(Required<BaseButton>(ai, name), GetWindow().GetVisibleRect(), context + "/ai");
        await CaptureAsync(context.Replace('/', '-') + "-ai");
        ai.Hide();
    }

    private async Task CheckReachableAsync(Control control, Rect2 bounds, string context)
    {
        if (control is BaseButton)
            Check(control.Size.Y <= control.GetCombinedMinimumSize().Y * 1.35f + 8,
                $"{context}: action {control.Name} must retain natural button height rather than filling the pane");
        Node? parent = control.GetParent();
        while (parent != null)
        {
            if (parent is ScrollContainer scroll && scroll.IsVisibleInTree())
            {
                Rect2 visible = scroll.GetGlobalRect();
                if (scroll.GetHScrollBar().Visible) visible.Size -= new Vector2(0, scroll.GetHScrollBar().Size.Y);
                if (scroll.GetVScrollBar().Visible) visible.Size -= new Vector2(scroll.GetVScrollBar().Size.X, 0);
                Rect2 action = ReachableActionRect(control);
                if (scroll.HorizontalScrollMode != ScrollContainer.ScrollMode.Disabled)
                {
                    int adjustment = action.Position.X < visible.Position.X ? (int)Math.Floor(action.Position.X - visible.Position.X)
                        : (int)Math.Ceiling(Math.Max(0, action.End.X - visible.End.X));
                    scroll.ScrollHorizontal += adjustment;
                }
                if (scroll.VerticalScrollMode != ScrollContainer.ScrollMode.Disabled)
                {
                    int adjustment = action.Position.Y < visible.Position.Y ? (int)Math.Floor(action.Position.Y - visible.Position.Y)
                        : (int)Math.Ceiling(Math.Max(0, action.End.Y - visible.End.Y));
                    scroll.ScrollVertical += adjustment;
                }
                await Frames(2);
                visible = scroll.GetGlobalRect();
                if (scroll.GetHScrollBar().Visible) visible.Size -= new Vector2(0, scroll.GetHScrollBar().Size.Y);
                if (scroll.GetVScrollBar().Visible) visible.Size -= new Vector2(scroll.GetVScrollBar().Size.X, 0);
                Check(visible.Size.X >= 40 && visible.Size.Y >= control.GetCombinedMinimumSize().Y,
                    $"{context}: scrolling actions must have a usable visible scrolling area for {control.Name}");
                Check(Fits(ReachableActionRect(control), visible), $"{context}: {control.Name} natural content/hit area must be reachable within its real scroll container excluding scrollbar tracks, action={ReachableActionRect(control)} visible={visible} scrollH={scroll.ScrollHorizontal} scrollV={scroll.ScrollVertical}");
            }
            parent = parent.GetParent();
        }
        Check(Fits(ReachableActionRect(control), bounds), $"{context}: action {control.Name} natural content/hit area is clipped/outside its available pane: {ReachableActionRect(control)} versus {bounds}");
    }

    private static Rect2 ReachableActionRect(Control control)
    {
        Rect2 action = control.GetGlobalRect();
        if (control is Button button && button is not OptionButton && button.Alignment == HorizontalAlignment.Center
            && (button.SizeFlagsHorizontal & Control.SizeFlags.Expand) != 0)
        {
            float naturalWidth = button.GetCombinedMinimumSize().X;
            if (naturalWidth > 0 && naturalWidth < action.Size.X)
            {
                action.Position += new Vector2((action.Size.X - naturalWidth) / 2, 0);
                action.Size = new Vector2(naturalWidth, action.Size.Y);
            }
        }
        return action;
    }

    private async Task CaptureAsync(string name)
    {
        string? directory = System.Environment.GetEnvironmentVariable("AGAS_LAYOUT_CAPTURE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory) || DisplayServer.GetName() == "headless") return;
        Directory.CreateDirectory(directory);
        await Frames(3);
        using Image image = GetViewport().GetTexture().GetImage();
        string destination = Path.Combine(directory, $"{++_captures:D3}-{name}.png");
        Require(image.SavePng(destination) == Error.Ok, "Save actual production-themed viewport render.");
        GD.Print($"{Prefix}[IMAGE] {destination} texture={image.GetWidth()}x{image.GetHeight()}");
    }

    private void Check(bool condition, string message)
    {
        _checks++;
        if (condition) return;
        _failures.Add(message);
        GD.Print($"{Prefix}[LAYOUT-FAIL] {message}");
    }

    private void VerifyGlyphs(Label label, string context)
    {
        foreach (string sample in new[] { "EnglishABCDEFGHIJKLMNOPQRSTUVWXYZ", "中文设置保存字体图层" })
        {
            using var paragraph = new TextParagraph { Width = 1024 };
            Require(paragraph.AddString(sample, label.GetThemeFont("font"), label.GetThemeFontSize("font_size")), "Shape the actual theme font including system fallback.");
            paragraph.GetLineCount();
            var glyphs = TextServerManager.GetPrimaryInterface().ShapedTextGetGlyphs(paragraph.GetRid());
            long drawableFlags = (long)(TextServer.GraphemeFlag.Valid | TextServer.GraphemeFlag.Virtual);
            int missing = glyphs.Count(glyph => (glyph["flags"].AsInt64() & drawableFlags) == 0);
            GD.Print($"{Prefix}[GLYPHS] {context}: sample={sample} shaped={glyphs.Count} missing={missing}");
            Check(glyphs.Count > 0 && missing == 0, $"{context}: actual shaped Latin/CJK glyphs must be drawable, missing={missing}");
        }
    }

    private static bool Fits(Rect2 child, Rect2 parent) => child.Position.X >= parent.Position.X - 3
        && child.Position.Y >= parent.Position.Y - 3 && child.End.X <= parent.End.X + 3 && child.End.Y <= parent.End.Y + 3;

    private static IEnumerable<Node> Descendants(Node root)
    {
        foreach (Node child in root.GetChildren())
        {
            yield return child;
            foreach (Node descendant in Descendants(child)) yield return descendant;
        }
    }

    private async Task Frames(int count)
    {
        for (int index = 0; index < count; index++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static T Required<T>(Node root, string name) where T : Node => root.FindChild(name, true, false) as T
        ?? throw new InvalidOperationException($"Missing actual production node {name} ({typeof(T).Name}).");

    private static void Invoke(object target, string name, params object[] arguments) =>
        (target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing production entry {name}.")).Invoke(target, arguments);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
