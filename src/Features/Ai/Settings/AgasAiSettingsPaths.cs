using System;
using System.IO;

namespace AppAgasCs.Features.Ai.Settings;

public static class AgasAiSettingsPaths
{
    public const string HomeEnvironmentVariable = "AGAS_HOME";
    public const string DefaultDirectoryName = ".agas";
    public const string ProfilesFileName = "image-generation-profiles.json";
    public const string UiAppearanceFileName = "ui-appearance.json";
    public const string ProvidersDirectoryName = "providers";
    public const string AdaptersDirectoryName = "ai-adapters";

    public static string ResolveHome()
    {
        string? configuredHome = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredHome))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredHome.Trim()));
        }

        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userHome))
        {
            throw new InvalidOperationException("无法确定当前用户目录；请设置 AGAS_HOME。");
        }

        return Path.Combine(userHome, DefaultDirectoryName);
    }

    public static string ProfilesFile(string home) => Path.Combine(home, ProfilesFileName);

    public static string ProvidersDirectory(string home) => Path.Combine(home, ProvidersDirectoryName);

    public static string AdaptersDirectory(string home) => Path.Combine(home, AdaptersDirectoryName);

    public static string UiAppearanceFile(string home) => Path.Combine(home, UiAppearanceFileName);
}
