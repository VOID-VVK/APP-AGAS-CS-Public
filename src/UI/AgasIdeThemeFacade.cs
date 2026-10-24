using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.UI.Theming;
using Godot;

namespace AGAS.UI;

/// <summary>
/// Compatibility facade for root-workspace callers. Feature and dialog code
/// must consume <see cref="AgasSharedIdeTheme"/> directly so dependencies stay
/// directed toward the shared theming layer.
/// </summary>
public static class AgasIdeTheme
{
    public static Color MutedTextColor => AgasSharedIdeTheme.MutedTextColor;
    public static Color ErrorTextColor => AgasSharedIdeTheme.ErrorTextColor;
    public static Color SuccessTextColor => AgasSharedIdeTheme.SuccessTextColor;

    public static Theme Create(int fontSize = AgasUiAppearanceSettings.DefaultFontSize) =>
        AgasSharedIdeTheme.Create(fontSize);

    public static int ScaleFontSize(int fontSize, int referenceSize) =>
        AgasSharedIdeTheme.ScaleFontSize(fontSize, referenceSize);

    public static void ApplyScaledFontOverrides(Node root, int fontSize) =>
        AgasSharedIdeTheme.ApplyScaledFontOverrides(root, fontSize);

    public static StyleBoxFlat PickerBackgroundStyle() => AgasSharedIdeTheme.PickerBackgroundStyle();

    public static StyleBoxFlat CardStyle() => AgasSharedIdeTheme.CardStyle();
}
