using System;
using AppAgasCs.Core.Resources;
using Godot;

namespace AGAS.UI.Controllers;

/// <summary>
/// Owns dock collapse/expand chrome, splitter drag capture, and deferred outer/center-right
/// layout application. Geometry math stays on <see cref="WorkspaceLayoutController"/>.
/// </summary>
internal sealed class MainWorkspaceDockChromeController
{
    private readonly WorkspaceLayoutController _layoutController;
    private readonly Func<WorkspaceLayoutResource?> _layout;
    private readonly Func<float> _workspaceWidth;
    private readonly Action<string, bool> _markDirty;
    private readonly Action _updateDocumentVisibility;
    private readonly Action _hidePropertiesPanel;

    private HSplitContainer? _mainHorizontalSplit;
    private HSplitContainer? _centerRightSplit;
    private VSplitContainer? _workspaceBottomSplit;
    private Control? _leftPanel;
    private Control? _rightPanel;
    private Control? _bottomPanel;
    private PanelContainer? _leftDock;
    private PanelContainer? _rightDock;
    private PanelContainer? _bottomDock;
    private Label? _leftDockTitle;
    private Label? _rightDockTitle;
    private Label? _bottomDockTitle;
    private Control? _bottomDockHeader;
    private Button? _leftDockToggle;
    private Button? _rightDockToggle;
    private Button? _bottomDockToggle;
    private AGAS.UI.FilesScenesPanel? _filesScenesPanel;
    private TabContainer? _bottomTabs;
    private Control? _canvas;
    private Label? _canvasContextStatusLabel;
    private Label? _canvasSelectionStatusLabel;
    private OptionButton? _aiProfileSelector;

    private bool _leftDockCollapsed;
    private bool _rightDockCollapsed;
    private bool _bottomDockCollapsed = true;
    private bool _layoutOffsetsApplyQueued;
    private bool _centerRightLayoutApplyQueued;
    private bool _workspaceReleased;
    private bool _applyingLayout;
    private bool _horizontalDragInProgress;
    private bool _respectManualWidths;

    internal MainWorkspaceDockChromeController(
        WorkspaceLayoutController layoutController,
        Func<WorkspaceLayoutResource?> layout,
        Func<float> workspaceWidth,
        Action<string, bool> markDirty,
        Action updateDocumentVisibility,
        Action hidePropertiesPanel)
    {
        _layoutController = layoutController ?? throw new ArgumentNullException(nameof(layoutController));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _workspaceWidth = workspaceWidth ?? throw new ArgumentNullException(nameof(workspaceWidth));
        _markDirty = markDirty ?? throw new ArgumentNullException(nameof(markDirty));
        _updateDocumentVisibility = updateDocumentVisibility
            ?? throw new ArgumentNullException(nameof(updateDocumentVisibility));
        _hidePropertiesPanel = hidePropertiesPanel ?? throw new ArgumentNullException(nameof(hidePropertiesPanel));
    }

    internal bool LeftDockCollapsed => _leftDockCollapsed;
    internal bool RightDockCollapsed => _rightDockCollapsed;
    internal bool BottomDockCollapsed => _bottomDockCollapsed;
    internal PanelContainer? LeftDock => _leftDock;
    internal PanelContainer? RightDock => _rightDock;
    internal PanelContainer? BottomDock => _bottomDock;
    internal Label? RightDockTitle => _rightDockTitle;
    internal HSplitContainer? CenterRightSplit => _centerRightSplit;
    internal VSplitContainer? WorkspaceBottomSplit => _workspaceBottomSplit;

    internal void NotifyWorkspaceReleased()
    {
        _workspaceReleased = true;
        _layoutOffsetsApplyQueued = false;
        _centerRightLayoutApplyQueued = false;
    }

