using System;
using System.Runtime.CompilerServices;
using Godot;

namespace AppAgasCs.UI.Shared;

public static class AgasPopupMenuAppearance
{
    private static readonly ConditionalWeakTable<PopupMenu, object> ConfiguredMenus = new();

    public static void Apply(PopupMenu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        if (!ConfiguredMenus.TryGetValue(menu, out _))
        {
            ConfiguredMenus.Add(menu, new object());
            menu.AboutToPopup += () => SynchronizeMinimumSize(menu);
        }
        foreach (Node child in menu.GetChildren(includeInternal: true))
        {
            if (child is PopupMenu submenu) Apply(submenu);
            if (child is not PanelContainer) continue;
            foreach (Node panelChild in child.GetChildren(includeInternal: true))
            {
                if (panelChild is ScrollContainer scroll && !scroll.HasThemeStyleboxOverride("panel"))
                {
                    scroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
                }
            }
        }
    }

    public static void SynchronizeMinimumSize(PopupMenu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        int requestedWidth = menu.Size.X;
        menu.Notification((int)Window.NotificationThemeChanged);
        menu.MinSize = new Vector2I(menu.MinSize.X, 0);
        menu.ResetSize();
        if (!menu.ShrinkWidth && menu.Size.X < requestedWidth)
        {
            menu.Size = new Vector2I(requestedWidth, menu.Size.Y);
        }
    }
}
