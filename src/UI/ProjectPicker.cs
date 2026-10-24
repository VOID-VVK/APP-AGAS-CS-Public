using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.UI.Shared;
using Godot;

namespace AGAS.UI;

public partial class ProjectPicker : Control
{
    private const int OpenRecentProjectMenuItem = 1;
    private const int RemoveRecentProjectMenuItem = 2;
    private const float ProjectRowHeight = 56f;
    private const float BadgeSize = 40f;

    private static readonly Color SidebarColor = Color.FromHtml("#26282c");
    private static readonly Color MainSurfaceColor = Color.FromHtml("#1f2124");
    private static readonly Color SelectedNavigationColor = Color.FromHtml("#334d80");
    private static readonly Color SelectedNavigationBorderColor = Color.FromHtml("#4671bd");
    private static readonly Color RowHoverColor = Color.FromHtml("#2a2d32");
    private static readonly Color RowSelectedColor = Color.FromHtml("#2d3d5c");
    private static readonly Color RowSelectedBorderColor = Color.FromHtml("#4671bd");
    private static readonly Color PathTextColor = Color.FromHtml("#979da6");
    private static readonly Color BadgeTextColor = Colors.White;

    private static readonly Color[] BadgePalette =
    {
        Color.FromHtml("#c77b3a"), // amber
        Color.FromHtml("#9b6bb8"), // purple
        Color.FromHtml("#4a7fc1"), // blue
        Color.FromHtml("#3d9b8f"), // teal
        Color.FromHtml("#c9a227"), // gold
        Color.FromHtml("#5a9e4f"), // green
        Color.FromHtml("#b85c8a"), // rose
        Color.FromHtml("#6b8f3a"), // olive
        Color.FromHtml("#5c7a9e"), // steel
        Color.FromHtml("#b86b4a"), // terracotta
        Color.FromHtml("#7a6bb8"), // indigo
        Color.FromHtml("#3a9bc7"), // cyan
    };

    public event Action<string>? ProjectOpened;
    public event Action<string>? ProjectCreated;
    public event Action? SettingsRequested;

    private readonly RecentProjectsStore _recentProjectsStore;
    private IReadOnlyList<RecentProjectEntryResource> _recentProjectEntries = Array.Empty<RecentProjectEntryResource>();
    private readonly List<ProjectRowView> _projectRows = new();

    private Label? _statusLabel;
    private string? _statusTemplate;
    private object[] _statusArguments = Array.Empty<object>();
    private Label? _emptyStateLabel;
    private FileDialog? _directoryDialog;
    private LineEdit? _searchEdit;
    private ScrollContainer? _recentProjectsScroll;
    private VBoxContainer? _recentProjectsList;
    private PopupMenu? _recentProjectContextMenu;
    private string? _selectedProjectPath;

    public ProjectPicker() : this(new RecentProjectsStore())
    {
    }

    public ProjectPicker(RecentProjectsStore recentProjectsStore)
    {
        _recentProjectsStore = recentProjectsStore ?? throw new ArgumentNullException(nameof(recentProjectsStore));
    }

    public override void _Ready()
    {
        Name = "ProjectPicker";
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        BuildUi();
        ReloadRecentProjects();
    }

    public override void _Notification(int what)
    {
        if (what != NotificationTranslationChanged || !IsNodeReady()) return;
        if (_statusLabel != null && _statusTemplate != null)
        {
            _statusLabel.Text = AppLocale.Format(_statusTemplate, _statusArguments);
            _statusLabel.TooltipText = _statusLabel.Text;
        }
        if (_searchEdit != null) _searchEdit.PlaceholderText = Tr("搜索项目名称或路径");
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey key || !key.Pressed || key.Echo)
        {
            return;
        }

        if (_projectRows.Count == 0 || _recentProjectsList == null || !_recentProjectsList.Visible)
        {
            return;
        }

        if (key.Keycode is Key.Up or Key.Down)
        {
            MoveSelection(key.Keycode == Key.Down ? 1 : -1);
            GetViewport().SetInputAsHandled();
            return;
        }

