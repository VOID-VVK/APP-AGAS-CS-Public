using System;
using System.IO;
using System.Text.Json;

namespace AppAgasCs.Features.Ai.Settings;

public sealed record AgasUiAppearanceSettings(int ResourceVersion, int FontSize, string UiLocale = "en")
{
    public const string DefaultLocale = "en";
    public const string SimplifiedChineseLocale = "zh_CN";
    public const int CurrentResourceVersion = 3;
    public const int LegacyResourceVersion = 1;
    public const int TripleScaleResourceVersion = 2;
    public const int LegacyDefaultFontSize = 13;
    public const int LegacyMinimumFontSize = 11;
    public const double LegacyFontScale = 2.7;
    public const double TripleScaleReduction = 0.9;
    public const int DefaultFontSize = 35;
    public const int MinimumFontSize = 11;

    public static AgasUiAppearanceSettings Default { get; } =
        new(CurrentResourceVersion, DefaultFontSize);

    public static bool IsValidFontSize(int fontSize) =>
        fontSize >= MinimumFontSize;

    public static string NormalizeLocale(string? locale) =>
        string.Equals(locale, SimplifiedChineseLocale, StringComparison.Ordinal)
            ? SimplifiedChineseLocale
            : DefaultLocale;

    public static int MigrateLegacyFontSize(int fontSize)
    {
        if (fontSize < LegacyMinimumFontSize)
        {
            return DefaultFontSize;
        }

        return ScaleAndClamp(fontSize, LegacyFontScale);
    }

    public static int MigrateTripleScaleFontSize(int fontSize)
    {
        if (fontSize < LegacyMinimumFontSize * 3)
        {
            return DefaultFontSize;
        }

        return ScaleAndClamp(fontSize, TripleScaleReduction);
    }

    private static int ScaleAndClamp(int fontSize, double scale)
    {
        double scaled = fontSize * scale;
        return scaled >= int.MaxValue
            ? int.MaxValue
            : (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// Stores non-sensitive UI preferences independently from AI profiles and credentials.
/// Invalid or damaged preference files fail closed to the built-in appearance defaults.
/// </summary>
public sealed class AgasUiAppearanceSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly object _gate = new();

    public AgasUiAppearanceSettingsStore(string? homeDirectory = null)
    {
        HomeDirectory = Path.GetFullPath(homeDirectory ?? AgasAiSettingsPaths.ResolveHome());
    }

    public string HomeDirectory { get; }
    public string SettingsPath => AgasAiSettingsPaths.UiAppearanceFile(HomeDirectory);

    public AgasUiAppearanceSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(SettingsPath))
            {
                return AgasUiAppearanceSettings.Default;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                JsonElement root = document.RootElement;
                string locale = root.TryGetProperty("uiLocale", out JsonElement localeValue)
                    && localeValue.ValueKind == JsonValueKind.String
                        ? AgasUiAppearanceSettings.NormalizeLocale(localeValue.GetString())
                        : AgasUiAppearanceSettings.DefaultLocale;
                var settings = new AgasUiAppearanceSettings(
                    root.GetProperty("resourceVersion").GetInt32(),
                    root.GetProperty("fontSize").GetInt32(),
                    locale);
                if (settings.ResourceVersion == AgasUiAppearanceSettings.LegacyResourceVersion)
                {
                    return new AgasUiAppearanceSettings(
                        AgasUiAppearanceSettings.CurrentResourceVersion,
                        AgasUiAppearanceSettings.MigrateLegacyFontSize(settings.FontSize),
                        AgasUiAppearanceSettings.NormalizeLocale(settings.UiLocale));
                }
                if (settings.ResourceVersion == AgasUiAppearanceSettings.TripleScaleResourceVersion)
                {
                    return new AgasUiAppearanceSettings(
                        AgasUiAppearanceSettings.CurrentResourceVersion,
                        AgasUiAppearanceSettings.MigrateTripleScaleFontSize(settings.FontSize),
                        AgasUiAppearanceSettings.NormalizeLocale(settings.UiLocale));
                }
                return settings.ResourceVersion == AgasUiAppearanceSettings.CurrentResourceVersion
                    && AgasUiAppearanceSettings.IsValidFontSize(settings.FontSize)
                        ? settings with { UiLocale = AgasUiAppearanceSettings.NormalizeLocale(settings.UiLocale) }
                        : AgasUiAppearanceSettings.Default;
            }
            catch
            {
                return AgasUiAppearanceSettings.Default;
            }
        }
    }

    public void Save(AgasUiAppearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.ResourceVersion != AgasUiAppearanceSettings.CurrentResourceVersion)
        {
            throw new ArgumentException("UI 外观设置版本无效。", nameof(settings));
        }
        if (!AgasUiAppearanceSettings.IsValidFontSize(settings.FontSize))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), $"全局字体大小不能小于 {AgasUiAppearanceSettings.MinimumFontSize}px。");
        }
        settings = settings with { UiLocale = AgasUiAppearanceSettings.NormalizeLocale(settings.UiLocale) };

        lock (_gate)
        {
            Directory.CreateDirectory(HomeDirectory);
            string tempPath = Path.Combine(
                HomeDirectory,
                $".{AgasAiSettingsPaths.UiAppearanceFileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(tempPath, JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions));
                File.Move(tempPath, SettingsPath, overwrite: true);
            }
            catch
            {
                TryDelete(tempPath);
                throw new IOException("UI 外观设置保存失败。");
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup for a non-sensitive preference file.
        }
    }
}
