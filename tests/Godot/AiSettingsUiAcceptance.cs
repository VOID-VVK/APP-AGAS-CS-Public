using System;
using System.IO;
using AGAS.UI.Dialogs;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Runtime;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class AiSettingsUiAcceptance : Node
{
    private const string Prefix = "[AGAS][AI-SETTINGS-UI]";

    public override void _Ready()
    {
        Run();
    }

    private void Run()
    {
        string home = Path.Combine(Path.GetTempPath(), "agas-ai-settings-ui", Guid.NewGuid().ToString("N"));
        string? previousHome = System.Environment.GetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable);
        string previousLocale = TranslationServer.GetLocale();
        try
        {
            System.Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, home);
            new AgasUiAppearanceSettingsStore(home).Save(AgasUiAppearanceSettings.Default with { UiLocale = "zh_CN" });
            TranslationServer.SetLocale("zh_CN");
            var dialog = new SettingsDialog();
            int appliedFontSize = 0;
            dialog.AppearanceFontSizeChanged += value => appliedFontSize = value;
            AddChild(dialog);
            dialog.Open(this);

            Require(dialog.MinSize.X <= 1100 && dialog.MinSize.Y <= 700
                && dialog.MinSize.X > 0 && dialog.MinSize.Y > 0,
                "Settings minimum size must remain usable and fit within the available viewport.");
            Require(!dialog.Unresizable, "Settings dialog must remain resizable.");
            RequireNode<ScrollContainer>(dialog, "AiProfileDetailsScroll");

            var store = new AgasAiSettingsStore(home);
            Button add = RequireNode<Button>(dialog, "AddAiProfile");
            Button save = RequireNode<Button>(dialog, "SaveAiProfile");
            ItemList profiles = RequireNode<ItemList>(dialog, "AiProfileList");
            ItemList categories = RequireNode<ItemList>(dialog, "SettingsCategories");
            LineEdit name = RequireNode<LineEdit>(dialog, "AiProfileName");
            LineEdit baseUrl = RequireNode<LineEdit>(dialog, "AiBaseUrl");
            LineEdit key = RequireNode<LineEdit>(dialog, "AiApiKey");
            Label credentialLabel = RequireNode<Label>(dialog, "AiCredentialLabel");
            Label credentialHint = RequireNode<Label>(dialog, "AiApiKeyMaskedHint");

            Require(store.LoadProfiles().Profiles.Count == 1,
                "First launch must create one New API connection without requiring Add.");
            Require(key.Secret && credentialLabel.Text == "API Key" && key.PlaceholderText == "输入 API Key",
                "The only credential field must be a masked API Key editor.");
            Require(profiles.CustomMinimumSize.X >= 280, "Connection list must be wide enough for names.");
            Require(profiles.GetItemText(0).Contains("当前", StringComparison.Ordinal),
                "The active connection must be clearly marked.");
            Require(dialog.FindChild("AiProvider", true, false) == null
                && dialog.FindChild("AiProviderField", true, false) == null,
                "Settings must not expose provider selection.");
            Require(dialog.FindChild("AiModelPreset", true, false) == null
                && dialog.FindChild("AiModelName", true, false) == null,
                "Settings must not expose model selection or custom model input.");

            name.Text = "Acceptance Connection";
            name.EmitSignal(LineEdit.SignalName.TextChanged, name.Text);
            baseUrl.Text = "https://relay.example/v1/";
            baseUrl.EmitSignal(LineEdit.SignalName.TextChanged, baseUrl.Text);
            const string secret = "ui-acceptance-secret";
            key.Text = secret;
            key.EmitSignal(LineEdit.SignalName.TextChanged, secret);
            Require(!dialog.StageMcpAiMutation("dirty-draft-workspace"),
                "Any name, URL, or credential draft must block MCP settings mutation.");
            save.EmitSignal(Button.SignalName.Pressed);

            AgasAiProfilesDocument document = store.LoadProfiles();
            AgasAiProfile profile = document.Profiles.Count == 1
                ? document.Profiles[0]
                : throw new InvalidOperationException("Expected one saved connection.");
            Require(profile.Name == "Acceptance Connection" && profile.BaseUrl == "https://relay.example/v1/",
                "Connection name and URL were not saved.");
            Require(store.HasCredential(profile.Id), "Credential was not saved on the connection.");
            Require(credentialHint.Text.Contains('*') && !credentialHint.Text.Contains(secret, StringComparison.Ordinal),
                "Saved credential hint must be masked and must not expose the complete key.");
            Require(string.IsNullOrEmpty(key.Text), "Saved credentials must not be copied back into the secret input.");

            byte[] profileBytesBeforeMaskedSave = File.ReadAllBytes(store.ProfilesPath);
            save.EmitSignal(Button.SignalName.Pressed);
            Require(profileBytesBeforeMaskedSave.AsSpan().SequenceEqual(File.ReadAllBytes(store.ProfilesPath)),
                "Saving an unchanged credential mask must preserve the profile file.");

            const string replacementSecret = "replacement-credential-789";
            key.Text = replacementSecret;
            key.EmitSignal(LineEdit.SignalName.TextChanged, replacementSecret);
            save.EmitSignal(Button.SignalName.Pressed);
            Require(store.GetCredential(profile.Id) == replacementSecret, "Entering a new credential must replace the saved key.");
            Require(!credentialHint.Text.Contains(replacementSecret, StringComparison.Ordinal),
                "Replacement credential must only be shown as a mask.");

            byte[] profilesBeforeAppearance = File.ReadAllBytes(store.ProfilesPath);
            categories.Select(1);
            categories.EmitSignal(ItemList.SignalName.ItemSelected, 1L);
            SpinBox fontSize = RequireNode<SpinBox>(dialog, "UiFontSize");
            Require(fontSize.MinValue == 11 && !fontSize.AllowLesser,
                "Font size editor must allow 11px but reject smaller values.");
            fontSize.Value = 11;
            Require(new AgasUiAppearanceSettingsStore(home).Load().FontSize == 11,
                "The minimum 11px font size must save immediately.");
            Require(appliedFontSize == 11 && dialog.Theme?.DefaultFontSize == 11,
                "The minimum 11px font size must apply immediately to Settings.");
            fontSize.Value = 10;
            Require(fontSize.Value == 11
                && new AgasUiAppearanceSettingsStore(home).Load().FontSize == 11,
                "Values below 11px must clamp to the minimum without persisting an invalid value.");
            fontSize.Value = 42;
            Require(new AgasUiAppearanceSettingsStore(home).Load().FontSize == 42,
                "Font size changes must save immediately.");
            Require(appliedFontSize == 42 && dialog.Theme?.DefaultFontSize == 42,
                "Font size changes must apply immediately to Settings.");
            Require(RequireNode<VBoxContainer>(dialog, "AppearanceSettingsPage").Visible
                && !RequireNode<VBoxContainer>(dialog, "AiSettingsPage").Visible,
                "Appearance category visibility is incorrect.");
            Require(profilesBeforeAppearance.AsSpan().SequenceEqual(File.ReadAllBytes(store.ProfilesPath)),
                "Appearance changes must not alter the connection file.");
            RequireNode<Button>(dialog, "RestoreDefaultAppearance").EmitSignal(Button.SignalName.Pressed);
            Require(new AgasUiAppearanceSettingsStore(home).Load().FontSize == AgasUiAppearanceSettings.DefaultFontSize,
                "Restore default must persist the default font size.");
            categories.Select(0);
            categories.EmitSignal(ItemList.SignalName.ItemSelected, 0L);

            add.EmitSignal(Button.SignalName.Pressed);
            Require(profiles.ItemCount == 2, "Second connection was not added.");
            string activeBeforeModelChange = store.LoadProfiles().ActiveProfileId
                ?? throw new InvalidOperationException("Added connection should be active.");
            Require(dialog.StageMcpAiMutation("ui-acceptance-workspace"),
                "A clean SettingsDialog must issue an MCP mutation stage.");
            Require(add.Disabled && save.Disabled && !name.Editable && !baseUrl.Editable && !key.Editable,
                "An active MCP mutation lease must disable connection controls.");
            var settingsFacade = new AgasMcpAiSettingsFacade(
                new AgasAiProfileSettingsService(store),
                dialog.McpAiMutationBridge,
                new AgasMcpDirtyState(),
                dialog.CaptureEffectiveAiSelection);
            const string mcpModel = "gpt-image-2.5-sunburst";
            AgasMcpToolResponse backgroundMutation = settingsFacade.SetModel(
                new AgasAiModelSetInput(mcpModel),
                new AgasMcpCommitExecution(AgasMcpCommitPolicy.ShortAtomicCommit));
            Require(backgroundMutation.Ok
                && backgroundMutation.Data is AgasMcpAiModelSetDataDto { UiSynchronized: false },
                "Background model commit must remain pending until the main-thread UI stage.");
            AgasMcpToolResponse finalizedMutation = settingsFacade.FinalizeStagedUiOnMainThread(backgroundMutation);
            dialog.FinishMcpAiMutation();
            Require(finalizedMutation.Data is AgasMcpAiModelSetDataDto { UiSynchronized: true },
                "Main-thread postcommit must synchronize Settings.");
            document = store.LoadProfiles();
            Require(document.ActiveModelName == mcpModel && document.ActiveProfileId == activeBeforeModelChange,
                "A global model change must preserve the active connection.");
            Require(!add.Disabled && !save.Disabled && name.Editable && baseUrl.Editable && key.Editable,
                "Postcommit completion must restore connection editing controls.");

            profiles.EmitSignal(ItemList.SignalName.ItemSelected, 0L);
            Require(store.LoadProfiles().ActiveProfileId == profile.Id,
                "Selecting a connection must persist ActiveProfileId.");
            Require(store.LoadProfiles().ActiveModelName == mcpModel,
                "Selecting a connection must preserve ActiveModelName.");

            Button clearKey = RequireNode<Button>(dialog, "ClearAiCredential");
            clearKey.EmitSignal(Button.SignalName.Pressed);
            ConfirmationDialog clearConfirmation = RequireNode<ConfirmationDialog>(dialog, "ConfirmClearAiCredential");
            Require(clearConfirmation.Title == "清除 API Key"
                && clearConfirmation.DialogText.Contains("API Key", StringComparison.Ordinal),
                "Credential confirmation must use API Key terminology.");
            clearConfirmation.EmitSignal(AcceptDialog.SignalName.Confirmed);
            clearConfirmation.Hide();
            Require(!store.HasCredential(profile.Id), "Confirmed clear must remove the saved credential.");

            profiles.EmitSignal(ItemList.SignalName.ItemSelected, 1L);
            string deletedId = store.LoadProfiles().ActiveProfileId
                ?? throw new InvalidOperationException("Second connection should be active.");
            Require(deletedId != profile.Id, "Second connection should have a stable distinct ID.");
            RequireNode<Button>(dialog, "DeleteAiProfile").EmitSignal(Button.SignalName.Pressed);
            ConfirmationDialog deleteConfirmation = RequireNode<ConfirmationDialog>(dialog, "ConfirmDeleteAiProfile");
            deleteConfirmation.EmitSignal(AcceptDialog.SignalName.Confirmed);
            AgasAiProfilesDocument afterDelete = store.LoadProfiles();
            Require(afterDelete.Profiles.Count == 1 && afterDelete.Profiles[0].Id == profile.Id,
                "Confirmed delete must remove only the selected connection.");
            Require(afterDelete.ActiveModelName == mcpModel, "Deleting a connection must preserve the global model.");

            GD.Print($"{Prefix}[PASS]");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GD.PushError($"{Prefix}[FAIL] {exception}");
            GetTree().Quit(1);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, previousHome);
            TranslationServer.SetLocale(previousLocale);
            try
            {
                Directory.Delete(home, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private static T RequireNode<T>(Node root, string name) where T : Node =>
        root.FindChild(name, recursive: true, owned: false) as T
        ?? throw new InvalidOperationException($"Required settings control is missing: {name}");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
