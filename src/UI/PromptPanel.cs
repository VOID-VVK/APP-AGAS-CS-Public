using System;
using System.Collections.Generic;
using System.Linq;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Prompting;
using AppAgasCs.UI.Shared;
using Godot;

namespace AGAS.UI;

public partial class PromptPanel : PanelContainer
{
    private const int MinMergedPreviewWidth = 240;
    private const int MinFragmentListWidth = 300;
    private const int FragmentSpacing = 12;

    public event Action<AgasLayerResource>? PromptAddRequested;
    public event Action<AgasLayerResource, PromptFragmentResource>? PromptDeleteRequested;
    public event Action<PromptFragmentResource, string>? PromptTextUpdateRequested;
    public event Action<PromptFragmentResource, bool>? PromptEnabledSetRequested;
    public event Action<AgasLayerResource, PromptFragmentResource, int>? PromptMoveRequested;

    private AgasLayerResource? _layer;
    private VBoxContainer? _list;
    private TextEdit? _mergedPreview;
    private HSplitContainer? _previewSplit;
    private Control? _previewSection;
    private Button? _addButton;
    private Label? _readOnlyLabel;
    private bool _refreshing;
    private bool _draggingPreviewSplit;
    private bool _hasUserAdjustedPreviewWidth;
    private int _mergedPreviewWidth;
    private readonly Dictionary<PromptFragmentResource, FragmentRow> _rows = new();

    private sealed record FragmentRow(
        HBoxContainer Root,
        CheckBox Enabled,
        PromptFragmentTextEdit Editor,
        Button Up,
        Button Down,
        Button Delete);

    public override void _Ready()
    {
        Name = "PromptPanel";
        BuildUi();
        Refresh();
        Callable.From(ApplyMergedPreviewWidthAfterLayout).CallDeferred();
    }

    public void SetLayer(AgasLayerResource? layer)
    {
        if (!ReferenceEquals(_layer, layer))
        {
            ClearRows();
        }
        _layer = layer;
        Refresh();
    }

