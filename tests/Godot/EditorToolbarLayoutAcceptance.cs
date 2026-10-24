#nullable enable

using System;
using System.Reflection;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using AGAS.UI;
using Agas.Features.Canvas;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.UI.Shared;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class EditorToolbarLayoutAcceptance : Node
{
    public override async void _Ready()
    {
        int exitCode = 0;
        string originalLocale = TranslationServer.GetLocale();
        try
        {
            await VerifySavedSplitLayout();
            if (System.Environment.GetEnvironmentVariable("AGAS_TOP_HEADER_ONLY") == "1"
                || System.Environment.GetEnvironmentVariable("AGAS_B_CENTER_ONLY") == "1")
            {
                await VerifySavedSplitLayout(defaultLayout: true);
                GD.Print("[AGAS][EDITOR-TOOLBAR-LAYOUT][CENTER-PASS] default/extreme layouts, manual drag, raw persisted bytes");
                return;
            }
            foreach (int fontSize in new[] { 35, 48 })
            foreach (string locale in new[] { "en", "zh_CN" })
            foreach (int width in new[] { 420, 720, 1200 })
            {
                TranslationServer.SetLocale(locale);
                var workspace = new MainWorkspace();
                object builder = typeof(MainWorkspace).GetField("_shellBuilder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace)!;
                object built = builder.GetType().GetMethod("CreateCanvasToolStatusBar", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(builder, null)!;
                Control bar = (Control)built.GetType().GetProperty("Root")!.GetValue(built)!;
                var host = new VBoxContainer { Size = new Vector2(width, 700), Theme = AgasIdeTheme.Create(fontSize) };
                AddChild(host);
                host.AddChild(bar);
                var canvas = new Control { Name = "CanvasLayoutSpace", CustomMinimumSize = new Vector2(1, 120), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
                host.AddChild(canvas);
                typeof(MainWorkspace).GetField("_canvasSelectionStatusLabel", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(workspace, bar.FindChild("CanvasSelectionStatusLabel", true, false));
                typeof(MainWorkspace).GetField("_canvasContextStatusLabel", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(workspace, bar.FindChild("CanvasContextStatusLabel", true, false));
                typeof(MainWorkspace).GetMethod("UpdateCanvasToolStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(workspace, null);
                AgasIdeTheme.ApplyScaledFontOverrides(host, fontSize);
                await Frames();
                Label context = (Label)bar.FindChild("CanvasContextStatusLabel", true, false);
                Button select = (Button)bar.FindChild("CanvasSelectionToolButton", true, false);
                GD.Print($"[AGAS][EDITOR-TOOLBAR-LAYOUT][METRIC] locale={locale} font={fontSize} requested={width}x700 host={host.Size} bar={bar.Size} min={bar.GetCombinedMinimumSize()} context={context.Size} button={select.Size} canvas={canvas.Size}");
                HBoxContainer row = (HBoxContainer)bar.FindChild("CanvasToolStatusContent", true, false);
                ScrollContainer toolbarScroll = (ScrollContainer)bar.FindChild("CanvasToolStatusScroll", true, false);
                float naturalHeight = row.GetCombinedMinimumSize().Y + bar.GetThemeStylebox("panel").GetMinimumSize().Y
                    + toolbarScroll.GetThemeStylebox("panel").GetMinimumSize().Y + toolbarScroll.GetHScrollBar().GetCombinedMinimumSize().Y;
                Require(row.Size.Y <= fontSize * 3 && bar.Size.Y <= naturalHeight + 2, "The canvas toolbar must stay at its production-theme natural single-row height, not grow with wrapped status text.");
                Require(bar.GetCombinedMinimumSize().X <= width && host.Size.X <= width + 1, "The toolbar must not force a giant root minimum width.");
                Require(canvas.Size.Y >= 400 && canvas.Position.Y + canvas.Size.Y <= 701, "The canvas must retain visible usable space inside the requested pane.");
                Require(select.Size.Y <= fontSize * 3 && context.AutowrapMode == TextServer.AutowrapMode.Off, "Toolbar buttons and information must remain single-row controls.");
                if (width == 420)
                {
                    ScrollContainer? scroll = bar.FindChild("CanvasToolStatusScroll", true, false) as ScrollContainer;
                    Require(scroll != null && scroll.GetHScrollBar().Visible, "Narrow panes must expose a horizontal scroll bar so every toolbar button remains reachable.");
                    scroll!.ScrollHorizontal = (int)scroll.GetHScrollBar().MaxValue;
                    await Frames();
                    Button fit = (Button)bar.FindChild("CanvasZoomFitButton", true, false);
                    Require(fit.GlobalPosition.X >= scroll.GlobalPosition.X - 1 && fit.GlobalPosition.X + fit.Size.X <= scroll.GlobalPosition.X + scroll.Size.X + 1, "Scrolling must expose the last button instead of clipping it permanently.");
                    Require(fit.GetGlobalRect().End.Y <= scroll.GetHScrollBar().GlobalPosition.Y + 1,
                        "The scrollbar must not overlap or clip the natural-height canvas buttons.");
                }
                host.Free();

                object globalBuilt = builder.GetType().GetMethod("CreateToolbar", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(builder, null)!;
                Control globalBar = (Control)globalBuilt.GetType().GetProperty("Root")!.GetValue(globalBuilt)!;
                var globalHost = new VBoxContainer { Size = new Vector2(width, 700), Theme = AgasIdeTheme.Create(fontSize) };
                AddChild(globalHost);
                globalHost.AddChild(globalBar);
                var globalCanvas = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
                globalHost.AddChild(globalCanvas);
                Label status = (Label)globalBar.FindChild("SaveStatusLabel", true, false);
                typeof(MainWorkspace).GetField("_statusLabel", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(workspace, status);
                string rawDetail = string.Concat(System.Linq.Enumerable.Repeat("设置/保存-user-name-0123456789 ", 80));
                typeof(MainWorkspace).GetMethod("SetStatusTemplate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(workspace, new object[] { "工具执行失败：{0}", true, new object[] { rawDetail } });
                AgasIdeTheme.ApplyScaledFontOverrides(globalHost, fontSize);
                await Frames();
                GD.Print($"[AGAS][EDITOR-TOOLBAR-LAYOUT][GLOBAL] locale={locale} font={fontSize} requested={width}x700 bar={globalBar.Size} min={globalBar.GetCombinedMinimumSize()} status={status.Size} canvas={globalCanvas.Size}");
                Require(globalHost.Size.X <= width + 1 && globalBar.Size.Y <= fontSize * 4 && globalCanvas.Size.Y >= 400,
                    "The global toolbar and a long tool failure must not inflate root width or consume the canvas.");
                Require(status.AutowrapMode == TextServer.AutowrapMode.Off && status.ClipText && status.TooltipText.Contains(rawDetail.TrimEnd(), StringComparison.Ordinal),
                    "The status must remain one-line while its full tooltip preserves raw details.");
                ScrollContainer globalScroll = (ScrollContainer)globalBar.FindChild("WorkspaceToolbarScroll", true, false);
                Require(globalScroll.GetHScrollBar().Visible && globalScroll.GetHScrollBar().Size.Y >= 16,
                    "A narrow global toolbar must offer a physically usable scrollbar, not a zero-height theme scrollbar.");
                foreach (Node child in globalBar.FindChild("WorkspaceToolbarContent", true, false).GetChildren())
                {
                    if (child is not BaseButton action || !action.Visible) continue;
                    globalScroll.EnsureControlVisible(action);
                    await Frames();
                    Require(action.GlobalPosition.X >= globalScroll.GlobalPosition.X - 1
                        && action.GlobalPosition.X + action.Size.X <= globalScroll.GlobalPosition.X + globalScroll.Size.X + 1,
                        $"Global toolbar action {action.Name} must remain reachable by scrolling without executing it.");
                    Require(action.GetGlobalRect().End.Y <= globalScroll.GetHScrollBar().GlobalPosition.Y + 1,
                        "The global scrollbar must not overlap or clip toolbar actions.");
                }
                globalHost.Free();
                workspace.Free();
            }
            GD.Print("[AGAS][EDITOR-TOOLBAR-LAYOUT][PASS] 12 canvas + 12 global production-theme pane cases");
        }
        catch (Exception error)
        {
            exitCode = 1;
            GD.PushError($"[AGAS][EDITOR-TOOLBAR-LAYOUT][FAIL] {error}");
        }
        finally
        {
            foreach (Node child in GetChildren()) child.Free();
            TranslationServer.SetLocale(originalLocale);
            GetTree().Quit(exitCode);
        }
    }

    private async Task VerifySavedSplitLayout(bool defaultLayout = false)
    {
        string? previousHome = System.Environment.GetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable);
        Require(!string.IsNullOrWhiteSpace(previousHome), "Use the isolated acceptance runner.");
        string root = Path.Combine(previousHome!, "B-split-layout-" + Guid.NewGuid().ToString("N"));
        string home = Path.Combine(root, "home");
        string project = Path.Combine(root, "project");
        Main? main = null;
        try
        {
            Directory.CreateDirectory(project);
            System.Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, home);
            new AgasUiAppearanceSettingsStore(home).Save(AgasUiAppearanceSettings.Default with { FontSize = 35, UiLocale = "en" });
            var repository = new AgasResourceRepository();
            Require(new AgasProjectInitializer(repository).EnsureInitialized(project).Success, "Initialize only a disposable fixture.");
            var preferences = defaultLayout ? new WorkspaceLayoutResource() : new WorkspaceLayoutResource
            {
                ResourceVersion = 3, LeftPanelWidth = 1582, RightPanelWidth = 572,
                PreviewHeight = 1058, BottomDockHeight = 1005,
                BottomDockCollapsed = false, LeftPanelCollapsed = false, RightPanelCollapsed = false,
                ActiveLeftTab = "Scenes", ActiveBottomTab = "Tools"
            };
            Require(repository.Save(preferences, AgasPaths.WorkspaceLayoutFile(project)).Success, "Save isolated split preferences.");
            byte[] originalLayoutBytes = File.ReadAllBytes(AgasPaths.WorkspaceLayoutFile(project));
            main = new Main(new RecentProjectsStore(Path.Combine(home, "recent.tres")), disableMcpRuntimeForTesting: true);
            GetWindow().Size = new Vector2I(1792, 997);
            AddChild(main);
            await Frames();
            typeof(Main).GetMethod("OpenWorkspace", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, new object[] { project, false });
            await Frames();
            await Frames();
            MainWorkspace workspace = (MainWorkspace)main.FindChild("MainWorkspace", true, false);
            foreach (Vector2I size in new[] { new Vector2I(1792, 997), new Vector2I(1024, 570), new Vector2I(1792, 997) })
            foreach (string locale in new[] { "en", "zh_CN" })
            {
                GetWindow().Size = size;
                AppLocale.Apply(locale);
                await Frames();
                await Frames();
                await Frames();
                foreach (string name in new[] { "WorkspaceRoot", "WorkspaceToolbar", "WorkspaceBottomSplitter", "LeftWorkspaceSplitter", "CenterWorkspace", "CanvasDocumentHost", "CanvasToolStatusBar", "LayerTabs", "CanvasWorkspaceHost", "LeftWorkspaceDock", "RightWorkspaceDock", "BottomWorkspaceDock", "BottomDockTabs", "BottomDockToggleButton" })
                {
                    Control control = (Control)workspace.FindChild(name, true, false);
                    GD.Print($"[AGAS][EDITOR-TOOLBAR-LAYOUT][SPLIT] fixture={(defaultLayout ? "default" : "overwide")} locale={locale} requested={size} {name} rect={control.GetGlobalRect()} min={control.GetCombinedMinimumSize()} custom={control.CustomMinimumSize}");
                }
                Control rootControl = (Control)workspace.FindChild("WorkspaceRoot", true, false);
                Control canvas = (Control)workspace.FindChild("CanvasWorkspaceHost", true, false);
                Control left = (Control)workspace.FindChild("LeftWorkspaceDock", true, false);
                Control right = (Control)workspace.FindChild("RightWorkspaceDock", true, false);
                Control toggle = (Control)workspace.FindChild("BottomDockToggleButton", true, false);
                Rect2 viewport = GetWindow().GetVisibleRect();
                Require(rootControl.Size.X <= viewport.Size.X + 1 && rootControl.Size.Y <= viewport.Size.Y + 1,
                    "Saved extreme split preferences must not make the real workspace root exceed the current viewport.");
                Require(canvas.Size.X >= 100 && canvas.Size.Y >= 100 && canvas.GetGlobalRect().End.Y <= viewport.End.Y + 1,
                    "The real canvas must retain a reasonable visible area at font35 without changing saved preferences.");
                Require(canvas.Size.X > left.Size.X && canvas.Size.X > right.Size.X,
                    "Startup and viewport resize must give the canvas the largest pane, even with overwide saved sidebars.");
                Require(canvas.Size.X >= viewport.Size.X * 0.30f,
                    "Header padding and grouping must preserve at least30% canvas width at both viewport sizes.");
                if (size.X == 1792) Require(canvas.Size.X >= viewport.Size.X * 0.55f,
                    "The main canvas must use at least55% of the wide viewport without restoring overwide saved preferences.");
                Require(toggle.GetGlobalRect().End.X <= viewport.End.X + 1 && toggle.GetGlobalRect().End.Y <= viewport.End.Y + 1,
                    "The bottom dock toggle must remain fully inside the constrained viewport.");
                Label tasks = (Label)workspace.FindChild("AiGenerationTaskCountLabel", true, false);
                Require(tasks.Text == (locale == "en" ? "Tasks 5/0" : "任务 5/0"),
                    "The existing task counter must translate into concrete English/Chinese text on notification without starting tasks.");
                await VerifyTopHeader(workspace, locale, size);
                foreach (string name in new[] { "CanvasSelectionToolButton", "CanvasReplacementToolButton", "CanvasEditToolButton", "CanvasToolboxMenuButton", "CanvasZoomFitButton", "LayerAddButton", "LayerRenameButton", "LayerMoveUpButton", "LayerMoveDownButton", "LayerPromptButton", "LayerDeleteButton" })
                {
                    Control action = (Control)workspace.FindChild(name, true, false);
                    Node? parent = action.GetParent();
                    while (parent != null && parent != workspace)
                    {
                        if (parent is ScrollContainer scroll && scroll.GetHScrollBar().Visible)
                            Require(action.GetGlobalRect().End.Y <= scroll.GetHScrollBar().GlobalPosition.Y + 1,
                                $"{name} must not be vertically clipped by an ancestor horizontal scrollbar at the production font.");
                        parent = parent.GetParent();
                    }
                }
                WorkspaceLayoutResource loaded = (WorkspaceLayoutResource)typeof(MainWorkspace).GetField("_layout", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace)!;
                Require(loaded.LeftPanelWidth == preferences.LeftPanelWidth && loaded.RightPanelWidth == preferences.RightPanelWidth
                    && loaded.BottomDockHeight == preferences.BottomDockHeight && loaded.PreviewHeight == preferences.PreviewHeight
                    && loaded.ActiveLeftTab == preferences.ActiveLeftTab && loaded.ActiveBottomTab == preferences.ActiveBottomTab,
                    "Runtime viewport clamps must preserve preferred dimensions and selected tabs.");
            }
            HSplitContainer leftSplit = (HSplitContainer)workspace.FindChild("LeftWorkspaceSplitter", true, false);
            Control leftDock = (Control)workspace.FindChild("LeftWorkspaceDock", true, false);
            float startupLeftWidth = leftDock.Size.X;
            leftSplit.EmitSignal(SplitContainer.SignalName.DragStarted);
            await Frames();
            GD.Print($"[AGAS][EDITOR-TOOLBAR-LAYOUT][DRAG] start={startupLeftWidth} releasedMin={leftDock.GetCombinedMinimumSize()} offsets={string.Join(",", leftSplit.SplitOffsets)}");
            leftSplit.SplitOffsets = new[] { (int)Math.Round(startupLeftWidth + 180) };
            await Frames();
            GD.Print($"[AGAS][EDITOR-TOOLBAR-LAYOUT][DRAG] moved={leftDock.Size.X} offsets={string.Join(",", leftSplit.SplitOffsets)}");
            leftSplit.EmitSignal(SplitContainer.SignalName.DragEnded);
            await Frames();
            await Frames();
            float manualWidth = leftDock.Size.X;
            GD.Print($"[AGAS][EDITOR-TOOLBAR-LAYOUT][DRAG] captured={manualWidth} offsets={string.Join(",", leftSplit.SplitOffsets)}");
            Require(manualWidth > startupLeftWidth + 100, "A user drag must be allowed to exceed the startup sidebar ratio.");
            AppLocale.Apply("en");
            await Frames();
            Require(Math.Abs(leftDock.Size.X - manualWidth) <= 2, "Locale refresh must not pull a manual sidebar back to its startup ratio.");
            GetWindow().Size = new Vector2I(1024, 570);
            await Frames();
            GetWindow().Size = new Vector2I(1792, 997);
            await Frames();
            await Frames();
            Require(Math.Abs(leftDock.Size.X - manualWidth) <= 2, "Resize clamps must restore the user's manual width when there is space again.");
            Require(originalLayoutBytes.SequenceEqual(File.ReadAllBytes(AgasPaths.WorkspaceLayoutFile(project))),
                "Startup, resize, locale and drag must not automatically rewrite the user's saved layout file.");
            GD.Print($"[AGAS][EDITOR-TOOLBAR-LAYOUT][MANUAL-PASS] fixture={(defaultLayout ? "default" : "overwide")} startupLeft={startupLeftWidth} draggedLeft={manualWidth} restoredLeft={leftDock.Size.X}; original layout bytes unchanged");
            GD.Print("[AGAS][EDITOR-TOOLBAR-LAYOUT][SPLIT-PASS] font35 en/zh saved1582/572/1058/1005 actual1792x997 -> 1024x570 -> 1792x997");
        }
        finally
        {
            main?.Free();
            System.Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, previousHome);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private async Task VerifyTopHeader(MainWorkspace workspace, string locale, Vector2I size)
    {
        PanelContainer toolbar = (PanelContainer)workspace.FindChild("WorkspaceToolbar", true, false);
        MarginContainer margin = (MarginContainer)toolbar.FindChild("WorkspaceToolbarMargin", true, false);
        HBoxContainer header = (HBoxContainer)toolbar.FindChild("WorkspaceToolbarHeader", true, false);
        Label title = (Label)toolbar.FindChild("WorkspaceProjectTitle", true, false);
        ScrollContainer scroll = (ScrollContainer)toolbar.FindChild("WorkspaceToolbarScroll", true, false);
        HBoxContainer row = (HBoxContainer)toolbar.FindChild("WorkspaceToolbarContent", true, false);
        Rect2 outer = toolbar.GetGlobalRect();
        Rect2 inner = header.GetGlobalRect();
        Vector2 leading = inner.Position - outer.Position;
        Vector2 trailing = outer.End - inner.End;
        GD.Print($"[AGAS][TOP-HEADER] locale={locale} viewport={size} margins={leading}/{trailing} toolbar={toolbar.Size} header={header.Size} row={row.Size} natural={row.GetCombinedMinimumSize()} track={scroll.GetHScrollBar().Size} scroll={scroll.ScrollHorizontal} title={title.GetGlobalRect()} text={title.Text} tooltip={title.TooltipText}");
        Require(leading.IsEqualApprox(new Vector2(8, 8)) && trailing.IsEqualApprox(new Vector2(8, 8)),
            "The global header must have real8px container margins on all four sides.");
        Require(margin.GetThemeConstant("margin_left") == 8 && margin.GetThemeConstant("margin_top") == 8,
            "Header padding must be supplied by a MarginContainer, not just painted borders.");
        Require(scroll.ScrollHorizontal == 0 && title.GetParent() == header && title.GlobalPosition.X == inner.Position.X,
            "Initial scrolling must be zero and the title's beginning must stay outside the scrollable tools.");
        Require(title.AutowrapMode == TextServer.AutowrapMode.Off && title.ClipText
            && title.TextOverrunBehavior == TextServer.OverrunBehavior.TrimEllipsis
            && title.Text.StartsWith("AGAS - ", StringComparison.Ordinal) && title.TooltipText == title.Text
            && title.TooltipAutoTranslateMode == Node.AutoTranslateModeEnum.Disabled,
            "Project titles must keep their prefix, single-line ellipsis and full untranslated tooltip.");
        Require(title.Size.X >= title.GetThemeFont("font").GetStringSize("AGAS - ", fontSize: title.GetThemeFontSize("font_size")).X,
            "The fixed title budget must visibly fit the project title's prefix at font35.");
        Require(scroll.Size.Y + 1 >= row.GetCombinedMinimumSize().Y + scroll.GetHScrollBar().GetCombinedMinimumSize().Y
            && Math.Abs(row.Size.Y - row.GetCombinedMinimumSize().Y) <= 1,
            "The top toolbar must retain natural row height plus a dedicated scrollbar track budget.");
        Require(Math.Abs(toolbar.Size.Y - (Math.Max(title.GetCombinedMinimumSize().Y, scroll.GetCombinedMinimumSize().Y) + 16)) <= 1,
            "Only real container padding may increase the global header height.");
        Require(toolbar.GetThemeStylebox("panel") is StyleBoxFlat { BorderWidthBottom: 1 },
            "The global header must have a single thin lower boundary.");
        foreach (string name in new[] { "WorkspaceHistoryGridSeparator", "WorkspaceGridViewSeparator", "WorkspaceViewAiSeparator",
            "CanvasToolsSelectionSeparator", "CanvasSelectionContextSeparator", "CanvasContextZoomSeparator" })
        {
            MarginContainer group = (MarginContainer)workspace.FindChild(name, true, false);
            VSeparator separator = group.GetChild<VSeparator>(0);
            Require(group.GetThemeConstant("margin_left") == 8 && group.GetThemeConstant("margin_right") == 8
                && separator.GetThemeStylebox("separator") is StyleBoxLine { Thickness: 1, Vertical: true } line
                && line.Color.A >= 0.5f && separator.Size.Y >= 16,
                $"{name} must provide one visible low-contrast line with fixed8px group gaps.");
        }
        Require(!row.GetChildren().OfType<VSeparator>().Any(), "Group separators must replace the old unstyled separators, not duplicate them.");
        foreach (Control action in row.GetChildren().OfType<Control>().Where(control => control.Visible
            && (control is BaseButton or SpinBox || control.Name == "SaveStatusLabel")))
        {
            scroll.EnsureControlVisible(action);
            await Frames();
            Require(action.GetGlobalRect().Position.X >= scroll.GlobalPosition.X - 1
                && action.GetGlobalRect().End.X <= scroll.GetGlobalRect().End.X + 1
                && action.GetGlobalRect().End.Y <= scroll.GetHScrollBar().GlobalPosition.Y + 1,
                $"{action.Name} must remain fully reachable without touching natural button height.");
            Require(title.GlobalPosition.X == inner.Position.X, "Scrolling tools must never scroll away the project title.");
        }
        string originalTitle = title.Text;
        string rawTitle = "AGAS - /tmp/设置/保存-project-0123456789/" + new string('x', 120);
        title.Text = rawTitle;
        title.TooltipText = rawTitle;
        title.Notification((int)Node.NotificationTranslationChanged);
        await Frames();
        Require(title.Text == rawTitle && title.TooltipText == rawTitle && title.Size.X < title.GetThemeFont("font").GetStringSize(rawTitle, fontSize: title.GetThemeFontSize("font_size")).X,
            "Long raw names/paths must be constrained to ellipsis without translating their full tooltip.");
        title.Text = originalTitle;
        title.TooltipText = originalTitle;
        scroll.ScrollHorizontal = 0;
        ScrollContainer canvasScroll = (ScrollContainer)workspace.FindChild("CanvasToolStatusScroll", true, false);
        HBoxContainer canvasRow = (HBoxContainer)workspace.FindChild("CanvasToolStatusContent", true, false);
        Require(canvasScroll.Size.Y + 1 >= canvasRow.GetCombinedMinimumSize().Y + canvasScroll.GetHScrollBar().GetCombinedMinimumSize().Y,
            "Canvas grouping must retain its natural row height and independent scrollbar track.");
        foreach (BaseButton action in canvasRow.GetChildren().OfType<BaseButton>().Where(button => button.Visible))
        {
            canvasScroll.EnsureControlVisible(action);
            await Frames();
            Require(action.GetGlobalRect().Position.X >= canvasScroll.GlobalPosition.X - 1
                && action.GetGlobalRect().End.X <= canvasScroll.GetGlobalRect().End.X + 1
                && action.GetGlobalRect().End.Y <= canvasScroll.GetHScrollBar().GlobalPosition.Y + 1,
                $"Canvas action {action.Name} must be fully reachable after toolbar grouping.");
        }
        canvasScroll.ScrollHorizontal = 0;
        await Frames();
        string? screenshot = System.Environment.GetEnvironmentVariable("AGAS_TOP_HEADER_SCREENSHOT");
        if (!string.IsNullOrWhiteSpace(screenshot) && locale == "en" && size.X == 1792 && !File.Exists(screenshot))
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            Require(GetViewport().GetTexture().GetImage().SavePng(screenshot) == Error.Ok, "Save only the isolated native viewport screenshot.");
            GD.Print($"[AGAS][TOP-HEADER][SCREENSHOT] {screenshot}");
        }
    }

    private async Task Frames()
    {
        for (int frame = 0; frame < 6; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
