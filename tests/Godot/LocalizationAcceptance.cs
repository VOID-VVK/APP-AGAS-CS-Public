#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AGAS.UI;
using AGAS.UI.Dialogs;
using AppAgasCs.Application.Contracts;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Features.Ai.UI;
using AppAgasCs.UI.Shared;
using Godot;

public partial class LocalizationAcceptance : Node
{
    private const string Prefix = "[AGAS][LOCALIZATION]";
    private const string UserText = "设置";
    private readonly List<Node> _fixtures = new();
    private string _home = string.Empty;
    private int _checks;

    public override async void _Ready()
    {
        int exitCode = 0;
        string? previousHome = System.Environment.GetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable);
        string previousLocale = TranslationServer.GetLocale();
        try
        {
            Require(!string.IsNullOrWhiteSpace(previousHome), "Run via tests/Godot/run-acceptance.sh with isolated AGAS_HOME.");
            _home = Path.GetFullPath(Path.Combine(previousHome!, "localization-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(_home);
            System.Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, _home);
            GetTree().Root.Size = new Vector2I(1600, 1000);
            GD.Print($"{Prefix} OS locale={Godot.OS.GetLocale()}; clean startup additionally exercises an inherited zh_CN engine locale");
            VerifyNativeResources();
            await VerifyApplicationAsync();
            await VerifyConnectionStatusArgumentsAsync();
            await VerifyRawContentAsync();
            await VerifyDynamicCountAsync();
            GD.Print($"{Prefix}[PASS] {_checks} checks; native resources, persistence, state, raw content, layout geometry");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] after {_checks} checks: {exception}");
        }
        finally
        {
            foreach (Node fixture in _fixtures)
                if (IsInstanceValid(fixture)) fixture.Free();
            TranslationServer.SetLocale(previousLocale);
            System.Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, previousHome);
            if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
            GetTree().Quit(exitCode);
        }
    }

    private void VerifyNativeResources()
    {
        string[] paths = ProjectSettings.GetSetting("internationalization/locale/translations").AsStringArray();
        Require(paths.Length >= 2, "project.godot must register both native translation resources.");
        var locales = new HashSet<string>(TranslationServer.GetLoadedLocales(), StringComparer.Ordinal);
        Require(locales.Contains("en") && locales.Contains("zh_CN"), "Both locales must already be loaded by Godot, without test AddTranslation calls.");
        using Godot.FileAccess catalog = Godot.FileAccess.Open("res://Localization/ui.csv", Godot.FileAccess.ModeFlags.Read)
            ?? throw new InvalidOperationException("Missing source catalog.");
        string[] header = catalog.GetCsvLine();
        var keys = new List<string>();
        while (!catalog.EofReached())
        {
            string[] row = catalog.GetCsvLine();
            if (row.Length == 1 && string.IsNullOrWhiteSpace(row[0])) continue;
            Require(row.Length == header.Length && row.All(value => !string.IsNullOrWhiteSpace(value)), "Catalog rows must provide a key and both translations.");
            keys.Add(row[0]);
        }
        Require(keys.Count > 50 && keys.Distinct(StringComparer.Ordinal).Count() == keys.Count, "Catalog must contain unique, nonempty UI keys.");
        foreach (string path in paths)
        {
            Translation? translation = ResourceLoader.Load<Translation>(path);
            Require(translation != null, $"Native ResourceLoader failed: {path}");
            foreach (string key in keys)
            {
                string value = translation!.GetMessage(key).ToString();
                Require(!string.IsNullOrWhiteSpace(value), $"Native translation is missing a source key in {path}: {key}");
                string[] sourceArguments = Regex.Matches(key, @"\{\d+\}").Select(match => match.Value).OrderBy(value => value).ToArray();
                string[] translatedArguments = Regex.Matches(value, @"\{\d+\}").Select(match => match.Value).OrderBy(value => value).ToArray();
                Require(sourceArguments.SequenceEqual(translatedArguments), $"Placeholder mismatch in {path}: {key} -> {value}");
            }
            GD.Print($"{Prefix} native resource: {path} ({translation!.Locale}, {keys.Count} verified source keys, {translation.GetType().Name})");
        }
        AppLocale.Apply("en");
        Require(AppLocale.Translate(UserText) == "Settings", "The exact user-data key must have a real English translation.");
        AppLocale.Apply("zh_CN");
        Require(AppLocale.Translate(UserText) == UserText, "The Chinese native catalog must resolve the original key.");
    }

    private async Task VerifyApplicationAsync()
    {
        var store = new AgasUiAppearanceSettingsStore(_home);
        TranslationServer.SetLocale("zh_CN");
        Main main = AddFixture(new Main(new RecentProjectsStore(Path.Combine(_home, "recent.tres")), disableMcpRuntimeForTesting: true));
        await Frames(4);
        Require(AppLocale.Current == "en" && store.Load().UiLocale == "en", "A clean startup must override an inherited Chinese engine locale with English.");
        Button newProject = Required<Button>(main, "NewProjectButton");
        Require(Rendered(newProject) == AppLocale.Translate("新建项目") && Rendered(newProject) != "新建项目", "The initial project picker must display native English text.");
        string missingDirectory = Path.Combine(_home, "missing-project");
        Required<FileDialog>(main, "ProjectDirectoryDialog").EmitSignal(FileDialog.SignalName.DirSelected, missingDirectory);
        await Frames(2);
        Label pickerStatus = Required<Label>(main, "ProjectStatusLabel");
        Require(Rendered(pickerStatus) == AppLocale.Format("目录不存在，已移除历史记录：{0}", missingDirectory),
            $"Missing-directory validation must translate its application-owned template, keeping the path raw: {Rendered(pickerStatus)}");
        Required<Button>(main, "ProjectPickerSettingsButton").EmitSignal(Button.SignalName.Pressed);
        await Frames(4);
        SettingsDialog settings = Descendants(main).OfType<SettingsDialog>().Single();
        ItemList categories = Required<ItemList>(settings, "SettingsCategories");
        OptionButton language = Required<OptionButton>(settings, "UiLanguage");
        ItemList connections = Required<ItemList>(settings, "AiProfileList");
        connections.Select(0);
        connections.EmitSignal(ItemList.SignalName.ItemSelected, 0L);
        await Frames(2);
        string connectionName = new AgasAiSettingsStore(_home).LoadProfiles().Profiles.Single(profile => profile.Id == settings.ActiveProfileId).Name;
        Label connectionStatus = Required<Label>(settings, "AiSettingsStatus");
        Require(connectionStatus.Text == AppLocale.Format("已启用连接：{0}", connectionName),
            "Connection activation status must translate its template, not an already-interpolated Chinese sentence.");
        LineEdit draftName = Required<LineEdit>(settings, "AiProfileName");
        LineEdit draftEndpoint = Required<LineEdit>(settings, "AiBaseUrl");
        LineEdit draftKey = Required<LineEdit>(settings, "AiApiKey");
        draftName.Text = UserText;
        draftEndpoint.Text = "https://draft.invalid/v1/";
        draftKey.Text = UserText;
        foreach (LineEdit editor in new[] { draftName, draftEndpoint, draftKey })
            editor.EmitSignal(LineEdit.SignalName.TextChanged, editor.Text);
        await Frames(2);
        byte[] profilesBefore = File.ReadAllBytes(new AgasAiSettingsStore(_home).ProfilesPath);
        int draftEvents = 0;
        foreach (LineEdit editor in new[] { draftName, draftEndpoint, draftKey })
            editor.TextChanged += _ => draftEvents++;
        Require(!settings.StageMcpAiMutation("localization-draft"), "Unsaved settings drafts must block MCP mutation before switching languages.");
        categories.Select(1);
        categories.EmitSignal(ItemList.SignalName.ItemSelected, 1L);
        await Frames(3);
        ulong mainId = main.GetInstanceId();
        ulong settingsId = settings.GetInstanceId();
        ulong buttonId = newProject.GetInstanceId();
        Vector2I dialogSize = settings.Size;
        SpinBox font = Required<SpinBox>(settings, "UiFontSize");
        double fontSize = font.Value;
        int appearanceEvents = 0;
        int profileEvents = 0;
        settings.AppearanceFontSizeChanged += _ => appearanceEvents++;
        settings.ActiveProfileChanged += _ => profileEvents++;
        for (int index = 0; index < 2; index++)
        {
            string locale = index == 0 ? "zh_CN" : "en";
            SelectLanguage(language, locale);
            await Frames(4);
            Require(AppLocale.Current == locale && store.Load().UiLocale == locale, "Settings selection must immediately apply and persist " + locale);
            Require(Rendered(newProject) == AppLocale.Translate("新建项目"), "Existing picker text must update without rebuilding Main.");
            Require(Rendered(pickerStatus) == AppLocale.Format("目录不存在，已移除历史记录：{0}", missingDirectory)
                && connectionStatus.Text == AppLocale.Format("已启用连接：{0}", connectionName),
                "Existing application-owned validation/status messages must retranslate on locale changes with raw path/name arguments.");
            Require(categories.AutoTranslateMode != Node.AutoTranslateModeEnum.Disabled
                && categories.GetItemAutoTranslateMode(1) != Node.AutoTranslateModeEnum.Disabled
                && AppLocale.Translate(categories.GetItemText(1)) == (locale == "en" ? "Appearance" : "外观"),
                "Static settings category source keys must resolve through native automatic translation.");
            Require(main.GetInstanceId() == mainId && settings.GetInstanceId() == settingsId && newProject.GetInstanceId() == buttonId,
                "Language switches must preserve existing scene, settings and controls.");
            Require(settings.Visible && categories.GetSelectedItems().SequenceEqual(new[] { 1 }) && settings.Size == dialogSize && font.Value == fontSize,
                "The open settings page, size, selection and font value must survive translation changes.");
            Require(appearanceEvents == 0 && profileEvents == 0, "Locale changes must not emit font/profile mutations.");
            Require(draftName.Text == UserText && draftEndpoint.Text == "https://draft.invalid/v1/" && draftKey.Text == UserText && draftEvents == 0,
                "Language selection must preserve all unsaved connection drafts without TextChanged events.");
            Require(new[] { draftName, draftEndpoint, draftKey }.All(editor => editor.AutoTranslateMode == Node.AutoTranslateModeEnum.Disabled),
                "Unsaved connection values matching catalog keys must remain untranslatable.");
            Require(!settings.StageMcpAiMutation("localization-draft") && profilesBefore.SequenceEqual(File.ReadAllBytes(new AgasAiSettingsStore(_home).ProfilesPath)),
                "Language selection must not clear draft guards or save profile/credential edits.");
            VerifyLayout(newProject, locale);
            VerifyLayout(language, locale);
            VerifyLayout(Required<Button>(settings, "RestoreDefaultAppearance"), locale);
        }
        categories.Select(2);
        categories.EmitSignal(ItemList.SignalName.ItemSelected, 2L);
        settings.UpdateMcpState(McpSettingsStatus.Running, true, UserText);
        await Frames(2);
        Label mcpStatus = Required<Label>(settings, "McpStatusValue");
        Label mcpError = Required<Label>(settings, "McpLastErrorValue");
        string statusEnglish = Rendered(mcpStatus);
        AppLocale.Apply("zh_CN");
        await Frames(3);
        Require(Rendered(mcpStatus) != statusEnglish && !Rendered(mcpStatus).Contains("{0}", StringComparison.Ordinal), "MCP state labels must translate immediately.");
        Require(mcpError.Text == UserText && mcpError.AutoTranslateMode == Node.AutoTranslateModeEnum.Disabled,
            "External MCP error text equal to a catalog key must remain raw.");
        settings.Hide();
        MenuBar menuBar = Required<MenuBar>(main, "AppMenuBar");
        menuBar.PreferGlobalMenu = false;
        PopupMenu menu = menuBar.GetChildren().OfType<PopupMenu>().Single();
        menu.Popup(new Rect2I(20, 20, 300, 120));
        await Frames(2);
        Require(menu.Visible, "The existing app popup must open in the headless embedded-window backend.");
        ulong menuId = menu.GetInstanceId();
        AppLocale.Apply("en");
        await Frames(3);
        Require(menu.Visible && menu.GetInstanceId() == menuId, "An open app popup must remain open with its original identity.");
        Require(menuBar.GetMenuTitle(0) == AppLocale.Translate("设置"), "The explicitly translated existing menu title must refresh immediately.");
        Require(AppLocale.Translate(menu.GetItemText(0)).Contains("Settings", StringComparison.Ordinal), "The app menu must resolve English settings text.");
        menu.Hide();
        SelectLanguage(language, "zh_CN");
        await Frames(2);
        main.Free();
        Main reloaded = AddFixture(new Main(new RecentProjectsStore(Path.Combine(_home, "recent.tres")), disableMcpRuntimeForTesting: true));
        await Frames(4);
        Require(AppLocale.Current == "zh_CN" && Rendered(Required<Button>(reloaded, "NewProjectButton")) == "新建项目",
            "Recreating the application must load the locale selected through Settings.");
        reloaded.Free();
        File.WriteAllText(store.SettingsPath, $"{{\"resourceVersion\":{AgasUiAppearanceSettings.CurrentResourceVersion},\"fontSize\":35,\"uiLocale\":\"invalid-locale\"}}");
        Main invalid = AddFixture(new Main(new RecentProjectsStore(Path.Combine(_home, "recent.tres")), disableMcpRuntimeForTesting: true));
        await Frames(3);
        Require(AppLocale.Current == "en" && store.Load().UiLocale == "en", "Unsupported persisted locales must safely normalize to English.");
        invalid.Free();
    }

    private async Task VerifyConnectionStatusArgumentsAsync()
    {
        string home = Path.Combine(_home, "connection-status-arguments");
        var store = new AgasAiSettingsStore(home);
        AgasAiProfile keyProfile = AgasAiProfile.CreateDefault() with
        {
            Id = "localization-key-profile",
            Name = UserText,
            BaseUrl = "https://fixture.invalid/v1/",
        };
        AgasAiProfile braceProfile = keyProfile with
        {
            Id = "localization-brace-profile",
            Name = UserText + " {0} {name} {unfinished",
        };
        AgasAiProfile[] profiles = { keyProfile, braceProfile };
        store.SaveProfiles(new AgasAiProfilesDocument(AgasAiProfilesDocument.CurrentResourceVersion, keyProfile.Id, profiles));
        var appearance = new AgasUiAppearanceSettingsStore(home);
        SettingsDialog dialog = AddFixture(new SettingsDialog(store, appearance));
        dialog.Open(this);
        await Frames(3);
        ItemList connections = Required<ItemList>(dialog, "AiProfileList");
        OptionButton language = Required<OptionButton>(dialog, "UiLanguage");
        Label status = Required<Label>(dialog, "AiSettingsStatus");
        LineEdit name = Required<LineEdit>(dialog, "AiProfileName");
        ulong statusId = status.GetInstanceId();
        ulong listId = connections.GetInstanceId();
        for (int profileIndex = 0; profileIndex < profiles.Length; profileIndex++)
        {
            connections.Select(profileIndex);
            connections.EmitSignal(ItemList.SignalName.ItemSelected, (long)profileIndex);
            await Frames(3);
            string profileName = profiles[profileIndex].Name;
            byte[] profilesBefore = File.ReadAllBytes(store.ProfilesPath);
            int profileEvents = 0;
            int inputEvents = 0;
            void ProfileChanged(string _) => profileEvents++;
            void InputChanged(string _) => inputEvents++;
            dialog.ActiveProfileChanged += ProfileChanged;
            name.TextChanged += InputChanged;
            foreach (string locale in new[] { "en", "zh_CN", "en", "zh_CN" })
            {
                SelectLanguage(language, locale);
                await Frames(3);
                Require(status.Text == AppLocale.Format("已启用连接：{0}", profileName),
                    $"Activated connection status must translate the template once with the raw profile name: {locale}, {profileName}, actual={status.Text}.");
                Require(status.Text.Contains(profileName, StringComparison.Ordinal) && name.Text == profileName,
                    "Profile names equal to translation keys or containing matched/unmatched braces must remain verbatim.");
                Require(connections.GetItemText(profileIndex) == AppLocale.Format("{0}  当前", profileName),
                    "The active connection marker must translate without interpreting its name as a translation key or format template.");
                Require(status.GetInstanceId() == statusId && connections.GetInstanceId() == listId
                    && connections.GetSelectedItems().SequenceEqual(new[] { profileIndex }) && dialog.ActiveProfileId == profiles[profileIndex].Id,
                    "Existing status, connection list, active profile and selection must survive live locale changes.");
                Require(profileEvents == 0 && inputEvents == 0 && profilesBefore.SequenceEqual(File.ReadAllBytes(store.ProfilesPath)),
                    "Language changes must not reactivate connections, emit name input changes, or save profile fields.");
                Require(appearance.Load().UiLocale == locale, "The real language selector must persist each locale while preserving raw profile arguments.");
            }
            dialog.ActiveProfileChanged -= ProfileChanged;
            name.TextChanged -= InputChanged;
        }
        dialog.Free();
        await Frames(2);
    }

    private async Task VerifyRawContentAsync()
    {
        var layer = new AgasLayerResource { Id = "localization-layer", Name = UserText };
        var fragment = new PromptFragmentResource { Id = "localization-fragment", Text = UserText, Enabled = true };
        layer.AddPromptFragment(fragment);
        PromptPanel prompt = AddFixture(new PromptPanel { Size = new Vector2(1500, 400) });
        prompt.SetLayer(layer);
        var runner = new ForbiddenRunner();
        AiGenerationPanel ai = AddFixture(new AiGenerationPanel(new AgasAiSettingsStore(_home), runner) { Size = new Vector2(700, 600) });
        ai.SetPromptText(UserText);
        PropertiesPanel properties = AddFixture(new PropertiesPanel { Size = new Vector2(700, 700) });
        properties.ShowSceneReadme(UserText);
        TextInputDialog input = AddFixture(new TextInputDialog());
        int confirms = 0;
        input.Open(this, "重命名图层", UserText, "图层名称", _ => confirms++);
        string filesRoot = Path.Combine(_home, "fixtures");
        Directory.CreateDirectory(Path.Combine(filesRoot, UserText));
        Directory.CreateDirectory(Path.Combine(filesRoot, "other"));
        using (Image image = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8))
        {
            image.Fill(Colors.White);
            Require(image.SavePng(Path.Combine(filesRoot, UserText, "image.png")) == Error.Ok, "Fixture image must be written.");
            Require(image.SavePng(Path.Combine(filesRoot, "other", "image.png")) == Error.Ok, "Second fixture image must be written.");
        }
        ProjectFilesTree files = AddFixture(new ProjectFilesTree { Size = new Vector2(400, 500) });
        files.LoadProject(filesRoot);
        FilesScenesPanel scenes = AddFixture(new FilesScenesPanel { Size = new Vector2(400, 500) });
        var project = new AgasProjectResource();
        project.AddScene(new SceneIndexResource { Id = "first", Name = UserText });
        scenes.OpenProject(filesRoot, project, string.Empty);
        await Frames(4);
        TextEdit fragmentEditor = Descendants(prompt).OfType<TextEdit>().Single(editor => editor.Name != "PromptMergedPreview");
        TextEdit preview = Required<TextEdit>(prompt, "PromptMergedPreview");
        TextEdit aiPrompt = Required<TextEdit>(ai, "AiGenerationPrompt");
        TextEdit readme = Required<TextEdit>(properties, "SceneReadmeEdit");
        LineEdit rename = Required<LineEdit>(input, "TextInput");
        LineEdit model = Required<LineEdit>(ai, "AiGenerationModelName");
        model.Text = UserText;
        ai.ReportExternalJobStatus(new AiGenerationExternalJobStatus(UserText, AiGenerationExternalJobState.Running, UserText));
        Label aiStatus = Required<Label>(ai, "AiGenerationStatus");
        Require(preview.Text.StartsWith(UserText + "\n", StringComparison.Ordinal), "The composed preview must preserve the original fragment before its canonical task suffix.");
        preview.Text = UserText;
        await Frames(2);
        int textEvents = 0;
        int promptMutations = 0;
        int readmeMutations = 0;
        int aiStatusEvents = 0;
        int aiProfileEvents = 0;
        foreach (TextEdit editor in new[] { fragmentEditor, preview, aiPrompt, readme })
            editor.TextChanged += () => textEvents++;
        rename.TextChanged += _ => textEvents++;
        model.TextChanged += _ => textEvents++;
        ai.StatusChanged += (_, _) => aiStatusEvents++;
        ai.ProfilesChanged += () => aiProfileEvents++;
        prompt.PromptTextUpdateRequested += (_, _) => promptMutations++;
        properties.SceneReadmeChanged += _ => readmeMutations++;
        ulong fragmentEditorId = fragmentEditor.GetInstanceId();
        fragmentEditor.SetCaretColumn(1);
        TreeItem folder = TreeItems(files.GetRoot()).Single(item => item.GetText(0) == UserText);
        folder.Select(0);
        ulong folderId = folder.GetInstanceId();
        Dictionary<string, byte[]> before = SnapshotFiles();
        foreach (string locale in new[] { "zh_CN", "en", "zh_CN", "en" })
        {
            AppLocale.Apply(locale);
            await Frames(3);
            foreach (TextEdit editor in new[] { fragmentEditor, preview, aiPrompt, readme })
                Require(editor.Text == UserText && editor.AutoTranslateMode == Node.AutoTranslateModeEnum.Disabled,
                    $"User text must not be translated or replaced: {editor.Name} ({locale}).");
            Require(rename.Text == UserText && rename.AutoTranslateMode == Node.AutoTranslateModeEnum.Disabled && input.Visible,
                "An open rename dialog must preserve its raw value and visibility.");
            Require(model.Text == UserText && model.AutoTranslateMode == Node.AutoTranslateModeEnum.Disabled,
                "An unsaved model name matching a translation key must remain raw.");
            Require(aiStatus.Text == AppLocale.Format("MCP AI {0}… {1}{2}", UserText, AppLocale.Translate("生成中"), AppLocale.Format("：{0}", UserText)),
                "Existing semantic AI state must translate while job identifiers and external details stay raw.");
            Require(fragmentEditor.GetInstanceId() == fragmentEditorId && fragmentEditor.GetCaretColumn() == 1,
                "Fragment editing state and node identity must not reset on locale changes.");
            Require(folder.GetInstanceId() == folderId && folder.GetText(0) == UserText && folder.GetAutoTranslateMode(0) == Node.AutoTranslateModeEnum.Disabled && files.GetSelected() == folder,
                "A selected filename/folder equal to a Chinese catalog key must remain raw and selected.");
            ItemList list = Required<ItemList>(scenes, "ScenesList");
            Require(list.GetItemText(0) == UserText && list.GetItemAutoTranslateMode(0) == Node.AutoTranslateModeEnum.Disabled,
                "User scene names must stay raw even when they exactly match a translation key.");
            Require(textEvents == 0 && promptMutations == 0 && readmeMutations == 0 && confirms == 0,
                "Translation notifications must not trigger TextChanged, prompt/readme persistence or dialog confirmation.");
            Require(runner.RequestCount == 0 && ai.ActiveGenerationCount == 0 && !ai.IsPreparingGeneration,
                "Changing language must never start an AI generation.");
            Require(aiStatusEvents == 0 && aiProfileEvents == 0, "Translating AI presentation must not re-emit status/profile events.");
            VerifyLayout(Required<Button>(prompt, "AddPromptFragmentButton"), locale);
            VerifyLayout(Required<Label>(prompt, "PromptMergedPreviewTitle"), locale);
        }
        Dictionary<string, byte[]> after = SnapshotFiles();
        Require(before.Keys.OrderBy(path => path).SequenceEqual(after.Keys.OrderBy(path => path)) && before.All(entry => entry.Value.SequenceEqual(after[entry.Key])),
            "Translation-only changes must not save or rewrite any fixture/config files.");
        input.Hide();
        foreach (Node fixture in new Node[] { prompt, ai, properties, input, files, scenes }) fixture.Free();
        await Frames(2);
    }

    private async Task VerifyDynamicCountAsync()
    {
        var coordinator = new AgasAiGenerationCoordinator();
        Require(coordinator.TryAcquire(out IAgasAiGenerationPermit? permit) && permit != null, "A synthetic permit must be acquired without an AI request.");
        using (permit)
        {
            MainWorkspace workspace = AddFixture(new MainWorkspace(coordinator) { Size = new Vector2(1600, 900) });
            await Frames(4);
            Label count = Required<Label>(workspace, "AiGenerationTaskCountLabel");
            ulong countId = count.GetInstanceId();
            AppLocale.Apply("zh_CN");
            await Frames(3);
            Require(Rendered(count) == "任务 5/1", "Chinese dynamic count must preserve both placeholder arguments.");
            AppLocale.Apply("en");
            await Frames(3);
            Require(count.GetInstanceId() == countId && Rendered(count) == AppLocale.Format("任务 {0}/{1}", 5, 1) && Rendered(count) != "任务 5/1",
                "An existing task count must translate immediately while preserving the live count and node identity.");
            Require(!workspace.HasUnsavedChanges && coordinator.ActiveCount == 1, "Locale refresh must not dirty the workspace or change active generation permits.");
            permit!.Dispose();
            await Frames(3);
            Require(Rendered(count) == AppLocale.Format("任务 {0}/{1}", 5, 0), "Permit release must still refresh the localized dynamic count.");
            workspace.Free();
        }
    }

    private void SelectLanguage(OptionButton language, string locale)
    {
        int selected = language.GetItemIndex(locale == "zh_CN" ? 1 : 0);
        Require(selected >= 0, "Settings must offer English (id 0) and Simplified Chinese (id 1).");
        language.Select(selected);
        language.EmitSignal(OptionButton.SignalName.ItemSelected, (long)selected);
    }

    private void VerifyLayout(Control control, string locale)
    {
        string text = control switch { Button button => Rendered(button), Label label => Rendered(label), _ => string.Empty };
        Font font = control.GetThemeFont("font");
        int fontSize = control.GetThemeFontSize("font_size");
        float textWidth = font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X;
        Require(control.Size.X >= textWidth - 1 && control.Size.Y >= font.GetHeight(fontSize) - 2,
            $"Obvious text clipping at {control.Name}/{locale}: text={textWidth}, size={control.Size}.");
    }

    private Dictionary<string, byte[]> SnapshotFiles() => Directory.GetFiles(_home, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(_home, path), File.ReadAllBytes, StringComparer.Ordinal);

    private T AddFixture<T>(T fixture) where T : Node
    {
        _fixtures.Add(fixture);
        AddChild(fixture);
        return fixture;
    }

    private static string Rendered(Button button) => button.AutoTranslateMode == Node.AutoTranslateModeEnum.Disabled ? button.Text : button.Tr(button.Text).ToString();
    private static string Rendered(Label label) => label.AutoTranslateMode == Node.AutoTranslateModeEnum.Disabled ? label.Text : label.Tr(label.Text).ToString();

    private static IEnumerable<Node> Descendants(Node root)
    {
        foreach (Node child in root.GetChildren())
        {
            yield return child;
            foreach (Node descendant in Descendants(child)) yield return descendant;
        }
    }

    private static IEnumerable<TreeItem> TreeItems(TreeItem? root)
    {
        if (root == null) yield break;
        yield return root;
        for (TreeItem? child = root.GetFirstChild(); child != null; child = child.GetNext())
            foreach (TreeItem descendant in TreeItems(child)) yield return descendant;
    }

    private static T Required<T>(Node root, string name) where T : Node =>
        root.FindChild(name, recursive: true, owned: false) as T ?? throw new InvalidOperationException($"Missing {name} ({typeof(T).Name}).");

    private async Task Frames(int count)
    {
        for (int frame = 0; frame < count; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private void Require(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ForbiddenRunner : IAiGenerationRunner
    {
        public int RequestCount { get; private set; }

        public Task<AiGenerationResult> GenerateAsync(AiGenerationProviderProfile profile, AiGenerationRequest request, CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("Localization acceptance forbids AI/model requests.");
        }
    }
}
