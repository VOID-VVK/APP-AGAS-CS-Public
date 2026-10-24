using System;
using System.Threading.Tasks;
using AppAgasCs.UI.Shared;
using Godot;

namespace AGAS.UI;

public partial class McpSettingsContent : VBoxContainer
{
    public event Func<bool, Task>? EnabledChangeRequested;

    private readonly CheckBox _enabledSwitch = new()
    {
        Name = "McpEnabledSwitch",
        Text = "启用 MCP",
        ClipText = true,
        TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
        TooltipText = "请求主界面启用或停止 MCP；本设置窗口不直接持有或启停 Host。"
    };

    private readonly LineEdit _endpointEdit = new()
    {
        Name = "McpEndpoint",
        Text = McpSettingsDialog.FixedEndpoint,
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        Editable = false,
        CustomMinimumSize = new Vector2(144, 0),
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        TooltipText = "固定 MCP Endpoint，端口不可编辑。"
    };

    private readonly Label _statusValue = new()
    {
        Name = "McpStatusValue",
        Text = nameof(McpSettingsStatus.Disabled),
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        ClipText = true,
        TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        CustomMinimumSize = new Vector2(144, 0),
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
    };

    private readonly Label _lastErrorValue = new()
    {
        Name = "McpLastErrorValue",
        Text = "-",
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
    };

    private bool _updating;
    private McpSettingsStatus _status = McpSettingsStatus.Disabled;
    private string _lastError = string.Empty;

    public McpSettingsContent()
    {
        Name = "McpSettingsContent";
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        AddThemeConstantOverride("separation", 10);
        AddChild(_enabledSwitch);
        AddChild(CreateValueRow("Endpoint", _endpointEdit));
        AddChild(CreateValueRow("状态", _statusValue));
        var errorSection = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        errorSection.AddChild(new Label { Text = "LastError", ClipText = true, TooltipText = "LastError" });
        errorSection.AddChild(_lastErrorValue);
        AddChild(errorSection);

        var hint = new Label
        {
            Name = "McpSettingsHint",
            Text = "切换开关只向 Main 发出请求；实际 Host 生命周期由 Main/MCP 接线处理。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        hint.AddThemeColorOverride("font_color", AgasIdeTheme.MutedTextColor);
        AddChild(hint);
        _enabledSwitch.Toggled += OnEnabledSwitchToggled;
        UpdateState(_status, _lastError);
    }

    public McpSettingsStatus Status => _status;
    public string LastError => _lastError;
    public bool RequestedEnabled => _enabledSwitch.ButtonPressed;

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged)
        {
            RefreshStateTranslation();
        }
    }

    public void UpdateState(McpSettingsStatus status, string? lastError = null, bool? requestedEnabled = null)
    {
        _status = status;
        _lastError = string.IsNullOrWhiteSpace(lastError) ? string.Empty : lastError;

        _updating = true;
        _enabledSwitch.ButtonPressed = requestedEnabled ?? (status is McpSettingsStatus.Starting or McpSettingsStatus.Running);
        _enabledSwitch.Disabled = status is McpSettingsStatus.Starting or McpSettingsStatus.Stopping;
        _updating = false;

        RefreshStateTranslation();
        _statusValue.AddThemeColorOverride("font_color", StatusColor(status));
        _lastErrorValue.Text = string.IsNullOrWhiteSpace(_lastError) ? "-" : _lastError;
        _lastErrorValue.TooltipText = _lastErrorValue.Text;
        _lastErrorValue.AddThemeColorOverride("font_color", status == McpSettingsStatus.Error ? AgasIdeTheme.ErrorTextColor : AgasIdeTheme.MutedTextColor);
    }

    private void RefreshStateTranslation()
    {
        _statusValue.Text = AppLocale.Translate(_status.ToString());
        _statusValue.TooltipText = _statusValue.Text;
        _endpointEdit.TooltipText = AppLocale.Translate("固定 MCP Endpoint，端口不可编辑。");
    }

    private static Control CreateValueRow(string labelText, Control valueControl)
    {
        var row = new HBoxContainer
        {
            Name = $"Mcp{labelText}Row",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        row.AddThemeConstantOverride("separation", 8);

        var label = new Label
        {
            Text = labelText,
            TooltipText = labelText,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(96, 1),
            VerticalAlignment = VerticalAlignment.Center
        };
        row.AddChild(label);
        row.AddChild(valueControl);
        return row;
    }

    private async void OnEnabledSwitchToggled(bool enabled)
    {
        if (_updating) return;

        McpSettingsStatus previousStatus = _status;
        string previousError = _lastError;
        bool previousEnabled = _enabledSwitch.ButtonPressed;
        UpdateState(enabled ? McpSettingsStatus.Starting : McpSettingsStatus.Stopping, previousError);

        try
        {
            await InvokeEnabledChangeRequested(enabled);
        }
        catch (Exception exception)
        {
            UpdateState(McpSettingsStatus.Error, exception.Message);
            return;
        }

        if (EnabledChangeRequested == null)
            UpdateState(previousStatus, previousError, previousEnabled);
    }

    private async Task InvokeEnabledChangeRequested(bool enabled)
    {
        Func<bool, Task>? handler = EnabledChangeRequested;
        if (handler == null) return;

        foreach (Delegate subscriber in handler.GetInvocationList())
        {
            if (subscriber is Func<bool, Task> request)
                await request(enabled);
        }
    }

    private static Color StatusColor(McpSettingsStatus status) => status switch
    {
        McpSettingsStatus.Running => AgasIdeTheme.SuccessTextColor,
        McpSettingsStatus.Starting or McpSettingsStatus.Stopping => Colors.Gold,
        McpSettingsStatus.Error => AgasIdeTheme.ErrorTextColor,
        _ => AgasIdeTheme.MutedTextColor
    };
}
