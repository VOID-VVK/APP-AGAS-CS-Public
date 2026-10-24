using System;
using System.IO;
using System.Linq;
using AGAS.UI.Dialogs;
using AppAgasCs.Core.Resources;
using AppAgasCs.UI.Shared;
using Godot;

namespace AGAS.UI;

public partial class FilesScenesPanel : PanelContainer
{
    private const int DefaultPreviewHeight = 180;
    private const int MinPreviewHeight = 80;
    private const int SceneContextRename = 1;
    private const int SceneContextDelete = 2;
    private const int SceneContextExport = 3;
    private const int SceneContextImport = 4;

    public event Action<string>? ImageSelected;
    public event Action<string, string>? ImagePreviewRequested;
    public event Action<string>? SceneSelected;
    public event Action? SceneAddRequested;
    public event Action<string, string>? SceneRenameRequested;
    public event Action<string>? SceneDeleteRequested;
    public event Action<string>? SceneExportRequested;
    public event Action<string>? SceneImportRequested;
    public event Action<string>? ActiveTabChanged;

    private string _projectRoot = string.Empty;
    private AgasProjectResource? _project;
    private string _activeSceneId = string.Empty;
    private TabContainer? _tabs;
    private ProjectFilesTree? _filesTree;
    private LineEdit? _fileSearch;
    private ItemList? _scenesList;
    private PopupMenu? _scenesContextMenu;
    private FileDialog? _sceneImportDialog;
    private TextInputDialog? _renameDialog;
    private int _previewHeight = DefaultPreviewHeight;
    private bool _previewCollapsed;
    private Control? _workspaceHost;
    private Control? _hostingDock;
    private SplitContainer? _hostingSplit;
    private Control? _rightHostingDock;
    private SplitContainer? _rightHostingSplit;
    private int _collapsedLayoutFrames;

    public override void _Ready()
    {
        Name = "FilesScenesPanel";
        BuildUi();
        SetProcess(false);
        Callable.From(BindHostLayout).CallDeferred();
    }

    public override void _Notification(int what)
    {
        if (what != NotificationTranslationChanged || !IsNodeReady()) return;
        if (_fileSearch != null)
        {
            _fileSearch.PlaceholderText = Tr("搜索文件名…");
            _fileSearch.TooltipText = Tr("按图片文件名搜索");
        }
        UpdateSceneImportFilters();
    }

    private void UpdateSceneImportFilters()
    {
        if (_sceneImportDialog == null) return;
        _sceneImportDialog.Filters = new[]
        {
            "*.zip ; " + Tr("AGAS 场景包"),
            "scene.json ; " + Tr("旧版场景包清单"),
        };
    }

    public override void _Process(double delta)
    {
        if (_collapsedLayoutFrames <= 0)
        {
            SetProcess(false);
            return;
        }

        EnforceCollapsedHostWidth();
        _collapsedLayoutFrames--;
    }

    public override void _ExitTree()
    {
        if (_workspaceHost != null)
        {
            _workspaceHost.Resized -= OnWorkspaceHostResized;
        }
        _workspaceHost = null;
        _hostingDock = null;
        _hostingSplit = null;
        _rightHostingDock = null;
        _rightHostingSplit = null;
    }

    public void OpenProject(string projectRoot, AgasProjectResource project, string activeSceneId)
    {
        _projectRoot = projectRoot;
        _project = project;
        _activeSceneId = activeSceneId;
        RefreshFiles();
        RefreshScenes();
    }

    public void SetActiveScene(string activeSceneId)
    {
        _activeSceneId = activeSceneId;
        RefreshScenes();
    }

