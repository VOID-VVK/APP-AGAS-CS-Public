using System;
using System.Runtime.CompilerServices;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.UI.Shared;
using Godot;

namespace AppAgasCs.UI.Theming;

/// <summary>
/// Shared theme implementation for root UI, dialogs, and feature-owned views.
/// It intentionally lives outside AGAS.UI so lower-level views never depend on
/// the root workspace namespace.
/// </summary>
public static class AgasSharedIdeTheme
{
    private sealed record FontSizeReference(int Size);

    private static readonly ConditionalWeakTable<Control, FontSizeReference> FontSizeReferences = new();

    public static Color MutedTextColor => Palette.TextMuted;
    public static Color ErrorTextColor => Rgb(255, 115, 115);
    public static Color SuccessTextColor => Rgb(112, 191, 124);

    public static Theme Create(int fontSize = AgasUiAppearanceSettings.DefaultFontSize)
    {
        int normalizedFontSize = NormalizeFontSize(fontSize);
        var theme = new Theme
        {
            DefaultFontSize = normalizedFontSize,
        };

        ApplyBase(theme, normalizedFontSize);
        ApplyPanels(theme, normalizedFontSize);
        ApplyButtons(theme, normalizedFontSize);
        ApplyInputs(theme, normalizedFontSize);
        ApplyTabs(theme, normalizedFontSize);
        ApplyLists(theme, normalizedFontSize);
        ApplyScrollBars(theme, normalizedFontSize);
        ApplyTooltips(theme, normalizedFontSize);
        ApplyPopups(theme, normalizedFontSize);

        return theme;
    }