    private void BuildUi()
    {
        MarginContainer margin = AgasMarginContainerFactory.Create(8, 8, 8, 8);
        AddChild(margin);

        var root = new VBoxContainer
        {
            Name = "PromptContent",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        margin.AddChild(root);

        _addButton = new Button
        {
            Name = "AddPromptFragmentButton",
            Text = "+ 片段",
            TooltipText = "新增提示词片段",
            FocusMode = FocusModeEnum.All
        };
        _addButton.Pressed += AddFragment;
        _readOnlyLabel = new Label
        {
            Name = "PromptReadOnlyLabel",
            Text = string.Empty,
            Visible = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        _previewSplit = new HSplitContainer
        {
            Name = "PromptPreviewSplit",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        // Center by default; preserve the preview width only after a manual drag.
        _previewSplit.DragStarted += OnPreviewSplitDragStarted;
        _previewSplit.DragEnded += OnPreviewSplitDragEnded;
        var paneScroll = new ScrollContainer
        {
            Name = "PromptPaneScroll",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            FollowFocus = true,
        };
        root.AddChild(paneScroll);
        paneScroll.AddChild(_previewSplit);

        var fragmentSection = new VBoxContainer
        {
            Name = "PromptFragmentSection",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(MinFragmentListWidth, 1)
        };
        fragmentSection.AddThemeConstantOverride("separation", FragmentSpacing);
        _previewSplit.AddChild(fragmentSection);

        var fragmentHeader = new HBoxContainer();
        fragmentSection.AddChild(fragmentHeader);
        fragmentHeader.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        fragmentHeader.AddChild(_addButton);
        fragmentSection.AddChild(_readOnlyLabel);

        var scroll = new ScrollContainer
        {
            Name = "PromptFragmentScroll",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(MinFragmentListWidth, 1),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        fragmentSection.AddChild(scroll);

        _list = new VBoxContainer
        {
            Name = "PromptFragmentList",
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _list.AddThemeConstantOverride("separation", FragmentSpacing);
        scroll.AddChild(_list);

        // Both panes expand so SplitOffsets are relative to the shared midpoint.
        _previewSection = new VBoxContainer
        {
            Name = "PromptMergedPreviewSection",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(MinMergedPreviewWidth, 1)
        };
        _previewSection.AddThemeConstantOverride("separation", FragmentSpacing);
        _previewSplit.AddChild(_previewSection);
        _previewSection.Resized += OnPreviewSplitResized;

        _previewSection.AddChild(new Label
        {
            Name = "PromptMergedPreviewTitle",
            Text = "合并预览",
            TooltipText = "拖动左侧分隔条可调整合并预览宽度",
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        });
        _mergedPreview = new TextEdit
        {
            Name = "PromptMergedPreview",
            AutoTranslateMode = AutoTranslateModeEnum.Disabled,
            Editable = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(MinMergedPreviewWidth, 1),
            WrapMode = TextEdit.LineWrappingMode.Boundary
        };
        _previewSection.AddChild(_mergedPreview);
    }

    private void OnPreviewSplitDragStarted()
    {
        _draggingPreviewSplit = true;
    }

    private void OnPreviewSplitDragEnded()
    {
        _draggingPreviewSplit = false;
        if (_previewSplit == null)
        {
            return;
        }

        _mergedPreviewWidth = GetCurrentMergedPreviewWidth(_mergedPreviewWidth);
        _hasUserAdjustedPreviewWidth = true;
        ApplyMergedPreviewWidthAfterLayout();
    }

    private void OnPreviewSplitResized()
    {
        if (_draggingPreviewSplit)
        {
            return;
        }

        ApplyMergedPreviewWidthDeferred();
    }

    private void ApplyMergedPreviewWidthDeferred()
    {
        Callable.From(ApplyMergedPreviewWidthAfterLayout).CallDeferred();
    }

    private void ApplyMergedPreviewWidthAfterLayout()
    {
        if (_previewSplit == null || !IsInsideTree() || _draggingPreviewSplit)
        {
            return;
        }

        int available = GetAvailableSplitSize(_previewSplit);
        if (available <= 0)
        {
            return;
        }

        int desiredWidth = _hasUserAdjustedPreviewWidth ? _mergedPreviewWidth : available / 2;
        int previewWidth = ClampMergedPreviewWidth(desiredWidth, available);

        // Adjust from the live trailing pane size so split offsets and expand flags agree.
        int currentFirstSize = GetActualLeadingPaneSize(_previewSplit, available);
        int currentSecondSize = available - currentFirstSize;
        if (currentSecondSize == previewWidth)
        {
            return;
        }

        int currentOffset = _previewSplit.SplitOffsets.Length > 0 ? _previewSplit.SplitOffsets[0] : 0;
        _previewSplit.SplitOffsets = new[] { currentOffset + currentSecondSize - previewWidth };
    }

    private int GetCurrentMergedPreviewWidth(int fallback)
    {
        if (_previewSplit == null)
        {
            return fallback;
        }

        int available = GetAvailableSplitSize(_previewSplit);
        if (available <= 0)
        {
            return fallback;
        }

        if (_previewSection != null && _previewSection.Size.X > 1f)
        {
            return ClampMergedPreviewWidth((int)Math.Round(_previewSection.Size.X), available);
        }

        int currentFirstSize = GetActualLeadingPaneSize(_previewSplit, available);
        return ClampMergedPreviewWidth(available - currentFirstSize, available);
    }

    private static int GetActualLeadingPaneSize(SplitContainer split, int available)
    {
        if (split.GetChildCount() == 0 || split.GetChild(0) is not Control first)
        {
            return available / 2;
        }

        int measured = (int)Math.Round(first.Size.X);
        return measured > 0 ? Math.Clamp(measured, 0, available) : available / 2;
    }

    private static int ClampMergedPreviewWidth(int desiredWidth, int available)
    {
        if (available <= 0)
        {
            return MinMergedPreviewWidth;
        }

        int lower = Math.Min(MinMergedPreviewWidth, available);
        int upper = Math.Max(0, available - Math.Min(MinFragmentListWidth, available));
        if (lower > upper)
        {
            return available / 2;
        }

        return Math.Clamp(desiredWidth, lower, upper);
    }

    private static int GetAvailableSplitSize(SplitContainer split)
    {
        int total = (int)Math.Round(split.Size.X);
        int separation = split.GetThemeConstant("separation");
        return Math.Max(0, total - separation);
    }

    private void AddFragment()
    {
        if (_layer == null || IsReadOnly())
        {
            Refresh();
            return;
        }

        PromptAddRequested?.Invoke(_layer);
    }

    private void Refresh()
    {
        if (_list == null)
        {
            return;
        }

        _refreshing = true;
        string readOnlyMessage = GetReadOnlyMessage();
        bool readOnly = !string.IsNullOrEmpty(readOnlyMessage);
        if (_addButton != null)
        {
            _addButton.Disabled = readOnly;
            _addButton.TooltipText = readOnly ? readOnlyMessage : "新增提示词片段";
        }
        if (_readOnlyLabel != null)
        {
            _readOnlyLabel.Visible = readOnly;
            _readOnlyLabel.Text = readOnly ? readOnlyMessage : string.Empty;
        }

        if (_layer == null)
        {
            if (_list.GetChildCount() == 0)
            {
                _list.AddChild(new Label { Text = "未选择当前图层。" });
            }
            UpdateMergedPreview();
            _refreshing = false;
            return;
        }

        var ordered = _layer.PromptFragments.OrderBy(fragment => fragment.Order).ToList();
        foreach (PromptFragmentResource removed in _rows.Keys.Where(fragment => !ordered.Contains(fragment)).ToList())
        {
            FragmentRow stale = _rows[removed];
            stale.Editor.CancelResize();
            _list.RemoveChild(stale.Root);
            stale.Root.QueueFree();
            _rows.Remove(removed);
        }
        for (int index = 0; index < ordered.Count; index++)
        {
            PromptFragmentResource fragment = ordered[index];
            if (!_rows.TryGetValue(fragment, out FragmentRow? row))
            {
                row = CreateRow(fragment);
                _rows.Add(fragment, row);
                _list.AddChild(row.Root);
            }
            if (row.Root.GetIndex() != index)
            {
                _list.MoveChild(row.Root, index);
            }
            row.Enabled.SetPressedNoSignal(fragment.Enabled);
            row.Enabled.Disabled = readOnly;
            row.Enabled.TooltipText = readOnly ? readOnlyMessage : "启用或停用该提示词片段";
            row.Editor.SynchronizeText(fragment.Text);
            row.Editor.TextEditor.Editable = !readOnly;
            row.Editor.SetTooltipKey(readOnly ? readOnlyMessage : "支持多行输入，修改即时同步；拖动右下角调整高度");
            row.Up.Disabled = readOnly;
            row.Down.Disabled = readOnly;
            row.Delete.Disabled = readOnly;
            row.Up.TooltipText = readOnly ? readOnlyMessage : "上移提示词片段";
            row.Down.TooltipText = readOnly ? readOnlyMessage : "下移提示词片段";
            row.Delete.TooltipText = readOnly ? readOnlyMessage : "删除提示词片段";
        }
        UpdateMergedPreview();
        _refreshing = false;
    }

    private void ClearRows()
    {
        foreach (FragmentRow row in _rows.Values)
        {
            row.Editor.CancelResize();
        }
        _rows.Clear();
        if (_list == null)
        {
            return;
        }
        foreach (Node child in _list.GetChildren())
        {
            _list.RemoveChild(child);
            child.QueueFree();
        }
    }

    private FragmentRow CreateRow(PromptFragmentResource fragment)
    {
        var row = new HBoxContainer
        {
            Name = "PromptFragmentRow",
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        row.AddThemeConstantOverride("separation", FragmentSpacing);

        var enabled = new CheckBox
        {
            Name = "PromptFragmentEnabled",
            ButtonPressed = fragment.Enabled,
            FocusMode = FocusModeEnum.All,
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        enabled.Toggled += pressed =>
        {
            if (!_refreshing && IsCurrentFragment(fragment))
            {
                SetFragmentEnabled(fragment, pressed);
            }
        };
        row.AddChild(enabled);

        var editor = new PromptFragmentTextEdit();
        TextEdit text = editor.TextEditor;
        text.Text = fragment.Text;
        text.TextChanged += () =>
        {
            if (!_refreshing && !editor.SynchronizingText && IsCurrentFragment(fragment))
            {
                SetFragmentText(fragment, text.Text);
            }
        };
        row.AddChild(editor);

        Button up = AgasIconButtonFactory.Create(
            LayerToggleIcons.MoveUp(),
            "上移提示词片段",
            () => MoveFragment(fragment, -1));
        up.Name = "PromptFragmentMoveUp";
        up.CustomMinimumSize = Vector2.One * AgasIconButtonFactory.ControlSize;
        up.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        up.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(up);

        Button down = AgasIconButtonFactory.Create(
            LayerToggleIcons.MoveDown(),
            "下移提示词片段",
            () => MoveFragment(fragment, 1));
        down.Name = "PromptFragmentMoveDown";
        down.CustomMinimumSize = Vector2.One * AgasIconButtonFactory.ControlSize;
        down.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        down.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(down);

        Button delete = AgasIconButtonFactory.Create(
            LayerToggleIcons.Delete(),
            "删除提示词片段",
            () => DeleteFragment(fragment));
        delete.Name = "PromptFragmentDelete";
        delete.CustomMinimumSize = Vector2.One * AgasIconButtonFactory.ControlSize;
        delete.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        delete.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(delete);

        return new FragmentRow(row, enabled, editor, up, down, delete);
    }

    private bool IsCurrentFragment(PromptFragmentResource fragment) =>
        _layer != null && _layer.PromptFragments.Contains(fragment) && _rows.ContainsKey(fragment);

    private void MoveFragment(PromptFragmentResource fragment, int direction)
    {
        if (!IsCurrentFragment(fragment))
        {
            return;
        }
        if (_layer == null || IsReadOnly())
        {
            Refresh();
            return;
        }

        var ordered = _layer.PromptFragments.OrderBy(item => item.Order).ToList();
        int index = ordered.IndexOf(fragment);
        int targetIndex = index + direction;
        if (index < 0 || targetIndex < 0 || targetIndex >= ordered.Count)
        {
            return;
        }

        PromptMoveRequested?.Invoke(_layer, fragment, direction);
    }

    private void SetFragmentEnabled(PromptFragmentResource fragment, bool enabled)
    {
        if (_layer == null || IsReadOnly())
        {
            Refresh();
            return;
        }
        if (fragment.Enabled == enabled)
        {
            Refresh();
            return;
        }

        PromptEnabledSetRequested?.Invoke(fragment, enabled);
    }

    private void SetFragmentText(PromptFragmentResource fragment, string value)
    {
        if (_layer == null || IsReadOnly())
        {
            Refresh();
            return;
        }
        if (fragment.Text == value)
        {
            return;
        }

        PromptTextUpdateRequested?.Invoke(fragment, value);
    }

    private void DeleteFragment(PromptFragmentResource fragment)
    {
        if (!IsCurrentFragment(fragment))
        {
            return;
        }
        if (_layer == null || IsReadOnly())
        {
            Refresh();
            return;
        }

        PromptDeleteRequested?.Invoke(_layer, fragment);
    }

    private bool IsReadOnly()
    {
        return !string.IsNullOrEmpty(GetReadOnlyMessage());
    }

    private string GetReadOnlyMessage()
    {
        if (_layer == null)
        {
            return "未选择当前图层，提示词只读。";
        }
        if (!_layer.Visible)
        {
            return "图层已隐藏，提示词只读。";
        }
        if (_layer.Locked)
        {
            return "图层已锁定，提示词只读。";
        }
        return string.Empty;
    }

    private void UpdateMergedPreview()
    {
        if (_mergedPreview == null)
        {
            return;
        }

        if (_layer == null)
        {
            _mergedPreview.Text = string.Empty;
            return;
        }

        _mergedPreview.Text = PromptComposer.Compose(
            _layer.PromptFragments,
            null,
            PromptCompositionMode.ReferenceEdit);
    }
}
