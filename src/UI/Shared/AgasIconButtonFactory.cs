using System;
using Godot;

namespace AppAgasCs.UI.Shared;

internal static class AgasIconButtonFactory
{
    public const int ControlSize = 48;
    public const int IconSize = 32;

    public static Button Create(Texture2D icon, string tooltip, Action pressed, bool disabled = false)
    {
        var button = new Button
        {
            Icon = icon,
            Text = string.Empty,
            TooltipText = tooltip,
            AccessibilityName = tooltip,
            FocusMode = Control.FocusModeEnum.All,
            Disabled = disabled,
            CustomMinimumSize = new Vector2(ControlSize, ControlSize),
            ExpandIcon = false,
            IconAlignment = HorizontalAlignment.Center,
        };
        ApplyFlatStyle(button);
        button.Pressed += pressed;
        return button;
    }

    private static void ApplyFlatStyle(Button button)
    {
        button.AddThemeStyleboxOverride("normal", Style(new Color(0, 0, 0, 0)));
        button.AddThemeStyleboxOverride("hover", Style(new Color(0.26f, 0.28f, 0.31f, 1f)));
        button.AddThemeStyleboxOverride("pressed", Style(new Color(0.20f, 0.23f, 0.28f, 1f)));
        button.AddThemeStyleboxOverride("disabled", Style(new Color(0, 0, 0, 0)));
    }

    private static StyleBoxFlat Style(Color background)
    {
        var style = new StyleBoxFlat
        {
            BgColor = background,
            ContentMarginLeft = 4,
            ContentMarginRight = 4,
            ContentMarginTop = 4,
            ContentMarginBottom = 4,
        };
        style.SetCornerRadiusAll(3);
        return style;
    }
}