    public static int ScaleFontSize(int fontSize, int referenceSize)
    {
        int normalizedFontSize = NormalizeFontSize(fontSize);
        double scaled = referenceSize * (normalizedFontSize / (double)AgasUiAppearanceSettings.LegacyDefaultFontSize);
        if (double.IsNaN(scaled) || scaled <= 1)
        {
            return 1;
        }
        if (double.IsPositiveInfinity(scaled) || scaled >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Keeps explicit title/header overrides proportional to the global base size.
    /// The first observed override is retained as its 13px-reference value.
    /// </summary>
    public static void ApplyScaledFontOverrides(Node root, int fontSize)
    {
        ArgumentNullException.ThrowIfNull(root);
        ApplyScaledFontOverridesRecursive(root, NormalizeFontSize(fontSize));
    }

    public static StyleBoxFlat PickerBackgroundStyle()
    {
        return Box(Palette.SurfaceInset, Palette.SurfaceInset, radius: 0, borderWidth: 0, marginX: 0, marginY: 0);
    }

    public static StyleBoxFlat CardStyle()
    {
        return Box(Palette.Surface, Palette.Border, radius: 6, marginX: 0, marginY: 0);
    }

    private static void ApplyBase(Theme theme, int fontSize)
    {
        int controlFontSize = ScaleFontSize(fontSize, 12);
        int compactFontSize = ScaleFontSize(fontSize, 11);
        SetFontSize(
            theme,
            fontSize,
            "Control",
            "Label");
        SetFontSize(
            theme,
            controlFontSize,
            "Button",
            "CheckBox",
            "CheckButton",
            "CodeEdit",
            "LinkButton",
            "LineEdit",
            "TextEdit",
            "SpinBox",
            "OptionButton",
            "MenuBar",
            "MenuButton");
        SetFontSize(
            theme,
            compactFontSize,
            "PopupMenu",
            "Tree",
            "ItemList",
            "TabContainer",
            "Tabs");
        theme.SetFontSize("title_font_size", "Window", fontSize);
        SetColor(theme, "font_color", Palette.Text, "Control", "Label");
        SetColor(theme, "font_shadow_color", Palette.Transparent, "Control", "Label");
        SetColor(theme, "font_disabled_color", Palette.TextDisabled, "Control", "Label");
        SetConstant(theme, "separation", ScaleMetric(fontSize, 6), "HBoxContainer", "VBoxContainer");
    }

    private static void ApplyPanels(Theme theme, int fontSize)
    {
        var surface = Box(Palette.Surface, Palette.Border, radius: 4,
            marginX: ScaleMetric(fontSize, 8), marginY: ScaleMetric(fontSize, 8));
        var inset = Box(Palette.SurfaceInset, Palette.BorderSoft, radius: 3,
            marginX: ScaleMetric(fontSize, 6), marginY: ScaleMetric(fontSize, 6));

        theme.SetStylebox("panel", "Panel", surface);
        theme.SetStylebox("panel", "PanelContainer", surface);
        theme.SetStylebox("panel", "MarginContainer", Empty());
        theme.SetStylebox("panel", "ScrollContainer", inset);
    }

    private static void ApplyButtons(Theme theme, int fontSize)
    {
        SetColor(theme, "font_color", Palette.Text, "Button");
        SetColor(theme, "font_hover_color", Palette.Text, "Button");
        SetColor(theme, "font_pressed_color", Palette.Text, "Button");
        SetColor(theme, "font_focus_color", Palette.Text, "Button");
        SetColor(theme, "font_disabled_color", Palette.TextDisabled, "Button");

        int marginX = ScaleMetric(fontSize, 10);
        int marginY = ScaleMetric(fontSize, 5);
        theme.SetStylebox("normal", "Button", Box(Palette.Control, Palette.Border, radius: 3, marginX: marginX, marginY: marginY));
        theme.SetStylebox("hover", "Button", Box(Palette.ControlHover, Palette.BorderHover, radius: 3, marginX: marginX, marginY: marginY));
        theme.SetStylebox("pressed", "Button", Box(Palette.ControlPressed, Palette.AccentSoft, radius: 3, marginX: marginX, marginY: marginY));
        theme.SetStylebox("disabled", "Button", Box(Palette.ControlDisabled, Palette.BorderSoft, radius: 3, marginX: marginX, marginY: marginY));
        theme.SetStylebox("focus", "Button", FocusBox());
        SetConstant(theme, "h_separation", ScaleMetric(fontSize, 6), "Button");
    }

    private static void ApplyInputs(Theme theme, int fontSize)
    {
        int marginX = ScaleMetric(fontSize, 7);
        int marginY = ScaleMetric(fontSize, 4);
        foreach (string type in new[] { "LineEdit", "TextEdit", "SpinBox" })
        {
            SetColor(theme, "font_color", Palette.Text, type);
            SetColor(theme, "font_placeholder_color", Palette.TextMuted, type);
            SetColor(theme, "font_readonly_color", Palette.TextMuted, type);
            SetColor(theme, "font_uneditable_color", Palette.TextMuted, type);
            SetColor(theme, "font_disabled_color", Palette.TextDisabled, type);
            SetColor(theme, "caret_color", Palette.Text, type);
            SetColor(theme, "selection_color", Palette.AccentSelection, type);
            theme.SetStylebox("normal", type, Box(Palette.Input, Palette.Border, radius: 3, marginX: marginX, marginY: marginY));
            theme.SetStylebox("focus", type, FocusBox());
            theme.SetStylebox("read_only", type, Box(Palette.InputReadOnly, Palette.BorderSoft, radius: 3, marginX: marginX, marginY: marginY));
            theme.SetStylebox("disabled", type, Box(Palette.ControlDisabled, Palette.BorderSoft, radius: 3, marginX: marginX, marginY: marginY));
        }

        SetConstant(theme, "minimum_character_width", 8, "LineEdit", "SpinBox");
    }

    private static void ApplyTabs(Theme theme, int fontSize)
    {
        theme.SetStylebox("panel", "TabContainer", Box(Palette.SurfaceInset, Palette.Border, radius: 3,
            marginX: ScaleMetric(fontSize, 8), marginY: ScaleMetric(fontSize, 8)));
        int marginX = ScaleMetric(fontSize, 10);
        int marginY = ScaleMetric(fontSize, 5);
        theme.SetStylebox("tab_selected", "TabContainer", Box(Palette.ControlPressed, Palette.AccentSoft, radius: 3, marginX: marginX, marginY: marginY));
        theme.SetStylebox("tab_unselected", "TabContainer", Box(Palette.Control, Palette.BorderSoft, radius: 3, marginX: marginX, marginY: marginY));
        theme.SetStylebox("tab_hovered", "TabContainer", Box(Palette.ControlHover, Palette.BorderHover, radius: 3, marginX: marginX, marginY: marginY));

        foreach (string type in new[] { "TabContainer", "Tabs" })
        {
            SetColor(theme, "font_selected_color", Palette.Text, type);
            SetColor(theme, "font_unselected_color", Palette.TextMuted, type);
            SetColor(theme, "font_hovered_color", Palette.Text, type);
            SetColor(theme, "font_disabled_color", Palette.TextDisabled, type);
            SetConstant(theme, "h_separation", ScaleMetric(fontSize, 8), type);
        }
    }

    private static void ApplyLists(Theme theme, int fontSize)
    {
        int panelMargin = ScaleMetric(fontSize, 4);
        int rowMarginY = ScaleMetric(fontSize, 2);
        foreach (string type in new[] { "Tree", "ItemList" })
        {
            SetColor(theme, "font_color", Palette.Text, type);
            SetColor(theme, "font_selected_color", Palette.Text, type);
            SetColor(theme, "font_outline_color", Palette.Transparent, type);
            SetColor(theme, "guide_color", Palette.BorderSoft, type);
            SetColor(theme, "drop_position_color", Palette.Accent, type);

            theme.SetStylebox("panel", type, Box(Palette.SurfaceInset, Palette.Border, radius: 3, marginX: panelMargin, marginY: panelMargin));
            theme.SetStylebox("focus", type, FocusBox());
            theme.SetStylebox("selected", type, Box(Palette.Selection, Palette.Selection, radius: 2, marginX: panelMargin, marginY: rowMarginY));
            theme.SetStylebox("selected_focus", type, Box(Palette.SelectionFocus, Palette.AccentSoft, radius: 2, marginX: panelMargin, marginY: rowMarginY));
            theme.SetStylebox("cursor", type, Outline(Palette.Accent, radius: 2));
            theme.SetStylebox("cursor_unfocused", type, Outline(Palette.BorderHover, radius: 2));
        }

        SetConstant(theme, "v_separation", ScaleMetric(fontSize, 2), "Tree", "ItemList");
        SetConstant(theme, "h_separation", ScaleMetric(fontSize, 6), "Tree", "ItemList");
    }

    private static void ApplyScrollBars(Theme theme, int fontSize)
    {
        foreach (string type in new[] { "HScrollBar", "VScrollBar" })
        {
            theme.SetStylebox("scroll", type, Box(Palette.SurfaceInset, Palette.SurfaceInset, radius: 2, borderWidth: 0, marginX: 0, marginY: 0));
            theme.SetStylebox("scroll_focus", type, Empty());
            theme.SetStylebox("grabber", type, Box(Palette.ScrollGrabber, Palette.ScrollGrabber, radius: 3, borderWidth: 0, marginX: 0, marginY: 0));
            theme.SetStylebox("grabber_highlight", type, Box(Palette.ScrollGrabberHover, Palette.ScrollGrabberHover, radius: 3, borderWidth: 0, marginX: 0, marginY: 0));
            theme.SetStylebox("grabber_pressed", type, Box(Palette.ScrollGrabberPressed, Palette.ScrollGrabberPressed, radius: 3, borderWidth: 0, marginX: 0, marginY: 0));
            SetConstant(theme, "scroll_width", ScaleMetric(fontSize, 10), type);
        }
    }

    private static void ApplyTooltips(Theme theme, int fontSize)
    {
        theme.SetStylebox("panel", "TooltipPanel", Box(Palette.Tooltip, Palette.BorderHover, radius: 4,
            marginX: ScaleMetric(fontSize, 8), marginY: ScaleMetric(fontSize, 5)));
        SetColor(theme, "font_color", Palette.Text, "TooltipLabel");
        SetFontSize(theme, ScaleFontSize(fontSize, 12), "TooltipLabel");
    }

    private static void ApplyPopups(Theme theme, int fontSize)
    {
        StyleBoxFlat globalPopupMenuStyleBox = Box(Palette.SurfaceInset, Palette.Border, radius: 3, borderWidth: 2,
            marginX: 12, marginY: 10);
        theme.SetStylebox("panel", "PopupMenu", globalPopupMenuStyleBox);
        theme.SetStylebox("hover", "PopupMenu", Box(Palette.Selection, Palette.Selection, radius: 2,
            borderWidth: 0, marginX: 0, marginY: 0));
        var separator = new StyleBoxLine { Color = Palette.BorderSoft, Thickness = 1 };
        theme.SetStylebox("separator", "PopupMenu", separator);
        theme.SetStylebox("labeled_separator_left", "PopupMenu", separator);
        theme.SetStylebox("labeled_separator_right", "PopupMenu", separator);
        theme.SetColor("font_color", "PopupMenu", Palette.Text);
        theme.SetColor("font_hover_color", "PopupMenu", Palette.Text);
        theme.SetColor("font_disabled_color", "PopupMenu", Palette.TextMuted);
        theme.SetColor("font_accelerator_color", "PopupMenu", Palette.TextMuted);
        theme.SetColor("font_separator_color", "PopupMenu", Palette.TextMuted);
        theme.SetColor("font_outline_color", "PopupMenu", Palette.Transparent);
        theme.SetColor("font_separator_outline_color", "PopupMenu", Palette.Transparent);
        theme.SetFontSize("font_separator_size", "PopupMenu", ScaleFontSize(fontSize, 11));
        theme.SetConstant("v_separation", "PopupMenu", ScaleMetric(fontSize, 4));
        theme.SetConstant("h_separation", "PopupMenu", ScaleMetric(fontSize, 6));
        theme.SetConstant("item_start_padding", "PopupMenu", ScaleMetric(fontSize, 4));
        theme.SetConstant("item_end_padding", "PopupMenu", ScaleMetric(fontSize, 4));
        int margin = ScaleMetric(fontSize, 8);
        theme.SetStylebox("panel", "PopupPanel", Box(Palette.Surface, Palette.BorderHover, radius: 4, marginX: margin, marginY: margin));
        theme.SetStylebox("panel", "FileDialog", Box(Palette.Surface, Palette.BorderHover, radius: 4, marginX: margin, marginY: margin));
        SetColor(theme, "title_color", Palette.Text, "Window");
    }

    private static void SetColor(Theme theme, string name, Color color, params string[] types)
    {
        foreach (string type in types)
        {
            theme.SetColor(name, type, color);
        }
    }

    private static void SetConstant(Theme theme, string name, int value, params string[] types)
    {
        foreach (string type in types)
        {
            theme.SetConstant(name, type, value);
        }
    }

    private static void SetFontSize(Theme theme, int size, params string[] types)
    {
        foreach (string type in types)
        {
            theme.SetFontSize("font_size", type, size);
        }
    }

    private static int NormalizeFontSize(int fontSize) =>
        AgasUiAppearanceSettings.IsValidFontSize(fontSize)
            ? fontSize
            : AgasUiAppearanceSettings.DefaultFontSize;

    private static int ScaleMetric(int fontSize, int referenceSize) =>
        ScaleFontSize(fontSize, referenceSize);

    private static void ApplyScaledFontOverridesRecursive(Node node, int fontSize)
    {
        if (node is OptionButton optionButton)
        {
            AgasPopupMenuAppearance.Apply(optionButton.GetPopup());
        }
        else if (node is MenuButton menuButton)
        {
            AgasPopupMenuAppearance.Apply(menuButton.GetPopup());
        }
        else if (node is PopupMenu popupMenu)
        {
            AgasPopupMenuAppearance.Apply(popupMenu);
        }

        if (node is Control control && control.HasThemeFontSizeOverride("font_size"))
        {
            FontSizeReference reference = FontSizeReferences.GetValue(
                control,
                static value => new FontSizeReference(value.GetThemeFontSize("font_size")));
            control.AddThemeFontSizeOverride("font_size", ScaleFontSize(fontSize, reference.Size));
        }

        foreach (Node child in node.GetChildren())
        {
            ApplyScaledFontOverridesRecursive(child, fontSize);
        }
    }

    private static StyleBoxFlat Box(Color background, Color border, int radius, int borderWidth = 1, int marginX = 6, int marginY = 4)
    {
        var box = new StyleBoxFlat
        {
            BgColor = background,
            BorderColor = border,
            ContentMarginLeft = marginX,
            ContentMarginRight = marginX,
            ContentMarginTop = marginY,
            ContentMarginBottom = marginY
        };
        box.SetBorderWidthAll(borderWidth);
        box.SetCornerRadiusAll(radius);
        return box;
    }

    private static StyleBoxFlat Outline(Color border, int radius)
    {
        return Box(Palette.Transparent, border, radius, marginX: 0, marginY: 0);
    }

    private static StyleBoxFlat FocusBox()
    {
        return Outline(Palette.Focus, radius: 3);
    }

    private static StyleBoxEmpty Empty()
    {
        return new StyleBoxEmpty();
    }

    private static Color Rgb(byte r, byte g, byte b, byte a = 255)
    {
        return new Color(r / 255.0f, g / 255.0f, b / 255.0f, a / 255.0f);
    }

    private static class Palette
    {
        public static readonly Color Transparent = Rgb(0, 0, 0, 0);
        public static readonly Color Surface = Rgb(43, 45, 48);
        public static readonly Color SurfaceInset = Rgb(31, 33, 36);
        public static readonly Color Control = Rgb(55, 58, 63);
        public static readonly Color ControlHover = Rgb(66, 70, 77);
        public static readonly Color ControlPressed = Rgb(49, 56, 68);
        public static readonly Color ControlDisabled = Rgb(40, 42, 45);
        public static readonly Color Input = Rgb(38, 40, 44);
        public static readonly Color InputReadOnly = Rgb(34, 36, 39);
        public static readonly Color Border = Rgb(72, 76, 82);
        public static readonly Color BorderSoft = Rgb(58, 61, 66);
        public static readonly Color BorderHover = Rgb(92, 99, 112);
        public static readonly Color Text = Rgb(207, 211, 218);
        public static readonly Color TextMuted = Rgb(151, 157, 166);
        public static readonly Color TextDisabled = Rgb(105, 110, 119);
        public static readonly Color Accent = Rgb(53, 116, 240);
        public static readonly Color AccentSoft = Rgb(64, 135, 255);
        public static readonly Color AccentSelection = Rgb(53, 116, 240, 96);
        public static readonly Color Focus = Rgb(75, 141, 255);
        public static readonly Color Selection = Rgb(45, 91, 168);
        public static readonly Color SelectionFocus = Rgb(54, 103, 190);
        public static readonly Color ScrollGrabber = Rgb(83, 87, 95);
        public static readonly Color ScrollGrabberHover = Rgb(103, 109, 119);
        public static readonly Color ScrollGrabberPressed = Rgb(124, 132, 145);
        public static readonly Color Tooltip = Rgb(58, 50, 34);
    }
}
