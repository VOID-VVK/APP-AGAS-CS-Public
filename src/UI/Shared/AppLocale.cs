using System.Globalization;
using AppAgasCs.Features.Ai.Settings;
using Godot;

namespace AppAgasCs.UI.Shared;

public static class AppLocale
{
    public static string Current => TranslationServer.GetLocale();

    public static string Apply(string? locale)
    {
        string normalized = AgasUiAppearanceSettings.NormalizeLocale(locale);
        TranslationServer.SetLocale(normalized);
        return normalized;
    }

    public static string Translate(string message) => TranslationServer.Translate(message).ToString();

    public static string Format(string template, params object[] arguments) =>
        string.Format(CultureInfo.InvariantCulture, Translate(template), arguments);
}
