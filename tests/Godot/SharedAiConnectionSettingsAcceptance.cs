using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AGAS.UI.Dialogs;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Features.Ai.UI;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class SharedAiConnectionSettingsAcceptance : Node
{
    private const string Prefix = "[AGAS][SHARED-AI-CONNECTION]";

    public override async void _Ready()
    {
        await Run();
    }

    private async Task Run()
    {
        string home = Path.Combine(Path.GetTempPath(), "agas-shared-ai-connection", Guid.NewGuid().ToString("N"));
        string? previousHome = System.Environment.GetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable);
        string previousLocale = TranslationServer.GetLocale();
        try
        {
            System.Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, home);
            new AgasUiAppearanceSettingsStore(home).Save(AgasUiAppearanceSettings.Default with { UiLocale = "zh_CN" });
            TranslationServer.SetLocale("zh_CN");
            const string firstId = "primary-connection";
            const string secondId = "backup-connection";
            const string firstUrl = "https://primary.example/v1/";
            const string secondUrl = "https://backup.example/v1/";
            const string firstKey = "synthetic-primary-key";
            const string secondKey = "synthetic-backup-key";
            const string initialModel = "gpt-image-2.5-flare";
            var first = new AgasAiProfile(firstId, "主连接", "legacy-provider", "legacy-model", firstUrl);
            var second = new AgasAiProfile(secondId, "备用连接", "other-legacy-provider", "other-model", secondUrl);
            var store = new AgasAiSettingsStore(home);
            _ = AgasAiNewApiSetup.EnsureProfile(store, AgasAiProviderCatalog.Load(home));
            store.SaveProfiles(new AgasAiProfilesDocument(
                AgasAiProfilesDocument.CurrentResourceVersion,
                firstId,
                initialModel,
                new[] { first, second }));
            store.SetCredential(firstId, firstKey);
            store.SetCredential(secondId, secondKey);

            var dialog = new SettingsDialog(store);
            AddChild(dialog);
            dialog.Open(this);

            ItemList connections = Required<ItemList>(dialog, "AiProfileList");
            LineEdit name = Required<LineEdit>(dialog, "AiProfileName");
            LineEdit baseUrl = Required<LineEdit>(dialog, "AiBaseUrl");
            LineEdit key = Required<LineEdit>(dialog, "AiApiKey");
            Label keyHint = Required<Label>(dialog, "AiApiKeyMaskedHint");
            Button save = Required<Button>(dialog, "SaveAiProfile");

            Require(dialog.FindChild("AiProvider", true, false) == null, "Settings must not expose a provider selector.");
            Require(dialog.FindChild("AiProviderField", true, false) == null, "Settings must not expose a provider field.");
            Require(dialog.FindChild("AiModelPreset", true, false) == null, "Settings must not expose a model selector.");
            Require(dialog.FindChild("AiModelName", true, false) == null, "Settings must not expose a custom model field.");
            Require(connections.ItemCount == 2 && connections.GetItemText(0).Contains("当前", StringComparison.Ordinal),
                "The active connection must be visibly identified in the connection list.");
            Require(baseUrl.Text == firstUrl && string.IsNullOrEmpty(key.Text),
                "The active connection URL must load while its saved key remains masked.");
            Require(keyHint.Text.Contains('*') && !keyHint.Text.Contains(firstKey, StringComparison.Ordinal),
                "The saved key hint must be masked.");

            connections.Select(1);
            connections.EmitSignal(ItemList.SignalName.ItemSelected, 1L);
            AgasAiProfilesDocument afterConnectionSwitch = store.LoadProfiles();
            Require(afterConnectionSwitch.ActiveProfileId == secondId, "Clicking a connection must activate it.");
            Require(afterConnectionSwitch.ActiveModelName == initialModel, "Switching connections must preserve the global model.");
            Require(connections.GetItemText(1).Contains("当前", StringComparison.Ordinal),
                "The newly active connection must receive the visible active marker.");
            Require(name.Text == "备用连接" && baseUrl.Text == secondUrl,
                "The activated connection must populate only name and URL fields.");

            const string renamed = "备用连接 B";
            const string changedUrl = "https://backup-new.example/v1/";
            const string changedKey = "synthetic-backup-new-key";
            name.Text = renamed;
            name.EmitSignal(LineEdit.SignalName.TextChanged, name.Text);
            baseUrl.Text = changedUrl;
            baseUrl.EmitSignal(LineEdit.SignalName.TextChanged, baseUrl.Text);
            key.Text = changedKey;
            key.EmitSignal(LineEdit.SignalName.TextChanged, key.Text);
            save.EmitSignal(Button.SignalName.Pressed);
            AgasAiProfilesDocument afterSave = store.LoadProfiles();
            AgasAiProfile savedSecond = afterSave.Profiles.Single(item => item.Id == secondId);
            Require(savedSecond.Name == renamed && savedSecond.BaseUrl == changedUrl,
                "Saving must update only the selected connection name and URL.");
            Require(afterSave.ActiveModelName == initialModel, "Saving a connection must preserve the global model.");
            Require(store.GetCredential(secondId) == changedKey, "Saving must persist the selected connection key.");
            Require(store.GetCredential(firstId) == firstKey, "Saving one connection must not change another connection key.");

            using var discovered = new ImageModelsAcceptanceFixture();
            discovered.Prime(first with { ApiKey = firstKey });
            var panel = discovered.Panel(store);
            AddChild(panel);
            for (int frame = 0; frame < 300 && panel.ProfileOptions.Count == 0; frame++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            OptionButton models = Required<OptionButton>(panel, "AiGenerationModelPreset");
            OptionButton categories = Required<OptionButton>(panel, "AiGenerationProvider");
            Require(categories.ItemCount == 4 && panel.ProfileOptions.Count == 20,
                "The current connection must expose all discovered models in four independent categories.");
            Require(models.ItemCount == 3, "The selected GPT category must contain only its three models.");

            const string nextModel = "jimeng-image-5.0-lite";
            Require(panel.SelectModelByName(nextModel), "Selecting a model must also select its category.");
            Require(models.ItemCount == 12 && categories.GetItemMetadata(categories.Selected).AsString() == "jimeng",
                "Jimeng must have a separate twelve-model category.");
            AgasAiProfilesDocument afterModelSwitch = store.LoadProfiles();
            Require(afterModelSwitch.ActiveModelName == nextModel, "Selecting a model must update the global model.");
            Require(afterModelSwitch.ActiveProfileId == secondId, "Selecting a model must preserve the active connection.");
            Require(afterModelSwitch.Profiles.Single(item => item.Id == secondId).BaseUrl == changedUrl,
                "Selecting a model must preserve the active connection URL.");
            Require(store.GetCredential(secondId) == changedKey,
                "Selecting a model must preserve the active connection key.");

            var explicitFirst = panel.ResolveProviderProfile(firstId);
            Require(explicitFirst.Success && explicitFirst.Profile != null, "An explicit connection id must resolve.");
            Require(explicitFirst.Profile!.Id == firstId && explicitFirst.Profile.ModelName == nextModel,
                "An explicit connection must still use the global model.");
            Require(explicitFirst.Profile.AdapterKind == AgasAiUnifiedModelCatalog.ConnectionAdapterKind,
                "Every resolved model must use the unified New API adapter.");
            Require(explicitFirst.Profile.BaseUri == new Uri(firstUrl) && explicitFirst.Profile.ApiKey == firstKey,
                "An explicit connection must retain its own URL and key.");

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

    private static int FindModelIndex(OptionButton options, string modelName)
    {
        for (int index = 0; index < options.ItemCount; index++)
        {
            if (string.Equals(options.GetItemMetadata(index).AsString(), modelName, StringComparison.Ordinal))
                return index;
        }
        return -1;
    }

    private static T Required<T>(Node root, string name) where T : Node =>
        root.FindChild(name, recursive: true, owned: false) as T
        ?? throw new InvalidOperationException($"Required node is missing: {name}");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class NeverRunAiGenerationRunner : IAiGenerationRunner
    {
        public Task<AiGenerationResult> GenerateAsync(
            AiGenerationProviderProfile profile,
            AiGenerationRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Generation is outside this settings acceptance.");
    }
}
