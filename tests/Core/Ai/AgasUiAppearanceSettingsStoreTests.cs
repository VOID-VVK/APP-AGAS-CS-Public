using AGAS.UI;
using AppAgasCs.Features.Ai.Settings;
using Xunit;

namespace AppAgasCs.Tests.Core.Ai;

public sealed class AgasUiAppearanceSettingsStoreTests
{
    [Fact]
    public void MissingSettings_UsesCurrentReducedScaleDefault()
    {
        using var home = new TemporaryHome();
        var store = new AgasUiAppearanceSettingsStore(home.Path);

        AgasUiAppearanceSettings loaded = store.Load();

        Assert.Equal(35, loaded.FontSize);
        Assert.Equal(AgasUiAppearanceSettings.CurrentResourceVersion, loaded.ResourceVersion);
        Assert.Equal("en", loaded.UiLocale);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh_CN")]
    public void Locale_RoundTripsAlongsideFontSizeWithoutChangingCredentials(string locale)
    {
        using var home = new TemporaryHome();
        var store = new AgasUiAppearanceSettingsStore(home.Path);
        var aiStore = new AgasAiSettingsStore(home.Path);
        var profile = new AgasAiProfile("profile_01", "Settings", AgasAiProviderIds.OpenAiCompatible,
            "model-name", "https://relay.example/v1/", "untouched-credential");
        aiStore.SaveProfiles(new AgasAiProfilesDocument(3, profile.Id, new[] { profile }));
        byte[] profilesBefore = File.ReadAllBytes(aiStore.ProfilesPath);

        store.Save(new AgasUiAppearanceSettings(AgasUiAppearanceSettings.CurrentResourceVersion, 48, locale));

        Assert.Equal(locale, store.Load().UiLocale);
        Assert.Equal(48, store.Load().FontSize);
        Assert.Equal(profilesBefore, File.ReadAllBytes(aiStore.ProfilesPath));
        Assert.Contains($"\"uiLocale\": \"{locale}\"", File.ReadAllText(store.SettingsPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("zh")]
    [InlineData("zh-CN")]
    [InlineData("fr")]
    public void InvalidLocale_LoadsEnglishWithoutChangingValidFontSize(string? locale)
    {
        using var home = new TemporaryHome();
        var store = new AgasUiAppearanceSettingsStore(home.Path);
        string localeJson = System.Text.Json.JsonSerializer.Serialize(locale);
        File.WriteAllText(store.SettingsPath, $"{{\"resourceVersion\":3,\"fontSize\":48,\"uiLocale\":{localeJson}}}");

        Assert.Equal("en", store.Load().UiLocale);
        Assert.Equal(48, store.Load().FontSize);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void NonStringLocale_LoadsEnglishWithoutChangingValidFontSize(string localeJson)
    {
        using var home = new TemporaryHome();
        var store = new AgasUiAppearanceSettingsStore(home.Path);
        File.WriteAllText(store.SettingsPath, $"{{\"resourceVersion\":3,\"fontSize\":48,\"uiLocale\":{localeJson}}}");

        Assert.Equal("en", store.Load().UiLocale);
        Assert.Equal(48, store.Load().FontSize);
    }

    [Fact]
    public void ExistingPreferencesWithoutLocale_KeepFontAndDefaultToEnglish()
    {
        using var home = new TemporaryHome();
        var store = new AgasUiAppearanceSettingsStore(home.Path);
        File.WriteAllText(store.SettingsPath, "{\"resourceVersion\":3,\"fontSize\":48}");

        AgasUiAppearanceSettings loaded = store.Load();
        Assert.Equal("en", loaded.UiLocale);
        Assert.Equal(48, loaded.FontSize);
        store.Save(loaded with { UiLocale = "zh_CN" });
        store.Save(store.Load() with { FontSize = 11 });
        Assert.Equal("zh_CN", store.Load().UiLocale);
        Assert.Equal(11, store.Load().FontSize);
    }

    [Fact]
    public void SavingInvalidLocale_NormalizesToEnglish()
    {
        using var home = new TemporaryHome();
        var store = new AgasUiAppearanceSettingsStore(home.Path);
        store.Save(new AgasUiAppearanceSettings(AgasUiAppearanceSettings.CurrentResourceVersion, 48, "invalid"));

        Assert.Equal("en", store.Load().UiLocale);
        Assert.Equal(48, store.Load().FontSize);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(35)]
    [InlineData(48)]
    [InlineData(96)]
    [InlineData(int.MaxValue)]
    public void FontSize_RoundTripsWithoutAnArtificialUpperLimit(int fontSize)
    {
        using var home = new TemporaryHome();
        var store = new AgasUiAppearanceSettingsStore(home.Path);

        store.Save(new AgasUiAppearanceSettings(AgasUiAppearanceSettings.CurrentResourceVersion, fontSize));

        Assert.Equal(fontSize, store.Load().FontSize);
        Assert.Equal(Path.Combine(home.Path, "ui-appearance.json"), store.SettingsPath);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"resourceVersion\":99,\"fontSize\":18}")]
    [InlineData("{\"resourceVersion\":3,\"fontSize\":10}")]
    [InlineData("{\"resourceVersion\":3,\"fontSize\":\"large\"}")]
    [InlineData("{\"resourceVersion\":3,\"fontSize\":2147483648}")]
    public void InvalidOrDamagedSettings_FallBackSafely(string contents)
    {
        using var home = new TemporaryHome();
        var store = new AgasUiAppearanceSettingsStore(home.Path);
        File.WriteAllText(store.SettingsPath, contents);

        Assert.Equal(AgasUiAppearanceSettings.Default, store.Load());
    }

    [Theory]
    [InlineData(10)]
    [InlineData(int.MinValue)]
    public void Save_RejectsFontSizeBelowMinimum(int fontSize)
    {
        using var home = new TemporaryHome();
        var store = new AgasUiAppearanceSettingsStore(home.Path);

        Assert.Throws<ArgumentOutOfRangeException>(() => store.Save(
            new AgasUiAppearanceSettings(AgasUiAppearanceSettings.CurrentResourceVersion, fontSize)));
    }

    [Theory]
    [InlineData(10, 35)]
    [InlineData(11, 30)]
    [InlineData(13, 35)]
    [InlineData(18, 49)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void LegacySettings_AreMigratedToReducedScale(int legacyFontSize, int expectedFontSize)
    {
        using var home = new TemporaryHome();
        var store = new AgasUiAppearanceSettingsStore(home.Path);
        File.WriteAllText(store.SettingsPath,
            $"{{\"resourceVersion\":1,\"fontSize\":{legacyFontSize}}}");

        AgasUiAppearanceSettings loaded = store.Load();

        Assert.Equal(AgasUiAppearanceSettings.CurrentResourceVersion, loaded.ResourceVersion);
        Assert.Equal(expectedFontSize, loaded.FontSize);
    }

    [Theory]
    [InlineData(32, 35)]
    [InlineData(33, 30)]
    [InlineData(39, 35)]
    [InlineData(48, 43)]
    [InlineData(int.MaxValue, 1932735282)]
    public void TripleScaleSettings_AreReducedByTenPercent(int previousFontSize, int expectedFontSize)
    {
        using var home = new TemporaryHome();
        var store = new AgasUiAppearanceSettingsStore(home.Path);
        File.WriteAllText(store.SettingsPath,
            $"{{\"resourceVersion\":2,\"fontSize\":{previousFontSize}}}");

        AgasUiAppearanceSettings loaded = store.Load();

        Assert.Equal(AgasUiAppearanceSettings.CurrentResourceVersion, loaded.ResourceVersion);
        Assert.Equal(expectedFontSize, loaded.FontSize);
    }

    [Fact]
    public void ThemeScaling_UsesSafeDoubleMathWithoutOverflowOrNegativeResults()
    {
        Assert.Equal(74, AgasIdeTheme.ScaleFontSize(48, 20));
        Assert.Equal(int.MaxValue, AgasIdeTheme.ScaleFontSize(int.MaxValue, int.MaxValue));
        Assert.Equal(1, AgasIdeTheme.ScaleFontSize(48, -20));
    }

    [Fact]
    public void SavingAppearance_DoesNotReadOrModifyCredentialBytes()
    {
        using var home = new TemporaryHome();
        var aiStore = new AgasAiSettingsStore(home.Path);
        var profile = new AgasAiProfile(
            "profile_01",
            "Relay",
            AgasAiProviderIds.OpenAiCompatible,
            "gpt-image-1",
            "https://relay.example/v1/",
            "credential-must-remain-byte-identical");
        aiStore.SaveProfiles(new AgasAiProfilesDocument(3, profile.Id, new[] { profile }));
        byte[] profilesBefore = File.ReadAllBytes(aiStore.ProfilesPath);

        var appearanceStore = new AgasUiAppearanceSettingsStore(home.Path);
        appearanceStore.Save(new AgasUiAppearanceSettings(
            AgasUiAppearanceSettings.CurrentResourceVersion,
            35));

        Assert.Equal(profilesBefore, File.ReadAllBytes(aiStore.ProfilesPath));
    }

    private sealed class TemporaryHome : IDisposable
    {
        public TemporaryHome()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "agas-ui-appearance-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
