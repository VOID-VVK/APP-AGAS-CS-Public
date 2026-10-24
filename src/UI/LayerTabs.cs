using System;
using System.Collections.Generic;
using System.Linq;
using AGAS.UI.Dialogs;
using AppAgasCs.Core.Resources;
using AppAgasCs.UI.Shared;
using Godot;

namespace AGAS.UI;

public partial class LayerTabs : PanelContainer
{
    public event Action<AgasLayerResource>? CurrentLayerChanged;
    public event Action<AgasLayerResource, bool>? LayerVisibilityChanged;
    public event Action<AgasLayerResource, bool>? LayerLockChanged;
    public event Action<AgasLayerResource, bool>? LayerShareChanged;
    public event Action? LayerAddRequested;
    public event Action<AgasLayerResource, string>? LayerRenameRequested;
    public event Action<AgasLayerResource>? LayerDeleteRequested;
    public event Action<AgasLayerResource, int>? LayerMoveRequested;
    public event Action<AgasLayerResource>? LayerPromptRequested;

    private AgasSceneResource? _scene;
    private HBoxContainer? _tabs;
    private HBoxContainer? _actionBar;
    private ScrollContainer? _tabScroll;
    private TextInputDialog? _renameDialog;
    private readonly Dictionary<string, Button> _tabButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PanelContainer> _tabPanels = new(StringComparer.Ordinal);
    private AgasLayerResource? _displayedCurrentLayer;
    private Button? _renameButton;
    private Button? _deleteButton;
    private Button? _moveUpButton;
    private Button? _moveDownButton;
    private Button? _promptButton;
    private readonly Dictionary<Button, (string Key, object[] Arguments)> _descriptions = new();

    public override void _Notification(int what)
    {
        if (what != NotificationTranslationChanged || !IsNodeReady()) return;
        foreach (var entry in _descriptions)
        {
            string description = string.Format(Tr(entry.Value.Key), entry.Value.Arguments);
            entry.Key.TooltipText = description;
            entry.Key.AccessibilityName = description;
        }
    }

    public override void _Ready()
    {
        Name = "LayerTabs";
        BuildUi();
        Refresh();
    }

    public void SetScene(AgasSceneResource? scene)
    {
        _scene = scene;
        Refresh();
    }