        if (key.Keycode is Key.Enter or Key.KpEnter)
        {
            OpenSelectedRecentProject();
            GetViewport().SetInputAsHandled();
        }
    }

    private void BuildUi()
    {
        var background = new PanelContainer
        {
            Name = "ProjectPickerPanel",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        background.AddThemeStyleboxOverride("panel", SolidPanel(MainSurfaceColor));
        AddChild(background);

        var columns = new HBoxContainer
        {
            Name = "ProjectPickerColumns",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        columns.AddThemeConstantOverride("separation", 0);
        background.AddChild(columns);

        columns.AddChild(CreateSidebar());
        columns.AddChild(CreateProjectContent());

        _directoryDialog = new FileDialog
        {
            Name = "ProjectDirectoryDialog",
            Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Title = "选择项目文件夹"
        };
        _directoryDialog.DirSelected += OnDirectorySelected;
        AddChild(_directoryDialog);

        _recentProjectContextMenu = new PopupMenu
        {
            Name = "RecentProjectContextMenu"
        };
        _recentProjectContextMenu.AddItem("打开项目", OpenRecentProjectMenuItem);
        _recentProjectContextMenu.AddSeparator();
        _recentProjectContextMenu.AddItem("从最近项目中移除", RemoveRecentProjectMenuItem);
        _recentProjectContextMenu.IdPressed += OnRecentProjectContextMenuItemPressed;
        AddChild(_recentProjectContextMenu);
    }

    private Control CreateSidebar()
    {
        var sidebar = new PanelContainer
        {
            Name = "ProjectPickerSidebar",
            CustomMinimumSize = new Vector2(280, 1),
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        sidebar.AddThemeStyleboxOverride("panel", SolidPanel(SidebarColor));

        MarginContainer margin = AgasMarginContainerFactory.Create(24, 34, 24, 24, "ProjectPickerSidebarMargin");
        sidebar.AddChild(margin);

        var content = new VBoxContainer
        {
            Name = "ProjectPickerSidebarContent",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        content.AddThemeConstantOverride("separation", 8);
        margin.AddChild(content);

        var brand = new Label
        {
            Name = "ProjectPickerBrand",
            Text = "AGAS"
        };
        brand.AddThemeFontSizeOverride("font_size", 30);
        content.AddChild(brand);

        var brandSubtitle = new Label
        {
            Name = "ProjectPickerBrandSubtitle",
            Text = "Game Asset Studio"
        };
        brandSubtitle.AddThemeColorOverride("font_color", AgasIdeTheme.MutedTextColor);
        content.AddChild(brandSubtitle);
        content.AddChild(new Control { CustomMinimumSize = new Vector2(1, 28) });

        var projectsNavigation = new Button
        {
            Name = "ProjectsNavigationButton",
            Text = "项目",
            Alignment = HorizontalAlignment.Left,
            ToggleMode = true,
            ButtonPressed = true,
            FocusMode = FocusModeEnum.All,
            TooltipText = "最近项目"
        };
        projectsNavigation.AddThemeStyleboxOverride("normal", NavigationStyle());
        projectsNavigation.AddThemeStyleboxOverride("hover", NavigationStyle());
        projectsNavigation.AddThemeStyleboxOverride("pressed", NavigationStyle());
        projectsNavigation.Pressed += () => projectsNavigation.ButtonPressed = true;
        content.AddChild(projectsNavigation);

        var flexibleSpacer = new Control
        {
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        content.AddChild(flexibleSpacer);

        var settingsButton = new Button
        {
            Name = "ProjectPickerSettingsButton",
            Text = "设置",
            Alignment = HorizontalAlignment.Left,
            FocusMode = FocusModeEnum.All,
            TooltipText = "打开 AGAS 设置"
        };
        settingsButton.Pressed += () => SettingsRequested?.Invoke();
        content.AddChild(settingsButton);

        return sidebar;
    }

    private Control CreateProjectContent()
    {
        MarginContainer margin = AgasMarginContainerFactory.Create(42, 38, 42, 32, "ProjectPickerMainMargin");
        margin.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        margin.SizeFlagsVertical = SizeFlags.ExpandFill;

        var content = new VBoxContainer
        {
            Name = "ProjectPickerMainContent",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        content.AddThemeConstantOverride("separation", 18);
        margin.AddChild(content);

        var header = new HBoxContainer
        {
            Name = "ProjectPickerHeader",
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        header.AddThemeConstantOverride("separation", 12);
        content.AddChild(header);

        var title = new Label
        {
            Name = "ProjectPickerWelcomeTitle",
            Text = "欢迎使用 AGAS",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.AddThemeFontSizeOverride("font_size", 28);
        header.AddChild(title);

        // Single entry point: pick a folder → open if it is already an AGAS project,
        // otherwise initialize it. Separate "打开项目" was confusing and redundant.
        var newProjectButton = new Button
        {
            Name = "NewProjectButton",
            Text = "新建项目",
            CustomMinimumSize = new Vector2(160, 44),
            FocusMode = FocusModeEnum.All,
            TooltipText = "选择文件夹：已有 AGAS 项目则直接打开，否则初始化为新项目"
        };
        newProjectButton.Pressed += OpenDirectoryDialog;
        header.AddChild(newProjectButton);

        _searchEdit = new LineEdit
        {
            AutoTranslateMode = AutoTranslateModeEnum.Disabled,
            TooltipAutoTranslateMode = AutoTranslateModeEnum.Always,
            Name = "ProjectSearchEdit",
            PlaceholderText = Tr("搜索项目名称或路径"),
            ClearButtonEnabled = true,
            CustomMinimumSize = new Vector2(1, 42),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.All,
            TooltipText = "按项目名称或完整路径筛选"
        };
        _searchEdit.TextChanged += _ => RefreshRecentProjectsView();
        content.AddChild(_searchEdit);

        var listHost = new Control
        {
            Name = "RecentProjectsHost",
            CustomMinimumSize = new Vector2(420, 300),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            ClipContents = true
        };
        content.AddChild(listHost);

        _recentProjectsScroll = new ScrollContainer
        {
            Name = "RecentProjectsTree",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.All,
            TooltipText = "双击或按 Enter 打开；右键可查看更多操作"
        };
        _recentProjectsScroll.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        listHost.AddChild(_recentProjectsScroll);

        _recentProjectsList = new VBoxContainer
        {
            Name = "RecentProjectsList",
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _recentProjectsList.AddThemeConstantOverride("separation", 2);
        _recentProjectsScroll.AddChild(_recentProjectsList);

        var emptyCenter = new CenterContainer
        {
            Name = "RecentProjectsEmptyCenter",
            MouseFilter = MouseFilterEnum.Ignore
        };
        emptyCenter.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        listHost.AddChild(emptyCenter);

        _emptyStateLabel = new Label
        {
            Name = "RecentProjectsEmptyState",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(320, 100)
        };
        _emptyStateLabel.AddThemeColorOverride("font_color", AgasIdeTheme.MutedTextColor);
        emptyCenter.AddChild(_emptyStateLabel);

        var interactionHint = new Label
        {
            Name = "RecentProjectsInteractionHint",
            Text = "双击项目或按 Enter 打开；右键可从最近项目中移除。",
            TooltipText = "双击项目或按 Enter 打开；右键可从最近项目中移除。",
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        interactionHint.AddThemeColorOverride("font_color", AgasIdeTheme.MutedTextColor);
        content.AddChild(interactionHint);

        _statusLabel = new Label
        {
            Name = "ProjectStatusLabel",
            Visible = false,
            TooltipAutoTranslateMode = AutoTranslateModeEnum.Disabled,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        content.AddChild(_statusLabel);

        return margin;
    }

    private void ReloadRecentProjects()
    {
        _recentProjectEntries = _recentProjectsStore.LoadEntries();
        RefreshRecentProjectsView();
    }

    private void RefreshRecentProjectsView()
    {
        if (_recentProjectsList == null || _emptyStateLabel == null || _recentProjectsScroll == null)
        {
            return;
        }

        foreach (Node child in _recentProjectsList.GetChildren())
        {
            child.QueueFree();
        }
        _projectRows.Clear();

        string query = _searchEdit?.Text.Trim() ?? string.Empty;
        IReadOnlyList<RecentProjectEntryResource> filtered = string.IsNullOrWhiteSpace(query)
            ? _recentProjectEntries
            : _recentProjectEntries
                .Where(entry => ProjectName(entry).Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || entry.ProjectPath.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (filtered.Count == 0)
        {
            _recentProjectsScroll.Visible = false;
            _emptyStateLabel.Text = _recentProjectEntries.Count == 0
                ? "暂无最近项目\n点击“新建项目”选择文件夹开始（已有项目会直接打开）。"
                : "没有匹配的项目\n请尝试搜索其他名称或路径。";
            _emptyStateLabel.Visible = true;
            _selectedProjectPath = null;
            return;
        }

        _emptyStateLabel.Visible = false;
        _recentProjectsScroll.Visible = true;

        string? preferredSelection = _selectedProjectPath;
        if (string.IsNullOrWhiteSpace(preferredSelection)
            || filtered.All(entry => !string.Equals(entry.ProjectPath, preferredSelection, StringComparison.Ordinal)))
        {
            preferredSelection = filtered[0].ProjectPath;
        }

        foreach (RecentProjectEntryResource entry in filtered)
        {
            ProjectRowView row = CreateProjectRow(entry);
            _recentProjectsList.AddChild(row.Root);
            _projectRows.Add(row);
        }

        SelectProjectPath(preferredSelection);
    }

    private ProjectRowView CreateProjectRow(RecentProjectEntryResource entry)
    {
        string projectName = ProjectName(entry);
        string projectPath = entry.ProjectPath;
        string displayPath = FormatDisplayPath(projectPath);
        string initials = ProjectInitials(projectName);
        Color badgeColor = BadgeColorFor(projectName, projectPath);

        var root = new PanelContainer
        {
            AutoTranslateMode = AutoTranslateModeEnum.Disabled,
            Name = $"RecentProjectRow_{SanitizeNodeName(projectName)}",
            CustomMinimumSize = new Vector2(1, ProjectRowHeight),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Stop,
            FocusMode = FocusModeEnum.All,
            TooltipText = $"{projectName}\n{projectPath}"
        };
        root.AddThemeStyleboxOverride("panel", RowStyle(false, false));
        root.MouseDefaultCursorShape = CursorShape.PointingHand;

        MarginContainer margin = AgasMarginContainerFactory.Create(10, 8, 14, 8);
        margin.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        margin.SizeFlagsVertical = SizeFlags.ExpandFill;
        margin.MouseFilter = MouseFilterEnum.Ignore;
        root.AddChild(margin);

        var row = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            Alignment = BoxContainer.AlignmentMode.Begin,
            MouseFilter = MouseFilterEnum.Ignore
        };
        row.AddThemeConstantOverride("separation", 14);
        margin.AddChild(row);

        var badge = new PanelContainer
        {
            Name = "ProjectBadge",
            CustomMinimumSize = new Vector2(BadgeSize, BadgeSize),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore
        };
        badge.AddThemeStyleboxOverride("panel", BadgeStyle(badgeColor));
        row.AddChild(badge);

        var badgeLabel = new Label
        {
            Name = "ProjectBadgeLabel",
            Text = initials,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore
        };
        badgeLabel.AddThemeFontSizeOverride("font_size", initials.Length >= 2 ? 13 : 16);
        badgeLabel.AddThemeColorOverride("font_color", BadgeTextColor);
        badge.AddChild(badgeLabel);

        var textColumn = new VBoxContainer
        {
            Name = "ProjectTextColumn",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Alignment = BoxContainer.AlignmentMode.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        textColumn.AddThemeConstantOverride("separation", 2);
        row.AddChild(textColumn);

        var nameLabel = new Label
        {
            Name = "ProjectNameLabel",
            Text = projectName,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = MouseFilterEnum.Ignore
        };
        nameLabel.AddThemeFontSizeOverride("font_size", 15);
        textColumn.AddChild(nameLabel);

        var pathLabel = new Label
        {
            Name = "ProjectPathLabel",
            Text = displayPath,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = MouseFilterEnum.Ignore
        };
        pathLabel.AddThemeFontSizeOverride("font_size", 12);
        pathLabel.AddThemeColorOverride("font_color", PathTextColor);
        textColumn.AddChild(pathLabel);

        var view = new ProjectRowView(root, nameLabel, pathLabel, projectPath, projectName, displayPath);

        root.MouseEntered += () =>
        {
            view.IsHovered = true;
            ApplyRowVisual(view);
        };
        root.MouseExited += () =>
        {
            view.IsHovered = false;
            ApplyRowVisual(view);
        };
        root.GuiInput += @event => OnProjectRowGuiInput(view, @event);
        root.FocusEntered += () => SelectProjectPath(projectPath);

        return view;
    }

    private void OnProjectRowGuiInput(ProjectRowView row, InputEvent @event)
    {
        if (@event is InputEventMouseButton mouseButton && mouseButton.Pressed)
        {
            if (mouseButton.ButtonIndex == MouseButton.Left)
            {
                SelectProjectPath(row.ProjectPath);
                if (mouseButton.DoubleClick)
                {
                    OpenProjectPath(row.ProjectPath, fromFolderPicker: false);
                }
                row.Root.AcceptEvent();
                return;
            }

            if (mouseButton.ButtonIndex == MouseButton.Right)
            {
                SelectProjectPath(row.ProjectPath);
                Vector2 popupPosition = row.Root.GetScreenPosition() + mouseButton.Position;
                _recentProjectContextMenu?.Popup(new Rect2I((Vector2I)popupPosition, Vector2I.Zero));
                row.Root.AcceptEvent();
            }
        }

        if (@event is InputEventKey key && key.Pressed && !key.Echo
            && key.Keycode is Key.Enter or Key.KpEnter)
        {
            SelectProjectPath(row.ProjectPath);
            OpenProjectPath(row.ProjectPath, fromFolderPicker: false);
            row.Root.AcceptEvent();
        }
    }

    private void MoveSelection(int delta)
    {
        if (_projectRows.Count == 0)
        {
            return;
        }

        int current = _projectRows.FindIndex(row =>
            string.Equals(row.ProjectPath, _selectedProjectPath, StringComparison.Ordinal));
        if (current < 0)
        {
            current = 0;
        }
        else
        {
            current = Math.Clamp(current + delta, 0, _projectRows.Count - 1);
        }

        SelectProjectPath(_projectRows[current].ProjectPath);
        _projectRows[current].Root.GrabFocus();
        _recentProjectsScroll?.EnsureControlVisible(_projectRows[current].Root);
    }

    private void SelectProjectPath(string? projectPath)
    {
        _selectedProjectPath = projectPath;
        foreach (ProjectRowView row in _projectRows)
        {
            row.IsSelected = !string.IsNullOrWhiteSpace(projectPath)
                && string.Equals(row.ProjectPath, projectPath, StringComparison.Ordinal);
            ApplyRowVisual(row);
        }
    }

    private static void ApplyRowVisual(ProjectRowView row)
    {
        row.Root.AddThemeStyleboxOverride("panel", RowStyle(row.IsSelected, row.IsHovered));
    }

    private void OpenDirectoryDialog()
    {
        if (_directoryDialog == null)
        {
            return;
        }

        _directoryDialog.Title = "选择项目文件夹（已有项目则打开，否则新建）";
        _directoryDialog.PopupCenteredRatio(0.72f);
    }

    private void OnDirectorySelected(string directory)
    {
        SetStatus("正在打开项目…", false);
        // Folder picker always uses the smart create-or-open path.
        OpenProjectPath(directory, fromFolderPicker: true);
    }

    private void OpenSelectedRecentProject()
    {
        OpenProjectPath(GetSelectedRecentProjectPath(), fromFolderPicker: false);
    }

    private void OnRecentProjectContextMenuItemPressed(long id)
    {
        string projectPath = GetSelectedRecentProjectPath();
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return;
        }

        switch ((int)id)
        {
            case OpenRecentProjectMenuItem:
                OpenProjectPath(projectPath, fromFolderPicker: false);
                break;
            case RemoveRecentProjectMenuItem:
                RemoveRecentProject(projectPath);
                break;
        }
    }

    private string GetSelectedRecentProjectPath()
    {
        return _selectedProjectPath ?? string.Empty;
    }

    private void OpenProjectPath(string projectRoot, bool fromFolderPicker)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            SetStatus("请选择项目目录。", true);
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(projectRoot);
        }
        catch (Exception ex)
        {
            SetStatusTemplate("目录无效：{0}", true, ex.Message);
            return;
        }

        if (!Directory.Exists(fullPath))
        {
            _recentProjectsStore.Remove(fullPath);
            ReloadRecentProjects();
            SetStatusTemplate("目录不存在，已移除历史记录：{0}", true, fullPath);
            return;
        }

        // Folder picker: ProjectCreated → Main opens existing or initializes.
        // Recent list: ProjectOpened → same smart open path on Main.
        if (fromFolderPicker)
        {
            ProjectCreated?.Invoke(fullPath);
        }
        else
        {
            ProjectOpened?.Invoke(fullPath);
        }
    }

    public void ShowProjectOpenFailure(string message)
    {
        SetStatus(string.IsNullOrWhiteSpace(message) ? "项目操作失败，请检查所选目录。" : message, true);
    }

    private void RemoveRecentProject(string projectRoot)
    {
        _recentProjectsStore.Remove(projectRoot);
        ReloadRecentProjects();
        SetStatus("已从最近项目中移除。项目文件未被删除。", false);
    }

    private void SetStatus(string message, bool isError)
    {
        _statusTemplate = null;
        if (_statusLabel == null)
        {
            return;
        }

        _statusLabel.AutoTranslateMode = AutoTranslateModeEnum.Inherit;
        _statusLabel.Text = message;
        _statusLabel.TooltipText = _statusLabel.Text;
        _statusLabel.Visible = true;
        _statusLabel.AddThemeColorOverride("font_color", isError ? AgasIdeTheme.ErrorTextColor : AgasIdeTheme.SuccessTextColor);
    }

    private void SetStatusTemplate(string template, bool isError, params object[] arguments)
    {
        SetStatus(AppLocale.Format(template, arguments), isError);
        _statusTemplate = template;
        _statusArguments = arguments;
        if (_statusLabel != null) _statusLabel.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
    }

    private static string ProjectName(RecentProjectEntryResource entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.ProjectName))
        {
            return entry.ProjectName.Trim();
        }

        string projectPath = entry.ProjectPath ?? string.Empty;
        string trimmed = projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string pathName = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(pathName) ? projectPath : pathName;
    }

    private static string FormatDisplayPath(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return string.Empty;
        }

        string normalized = projectPath.Replace('\\', '/');
        string home;
        try
        {
            home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile).Replace('\\', '/');
        }
        catch
        {
            return normalized;
        }

        if (!string.IsNullOrWhiteSpace(home)
            && normalized.StartsWith(home, StringComparison.Ordinal))
        {
            return "~" + normalized[home.Length..];
        }

        return normalized;
    }

    private static string ProjectInitials(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return "?";
        }

        string trimmed = projectName.Trim();
        string[] tokens = trimmed.Split(new[] { ' ', '-', '_', '.', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 2)
        {
            return (char.ToUpperInvariant(tokens[0][0]).ToString()
                    + char.ToUpperInvariant(tokens[1][0])).ToString();
        }

        // CamelCase: GameVoidPluginDemo -> GV
        var upper = new List<char>();
        for (int i = 0; i < trimmed.Length && upper.Count < 2; i++)
        {
            char c = trimmed[i];
            if (!char.IsLetterOrDigit(c))
            {
                continue;
            }

            if (i == 0 || char.IsUpper(c) || char.IsDigit(c))
            {
                upper.Add(char.ToUpperInvariant(c));
            }
        }

        if (upper.Count >= 2)
        {
            return new string(upper.Take(2).ToArray());
        }

        if (upper.Count == 1)
        {
            char second = trimmed.Skip(1).FirstOrDefault(char.IsLetterOrDigit);
            return second == default
                ? upper[0].ToString()
                : new string(new[] { upper[0], char.ToUpperInvariant(second) });
        }

        return trimmed.Length == 1
            ? trimmed.ToUpperInvariant()
            : trimmed[..Math.Min(2, trimmed.Length)].ToUpperInvariant();
    }

    private static Color BadgeColorFor(string projectName, string projectPath)
    {
        unchecked
        {
            int hash = 17;
            string seed = string.IsNullOrWhiteSpace(projectName) ? projectPath : projectName;
            foreach (char c in seed)
            {
                hash = (hash * 31) + c;
            }

            int index = Math.Abs(hash) % BadgePalette.Length;
            return BadgePalette[index];
        }
    }

    private static string SanitizeNodeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Project";
        }

        char[] chars = value.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return new string(chars);
    }

    private static StyleBoxFlat SolidPanel(Color color)
    {
        var style = new StyleBoxFlat { BgColor = color };
        style.SetBorderWidthAll(0);
        style.SetCornerRadiusAll(0);
        return style;
    }

    private static StyleBoxFlat NavigationStyle()
    {
        var style = new StyleBoxFlat
        {
            BgColor = SelectedNavigationColor,
            BorderColor = SelectedNavigationBorderColor,
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            ContentMarginTop = 10,
            ContentMarginBottom = 10
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(4);
        return style;
    }

    private static StyleBoxFlat BadgeStyle(Color color)
    {
        var style = new StyleBoxFlat
        {
            BgColor = color,
            ContentMarginLeft = 0,
            ContentMarginRight = 0,
            ContentMarginTop = 0,
            ContentMarginBottom = 0
        };
        style.SetBorderWidthAll(0);
        style.SetCornerRadiusAll(8);
        return style;
    }

    private static StyleBoxFlat RowStyle(bool selected, bool hovered)
    {
        Color background = selected
            ? RowSelectedColor
            : hovered
                ? RowHoverColor
                : Colors.Transparent;
        Color border = selected ? RowSelectedBorderColor : Colors.Transparent;

        var style = new StyleBoxFlat
        {
            BgColor = background,
            BorderColor = border,
            ContentMarginLeft = 0,
            ContentMarginRight = 0,
            ContentMarginTop = 0,
            ContentMarginBottom = 0
        };
        style.SetBorderWidthAll(selected ? 1 : 0);
        style.SetCornerRadiusAll(6);
        return style;
    }

    private sealed class ProjectRowView
    {
        public ProjectRowView(
            PanelContainer root,
            Label nameLabel,
            Label pathLabel,
            string projectPath,
            string projectName,
            string displayPath)
        {
            Root = root;
            NameLabel = nameLabel;
            PathLabel = pathLabel;
            ProjectPath = projectPath;
            ProjectName = projectName;
            DisplayPath = displayPath;
        }

        public PanelContainer Root { get; }
        public Label NameLabel { get; }
        public Label PathLabel { get; }
        public string ProjectPath { get; }
        public string ProjectName { get; }
        public string DisplayPath { get; }
        public bool IsSelected { get; set; }
        public bool IsHovered { get; set; }
    }
}
