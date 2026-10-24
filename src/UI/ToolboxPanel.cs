#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using AppAgasCs.Features.Tools.Model;
using AppAgasCs.Features.Tools.Storage;
using Godot;

namespace AGAS.UI;

public sealed partial class ToolboxPanel : Control
{
    private const string DefaultSource = "extends RefCounted\n\nfunc process(image: Image) -> Dictionary:\n\treturn {\"image\": image}\n";
    private const int DefaultDirectoryWidth = 300;
    private const int MinDirectoryWidth = 240;
    private const int MinEditorWidth = 300;
    private readonly ToolCatalog _tools;
    private readonly ItemList _items = new()
    {
        Name = "ToolboxItems",
        SelectMode = ItemList.SelectModeEnum.Single,
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
        SizeFlagsVertical = SizeFlags.ExpandFill,
    };
    private readonly Button _createTool = new() { Name = "ToolboxCreateTool", Text = "+ 工具" };
    private readonly Button _createChain = new() { Name = "ToolboxCreateChain", Text = "+ 工具链" };
    private readonly Button _refresh = new() { Name = "ToolboxRefresh", Text = "刷新" };
    private readonly Button _run = new() { Name = "ToolboxRun", Text = "运行" };
    private readonly Button _save = new() { Name = "ToolboxSave", Text = "保存" };
    private readonly Button _delete = new() { Name = "ToolboxDelete", Text = "删除" };
    private readonly LineEdit _name = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, Name = "ToolboxItemName", SizeFlagsHorizontal = SizeFlags.ExpandFill };
    private readonly Label _details = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, TooltipAutoTranslateMode = AutoTranslateModeEnum.Disabled, Name = "ToolboxDetails", ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
    private readonly Label _status = new()
    {
        Name = "ToolboxStatus",
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        TooltipAutoTranslateMode = AutoTranslateModeEnum.Disabled,
        ClipText = true,
        TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
    };
    private readonly CodeEdit _code = new()
    {
        Name = "ToolboxCodeEditor",
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        GuttersDrawLineNumbers = true,
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
        SizeFlagsVertical = SizeFlags.ExpandFill,
        CustomMinimumSize = new Vector2(300, 170),
    };
    private readonly VBoxContainer _chainEditor = new()
    {
        Name = "ToolboxChainEditor",
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
        SizeFlagsVertical = SizeFlags.ExpandFill,
    };
    private readonly ItemList _steps = new()
    {
        Name = "ToolboxChainSteps",
        SelectMode = ItemList.SelectModeEnum.Single,
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
        SizeFlagsVertical = SizeFlags.ExpandFill,
    };
    private readonly OptionButton _availableTools = new() { Name = "ToolboxAvailableTools", FitToLongestItem = false, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, CustomMinimumSize = new Vector2(160, 1) };
    private readonly Button _addStep = new() { Name = "ToolboxAddStep", Text = "添加" };
    private readonly Button _removeStep = new() { Name = "ToolboxRemoveStep", Text = "移除" };
    private readonly Button _moveUp = new() { Name = "ToolboxMoveStepUp", Text = "上移" };
    private readonly Button _moveDown = new() { Name = "ToolboxMoveStepDown", Text = "下移" };
    private readonly AcceptDialog _createDialog = new()
    {
        Name = "ToolboxCreateDialog",
        MinSize = new Vector2I(430, 140),
        OkButtonText = "创建",
    };
    private readonly LineEdit _createName = new()
    {
        Name = "ToolboxCreateName",
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        PlaceholderText = "名称",
    };
    private readonly ConfirmationDialog _deleteDialog = new()
    {
        Name = "ToolboxDeleteDialog",
        OkButtonText = "删除",
    };
    private readonly List<string> _stepIds = [];
    private IReadOnlyList<ToolDefinition> _toolItems = [];
    private IReadOnlyList<ToolChainDefinition> _chainItems = [];
    private string? _selectedId;
    private int _selectedRevision;
    private bool _creatingChain;
    private string _detailsKey = "未选择工具";
    private object[] _detailsArguments = Array.Empty<object>();
    private string _statusKey = string.Empty;
    private object[] _statusArguments = Array.Empty<object>();
    private string _deleteName = string.Empty;
    private bool _loading;
    private HSplitContainer? _split;
    private Control? _directory;
    private int _directoryWidth = DefaultDirectoryWidth;
    private bool _draggingSplit;
    private bool _directoryWidthApplyPending;

    public ToolboxPanel(ToolCatalog tools)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        Name = "ToolboxPanel";
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        AddChild(BuildLayout());
        BuildDialogs();
        WireEvents();
        ShowNoSelection();
    }

    public event Action<string>? RunRequested;

    public string? SelectedItemId => _selectedId;

    public override void _Ready()
    {
        _createName.PlaceholderText = Tr("名称");
        RefreshTools();
        ApplyDirectoryWidthDeferred();
    }

    public override void _Notification(int what)
    {
        if (what != NotificationTranslationChanged || !IsNodeReady()) return;
        _details.Text = string.Format(Tr(_detailsKey), _detailsArguments);
        _status.Text = string.Format(Tr(_statusKey), _statusArguments);
        _details.TooltipText = _details.Text;
        _status.TooltipText = _status.Text;
        _createName.PlaceholderText = Tr("名称");
        _deleteDialog.DialogText = string.Format(Tr("删除“{0}”？此操作无法撤销。"), _deleteName);
        ToolDefinition? tool = _toolItems.FirstOrDefault(item => item.Id == _selectedId);
        if (tool != null && tool.Kind != ToolImplementationKind.Gdscript)
            _code.Text = BuildReadOnlyCodeSummary(tool);
    }

    public void RefreshTools()
    {
        string? previous = _selectedId;
        try
        {
            _toolItems = _tools.ListTools();
            _chainItems = _tools.ListChains();
            _items.Clear();
            foreach (ToolDefinition item in _toolItems)
            {
                _items.AddItem(item.Name);
                _items.SetItemAutoTranslateMode(_items.ItemCount - 1, AutoTranslateModeEnum.Disabled);
                _items.SetItemMetadata(_items.ItemCount - 1, item.Id);
            }
            foreach (ToolChainDefinition item in _chainItems)
            {
                _items.AddItem($"{item.Name}  ·  工具链");
                _items.SetItemMetadata(_items.ItemCount - 1, item.Id);
            }

            _availableTools.Clear();
            foreach (ToolDefinition item in _toolItems)
            {
                _availableTools.AddItem(item.Name);
                _availableTools.SetItemAutoTranslateMode(_availableTools.ItemCount - 1, AutoTranslateModeEnum.Disabled);
                _availableTools.SetItemMetadata(_availableTools.ItemCount - 1, item.Id);
            }

            _createChain.Disabled = _availableTools.ItemCount == 0;
            if (previous != null && SelectItem(previous)) return;
            if (_items.ItemCount > 0)
            {
                SelectItem(_items.GetItemMetadata(0).AsString());
            }
            else
            {
                ShowNoSelection();
            }
            SetStatus(string.Empty);
        }
        catch (Exception error)
        {
            ShowOperationError("读取工具失败：{0}", error);
        }
    }

    public bool SelectItem(string id)
    {
        for (int index = 0; index < _items.ItemCount; index++)
        {
            if (_items.GetItemMetadata(index).AsString() != id) continue;
            _items.Select(index);
            ApplySelection(id);
            return true;
        }
        return false;
    }

    private Control BuildLayout()
    {
        var scroll = new ScrollContainer
        {
            Name = "ToolboxContentScroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            FollowFocus = true
        };
        scroll.GetHScrollBar().CustomMinimumSize = new Vector2(1, 16);
        scroll.GetVScrollBar().CustomMinimumSize = new Vector2(16, 1);
        scroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        scroll.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var split = new HSplitContainer
        {
            Name = "ToolboxSplit",
            SplitOffsets = new[] { -200 },
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        scroll.AddChild(split);
        _split = split;
        split.Resized += ApplyDirectoryWidthDeferred;
        split.DragStarted += () => _draggingSplit = true;
        split.DragEnded += () =>
        {
            _draggingSplit = false;
            if (_directory != null) _directoryWidth = (int)Math.Round(_directory.Size.X);
        };
        var directory = new VBoxContainer
        {
            Name = "ToolboxDirectory",
            CustomMinimumSize = new Vector2(MinDirectoryWidth, 1),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _directory = directory;
        directory.Resized += ApplyDirectoryWidthDeferred;
        var directoryActions = new HFlowContainer();
        directoryActions.AddChild(_createTool);
        directoryActions.AddChild(_createChain);
        directoryActions.AddChild(_refresh);
        directory.AddChild(directoryActions);
        directory.AddChild(_items);
        split.AddChild(directory);

        var editor = new VBoxContainer
        {
            Name = "ToolboxEditor",
            CustomMinimumSize = new Vector2(MinEditorWidth, 1),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        var toolbar = new HFlowContainer { Name = "ToolboxEditorToolbar" };
        toolbar.AddChild(_name);
        toolbar.AddChild(_save);
        toolbar.AddChild(_run);
        toolbar.AddChild(_delete);
        editor.AddChild(toolbar);
        editor.AddChild(_details);
        editor.AddChild(_code);
        BuildChainEditor();
        editor.AddChild(_chainEditor);
        editor.AddChild(_status);
        split.AddChild(editor);
        return scroll;
    }

    private void ApplyDirectoryWidthDeferred()
    {
        if (_directoryWidthApplyPending) return;
        _directoryWidthApplyPending = true;
        Callable.From(ApplyDirectoryWidth).CallDeferred();
    }

    private void ApplyDirectoryWidth()
    {
        _directoryWidthApplyPending = false;
        if (!IsInsideTree() || _draggingSplit || _split == null || _directory == null) return;
        int available = Math.Max(0, (int)Math.Round(_split.Size.X) - _split.GetThemeConstant("separation"));
        if (available <= 0) return;

        int desired = Math.Clamp(_directoryWidth,
            Math.Min(MinDirectoryWidth, available),
            Math.Max(MinDirectoryWidth, available - MinEditorWidth));
        int current = (int)Math.Round(_directory.Size.X);
        if (current == desired) return;
        _split.SplitOffsets = new[] { desired - available / 2 };
    }

    private void BuildChainEditor()
    {
        _chainEditor.AddChild(new Label { Text = "执行顺序" });
        _chainEditor.AddChild(_steps);
        var actions = new HFlowContainer();
        _availableTools.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        actions.AddChild(_availableTools);
        actions.AddChild(_addStep);
        actions.AddChild(_removeStep);
        actions.AddChild(_moveUp);
        actions.AddChild(_moveDown);
        _chainEditor.AddChild(actions);
    }

    private void BuildDialogs()
    {
        _createDialog.AddChild(_createName);
        AddChild(_createDialog);
        AddChild(_deleteDialog);
    }

    private void WireEvents()
    {
        _items.ItemSelected += index => ApplySelection(_items.GetItemMetadata((int)index).AsString());
        _createTool.Pressed += () => OpenCreateDialog(false);
        _createChain.Pressed += () => OpenCreateDialog(true);
        _refresh.Pressed += RefreshTools;
        _save.Pressed += SaveSelection;
        _run.Pressed += () => { if (_selectedId != null) RunRequested?.Invoke(_selectedId); };
        _delete.Pressed += () =>
        {
            if (_selectedId == null) return;
            _deleteName = _name.Text;
            _deleteDialog.GetLabel().AutoTranslateMode = AutoTranslateModeEnum.Disabled;
            _deleteDialog.DialogText = string.Format(Tr("删除“{0}”？此操作无法撤销。"), _deleteName);
            _deleteDialog.PopupCentered();
        };
        _deleteDialog.Confirmed += DeleteSelection;
        _createDialog.Confirmed += CreateItem;
        _addStep.Pressed += () =>
        {
            int selected = _availableTools.Selected;
            if (selected < 0) return;
            _stepIds.Add(_availableTools.GetItemMetadata(selected).AsString());
            RefreshSteps(_stepIds.Count - 1);
        };
        _removeStep.Pressed += () =>
        {
            int selected = GetSelectedStep();
            if (selected < 0) return;
            _stepIds.RemoveAt(selected);
            RefreshSteps(Math.Min(selected, _stepIds.Count - 1));
        };
        _moveUp.Pressed += () => MoveStep(-1);
        _moveDown.Pressed += () => MoveStep(1);
        _steps.ItemSelected += _ => UpdateStepButtons();
    }

    private void OpenCreateDialog(bool chain)
    {
        _creatingChain = chain;
        _createName.Text = string.Empty;
        _createDialog.Title = chain ? "新建工具链" : "新建自定义工具";
        _createDialog.PopupCentered();
        _createName.GrabFocus();
    }

    private void CreateItem()
    {
        string name = _createName.Text.Trim();
        if (name.Length == 0)
        {
            SetStatus("请输入名称。", true);
            return;
        }

        string id = (_creatingChain ? "chain-" : "tool-") + Guid.NewGuid().ToString("N")[..12];
        try
        {
            if (_creatingChain)
            {
                // A new chain stays in the editor until it has at least two steps.
                _selectedId = id;
                _selectedRevision = 0;
                _name.Text = name;
                _name.Editable = true;
                _stepIds.Clear();
                _code.Visible = false;
                _chainEditor.Visible = true;
                SetDetails("未保存的工具链");
                _save.Disabled = false;
                _run.Disabled = true;
                _delete.Disabled = true;
                RefreshSteps(-1);
                SetStatus("添加至少两个工具后保存。");
            }
            else
            {
                _tools.CreateTool(id, name, DefaultSource);
                RefreshTools();
                SelectItem(id);
            }
        }
        catch (Exception error)
        {
            ShowOperationError("创建失败：{0}", error);
        }
    }

    private void ApplySelection(string id)
    {
        ToolDefinition? tool = _toolItems.FirstOrDefault(candidate => candidate.Id == id);
        ToolChainDefinition? chain = _chainItems.FirstOrDefault(candidate => candidate.Id == id);
        if (tool == null && chain == null) return;
        _loading = true;
        try
        {
            _selectedId = id;
            _selectedRevision = tool?.Revision ?? chain!.Revision;
            _name.Text = tool?.Name ?? chain!.Name;
            _name.Editable = tool?.IsReadOnly != true;
            _delete.Disabled = tool?.IsReadOnly == true;
            _run.Disabled = false;
            _code.Visible = tool != null;
            _chainEditor.Visible = chain != null;
            _code.Editable = tool?.Kind == ToolImplementationKind.Gdscript && !tool.IsReadOnly;
            _stepIds.Clear();
            if (chain != null)
            {
                _stepIds.AddRange(chain.ToolIds);
                SetDetails("工具链 · {0} 步", chain.ToolIds.Count);
                RefreshSteps(-1);
            }
            else
            {
                _code.Text = tool!.Kind == ToolImplementationKind.Gdscript
                    ? tool.Source ?? string.Empty
                    : BuildReadOnlyCodeSummary(tool);
                SetDetails(tool.IsReadOnly ? "内置工具 · 只读" : "自定义工具");
            }
            _save.Disabled = tool?.IsReadOnly == true;
            SetStatus(string.Empty);
        }
        catch (Exception error)
        {
            ShowOperationError("加载工具失败：{0}", error);
        }
        finally
        {
            _loading = false;
        }
    }

    private void SaveSelection()
    {
        if (_loading || _selectedId == null) return;
        string name = _name.Text.Trim();
        if (name.Length == 0)
        {
            SetStatus("请输入名称。", true);
            return;
        }
        ToolDefinition? tool = _toolItems.FirstOrDefault(candidate => candidate.Id == _selectedId);
        ToolChainDefinition? chain = _chainItems.FirstOrDefault(candidate => candidate.Id == _selectedId);
        try
        {
            if (tool == null)
            {
                if (_stepIds.Count < 2)
                {
                    SetStatus("工具链至少需要两个工具。", true);
                    return;
                }
                if (chain == null) _tools.CreateChain(_selectedId, name, _stepIds);
                else _tools.UpdateChain(_selectedId, _selectedRevision, name, _stepIds);
            }
            else if (tool.Kind == ToolImplementationKind.Gdscript && !tool.IsReadOnly)
            {
                _tools.UpdateTool(_selectedId, _selectedRevision, name, _code.Text);
            }
            else
            {
                throw new InvalidOperationException("内置工具不可修改。");
            }
            RefreshTools();
            SelectItem(_selectedId);
            SetStatus("已保存。");
        }
        catch (Exception error)
        {
            ShowOperationError("保存失败：{0}", error);
        }
    }

    private void DeleteSelection()
    {
        if (_selectedId == null) return;
        try
        {
            ToolDefinition? tool = _toolItems.FirstOrDefault(candidate => candidate.Id == _selectedId);
            if (tool != null) _tools.DeleteTool(_selectedId, _selectedRevision);
            else _tools.DeleteChain(_selectedId, _selectedRevision);
            _selectedId = null;
            RefreshTools();
            SetStatus("已删除。");
        }
        catch (Exception error)
        {
            ShowOperationError("删除失败：{0}", error);
        }
    }

    private void RefreshSteps(int selected)
    {
        _steps.Clear();
        for (int index = 0; index < _stepIds.Count; index++)
        {
            string id = _stepIds[index];
            string name = _toolItems.FirstOrDefault(item => item.Id == id)?.Name ?? id;
            _steps.AddItem($"{index + 1}. {name}");
            _steps.SetItemAutoTranslateMode(_steps.ItemCount - 1, AutoTranslateModeEnum.Disabled);
        }
        if (selected >= 0 && selected < _steps.ItemCount) _steps.Select(selected);
        UpdateStepButtons();
    }

    private void MoveStep(int distance)
    {
        int selected = GetSelectedStep();
        int target = selected + distance;
        if (selected < 0 || target < 0 || target >= _stepIds.Count) return;
        (_stepIds[selected], _stepIds[target]) = (_stepIds[target], _stepIds[selected]);
        RefreshSteps(target);
    }

    private int GetSelectedStep()
    {
        int[] selected = _steps.GetSelectedItems();
        return selected.Length == 0 ? -1 : selected[0];
    }

    private void UpdateStepButtons()
    {
        int selected = GetSelectedStep();
        bool readOnly = _selectedId != null && _toolItems.FirstOrDefault(item => item.Id == _selectedId)?.IsReadOnly == true;
        _addStep.Disabled = readOnly || _availableTools.ItemCount == 0;
        _removeStep.Disabled = readOnly || selected < 0;
        _moveUp.Disabled = readOnly || selected <= 0;
        _moveDown.Disabled = readOnly || selected < 0 || selected >= _stepIds.Count - 1;
    }

    private void ShowNoSelection()
    {
        _selectedId = null;
        _name.Text = string.Empty;
        _name.Editable = false;
        SetDetails("未选择工具");
        _code.Visible = false;
        _chainEditor.Visible = false;
        _save.Disabled = true;
        _run.Disabled = true;
        _delete.Disabled = true;
    }

    private void SetDetails(string key, params object[] arguments)
    {
        _detailsKey = key;
        _detailsArguments = arguments;
        _details.Text = string.Format(Tr(key), arguments);
        _details.TooltipText = _details.Text;
    }

    private void SetStatus(string message, bool error = false, params object[] arguments)
    {
        _statusKey = message;
        _statusArguments = arguments;
        _status.Text = string.Format(Tr(message), arguments);
        _status.TooltipText = _status.Text;
        _status.AddThemeColorOverride("font_color", error ? new Color("e5a1a1") : new Color("9db7a5"));
    }

    private void ShowOperationError(string operation, Exception error)
    {
        GD.PushWarning($"Toolbox {operation} failed: {error}");
        SetStatus(operation, true, FormatErrorMessageForUi(error.Message));
    }

    internal static string FormatErrorMessageForUi(string message) => message;

    private string BuildReadOnlyCodeSummary(ToolDefinition item)
    {
        string operation = item.Id switch
        {
            "resize-256" => "将图片缩放至 256×256。",
            "crop-128" => "裁剪图片左上角的 128×128 区域。",
            "matting-edge-connected" => "执行边缘连通抠图。",
            "matting-checkerboard" => "执行棋盘格抠图。",
            "matting-light-single" => "执行浅色背景抠图。",
            "defringe-white" => "去除白色边缘杂色。",
            _ => "由当前工具定义执行图片处理。",
        };
        return string.Format(Tr("# {0}\n# {1}\n# 输入：Image\n# 输出：Image\n# 此工具没有可编辑的 GDScript 源码。"), item.Name, Tr(operation));
    }
}