    private void BuildUi()
    {
        AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        MarginContainer margin = AgasMarginContainerFactory.Create(8, 4, 8, 4);
        AddChild(margin);

        var scroll = new ScrollContainer
        {
            Name = "LayerActionsScroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Reserve,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FollowFocus = true
        };
        scroll.GetHScrollBar().CustomMinimumSize = new Vector2(1, 16);
        scroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        margin.AddChild(scroll);

        _actionBar = new HBoxContainer
        {
            Name = "LayerActionBar",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(1, 56)
        };
        _actionBar.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_actionBar);
        Action refreshScrollHeight = () => scroll.CustomMinimumSize = new Vector2(1,
            _actionBar.GetCombinedMinimumSize().Y + scroll.GetHScrollBar().GetCombinedMinimumSize().Y);
        _actionBar.MinimumSizeChanged += refreshScrollHeight;
        scroll.GetHScrollBar().MinimumSizeChanged += refreshScrollHeight;
        refreshScrollHeight();
    }

    private void Refresh()
    {
        if (_actionBar == null)
        {
            return;
        }

        ClearChildren(_actionBar);
        _descriptions.Clear();
        _tabButtons.Clear();
        _tabPanels.Clear();
        _displayedCurrentLayer = null;
        _renameButton = null;
        _deleteButton = null;
        _moveUpButton = null;
        _moveDownButton = null;
        _promptButton = null;

        var orderedLayers = _scene?.Layers.OrderBy(layer => layer.Order).ToList();
        AgasLayerResource? currentLayer = orderedLayers?.FirstOrDefault(layer => layer.Id == _scene?.ActiveLayerId);

        BuildActionBar(orderedLayers, currentLayer);
    }

    private void BuildActionBar(IReadOnlyList<AgasLayerResource>? orderedLayers, AgasLayerResource? currentLayer)
    {
        if (_actionBar == null)
        {
            return;
        }

        Button addButton = CreateSmallButton("新增", "新增图层", () => LayerAddRequested?.Invoke());
        addButton.Name = "LayerAddButton";
        addButton.CustomMinimumSize = new Vector2(72, AgasIconButtonFactory.ControlSize);
        _actionBar.AddChild(addButton);

        _tabScroll = new ScrollContainer
        {
            Name = "LayerTabStrip",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Reserve,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(AgasIconButtonFactory.ControlSize, AgasIconButtonFactory.ControlSize)
        };
        _tabScroll.GetHScrollBar().CustomMinimumSize = new Vector2(1, 16);
        _tabScroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        _actionBar.AddChild(_tabScroll);

        _tabs = new HBoxContainer
        {
            Name = "LayerTabButtons",
            SizeFlagsHorizontal = SizeFlags.Fill
        };
        _tabs.AddThemeConstantOverride("separation", 4);
        _tabScroll.AddChild(_tabs);
        ScrollContainer tabScroll = _tabScroll;
        HBoxContainer tabs = _tabs;
        Action refreshTabScrollHeight = () => tabScroll.CustomMinimumSize = new Vector2(AgasIconButtonFactory.ControlSize,
            tabs.GetCombinedMinimumSize().Y + tabScroll.GetHScrollBar().GetCombinedMinimumSize().Y);
        tabs.MinimumSizeChanged += refreshTabScrollHeight;
        tabScroll.GetHScrollBar().MinimumSizeChanged += refreshTabScrollHeight;
        BuildTabStrip(orderedLayers, currentLayer);
        refreshTabScrollHeight();

        var tools = new HBoxContainer
        {
            Name = "LayerCurrentTools",
            SizeFlagsHorizontal = SizeFlags.ShrinkEnd
        };
        tools.AddThemeConstantOverride("separation", 3);
        _actionBar.AddChild(tools);

        _renameButton = AgasIconButtonFactory.Create(LayerToggleIcons.Rename(), "重命名当前图层", () => WithDisplayedLayer(RenameLayer));
        _deleteButton = AgasIconButtonFactory.Create(LayerToggleIcons.Delete(), "删除当前图层", () => WithDisplayedLayer(layer => LayerDeleteRequested?.Invoke(layer)));
        _moveUpButton = AgasIconButtonFactory.Create(LayerToggleIcons.MoveUp(), "上移当前图层", () => WithDisplayedLayer(layer => LayerMoveRequested?.Invoke(layer, -1)));
        _moveDownButton = AgasIconButtonFactory.Create(LayerToggleIcons.MoveDown(), "下移当前图层", () => WithDisplayedLayer(layer => LayerMoveRequested?.Invoke(layer, 1)));
        _promptButton = AgasIconButtonFactory.Create(LayerToggleIcons.Prompt(), "打开当前图层提示词", () => WithDisplayedLayer(layer => LayerPromptRequested?.Invoke(layer)));
        _renameButton.Name = "LayerRenameButton";
        _deleteButton.Name = "LayerDeleteButton";
        _moveUpButton.Name = "LayerMoveUpButton";
        _moveDownButton.Name = "LayerMoveDownButton";
        _promptButton.Name = "LayerPromptButton";
        tools.AddChild(_renameButton);
        tools.AddChild(_moveUpButton);
        tools.AddChild(_moveDownButton);
        tools.AddChild(_promptButton);
        tools.AddChild(_deleteButton);

        if (currentLayer == null)
        {
            SetToolButtonsDisabled(true);
            return;
        }

        UpdateSelectionState(currentLayer, orderedLayers?.Count ?? 0);
    }

    private void BuildTabStrip(System.Collections.Generic.IReadOnlyList<AgasLayerResource>? orderedLayers, AgasLayerResource? currentLayer)
    {
        if (_tabs == null)
        {
            return;
        }

        if (orderedLayers == null || orderedLayers.Count == 0)
        {
            _tabs.AddChild(new Label
            {
                Text = "无图层",
                VerticalAlignment = VerticalAlignment.Center,
                TooltipText = "当前场景没有图层"
            });
            return;
        }

        foreach (AgasLayerResource layer in orderedLayers)
        {
            bool current = currentLayer != null && layer.Id == currentLayer.Id;
            var tabPanel = new PanelContainer
            {
                Name = $"LayerTab-{layer.Id}",
                CustomMinimumSize = new Vector2(1, AgasIconButtonFactory.ControlSize),
                MouseFilter = MouseFilterEnum.Pass
            };
            tabPanel.AddThemeStyleboxOverride("panel", CreateTabPanelStyle(current));
            tabPanel.MouseEntered += () => tabPanel.AddThemeStyleboxOverride(
                "panel",
                CreateTabPanelStyle(IsDisplayed(layer), hovered: true));
            tabPanel.MouseExited += () => tabPanel.AddThemeStyleboxOverride(
                "panel",
                CreateTabPanelStyle(IsDisplayed(layer)));

            var row = new HBoxContainer { Name = $"LayerTabContent-{layer.Id}" };
            row.AddThemeConstantOverride("separation", 2);
            tabPanel.AddChild(row);

            var tab = new Button
            {
                AutoTranslateMode = AutoTranslateModeEnum.Disabled,
                Name = $"LayerTabSelect-{layer.Id}",
                Text = layer.Name,
                ToggleMode = true,
                ButtonPressed = current,
                FocusMode = FocusModeEnum.All,
                CustomMinimumSize = new Vector2(72, AgasIconButtonFactory.ControlSize)
            };
            SetButtonDescription(tab, "切换到图层：{0}。右键打开该图层提示词并设为当前图层。", layer.Name);
            ApplyFlatTabButtonStyle(tab);
            tab.Pressed += () => SelectLayer(layer);
            tab.GuiInput += input =>
            {
                if (input is InputEventMouseButton mouse &&
                    mouse.ButtonIndex == MouseButton.Right &&
                    mouse.Pressed)
                {
                    tab.AcceptEvent();
                    SelectLayer(layer, openPrompt: true);
                }
            };
            row.AddChild(tab);
            Button visibility = AgasIconButtonFactory.Create(
                LayerToggleIcons.Eye(layer.Visible),
                string.Format(Tr(layer.Visible ? "隐藏图层：{0}" : "显示图层：{0}"), layer.Name),
                () =>
                {
                    LayerVisibilityChanged?.Invoke(layer, !layer.Visible);
                });
            visibility.Name = $"LayerVisible-{layer.Id}";
            SetButtonDescription(visibility, layer.Visible ? "隐藏图层：{0}" : "显示图层：{0}", layer.Name);
            row.AddChild(visibility);

            Button locked = AgasIconButtonFactory.Create(
                LayerToggleIcons.Lock(layer.Locked),
                string.Format(Tr(layer.Locked ? "解锁图层：{0}" : "锁定图层：{0}"), layer.Name),
                () =>
                {
                    LayerLockChanged?.Invoke(layer, !layer.Locked);
                });
            locked.Name = $"LayerLocked-{layer.Id}";
            SetButtonDescription(locked, layer.Locked ? "解锁图层：{0}" : "锁定图层：{0}", layer.Name);
            row.AddChild(locked);

            Button shared = AgasIconButtonFactory.Create(
                LayerToggleIcons.Share(layer.Shared),
                string.Format(Tr(layer.Shared ? "取消分享图层：{0}。导出场景包时不再带上该层图片。" : "分享图层：{0}。导出场景包时把该层成图作为参考图带给对方。"), layer.Name),
                () =>
                {
                    LayerShareChanged?.Invoke(layer, !layer.Shared);
                });
            shared.Name = $"LayerShared-{layer.Id}";
            SetButtonDescription(shared, layer.Shared ? "取消分享图层：{0}。导出场景包时不再带上该层图片。" : "分享图层：{0}。导出场景包时把该层成图作为参考图带给对方。", layer.Name);
            row.AddChild(shared);

            _tabs.AddChild(tabPanel);
            if (!string.IsNullOrWhiteSpace(layer.Id))
            {
                _tabButtons[layer.Id] = tab;
                _tabPanels[layer.Id] = tabPanel;
            }
        }
    }

    private void SelectLayer(AgasLayerResource layer, bool openPrompt = false)
    {
        CurrentLayerChanged?.Invoke(layer);
        if (openPrompt)
        {
            LayerPromptRequested?.Invoke(layer);
        }
        UpdateSelectionState(layer, _scene?.Layers.Count ?? 0);
    }

    private void UpdateSelectionState(AgasLayerResource currentLayer, int layerCount)
    {
        _displayedCurrentLayer = currentLayer;
        foreach ((string layerId, Button button) in _tabButtons)
        {
            bool selected = string.Equals(layerId, currentLayer.Id, StringComparison.Ordinal);
            button.ButtonPressed = selected;
            if (_tabPanels.TryGetValue(layerId, out PanelContainer? panel))
            {
                panel.AddThemeStyleboxOverride("panel", CreateTabPanelStyle(selected));
            }
        }

        SetToolButtonsDisabled(false);
        SetButtonDescription(_renameButton, "重命名图层：{0}", currentLayer.Name);
        if (_deleteButton != null)
        {
            SetButtonDescription(_deleteButton, "删除图层：{0}；至少保留 1 个图层", currentLayer.Name);
            _deleteButton.Disabled = layerCount <= 1;
        }
        IReadOnlyList<AgasLayerResource> orderedLayers = _scene == null
            ? Array.Empty<AgasLayerResource>()
            : _scene.Layers.OrderBy(layer => layer.Order).ToList();
        int currentIndex = orderedLayers.ToList().FindIndex(layer => layer.Id == currentLayer.Id);
        if (_moveUpButton != null)
        {
            SetButtonDescription(_moveUpButton, "上移图层：{0}", currentLayer.Name);
            _moveUpButton.Disabled = currentIndex <= 0;
        }
        if (_moveDownButton != null)
        {
            SetButtonDescription(_moveDownButton, "下移图层：{0}", currentLayer.Name);
            _moveDownButton.Disabled = currentIndex < 0 || currentIndex >= orderedLayers.Count - 1;
        }
        SetButtonDescription(_promptButton, "打开图层提示词：{0}", currentLayer.Name);

        if (_tabScroll != null && _tabPanels.TryGetValue(currentLayer.Id, out PanelContainer? selectedPanel))
        {
            Callable.From(() => EnsureTabVisible(selectedPanel)).CallDeferred();
        }
    }

    private void EnsureTabVisible(Control tab)
    {
        if (_tabScroll != null && GodotObject.IsInstanceValid(tab) && tab.IsInsideTree())
        {
            _tabScroll.EnsureControlVisible(tab);
        }
    }

    private void WithDisplayedLayer(Action<AgasLayerResource> action)
    {
        if (_displayedCurrentLayer != null)
        {
            action(_displayedCurrentLayer);
        }
    }

    private bool IsDisplayed(AgasLayerResource layer)
    {
        return string.Equals(_displayedCurrentLayer?.Id, layer.Id, StringComparison.Ordinal);
    }

    private static void ClearChildren(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }

    private static Button CreateSmallButton(string text, string tooltip, Action pressed, bool disabled = false)
    {
        var button = new Button
        {
            Text = text,
            TooltipText = tooltip,
            AccessibilityName = tooltip,
            FocusMode = FocusModeEnum.All,
            Disabled = disabled
        };
        button.Pressed += pressed;
        return button;
    }

    private void SetButtonDescription(Button? button, string key, params object[] arguments)
    {
        if (button == null)
        {
            return;
        }

        _descriptions[button] = (key, arguments);
        string description = string.Format(Tr(key), arguments);
        button.TooltipAutoTranslateMode = AutoTranslateModeEnum.Disabled;
        button.TooltipText = description;
        button.AccessibilityName = description;
    }

    private void SetToolButtonsDisabled(bool disabled)
    {
        if (_renameButton != null) _renameButton.Disabled = disabled;
        if (_deleteButton != null) _deleteButton.Disabled = disabled;
        if (_moveUpButton != null) _moveUpButton.Disabled = disabled;
        if (_moveDownButton != null) _moveDownButton.Disabled = disabled;
        if (_promptButton != null) _promptButton.Disabled = disabled;
    }

    private static void ApplyFlatTabButtonStyle(Button button)
    {
        button.AddThemeStyleboxOverride("normal", FlatButtonStyle(new Color(0, 0, 0, 0)));
        button.AddThemeStyleboxOverride("hover", FlatButtonStyle(new Color(0.26f, 0.28f, 0.31f, 1f)));
        button.AddThemeStyleboxOverride("pressed", FlatButtonStyle(new Color(0.20f, 0.23f, 0.28f, 1f)));
        button.AddThemeStyleboxOverride("disabled", FlatButtonStyle(new Color(0, 0, 0, 0)));
    }

    private static StyleBoxFlat CreateTabPanelStyle(bool selected, bool hovered = false)
    {
        var style = new StyleBoxFlat
        {
            BgColor = selected
                ? new Color(0.19f, 0.22f, 0.27f, 1f)
                : hovered
                    ? new Color(0.24f, 0.26f, 0.29f, 1f)
                    : new Color(0.16f, 0.17f, 0.19f, 1f),
            BorderColor = selected
                ? new Color(0.25f, 0.53f, 1f, 1f)
                : new Color(0.32f, 0.34f, 0.38f, 1f),
            ContentMarginLeft = 3,
            ContentMarginRight = 3,
            ContentMarginTop = 2,
            ContentMarginBottom = 2
        };
        style.SetBorderWidthAll(selected ? 2 : 1);
        style.SetCornerRadiusAll(4);
        return style;
    }

    private static StyleBoxFlat FlatButtonStyle(Color background)
    {
        var style = new StyleBoxFlat
        {
            BgColor = background,
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 3,
            ContentMarginBottom = 3
        };
        style.SetCornerRadiusAll(2);
        return style;
    }

    private void RenameLayer(AgasLayerResource layer)
    {
        _renameDialog ??= new TextInputDialog();
        _renameDialog.Open(this, "重命名图层", layer.Name, "图层名称", value =>
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                LayerRenameRequested?.Invoke(layer, value);
            }
        });
    }
}
