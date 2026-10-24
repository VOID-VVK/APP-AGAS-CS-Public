using Godot;

namespace AppAgasCs.UI.Shared;

public static class AgasMarginContainerFactory
{
    public static MarginContainer Create(
        int left,
        int top,
        int right,
        int bottom,
        string? name = null)
    {
        var margin = new MarginContainer();
        if (!string.IsNullOrWhiteSpace(name))
        {
            margin.Name = name;
        }

        margin.AddThemeConstantOverride("margin_left", left);
        margin.AddThemeConstantOverride("margin_top", top);
        margin.AddThemeConstantOverride("margin_right", right);
        margin.AddThemeConstantOverride("margin_bottom", bottom);
        return margin;
    }
}
