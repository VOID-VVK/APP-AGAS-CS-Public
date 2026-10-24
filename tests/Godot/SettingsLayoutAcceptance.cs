using System;
using System.IO;
using System.Threading.Tasks;
using AGAS.UI;
using AGAS.UI.Dialogs;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.UI.Theming;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class SettingsLayoutAcceptance : Node
{
    private const string Prefix = "[AGAS][SETTINGS-LAYOUT]";

    public override async void _Ready()
    {
        string home = Path.Combine(Path.GetTempPath(), "agas-settings-layout-" + Guid.NewGuid().ToString("N"));
        string? previousHome = System.Environment.GetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable);
        string previousLocale = TranslationServer.GetLocale();
        string locale = System.Environment.GetEnvironmentVariable("AGAS_ACCEPTANCE_LOCALE") ?? "en";
        try
        {
            Require(locale is "en" or "zh_CN", "Layout fixture locale must be explicitly en or zh_CN.");
            System.Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, home);
            var appearanceStore = new AgasUiAppearanceSettingsStore(home);
            appearanceStore.Save(AgasUiAppearanceSettings.Default with { UiLocale = locale });
            TranslationServer.SetLocale(locale);
            GD.Print($"{Prefix} validation locale={locale}");
            GetWindow().GuiEmbedSubwindows = true;
            GetWindow().Size = new Vector2I(1920, 1080);
            await Frames(3);
            var store = new AgasAiSettingsStore(home);
            var dialog = new SettingsDialog(store, appearanceStore)
            {
                Theme = AgasSharedIdeTheme.Create(AgasUiAppearanceSettings.DefaultFontSize),
            };
            AddChild(dialog);
            AgasSharedIdeTheme.ApplyScaledFontOverrides(dialog, AgasUiAppearanceSettings.DefaultFontSize);
            Vector2I requestedPopupSize = Vector2I.Zero;
            void CapturePopupSize() => requestedPopupSize = requestedPopupSize.Max(dialog.Size);
            dialog.SizeChanged += CapturePopupSize;
            dialog.Open(this);
            Require(TranslationServer.GetLocale() == locale, "Settings must retain the explicitly persisted fixture locale.");
            dialog.SizeChanged -= CapturePopupSize;
            Require(requestedPopupSize == new Vector2I(1600, 1000), "Settings must request the larger default window size.");
            if (DisplayServer.GetName() == "headless") dialog.Size = requestedPopupSize;
            await Frames(8);
            GD.Print($"{Prefix} viewport={GetWindow().GetVisibleRect().Size}, default={dialog.Size}, minimum={dialog.MinSize}");
            Require(dialog.Size == new Vector2I(1600, 1000), "Settings must default to the larger window size.");
            Require(dialog.MinSize == new Vector2I(1100, 700), "Settings must keep a usable minimum size.");

            dialog.Size = new Vector2I(1100, 700);
            await Frames(8);
            Label status = Required<Label>(dialog, "AiSettingsStatus");
            ScrollContainer scroll = Required<ScrollContainer>(dialog, "AiProfileDetailsScroll");
            Button save = Required<Button>(dialog, "SaveAiProfile");
            GD.Print($"{Prefix} narrow scroll={scroll.Size}, status={status.Size}, save={save.Size}");
            Require(status.Size.X >= scroll.Size.X - 40,
                "The connection explanation must not be squeezed beside footer buttons.");
            foreach (string buttonName in new[] { "ClearAiCredential", "DeleteAiProfile", "SaveAiProfile" })
            {
                Button button = Required<Button>(dialog, buttonName);
                Require(button.Size.Y <= button.GetCombinedMinimumSize().Y + 2,
                    $"{buttonName} must keep its natural height in a narrow window.");
            }
            Require(scroll.Size.Y >= 180, "The connection form must retain usable scroll space.");
            Require(save.GetGlobalRect().End.Y <= dialog.Size.Y - dialog.GetOkButton().Size.Y,
                "Connection actions must remain above the dialog close button.");
            Require(Required<HFlowContainer>(dialog, "AiProfileActions").GetLineCount() > 1,
                "Narrow windows must wrap connection actions instead of stretching them.");
            Require(status.GetParent() == Required<VBoxContainer>(dialog, "AiProfileDetails"),
                "The full explanation must scroll with the form instead of consuming footer space.");
            float narrowScrollHeight = scroll.Size.Y;
            status.Text = new string('错', 600);
            await Frames(8);
            Require(Math.Abs(scroll.Size.Y - narrowScrollHeight) <= 2,
                "Long connection errors must not grow the footer or shrink the form.");

            dialog.Size = new Vector2I(1600, 1000);
            await Frames(8);
            Require(Required<HFlowContainer>(dialog, "AiProfileActions").GetLineCount() == 1,
                "Wide windows must keep the original single-row action layout.");
            await VerifyMcpPage(dialog, locale);

            dialog.Hide();
            GetWindow().Size = new Vector2I(960, 640);
            await Frames(3);
            dialog.Open(this);
            await Frames(8);
            GD.Print($"{Prefix} small-screen={dialog.Size}, minimum={dialog.MinSize}, scroll={scroll.Size}");
            Require(dialog.Size == new Vector2I(912, 592) && dialog.MinSize == dialog.Size,
                "Default and minimum sizes must fit smaller parent viewports with a margin.");
            Require(scroll.Size.Y >= 100, "Smaller screens must retain a usable scrolling form.");
            Require(save.GetGlobalRect().End.X <= dialog.Size.X && save.GetGlobalRect().End.Y < dialog.Size.Y,
                "Connection actions must not overflow a small screen.");
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
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }

    private async Task VerifyMcpPage(SettingsDialog dialog, string locale)
    {
        string startingText = locale == "zh_CN" ? "启动中" : "Starting";
        string runningText = locale == "zh_CN" ? "运行中" : "Running";
        string stoppingText = locale == "zh_CN" ? "停止中" : "Stopping";
        ItemList categories = Required<ItemList>(dialog, "SettingsCategories");
        categories.Select(2);
        categories.EmitSignal(ItemList.SignalName.ItemSelected, 2L);
        await Frames(3);
        VBoxContainer page = Required<VBoxContainer>(dialog, "McpSettingsPage");
        CheckBox enabled = Required<CheckBox>(page, "McpEnabledSwitch");
        LineEdit endpoint = Required<LineEdit>(page, "McpEndpoint");
        Label status = Required<Label>(page, "McpStatusValue");
        Label error = Required<Label>(page, "McpLastErrorValue");
        Require(enabled.IsVisibleInTree() && endpoint.IsVisibleInTree() && status.IsVisibleInTree() && error.IsVisibleInTree(),
            "MCP controls must be directly visible on the settings page.");
        Require(dialog.FindChild("OpenMcpSettings", true, false) == null,
            "The MCP page must not contain a nested settings popup entry.");
        Require(endpoint.Text == McpSettingsDialog.FixedEndpoint && !endpoint.Editable,
            "The shared fixed endpoint must stay read-only.");

        int requests = 0;
        bool requestedEnabled = false;
        var requestFinished = new TaskCompletionSource();
        Func<bool, Task> request = value =>
        {
            requests++;
            requestedEnabled = value;
            return requestFinished.Task;
        };
        dialog.McpEnabledChangeRequested += request;
        dialog.UpdateMcpState(McpSettingsStatus.Disabled, false, null);
        enabled.SetPressedNoSignal(true);
        enabled.EmitSignal(BaseButton.SignalName.Toggled, true);
        Require(requests == 1 && requestedEnabled && status.Text == startingText && enabled.Disabled,
            "Embedded toggles must forward one request and disable the switch while starting.");
        dialog.UpdateMcpState(McpSettingsStatus.Running, true, null);
        requestFinished.SetResult();
        await Frames(3);
        Require(requests == 1 && enabled.ButtonPressed && !enabled.Disabled && status.Text == runningText,
            "Runtime updates must not emit another enabled request.");
        enabled.SetPressedNoSignal(false);
        enabled.EmitSignal(BaseButton.SignalName.Toggled, false);
        Require(requests == 2 && !requestedEnabled && status.Text == stoppingText && enabled.Disabled,
            "Embedded disable requests must retain the existing stopping behavior.");
        dialog.UpdateMcpState(McpSettingsStatus.Error, false, "synthetic MCP error");
        Require(error.Text == "synthetic MCP error" && !enabled.ButtonPressed && !enabled.Disabled,
            "MCP errors and switch state must be reflected directly on the page.");
        dialog.McpEnabledChangeRequested -= request;

        var standalone = new McpSettingsDialog();
        AddChild(standalone);
        standalone.EnabledChangeRequested += value =>
        {
            requests++;
            standalone.UpdateState(McpSettingsStatus.Running, requestedEnabled: value);
            return Task.CompletedTask;
        };
        CheckBox standaloneEnabled = Required<CheckBox>(standalone, "McpEnabledSwitch");
        standaloneEnabled.SetPressedNoSignal(true);
        standaloneEnabled.EmitSignal(BaseButton.SignalName.Toggled, true);
        Require(requests == 3 && standalone.Status == McpSettingsStatus.Running && standalone.RequestedEnabled,
            "The independent MCP dialog must retain its public event and state API.");
        standalone.QueueFree();
        categories.Select(0);
        categories.EmitSignal(ItemList.SignalName.ItemSelected, 0L);
        GD.Print($"{Prefix} embedded MCP controls and legacy signal API: PASS (no host started)");
    }

    private async Task Frames(int count)
    {
        for (int frame = 0; frame < count; frame++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static T Required<T>(Node root, string name) where T : Node =>
        root.FindChild(name, recursive: true, owned: false) as T
        ?? throw new InvalidOperationException($"Missing settings control: {name}");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