    internal void BindShell(MainWorkspaceShellUi shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _mainHorizontalSplit = shell.MainHorizontalSplit;
        _centerRightSplit = shell.CenterRightSplit;
        _workspaceBottomSplit = shell.WorkspaceBottomSplit;
        if (_workspaceBottomSplit != null)
        {
            _workspaceBottomSplit.Resized += OnWorkspaceBottomSplitResized;
        }
        _leftPanel = shell.LeftPanel;
        _rightPanel = shell.RightPanel;
        _bottomPanel = shell.BottomPanel;
        _leftDock = shell.LeftDock;
        if (_mainHorizontalSplit != null)
        {
            _mainHorizontalSplit.DragStarted += () =>
            {
                _horizontalDragInProgress = true;
                if (_leftDock != null && TryGetWorkspaceWidth(out float width))
                {
                    int currentWidth = (int)Math.Round(_leftDock.Size.X);
                    _leftDock.CustomMinimumSize = new Vector2(_layoutController.IsCompact(width)
                        ? WorkspaceLayoutController.CompactLeftPanelWidth : WorkspaceLayoutController.MinLeftPanelWidth, 1);
                    _mainHorizontalSplit.SplitOffsets = new[] { currentWidth };
                }
            };
        }
        _rightDock = shell.RightDock;
        _bottomDock = shell.BottomDock;
        _leftDockTitle = shell.LeftDockTitle;
        _rightDockTitle = shell.RightDockTitle;
        _bottomDockTitle = shell.BottomDockTitle;
        _bottomDockHeader = shell.BottomDockTitle?.GetParent<Control>();
        _leftDockToggle = shell.LeftDockToggle;
        _rightDockToggle = shell.RightDockToggle;
        _bottomDockToggle = shell.BottomDockToggle;
        if (_bottomDockToggle != null)
        {
            _bottomDockToggle.MinimumSizeChanged += () =>
            {
                if (_bottomDockCollapsed) return;
                Vector2 size = _bottomDockToggle.GetCombinedMinimumSize();
                _bottomDockToggle.OffsetLeft = -size.X;
                _bottomDockToggle.OffsetBottom = size.Y;
            };
        }
        _filesScenesPanel = shell.FilesScenesPanel;
        _bottomTabs = shell.BottomTabs;
        _canvas = shell.Canvas;
        if (_bottomTabs != null)
        {
            _bottomTabs.TabChanged += OnBottomTabChanged;
        }
        _canvasContextStatusLabel = shell.CanvasContextStatusLabel;
        _canvasSelectionStatusLabel = shell.CanvasSelectionStatusLabel;
        _aiProfileSelector = shell.AiProfileSelector;
    }

