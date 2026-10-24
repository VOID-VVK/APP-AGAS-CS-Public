using System;
using System.Reflection;
using System.Threading.Tasks;
using AGAS.UI;
using AppAgasCs.UI.Theming;
using Godot;

public partial class PopupMenuAppearanceAcceptance : Node
{
    private const string Prefix = "[AGAS][POPUP-APPEARANCE]";
    private int _checks;

    public override async void _Ready()
    {
        int exitCode = 0;
        string previousLocale = TranslationServer.GetLocale();
        try
        {
            GetWindow().GuiEmbedSubwindows = true;
            GetWindow().Size = new Vector2I(1920, 1080);
            await VerifyPopupsAsync();
            GD.Print($"{Prefix}[PASS] {_checks} checks; 35/48/64px, en/zh_CN, dropdowns, menu buttons, context/submenus, live theme changes");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] after {_checks} checks: {exception}");
        }
        finally
        {
            foreach (Node child in GetChildren()) child.Free();
            TranslationServer.SetLocale(previousLocale);
            GetTree().Quit(exitCode);
        }
    }

    private async Task VerifyPopupsAsync()
    {
        var root = new Control { Theme = AgasSharedIdeTheme.Create(35) };
        AddChild(root);
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var ordinaryScroll = new ScrollContainer();
        root.AddChild(ordinaryScroll);
        var language = new OptionButton { Position = new Vector2(100, 100), Size = new Vector2(700, 100) };
        language.AddItem("English");
        language.AddItem("简体中文");
        root.AddChild(language);
        var menuButton = new MenuButton { Position = new Vector2(850, 100), Text = "View / 视图" };
        root.AddChild(menuButton);
        menuButton.GetPopup().AddCheckItem("Layers / 图层");
        menuButton.GetPopup().SetItemChecked(0, true);
        var context = new PopupMenu { Name = "Context", Position = new Vector2I(100, 250) };
        root.AddChild(context);
        var submenu = new PopupMenu { Name = "Tools" };
        submenu.AddItem("Run tool / 运行工具");
        context.AddSubmenuNodeItem("Tools / 工具", submenu);
        context.AddSeparator();
        context.AddItem("Disabled / 不可用");
        context.SetItemDisabled(2, true);
        context.AddItem("Last row / 最后一项");
        MethodInfo configure = typeof(MainWorkspace).GetMethod("ConfigureCanvasContextMenuLayout", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Canvas popup layout entry point is missing.");
        configure.Invoke(null, new object[] { context });
        configure.Invoke(null, new object[] { submenu });
        int selections = 0;
        language.ItemSelected += _ => selections++;
        foreach (string locale in new[] { "en", "zh_CN" })
        {
            TranslationServer.SetLocale(locale);
            foreach (int fontSize in new[] { 35, 48, 64, 35 })
            {
                root.Theme = AgasSharedIdeTheme.Create(fontSize);
                AgasSharedIdeTheme.ApplyScaledFontOverrides(root, fontSize);
                AgasSharedIdeTheme.ApplyScaledFontOverrides(root, fontSize);
                await Frames(3);
                Check(ordinaryScroll.GetThemeStylebox("panel").GetMinimumSize() != Vector2.Zero,
                    "Ordinary scroll containers must retain their shared inset style.");
                language.Select(0);
                language.ShowPopup();
                await Frames(3);
                VerifyMenu(language.GetPopup(), fontSize, "language dropdown");
                Check(!language.GetPopup().ShrinkWidth, "Dropdown must retain Godot's trigger-width policy.");
                language.GetPopup().Hide();
                language.GetPopup().EmitSignal(PopupMenu.SignalName.IndexPressed, 1L);
                Check(language.Selected == 1, "Dropdown selection must remain functional.");
                menuButton.GetPopup().Popup();
                await Frames(3);
                VerifyMenu(menuButton.GetPopup(), fontSize, "menu button");
                menuButton.GetPopup().Hide();
                context.Popup();
                await Frames(3);
                VerifyMenu(context, fontSize, "canvas context menu");
                Check(context.IsItemDisabled(2), "Disabled state must survive appearance changes.");
                Check(context.Size.X >= 360, "Canvas menus must retain their existing minimum width.");
                context.Hide();
                submenu.Popup();
                await Frames(3);
                VerifyMenu(submenu, fontSize, "canvas submenu");
                submenu.Hide();
            }
        }
        Check(selections == 8, "Repeated theme application must not duplicate selection handlers.");
        root.Free();
    }

    private void VerifyMenu(PopupMenu menu, int fontSize, string name)
    {
        ScrollContainer scroll = InternalScroll(menu);
        StyleBox scrollPanel = scroll.GetThemeStylebox("panel");
        Check(scrollPanel.GetMinimumSize() == Vector2.Zero,
            $"{name} at {fontSize}px: internal scroll must not add a second panel or consume item space; actual={scrollPanel.GetMinimumSize()}.");
        StyleBoxFlat panel = menu.GetThemeStylebox("panel") as StyleBoxFlat
            ?? throw new InvalidOperationException($"{name}: shared flat popup panel missing.");
        Check(panel.BorderWidthLeft == 1 && panel.BorderWidthTop == 1 && panel.BorderWidthRight == 1 && panel.BorderWidthBottom == 1,
            $"{name}: outer border must remain a single physical pixel at every font size.");
        Check(!menu.HasThemeStyleboxOverride("panel"), $"{name}: panel must inherit live shared theme metrics.");
        Check(menu.GetThemeFontSize("font_size") == AgasSharedIdeTheme.ScaleFontSize(fontSize, 11),
            $"{name}: popup text must follow the compact shared font tier.");
        Check(menu.GetThemeConstant("v_separation") == AgasSharedIdeTheme.ScaleFontSize(fontSize, 4),
            $"{name}: row spacing must scale without stale context-menu overrides.");
        Check(menu.Size.Y >= menu.GetContentsMinimumSize().Y - 1,
            $"{name}: window must fit its current item metrics rather than a stale height.");
        float rowsHeight = menu.GetContentsMinimumSize().Y - panel.GetMinimumSize().Y;
        Check(scroll.Size.Y >= rowsHeight - 1,
            $"{name}: last row must fit in the internal scroll viewport; viewport={scroll.Size.Y}, rows={rowsHeight}.");
        Check(menu.GetThemeColor("font_disabled_color").A > 0, $"{name}: disabled labels must remain visible.");
        Check(menu.GetThemeStylebox("hover") is StyleBoxFlat hover && hover.BgColor.A > 0,
            $"{name}: keyboard/mouse focused rows must have a visible highlight.");
    }

    private static ScrollContainer InternalScroll(PopupMenu menu)
    {
        foreach (Node child in menu.GetChildren(includeInternal: true))
            if (child is PanelContainer)
                foreach (Node panelChild in child.GetChildren(includeInternal: true))
                    if (panelChild is ScrollContainer scroll) return scroll;
        throw new InvalidOperationException("PopupMenu internal scroll container was not found.");
    }

    private async Task Frames(int count)
    {
        for (int frame = 0; frame < count; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