    public string ActiveTab
    {
        get => _tabs?.CurrentTab switch
        {
            1 => "Scenes",
            _ => "Files"
        };
        set
        {
            if (_tabs != null)
            {
                _tabs.CurrentTab = string.Equals(value, "Scenes", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 0;
            }
        }
    }

    public int PreviewHeight
    {
        get => _previewHeight;
        set => _previewHeight = Math.Max(MinPreviewHeight, value);
    }

    public bool PreviewCollapsed
    {
        get => _previewCollapsed;
        set => _previewCollapsed = value;
    }

    private void BuildUi()
    {
        MarginContainer margin = AgasMarginContainerFactory.Create(6, 6, 6, 6);
        var scroll = new ScrollContainer
        {
            Name = "FilesScenesContentScroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            FollowFocus = true
        };
        scroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        scroll.GetVScrollBar().CustomMinimumSize = new Vector2(16, 1);
        AddChild(scroll);
        margin.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        margin.SizeFlagsVertical = SizeFlags.ExpandFill;
        scroll.AddChild(margin);

        var topPanel = new VBoxContainer
        {
            Name = "FilesScenesTop",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        margin.AddChild(topPanel);

        _tabs = new TabContainer
        {
            Name = "FilesScenesTabs",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };

        _fileSearch = new LineEdit
        {
            AutoTranslateMode = AutoTranslateModeEnum.Disabled,
            Name = "FileSearchEdit",
            PlaceholderText = Tr("搜索文件名…"),
            TooltipText = Tr("按图片文件名搜索"),
            AccessibilityName = "搜索文件名",
            ClearButtonEnabled = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        topPanel.AddChild(_fileSearch);
        topPanel.AddChild(_tabs);

        var filesRoot = new VBoxContainer
        {
            Name = "FilesTab",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _tabs.AddChild(filesRoot);
        _tabs.SetTabTitle(0, "文件");

        _filesTree = new ProjectFilesTree
        {
            Name = "Files",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        _fileSearch.TextChanged += OnFileSearchTextChanged;
        _filesTree.ImageSelected += SelectImage;
        filesRoot.AddChild(_filesTree);

        var scenesRoot = new VBoxContainer
        {
            Name = "Scenes",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        scenesRoot.AddThemeConstantOverride("separation", 4);
        _tabs.AddChild(scenesRoot);
        _tabs.SetTabTitle(1, "场景");

        // Only + stays as a visible control; rename/delete live on the item context menu.
        scenesRoot.AddChild(CreateAddRowButton(
            "SceneAddButton",
            "新增场景",
            () => SceneAddRequested?.Invoke()));

        _scenesList = new ItemList
        {
            Name = "ScenesList",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            AllowReselect = true,
            // Godot ItemList incremental search does not ignore Cmd/Ctrl. Typing S while
            // the list is focused (e.g. Cmd+S save) jumps between scenes whose names
            // start with "S" and fires ItemSelected. Keep arrow-key navigation; disable search.
            AllowSearch = false,
        };
        _scenesList.ItemSelected += OnSceneSelected;
        _scenesList.GuiInput += OnScenesListGuiInput;
        scenesRoot.AddChild(_scenesList);

        _scenesContextMenu = new PopupMenu { Name = "ScenesContextMenu" };
        _scenesContextMenu.AddItem("重命名", SceneContextRename);
        _scenesContextMenu.AddItem("删除", SceneContextDelete);
        _scenesContextMenu.AddSeparator();
        _scenesContextMenu.AddItem("导出场景包…", SceneContextExport);
        _scenesContextMenu.SetItemTooltip(
            _scenesContextMenu.GetItemIndex(SceneContextExport),
            "导出为项目 .agas/scene-exports/ 下的单个 ZIP 文件");
        _scenesContextMenu.AddItem("导入场景包…", SceneContextImport);
        _scenesContextMenu.SetItemTooltip(
            _scenesContextMenu.GetItemIndex(SceneContextImport),
            "选择场景包 ZIP，程序将自动解压并导入为全新场景");
        _scenesContextMenu.IdPressed += OnScenesContextMenuIdPressed;
        scenesRoot.AddChild(_scenesContextMenu);

        _sceneImportDialog = new FileDialog
        {
            Name = "ScenePackageImportDialog",
            Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Title = "导入场景包（选择 ZIP 文件）",
            Filters = new[]
            {
                "*.zip ; AGAS 场景包",
                "scene.json ; 旧版场景包清单",
            },
        };
        _sceneImportDialog.FileSelected += path => SceneImportRequested?.Invoke(path);
        scenesRoot.AddChild(_sceneImportDialog);
        UpdateSceneImportFilters();

        _tabs.TabChanged += OnTabChanged;
    }

    private void OnFileSearchTextChanged(string text)
    {
        _filesTree?.SetFileNameFilter(text);
        if (!string.IsNullOrWhiteSpace(text) && _tabs != null && _tabs.CurrentTab != 0)
        {
            _tabs.CurrentTab = 0;
        }
    }

    private void BindHostLayout()
    {
        Node? current = GetParent();
        while (current != null)
        {
            if (current is Control control && control.Name == "LeftWorkspaceDock")
            {
                _hostingDock = control;
                _hostingSplit = control.GetParent() as SplitContainer;
            }
            if (current is Control host && host.Name == "MainWorkspace")
            {
                _workspaceHost = host;
                _rightHostingDock = host.FindChild("RightWorkspaceDock", recursive: true, owned: false) as Control;
                _rightHostingSplit = _rightHostingDock?.GetParent() as SplitContainer;
                _workspaceHost.Resized += OnWorkspaceHostResized;
                break;
            }
            current = current.GetParent();
        }
    }

    private void OnWorkspaceHostResized()
    {
        if (!Visible)
        {
            _collapsedLayoutFrames = 4;
            SetProcess(true);
        }
    }

    private void EnforceCollapsedHostWidth()
    {
        if (Visible || _hostingDock == null || _hostingSplit == null)
        {
            return;
        }

        int desiredWidth = Math.Max(1, (int)Math.Round(_hostingDock.CustomMinimumSize.X));
        int available = Math.Max(0, (int)Math.Round(_hostingSplit.Size.X) - _hostingSplit.GetThemeConstant("separation"));
        if (available > 0)
        {
            _hostingSplit.SplitOffsets = new[] { desiredWidth - (available / 2) };
        }

        if (_rightHostingDock != null
            && _rightHostingSplit != null
            && _rightHostingDock.CustomMinimumSize.X <= 64)
        {
            int rightWidth = Math.Max(1, (int)Math.Round(_rightHostingDock.CustomMinimumSize.X));
            int rightAvailable = Math.Max(0,
                (int)Math.Round(_rightHostingSplit.Size.X) - _rightHostingSplit.GetThemeConstant("separation"));
            if (rightAvailable > 0)
            {
                _rightHostingSplit.SplitOffsets = new[] { (rightAvailable / 2) - rightWidth };
            }
        }
    }

    private void OnTabChanged(long tab)
    {
        ActiveTabChanged?.Invoke(ActiveTab);
    }

    private void RefreshFiles()
    {
        if (_filesTree == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_projectRoot) || !Directory.Exists(_projectRoot))
        {
            _filesTree.ShowUnavailableProject();
            return;
        }

        _filesTree.LoadProject(_projectRoot);
    }

    private void RefreshScenes()
    {
        if (_scenesList == null)
        {
            return;
        }

        _scenesList.Clear();
        if (_project == null)
        {
            return;
        }

        foreach (SceneIndexResource scene in _project.Scenes.OrderBy(scene => scene.Order))
        {
            int index = _scenesList.AddItem(scene.Id == _activeSceneId ? $"{scene.Name} *" : scene.Name);
            _scenesList.SetItemAutoTranslateMode(index, AutoTranslateModeEnum.Disabled);
            _scenesList.SetItemMetadata(index, scene.Id);
            if (scene.Id == _activeSceneId)
            {
                _scenesList.Select(index);
            }
        }

        UpdateScenesContextMenu();
    }

    private void OnScenesListGuiInput(InputEvent @event)
    {
        if (_scenesList == null
            || @event is not InputEventMouseButton mouse
            || mouse.ButtonIndex != MouseButton.Right
            || !mouse.Pressed)
        {
            return;
        }

        int index = _scenesList.GetItemAtPosition(mouse.Position, exact: false);
        if (index < 0)
        {
            return;
        }

        _scenesList.Select(index);
        OnSceneSelected(index);
        UpdateScenesContextMenu();
        if (_scenesContextMenu == null)
        {
            return;
        }

        Vector2 popupPosition = _scenesList.GetScreenPosition() + mouse.Position;
        _scenesContextMenu.Popup(new Rect2I((Vector2I)popupPosition, Vector2I.Zero));
        _scenesList.AcceptEvent();
    }

    private void UpdateScenesContextMenu()
    {
        if (_scenesContextMenu == null)
        {
            return;
        }

        bool hasSelection = !string.IsNullOrWhiteSpace(GetSelectedSceneId());
        bool canDelete = hasSelection && _project != null && _project.Scenes.Count > 1;
        int renameIndex = _scenesContextMenu.GetItemIndex(SceneContextRename);
        int deleteIndex = _scenesContextMenu.GetItemIndex(SceneContextDelete);
        int exportIndex = _scenesContextMenu.GetItemIndex(SceneContextExport);
        if (renameIndex >= 0)
        {
            _scenesContextMenu.SetItemDisabled(renameIndex, !hasSelection);
        }
        if (deleteIndex >= 0)
        {
            _scenesContextMenu.SetItemDisabled(deleteIndex, !canDelete);
        }
        if (exportIndex >= 0)
        {
            _scenesContextMenu.SetItemDisabled(exportIndex, !hasSelection);
        }
    }

    private void OnScenesContextMenuIdPressed(long id)
    {
        switch ((int)id)
        {
            case SceneContextRename:
                RenameSelectedScene();
                break;
            case SceneContextDelete:
                DeleteSelectedScene();
                break;
            case SceneContextExport:
            {
                string? sceneId = GetSelectedSceneId();
                if (!string.IsNullOrWhiteSpace(sceneId))
                {
                    SceneExportRequested?.Invoke(sceneId);
                }
                break;
            }
            case SceneContextImport:
                if (_sceneImportDialog != null)
                {
                    if (!string.IsNullOrWhiteSpace(_projectRoot))
                    {
                        string exportRoot = Path.Combine(_projectRoot, ".agas", "scene-exports");
                        Directory.CreateDirectory(exportRoot);
                        _sceneImportDialog.CurrentDir = exportRoot;
                    }
                    _sceneImportDialog.PopupCenteredClamped(new Vector2I(960, 640));
                }
                break;
        }
    }

    private static Button CreateAddRowButton(string name, string tooltip, Action pressed)
    {
        var button = new Button
        {
            Name = name,
            Text = "+",
            TooltipText = tooltip,
            FocusMode = FocusModeEnum.All,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(1, 28),
            Alignment = HorizontalAlignment.Center
        };
        button.Pressed += pressed;
        return button;
    }

    private void SelectImage(string relativePath, string absolutePath)
    {
        ImageSelected?.Invoke(relativePath);
        ImagePreviewRequested?.Invoke(relativePath, absolutePath);
    }

    private void OnSceneSelected(long index)
    {
        string sceneId = _scenesList?.GetItemMetadata((int)index).AsString() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(sceneId))
        {
            SceneSelected?.Invoke(sceneId);
        }
    }

    private string GetSelectedSceneId()
    {
        if (_scenesList == null || _scenesList.ItemCount == 0)
        {
            return string.Empty;
        }

        int[] selected = _scenesList.GetSelectedItems();
        int index = selected.Length > 0 ? selected[0] : -1;
        if (index < 0 && !string.IsNullOrWhiteSpace(_activeSceneId))
        {
            for (int i = 0; i < _scenesList.ItemCount; i++)
            {
                if (_scenesList.GetItemMetadata(i).AsString() == _activeSceneId)
                {
                    index = i;
                    break;
                }
            }
        }

        return index >= 0 ? _scenesList.GetItemMetadata(index).AsString() : string.Empty;
    }

    private void RenameSelectedScene()
    {
        if (_project == null)
        {
            return;
        }

        string sceneId = GetSelectedSceneId();
        SceneIndexResource? scene = _project.Scenes.FirstOrDefault(item => item.Id == sceneId);
        if (scene == null)
        {
            return;
        }

        _renameDialog ??= new TextInputDialog();
        _renameDialog.Open(this, "重命名场景", scene.Name, "场景名称", value =>
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                SceneRenameRequested?.Invoke(scene.Id, value);
            }
        });
    }

    private void DeleteSelectedScene()
    {
        string sceneId = GetSelectedSceneId();
        if (!string.IsNullOrWhiteSpace(sceneId))
        {
            SceneDeleteRequested?.Invoke(sceneId);
        }
    }

}