    internal void ApplyLayoutToUi()
    {
        WorkspaceLayoutResource? layout = _layout();
        if (layout == null)
        {
            return;
        }

        _applyingLayout = true;
        _respectManualWidths = false;
        try
        {
            SetLeftDockCollapsed(layout.LeftPanelCollapsed, notify: false);
            SetRightDockCollapsed(layout.RightPanelCollapsed, notify: false);
            SetBottomDockCollapsed(layout.BottomDockCollapsed, notify: false);
            if (_filesScenesPanel != null)
            {
                _filesScenesPanel.ActiveTab = layout.ActiveLeftTab;
                _filesScenesPanel.PreviewHeight = Math.Max(80, (int)Math.Round(layout.PreviewHeight));
                _filesScenesPanel.PreviewCollapsed = layout.PreviewCollapsed;
            }
            if (_bottomTabs != null)
            {
                _bottomTabs.CurrentTab = string.Equals(layout.ActiveBottomTab, "Tools", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 0;
            }
        }
        finally
        {
            _applyingLayout = false;
        }
        UpdateResponsiveLayout();
        QueueApplyLayoutOffsetsAfterLayout();
    }

    internal void CaptureLayoutFromUi()
    {
        WorkspaceLayoutResource? layout = _layout();
        if (layout == null || !TryGetWorkspaceWidth(out float workspaceWidth))
        {
            return;
        }

        _layoutController.CapturePanelSizes(
            layout,
            workspaceWidth,
            _leftDockCollapsed,
            _rightDockCollapsed,
            _bottomDockCollapsed,
            _rightDock is not { Visible: false },
            _mainHorizontalSplit,
            _centerRightSplit,
            _workspaceBottomSplit,
            _canvas,
            captureHorizontalSizes: _horizontalDragInProgress);
        layout.LeftPanelCollapsed = _leftDockCollapsed;
        layout.RightPanelCollapsed = _rightDockCollapsed;
        layout.BottomDockCollapsed = _bottomDockCollapsed;
        if (_filesScenesPanel != null)
        {
            layout.PreviewHeight = _filesScenesPanel.PreviewHeight;
            layout.PreviewCollapsed = _filesScenesPanel.PreviewCollapsed;
            layout.ActiveLeftTab = _filesScenesPanel.ActiveTab;
        }
        if (_bottomTabs != null && !_bottomDockCollapsed)
        {
            layout.ActiveBottomTab = _bottomTabs.CurrentTab == 1 ? "Tools" : "Prompt";
        }
    }

    internal void OnPreviewCollapsedChanged(bool collapsed)
    {
        CaptureLayoutFromUi();
        _markDirty(collapsed ? "预览已折叠。" : "预览已展开。", false);
    }

    internal void OnCenterRightSplitDragStarted()
    {
        _horizontalDragInProgress = true;
        if (_rightDock == null
            || _rightDockCollapsed
            || !_rightDock.Visible
            || !TryGetWorkspaceWidth(out float workspaceWidth))
        {
            return;
        }

        _layoutController.PrepareRightDockForDrag(workspaceWidth, _rightDock);
    }

    internal void OnWorkspaceSplitterDragEnded()
    {
        if (_horizontalDragInProgress) _respectManualWidths = true;
        CaptureLayoutFromUi();
        _horizontalDragInProgress = false;
        // Re-pin the right dock width after a manual split drag.
        QueueApplyLayoutOffsetsAfterLayout();
        _markDirty("面板尺寸已更新。", false);
    }

    internal void QueueApplyLayoutOffsetsAfterLayout()
    {
        if (_workspaceReleased)
        {
            return;
        }
        if (_layoutOffsetsApplyQueued)
        {
            return;
        }

        _layoutOffsetsApplyQueued = true;
        Callable.From(() =>
        {
            _layoutOffsetsApplyQueued = false;
            if (_workspaceReleased)
            {
                return;
            }
            ApplyLayoutOffsetsAfterLayout();
        }).CallDeferred();
    }

    private void OnWorkspaceBottomSplitResized()
    {
        ApplyLayoutOffsetsAfterLayout();
    }

    internal void ApplyLayoutOffsetsAfterLayout()
    {
        if (_horizontalDragInProgress) return;
        WorkspaceLayoutResource? layout = _layout();
        if (layout == null || !TryGetWorkspaceWidth(out float workspaceWidth))
        {
            return;
        }

        _layoutController.ApplyOuterSplitLayout(
            layout,
            workspaceWidth,
            _leftDockCollapsed,
            _rightDockCollapsed,
            _bottomDockCollapsed,
            _mainHorizontalSplit,
            _centerRightSplit,
            _workspaceBottomSplit,
            _canvas,
            respectManualWidths: _respectManualWidths);

        QueueCenterRightLayoutAfterOuterSplit();
    }

    internal void QueueCenterRightLayoutAfterOuterSplit()
    {
        if (_workspaceReleased)
        {
            return;
        }
        if (_centerRightLayoutApplyQueued)
        {
            return;
        }

        _centerRightLayoutApplyQueued = true;
        Callable.From(() =>
        {
            _centerRightLayoutApplyQueued = false;
            if (_workspaceReleased)
            {
                return;
            }
            ApplyCenterRightLayoutAfterOuterSplit();
        }).CallDeferred();
    }

    internal void ApplyCenterRightLayoutAfterOuterSplit()
    {
        if (_horizontalDragInProgress) return;
        WorkspaceLayoutResource? layout = _layout();
        if (layout == null
            || _centerRightSplit == null
            || _rightDock == null
            || !TryGetWorkspaceWidth(out float workspaceWidth))
        {
            return;
        }

        _layoutController.ApplyCenterRightLayout(
            layout,
            workspaceWidth,
            _rightDockCollapsed,
            _centerRightSplit,
            _rightDock,
            respectManualWidths: _respectManualWidths);
    }

    internal void UpdateResponsiveLayout()
    {
        if (!TryGetWorkspaceWidth(out float workspaceWidth))
        {
            return;
        }

        bool compact = _layoutController.IsCompact(workspaceWidth);
        if (_canvasContextStatusLabel != null) _canvasContextStatusLabel.Visible = true;
        if (_canvasSelectionStatusLabel != null)
        {
            _canvasSelectionStatusLabel.CustomMinimumSize = new Vector2(compact ? 110 : 180, 1);
        }
        if (_aiProfileSelector != null)
        {
            _aiProfileSelector.CustomMinimumSize = new Vector2(compact ? 180 : 240, 1);
        }
        _layoutController.ApplyResponsiveMinimums(
            workspaceWidth,
            _leftPanel,
            _rightPanel,
            _leftDock,
            _rightDock,
            _leftDockCollapsed,
            _rightDockCollapsed,
            _layout());
    }

    internal void ToggleLeftPanel()
    {
        if (_leftPanel == null || _leftDock == null)
        {
            return;
        }
        if (!_leftDockCollapsed && _layout() != null)
        {
            CaptureLayoutFromUi();
        }
        SetLeftDockCollapsed(!_leftDockCollapsed, notify: true);
    }

    internal void ToggleRightPanel()
    {
        if (_rightPanel == null || _rightDock == null)
        {
            return;
        }
        if (!_rightDockCollapsed && _layout() != null)
        {
            CaptureLayoutFromUi();
        }
        SetRightDockCollapsed(!_rightDockCollapsed, notify: true);
    }

    internal void ToggleBottomDock()
    {
        if (_bottomPanel == null || _bottomDock == null)
        {
            return;
        }
        WorkspaceLayoutResource? layout = _layout();
        if (!_bottomDockCollapsed && layout != null)
        {
            CaptureLayoutFromUi();
        }
        SetBottomDockCollapsed(!_bottomDockCollapsed, notify: true);
    }

    internal void OpenPromptDock()
    {
        SetBottomDockCollapsed(false, notify: true);
        if (_bottomTabs != null)
        {
            _bottomTabs.CurrentTab = 0;
        }
    }

    private void OnBottomTabChanged(long tab)
    {
        if (_applyingLayout || _bottomDockCollapsed)
        {
            return;
        }

        WorkspaceLayoutResource? layout = _layout();
        if (layout == null)
        {
            return;
        }

        string value = tab == 1 ? "Tools" : "Prompt";
        if (!string.Equals(layout.ActiveBottomTab, value, StringComparison.Ordinal))
        {
            layout.ActiveBottomTab = value;
            _markDirty("底部面板已切换。", false);
        }
    }

    internal void SetLeftDockCollapsed(bool collapsed, bool notify)
    {
        _leftDockCollapsed = collapsed;
        if (_leftPanel != null)
        {
            _leftPanel.Visible = !collapsed;
        }
        if (_leftDock != null)
        {
            _leftDock.CustomMinimumSize = new Vector2(
                collapsed ? WorkspaceLayoutController.CollapsedDockThickness : WorkspaceLayoutController.MinLeftPanelWidth,
                1);
        }
        if (_mainHorizontalSplit != null)
        {
            _mainHorizontalSplit.DraggingEnabled = !collapsed;
        }
        UpdateDockHeader(_leftDockTitle, _leftDockToggle, collapsed, "文件 / 场景");
        WorkspaceLayoutResource? layout = _layout();
        if (layout != null)
        {
            layout.LeftPanelCollapsed = collapsed;
        }
        QueueApplyLayoutOffsetsAfterLayout();
        if (notify)
        {
            _markDirty(collapsed ? "左栏已最小化。" : "左栏已恢复。", false);
        }
    }

    internal void SetRightDockCollapsed(bool collapsed, bool notify)
    {
        _rightDockCollapsed = collapsed;
        if (_rightPanel != null)
        {
            _rightPanel.Visible = !collapsed;
        }
        if (collapsed)
        {
            _hidePropertiesPanel();
        }
        else
        {
            _updateDocumentVisibility();
        }
        if (_rightDock != null)
        {
            if (collapsed)
            {
                _rightDock.CustomMinimumSize = new Vector2(WorkspaceLayoutController.CollapsedDockThickness, 1);
            }
            else
            {
                _rightDock.CustomMinimumSize = new Vector2(WorkspaceLayoutController.MinRightPanelWidth, 1);
            }
            _rightDock.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        }
        if (_centerRightSplit != null)
        {
            _centerRightSplit.DraggingEnabled = !collapsed;
        }
        UpdateDockHeader(_rightDockTitle, _rightDockToggle, collapsed, "属性");
        WorkspaceLayoutResource? layoutForFlag = _layout();
        if (layoutForFlag != null)
        {
            layoutForFlag.RightPanelCollapsed = collapsed;
        }
        QueueApplyLayoutOffsetsAfterLayout();
        if (notify)
        {
            _markDirty(collapsed ? "属性面板已最小化。" : "属性面板已恢复。", false);
        }
    }

    internal void SetBottomDockCollapsed(bool collapsed, bool notify)
    {
        WorkspaceLayoutResource? layout = _layout();
        if (collapsed && !_applyingLayout && !_bottomDockCollapsed && _bottomTabs != null && layout != null)
        {
            layout.ActiveBottomTab = _bottomTabs.CurrentTab == 1 ? "Tools" : "Prompt";
        }
        _bottomDockCollapsed = collapsed;
        if (_bottomPanel != null)
        {
            _bottomPanel.Visible = !collapsed;
        }
        if (_bottomTabs != null && layout != null)
        {
            _bottomTabs.CurrentTab = string.Equals(layout.ActiveBottomTab, "Tools", StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;
        }
        if (_bottomDock != null)
        {
            _bottomDock.CustomMinimumSize = new Vector2(
                1,
                collapsed
                    ? WorkspaceLayoutController.CollapsedDockThickness
                    : WorkspaceLayoutController.MinBottomDockHeight);
        }
        if (_workspaceBottomSplit != null)
        {
            _workspaceBottomSplit.DraggingEnabled = !collapsed;
        }
        if (_bottomDockHeader != null && _bottomDockToggle != null && _bottomTabs != null)
        {
            Control toggleParent = collapsed ? _bottomDockHeader : _bottomTabs.GetTabBar();
            if (_bottomDockToggle.GetParent() != toggleParent)
            {
                _bottomDockToggle.Reparent(toggleParent);
            }
            if (!collapsed)
            {
                _bottomDockToggle.SetAnchorsPreset(Control.LayoutPreset.TopRight);
                Vector2 toggleSize = _bottomDockToggle.GetCombinedMinimumSize();
                _bottomDockToggle.OffsetLeft = -toggleSize.X;
                _bottomDockToggle.OffsetRight = 0;
                _bottomDockToggle.OffsetTop = 0;
                _bottomDockToggle.OffsetBottom = toggleSize.Y;
            }
            _bottomDockHeader.Visible = collapsed;
        }
        UpdateDockHeader(_bottomDockTitle, _bottomDockToggle, collapsed, "提示词 / 工具", keepTitleWhenCollapsed: true);
        if (layout != null)
        {
            layout.BottomDockCollapsed = collapsed;
        }
        QueueApplyLayoutOffsetsAfterLayout();
        if (notify)
        {
            _markDirty(collapsed ? "底部面板已最小化。" : "底部面板已恢复。", false);
        }
    }

    internal static void UpdateDockHeader(
        Label? title,
        Button? toggle,
        bool collapsed,
        string panelName,
        bool keepTitleWhenCollapsed = false)
    {
        if (title != null)
        {
            title.Visible = !collapsed || keepTitleWhenCollapsed;
        }
        if (toggle != null)
        {
            toggle.Text = collapsed ? "+" : "−";
            toggle.TooltipAutoTranslateMode = Node.AutoTranslateModeEnum.Disabled;
            toggle.TooltipText = string.Format(toggle.Tr(collapsed ? "恢复{0}面板" : "最小化{0}面板"), toggle.Tr(panelName));
        }
    }

    internal void RefreshTranslations()
    {
        UpdateDockHeader(_leftDockTitle, _leftDockToggle, _leftDockToggle?.Text == "+", "文件 / 场景");
        UpdateDockHeader(_rightDockTitle, _rightDockToggle, _rightDockToggle?.Text == "+", "属性");
        UpdateDockHeader(_bottomDockTitle, _bottomDockToggle, _bottomDockToggle?.Text == "+", "提示词 / 工具", keepTitleWhenCollapsed: true);
    }

    private bool TryGetWorkspaceWidth(out float workspaceWidth)
    {
        workspaceWidth = 0;
        if (_workspaceReleased)
        {
            return false;
        }

        try
        {
            workspaceWidth = _workspaceWidth();
            return true;
        }
        catch (ObjectDisposedException)
        {
            NotifyWorkspaceReleased();
            return false;
        }
    }
}
