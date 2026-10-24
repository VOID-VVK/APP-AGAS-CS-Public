using System;
using System.Threading.Tasks;
using AppAgasCs.UI.Shared;
using Godot;

namespace AGAS.UI;

public enum McpSettingsStatus
{
    Disabled,
    Starting,
    Running,
    Stopping,
    Stopped,
    Error
}

public partial class McpSettingsDialog : AcceptDialog
{
    public const string FixedEndpoint = "http://127.0.0.1:54119/mcp";

    private readonly McpSettingsContent _content = new();

    public event Func<bool, Task>? EnabledChangeRequested
    {
        add => _content.EnabledChangeRequested += value;
        remove => _content.EnabledChangeRequested -= value;
    }

    public McpSettingsDialog()
    {
        Title = "MCP 设置";
        WrapControls = false;
        MinSize = new Vector2I(520, 240);
        OkButtonText = "关闭";

        MarginContainer margin = AgasMarginContainerFactory.Create(16, 12, 16, 12, "McpSettingsMargin");
        var scroll = new ScrollContainer
        {
            Name = "McpDialogScroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        scroll.AddChild(_content);
        margin.AddChild(scroll);
        AddChild(margin);
    }

    public McpSettingsStatus Status => _content.Status;
    public string LastError => _content.LastError;
    public bool RequestedEnabled => _content.RequestedEnabled;

    public void Open(Node parent)
    {
        if (GetParent() == null)
        {
            parent.AddChild(this);
        }

        Vector2 available = parent.GetViewport().GetVisibleRect().Size - new Vector2(48, 48);
        var popupSize = new Vector2I(
            Math.Max(1, Math.Min(560, (int)available.X)),
            Math.Max(1, Math.Min(420, (int)available.Y)));
        MinSize = new Vector2I(Math.Min(520, popupSize.X), Math.Min(240, popupSize.Y));
        PopupCentered(popupSize);
    }

    public void UpdateState(McpSettingsStatus status, string? lastError = null, bool? requestedEnabled = null) =>
        _content.UpdateState(status, lastError, requestedEnabled);
}
