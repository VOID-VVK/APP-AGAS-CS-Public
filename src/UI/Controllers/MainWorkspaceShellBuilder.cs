using System;
using Agas.Features.Canvas;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.UI;
using AppAgasCs.Features.Ai.Workspace;
using AppAgasCs.Features.Tools.Storage;
using Godot;

namespace AGAS.UI.Controllers;

/// <summary>
/// Imperative Godot tree construction for the MainWorkspace shell: toolbar, view menu,
/// dock chrome containers, and left/center/right/bottom panel hosts. Domain wiring is
/// supplied via host callbacks so MainWorkspace retains session/AI/tool ownership.
/// </summary>
internal sealed class MainWorkspaceShellBuilder
{
    private const int ViewMenuLeftPanel = 1;
    private const int ViewMenuRightPanel = 2;
    private const int ViewMenuBottomPanel = 3;

    private readonly MainWorkspaceShellHost _host;
    private readonly System.Collections.Generic.Dictionary<Button, string> _canvasToolButtonKeys = new();

    internal void RefreshTranslations()
    {
        foreach (var entry in _canvasToolButtonKeys)
            entry.Key.TooltipText = string.Format(entry.Key.Tr("切换到{0}工具"), entry.Key.Tr(entry.Value));
    }

    internal MainWorkspaceShellBuilder(MainWorkspaceShellHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    internal MainWorkspaceShellUi BuildUi(Control rootHost)
    {
        ArgumentNullException.ThrowIfNull(rootHost);

        var root = new VBoxContainer
        {
            Name = "WorkspaceRoot",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        rootHost.AddChild(root);

        ToolbarBuildResult toolbar = CreateToolbar();
        root.AddChild(toolbar.Root);

        var workspaceBottomSplit = new VSplitContainer
        {
            Name = "WorkspaceBottomSplitter",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        workspaceBottomSplit.DragEnded += _host.OnWorkspaceSplitterDragEnded;
        root.AddChild(workspaceBottomSplit);

        var mainHorizontalSplit = new HSplitContainer
        {
            Name = "LeftWorkspaceSplitter",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        mainHorizontalSplit.DragEnded += _host.OnWorkspaceSplitterDragEnded;
        workspaceBottomSplit.AddChild(mainHorizontalSplit);

        AGAS.UI.FilesScenesPanel filesScenesPanel = BuildLeftPanel();
        Label leftDockTitle;
        Button leftDockToggle;
        PanelContainer leftDock = BuildDock(
            "LeftWorkspaceDock",
            "文件 / 场景",
            "LeftDockToggleButton",
            _host.ToggleLeftPanel,
            filesScenesPanel,
            out leftDockTitle,
            out leftDockToggle);
        mainHorizontalSplit.AddChild(leftDock);

        var centerRightSplit = new HSplitContainer
        {
            Name = "CanvasPropertiesSplitter",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        // While dragging, drop the right dock minimum to Min* so the user can shrink it;
        // after drag ends we re-pin CustomMinimumSize to the captured width.
        centerRightSplit.DragStarted += _host.OnCenterRightSplitDragStarted;
        centerRightSplit.DragEnded += _host.OnWorkspaceSplitterDragEnded;
        mainHorizontalSplit.AddChild(centerRightSplit);

        CenterPanelBuildResult center = BuildCenterPanel();
        centerRightSplit.AddChild(center.Root);

        AGAS.UI.PropertiesPanel propertiesPanel = BuildPropertiesPanel(center.Canvas);
        filesScenesPanel.ImagePreviewRequested += propertiesPanel.ShowFilePreview;
        propertiesPanel.SceneReadmeChanged += _host.SetSceneReadme;
        Control rightPanel = propertiesPanel;
        Label rightDockTitle;
        Button rightDockToggle;
        PanelContainer rightDock = BuildDock(
            "RightWorkspaceDock",
            "属性",
            "RightDockToggleButton",
            _host.ToggleRightPanel,
            rightPanel,
            out rightDockTitle,
            out rightDockToggle);
        // Do not Expand: Godot 4 split_offsets are relative to the expand-based default,
        // so an expanding right dock grows with the window and can swallow the canvas.
        // Size is controlled via CustomMinimumSize in ApplyCenterRightLayoutAfterOuterSplit.
        rightDock.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        centerRightSplit.AddChild(rightDock);

        BottomDockBuildResult bottom = BuildBottomDock(rootHost);
        Label bottomDockTitle;
        Button bottomDockToggle;
        PanelContainer bottomDock = BuildDock(
            "BottomWorkspaceDock",
            "提示词 / 工具",
            "BottomDockToggleButton",
            _host.ToggleBottomDock,
            bottom.Root,
            out bottomDockTitle,
            out bottomDockToggle);
        bottomDock.AddThemeStyleboxOverride("panel", new StyleBoxEmpty
        {
            ContentMarginLeft = 4, ContentMarginRight = 4,
            ContentMarginTop = 4, ContentMarginBottom = 4
        });
        workspaceBottomSplit.AddChild(bottomDock);

        return new MainWorkspaceShellUi
        {
            TitleLabel = toolbar.TitleLabel,
            StatusLabel = toolbar.StatusLabel,
            UndoButton = toolbar.UndoButton,
            RedoButton = toolbar.RedoButton,
            SelectionToolButton = center.SelectionToolButton,
            ReplacementToolButton = center.ReplacementToolButton,
            EditToolButton = center.EditToolButton,
            CanvasSelectionStatusLabel = center.CanvasSelectionStatusLabel,
            CanvasContextStatusLabel = center.CanvasContextStatusLabel,
            CanvasZoomButton = center.CanvasZoomButton,
            AiTaskCountLabel = toolbar.AiTaskCountLabel,
            AiProfileSelector = toolbar.AiProfileSelector,
            AiQualitySelector = toolbar.AiQualitySelector,
            GridSize = toolbar.GridSize,
            SnapEnabled = toolbar.SnapEnabled,
            MainHorizontalSplit = mainHorizontalSplit,
            CenterRightSplit = centerRightSplit,
            WorkspaceBottomSplit = workspaceBottomSplit,
            FilesScenesPanel = filesScenesPanel,
            LayerTabs = center.LayerTabs,
            PropertiesPanel = propertiesPanel,
            PromptPanel = bottom.PromptPanel,
            ToolboxPanel = bottom.ToolboxPanel,
            AiGenerationPanel = bottom.AiGenerationPanel,
            Canvas = center.Canvas,
            LeftPanel = filesScenesPanel,
            RightPanel = rightPanel,
            BottomPanel = bottom.Root,
            LeftDock = leftDock,
            RightDock = rightDock,
            BottomDock = bottomDock,
            LeftDockTitle = leftDockTitle,
            RightDockTitle = rightDockTitle,
            BottomDockTitle = bottomDockTitle,
            LeftDockToggle = leftDockToggle,
            RightDockToggle = rightDockToggle,
            BottomDockToggle = bottomDockToggle,
            BottomTabs = bottom.BottomTabs,
        };
    }

    private ToolbarBuildResult CreateToolbar()
    {
        var toolbar = new PanelContainer
        {
            Name = "WorkspaceToolbar",
            CustomMinimumSize = new Vector2(1, 44)
        };
        toolbar.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = Colors.Transparent,
            BorderColor = new Color(AGAS.UI.AgasIdeTheme.MutedTextColor, 0.45f),
            BorderWidthBottom = 1,
            ContentMarginLeft = 0, ContentMarginRight = 0,
            ContentMarginTop = 0, ContentMarginBottom = 0
        });

        var margin = new MarginContainer { Name = "WorkspaceToolbarMargin" };
        margin.AddThemeConstantOverride("margin_left", 8);
        margin.AddThemeConstantOverride("margin_right", 8);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        toolbar.AddChild(margin);
        var header = new HBoxContainer { Name = "WorkspaceToolbarHeader" };
        margin.AddChild(header);
        var titleLabel = new Label
        {
            Name = "WorkspaceProjectTitle",
            AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
            TooltipAutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
            Text = TranslationServer.Translate("AGAS - 未打开项目"),
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            SizeFlagsStretchRatio = 0.25f,
            CustomMinimumSize = new Vector2(100, 1),
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        titleLabel.TooltipText = titleLabel.Text;
        titleLabel.AddThemeFontSizeOverride("font_size", 18);
        header.AddChild(titleLabel);
        var scroll = new ScrollContainer
        {
            Name = "WorkspaceToolbarScroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Reserve,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FollowFocus = true
        };
        scroll.GetHScrollBar().CustomMinimumSize = new Vector2(1, 16);
        scroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        header.AddChild(scroll);
        var row = new HBoxContainer
        {
            Name = "WorkspaceToolbarContent",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };
        Action refreshScrollHeight = () => scroll.CustomMinimumSize = new Vector2(1,
            row.GetCombinedMinimumSize().Y + scroll.GetHScrollBar().GetCombinedMinimumSize().Y);
        row.MinimumSizeChanged += refreshScrollHeight;
        scroll.GetHScrollBar().MinimumSizeChanged += refreshScrollHeight;
        scroll.AddChild(row);

        row.AddChild(CreateToolbarButton("保存", () => _host.SaveProject(), "保存项目、布局和当前场景（Ctrl/Cmd+S）"));
        Button undoButton = CreateToolbarButton("撤销", () => _host.UndoActiveDocument(), "撤销当前编辑器（Ctrl/Cmd+Z）");
        row.AddChild(undoButton);
        Button redoButton = CreateToolbarButton("重做", () => _host.RedoActiveDocument(), "重做当前编辑器（Ctrl/Cmd+Shift+Z / Ctrl/Cmd+Y）");
        row.AddChild(redoButton);
        row.AddChild(CreateToolbarGroupSeparator("WorkspaceHistoryGridSeparator"));
        row.AddChild(CreateToolbarButton("网格 24", () => _host.SetGridSize(24), "设置网格为 24 像素"));
        row.AddChild(CreateToolbarButton("网格 48", () => _host.SetGridSize(48), "设置网格为 48 像素"));

        var gridSize = new SpinBox
        {
            AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
            TooltipAutoTranslateMode = Node.AutoTranslateModeEnum.Always,
            Name = "GridSizeSpinBox",
            MinValue = 1,
            MaxValue = 4096,
            Step = 1,
            Value = 48,
            CustomMinimumSize = new Vector2(92, 1),
            TooltipText = "自定义正整数网格尺寸"
        };
        gridSize.ValueChanged += value => _host.SetGridSize((int)Math.Round(value));
        row.AddChild(gridSize);

        var snapEnabled = new CheckBox
        {
            Text = "吸附",
            ButtonPressed = true,
            TooltipText = "吸附网格"
        };
        snapEnabled.Toggled += enabled => _host.OnSnapToggled(enabled);
        row.AddChild(snapEnabled);

        row.AddChild(CreateToolbarGroupSeparator("WorkspaceGridViewSeparator"));
        row.AddChild(CreateViewMenu());
        row.AddChild(CreateToolbarButton("关闭", _host.CloseProject, "关闭当前项目"));

        // AI model/task controls are workspace-global, not canvas-only tools.
        row.AddChild(CreateToolbarGroupSeparator("WorkspaceViewAiSeparator"));
        var aiTaskCountLabel = new Label
        {
            AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
            TooltipAutoTranslateMode = Node.AutoTranslateModeEnum.Always,
            Name = "AiGenerationTaskCountLabel",
            Text = string.Format(TranslationServer.Translate("任务 {0}/{1}"), AiGenerationPanel.MaximumConcurrentGenerations, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TooltipText = "AI 生成任务：并发上限/进行中"
        };
        row.AddChild(aiTaskCountLabel);

        var aiProfileSelector = new OptionButton
        {
            AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
            Name = "AiGenerationProfileSelector",
            CustomMinimumSize = new Vector2(240, 1),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            FocusMode = Control.FocusModeEnum.All,
            TooltipText = TranslationServer.Translate("选择生图模型"),
            AccessibilityName = TranslationServer.Translate("生图模型选择"),
            FitToLongestItem = false,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        aiProfileSelector.ItemSelected += index => _host.OnAiProfileSelected(index);
        row.AddChild(aiProfileSelector);

        var aiQualitySelector = new OptionButton
        {
            Name = "AiGenerationQualitySelector",
            CustomMinimumSize = new Vector2(92, 1),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            FocusMode = Control.FocusModeEnum.All,
            TooltipText = "Grok Imagine Image 2.0 生成质量",
            AccessibilityName = "生图质量选择",
            FitToLongestItem = false,
            Visible = false,
        };
        aiQualitySelector.AddItem("中等", (int)AiGenerationQuality.Medium);
        aiQualitySelector.AddItem("低", (int)AiGenerationQuality.Low);
        aiQualitySelector.Select(0);
        aiQualitySelector.ItemSelected += index => _host.OnAiQualitySelected(index);
        row.AddChild(aiQualitySelector);

        var statusLabel = new Label
        {
            AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
            Name = "SaveStatusLabel",
            Text = TranslationServer.Translate("未保存"),
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(180, 1),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        row.AddChild(statusLabel);

        refreshScrollHeight();
        _host.UpdateUndoRedoButtons(false, false);
        return new ToolbarBuildResult(
            toolbar,
            titleLabel,
            statusLabel,
            undoButton,
            redoButton,
            aiTaskCountLabel,
            aiProfileSelector,
            aiQualitySelector,
            gridSize,
            snapEnabled);
    }

    private MenuButton CreateViewMenu()
    {
        var viewMenuButton = new MenuButton
        {
            Name = "WorkspaceViewMenu",
            Text = "视图",
            TooltipText = "最小化或恢复工作区面板",
            FocusMode = Control.FocusModeEnum.All
        };
        PopupMenu popup = viewMenuButton.GetPopup();
        popup.AddItem("最小化/恢复 文件/场景面板", ViewMenuLeftPanel);
        popup.AddItem("最小化/恢复 属性面板", ViewMenuRightPanel);
        popup.AddItem("最小化/恢复 提示词/工具面板", ViewMenuBottomPanel);
        popup.IdPressed += OnViewMenuItemPressed;
        return viewMenuButton;
    }

    private void OnViewMenuItemPressed(long id)
    {
        switch ((int)id)
        {
            case ViewMenuLeftPanel:
                _host.ToggleLeftPanel();
                break;
            case ViewMenuRightPanel:
                _host.ToggleRightPanel();
                break;
            case ViewMenuBottomPanel:
                _host.ToggleBottomDock();
                break;
        }
    }

    private static MarginContainer CreateToolbarGroupSeparator(string name)
    {
        var margin = new MarginContainer { Name = name, MouseFilter = Control.MouseFilterEnum.Ignore };
        margin.AddThemeConstantOverride("margin_left", 8);
        margin.AddThemeConstantOverride("margin_right", 8);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        var separator = new VSeparator { MouseFilter = Control.MouseFilterEnum.Ignore };
        separator.AddThemeStyleboxOverride("separator", new StyleBoxLine
        {
            Color = new Color(AGAS.UI.AgasIdeTheme.MutedTextColor, 0.55f),
            Thickness = 1,
            Vertical = true
        });
        margin.AddChild(separator);
        return margin;
    }

    private static Button CreateToolbarButton(string text, Action pressed, string tooltip)
    {
        var button = new Button
        {
            Text = text,
            TooltipText = tooltip,
            FocusMode = Control.FocusModeEnum.All
        };
        button.Pressed += pressed;
        return button;
    }

    private static PanelContainer BuildDock(
        string dockName,
        string titleText,
        string toggleName,
        Action toggle,
        Control body,
        out Label title,
        out Button toggleButton)
    {
        var dock = new PanelContainer
        {
            Name = dockName,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            ClipContents = true
        };

        var content = new VBoxContainer
        {
            Name = dockName + "Content",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        dock.AddChild(content);

        var header = new HBoxContainer
        {
            Name = dockName + "Header",
            CustomMinimumSize = new Vector2(1, WorkspaceLayoutController.CollapsedDockThickness),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        header.AddThemeConstantOverride("separation", 2);
        content.AddChild(header);

        title = new Label
        {
            Name = dockName + "Title",
            Text = titleText,
            TooltipText = titleText,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        header.AddChild(title);

        toggleButton = new Button
        {
            Name = toggleName,
            Text = "−",
            TooltipAutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
            TooltipText = string.Format(TranslationServer.Translate("最小化{0}面板"), TranslationServer.Translate(titleText)),
            CustomMinimumSize = new Vector2(
                WorkspaceLayoutController.CollapsedDockThickness,
                WorkspaceLayoutController.CollapsedDockThickness),
            FocusMode = Control.FocusModeEnum.All
        };
        toggleButton.Pressed += toggle;
        header.AddChild(toggleButton);

        body.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        body.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        content.AddChild(body);
        return dock;
    }

    private AGAS.UI.FilesScenesPanel BuildLeftPanel()
    {
        var filesScenesPanel = new AGAS.UI.FilesScenesPanel
        {
            Name = "LeftFilesScenesPanel",
            CustomMinimumSize = new Vector2(220, 1),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        filesScenesPanel.ImageSelected += relativePath =>
        {
            if (_host.SetStatusTemplate != null)
                _host.SetStatusTemplate("选中图片：{0}", false, new object[] { relativePath });
            else
                _host.SetStatus(string.Format(TranslationServer.Translate("选中图片：{0}"), relativePath), false);
        };
        filesScenesPanel.SceneSelected += _host.SwitchScene;
        filesScenesPanel.SceneAddRequested += _host.AddScene;
        filesScenesPanel.SceneRenameRequested += _host.RenameScene;
        filesScenesPanel.SceneDeleteRequested += _host.DeleteScene;
        filesScenesPanel.SceneExportRequested += _host.OnSceneExportRequested;
        filesScenesPanel.SceneImportRequested += path => _host.ImportScenePackage(path);
        return filesScenesPanel;
    }

    private CenterPanelBuildResult BuildCenterPanel()
    {
        var center = new VBoxContainer
        {
            Name = "CenterWorkspace",
            CustomMinimumSize = new Vector2(WorkspaceLayoutController.MinCenterWidth, 1),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        _host.BuildDocumentTabs(center);

        CanvasToolStatusBuildResult toolStatus = CreateCanvasToolStatusBar();
        _host.AddCanvasDocumentChild(toolStatus.Root);

        var layerTabs = new AGAS.UI.LayerTabs
        {
            CustomMinimumSize = new Vector2(1, 64)
        };
        layerTabs.CurrentLayerChanged += _host.SetCurrentLayer;
        layerTabs.LayerVisibilityChanged += _host.SetLayerVisible;
        layerTabs.LayerLockChanged += _host.SetLayerLocked;
        layerTabs.LayerShareChanged += _host.SetLayerShared;
        layerTabs.LayerAddRequested += _host.AddLayer;
        layerTabs.LayerRenameRequested += _host.RenameLayer;
        layerTabs.LayerDeleteRequested += _host.DeleteLayer;
        layerTabs.LayerMoveRequested += _host.MoveLayer;
        layerTabs.LayerPromptRequested += _host.OpenLayerPrompt;
        _host.AddCanvasDocumentChild(layerTabs);

        var canvas = new CanvasWorkspace
        {
            Name = "CanvasWorkspaceHost",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            MutationGuard = action => _host.CanMutateProject(action),
        };
        canvas.SelectionChanged += () => _host.OnCanvasSelectionChanged();
        canvas.CanvasChanged += () => _host.OnCanvasChanged();
        canvas.ViewChanged += zoom => _host.OnCanvasViewChanged(zoom);
        canvas.UndoRedoAvailabilityChanged += (canUndo, canRedo) =>
        {
            _host.UpdateUndoRedoButtons(canUndo, canRedo);
        };
        canvas.RegionStateChanged += () => _host.OnCanvasRegionStateChanged();
        canvas.ContextMenuRequested = _host.ShowCanvasAiContextMenu;
        _host.AddCanvasDocumentChild(canvas);

        return new CenterPanelBuildResult(
            center,
            canvas,
            layerTabs,
            toolStatus.SelectionToolButton,
            toolStatus.ReplacementToolButton,
            toolStatus.EditToolButton,
            toolStatus.CanvasSelectionStatusLabel,
            toolStatus.CanvasContextStatusLabel,
            toolStatus.CanvasZoomButton);
    }

    private AGAS.UI.PropertiesPanel BuildPropertiesPanel(CanvasWorkspace? canvas)
    {
        var propertiesPanel = new AGAS.UI.PropertiesPanel
        {
            Name = "RightPropertiesPanel",
            CustomMinimumSize = new Vector2(240, 1),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            MutationGuard = action => _host.CanMutateProject(action),
            Editable = !_host.ProjectRequiresReadOnly(),
        };
        if (canvas != null)
        {
            propertiesPanel.BindCanvas(canvas);
        }
        return propertiesPanel;
    }

    private BottomDockBuildResult BuildBottomDock(Control rootHost)
    {
        var bottomTabs = new TabContainer
        {
            Name = "BottomDockTabs",
            CustomMinimumSize = new Vector2(1, 1),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };

        var promptPanel = new AGAS.UI.PromptPanel { Name = "Prompt" };
        promptPanel.PromptAddRequested += _host.AddPromptFragment;
        promptPanel.PromptDeleteRequested += _host.DeletePromptFragment;
        promptPanel.PromptTextUpdateRequested += _host.UpdatePromptFragmentText;
        promptPanel.PromptEnabledSetRequested += _host.SetPromptFragmentEnabled;
        promptPanel.PromptMoveRequested += _host.MovePromptFragment;
        bottomTabs.AddChild(promptPanel);
        bottomTabs.SetTabTitle(0, "提示词");

        var toolboxPanel = new AGAS.UI.ToolboxPanel(_host.ImageTools);
        bottomTabs.AddChild(toolboxPanel);
        bottomTabs.SetTabTitle(1, "工具");

        var aiGenerationPanel = new AiGenerationPanel(_host.AiGenerationCoordinator)
        {
            Name = "AiGeneration",
            Visible = false
        };
        aiGenerationPanel.PrepareGenerationRequest = _host.AiGenerationWorkspaceBridge.Prepare;
        aiGenerationPanel.ApplyGeneratedImage = application => _host.AiGenerationWorkspaceBridge.Apply(
            application,
            AiGenerationApplyPersistence.SaveImmediately);
        aiGenerationPanel.DiscardGeneratedImage = _host.AiGenerationWorkspaceBridge.Discard;
        aiGenerationPanel.GenerationOverlaysChanged = _host.OnAiGenerationOverlaysChanged;
        aiGenerationPanel.StatusChanged += _host.OnAiGenerationStatusChanged;
        aiGenerationPanel.ProfilesChanged += _host.SyncAiProfileSelector;
        rootHost.AddChild(aiGenerationPanel);
        bottomTabs.CurrentTab = 0;

        return new BottomDockBuildResult(bottomTabs, promptPanel, toolboxPanel, aiGenerationPanel, bottomTabs);
    }

    private CanvasToolStatusBuildResult CreateCanvasToolStatusBar()
    {
        var bar = new PanelContainer
        {
            Name = "CanvasToolStatusBar",
            CustomMinimumSize = new Vector2(1, 32),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        bar.AddThemeStyleboxOverride("panel", new StyleBoxEmpty
        {
            ContentMarginLeft = 4, ContentMarginRight = 4,
            ContentMarginTop = 4, ContentMarginBottom = 4
        });
        var scroll = new ScrollContainer
        {
            Name = "CanvasToolStatusScroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Reserve,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FollowFocus = true
        };
        scroll.GetHScrollBar().CustomMinimumSize = new Vector2(1, 16);
        scroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        bar.AddChild(scroll);
        var row = new HBoxContainer
        {
            Name = "CanvasToolStatusContent",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };
        Action refreshScrollHeight = () => scroll.CustomMinimumSize = new Vector2(1,
            row.GetCombinedMinimumSize().Y + scroll.GetHScrollBar().GetCombinedMinimumSize().Y);
        row.MinimumSizeChanged += refreshScrollHeight;
        scroll.GetHScrollBar().MinimumSizeChanged += refreshScrollHeight;
        row.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(row);

        Button selectionToolButton = CreateCanvasToolButton(
            "CanvasSelectionToolButton",
            "Q 框选",
            CanvasRegionTool.Selection);
        Button replacementToolButton = CreateCanvasToolButton(
            "CanvasReplacementToolButton",
            "W 抠空",
            CanvasRegionTool.Replacement);
        Button editToolButton = CreateCanvasToolButton(
            "CanvasEditToolButton",
            "E 编辑/拖拽",
            CanvasRegionTool.Edit);
        row.AddChild(selectionToolButton);
        row.AddChild(replacementToolButton);
        row.AddChild(editToolButton);
        row.AddChild(CreateCanvasViewButton(
            "CanvasToolboxMenuButton",
            "工具箱",
            "打开所选图片可运行的工具菜单（也可在图片上点击右键）",
            _host.ShowCanvasAiContextMenuFromToolbar));
        row.AddChild(CreateToolbarGroupSeparator("CanvasToolsSelectionSeparator"));

        var canvasSelectionStatusLabel = new Label
        {
            AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
            Name = "CanvasSelectionStatusLabel",
            Text = "未选中图片",
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(180, 1),
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        row.AddChild(canvasSelectionStatusLabel);
        row.AddChild(CreateToolbarGroupSeparator("CanvasSelectionContextSeparator"));

        var canvasContextStatusLabel = new Label
        {
            AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
            Name = "CanvasContextStatusLabel",
            Text = "图层：无 · Q：未设置 · W：未设置",
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(240, 1),
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        row.AddChild(canvasContextStatusLabel);
        row.AddChild(CreateToolbarGroupSeparator("CanvasContextZoomSeparator"));
        row.AddChild(CreateCanvasViewButton(
            "CanvasZoomOutButton",
            "−",
            "缩小场景画布（最低 10%）",
            () => _host.CanvasZoomOut()));
        var canvasZoomButton = new Button
        {
            Name = "CanvasZoomPercentButton",
            Text = "100%",
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(56, 1),
            TooltipText = "当前场景画布缩放比例；点击恢复到 100%",
            FocusMode = Control.FocusModeEnum.None,
        };
        canvasZoomButton.Pressed += () =>
        {
            _host.CanvasResetZoomTo100Percent();
            _host.CanvasGrabFocus();
        };
        row.AddChild(canvasZoomButton);
        row.AddChild(CreateCanvasViewButton(
            "CanvasZoomInButton",
            "+",
            "放大场景画布（最高 800%）",
            () => _host.CanvasZoomIn()));
        row.AddChild(CreateCanvasViewButton(
            "CanvasZoomFitButton",
            "适应窗口",
            "显示全部可见图片，自动缩放不超过 100%",
            () => _host.CanvasFitVisibleContent()));
        refreshScrollHeight();
        return new CanvasToolStatusBuildResult(
            bar,
            selectionToolButton,
            replacementToolButton,
            editToolButton,
            canvasSelectionStatusLabel,
            canvasContextStatusLabel,
            canvasZoomButton);
    }

    private Button CreateCanvasViewButton(string name, string text, string tooltip, Action action)
    {
        var button = new Button
        {
            Name = name,
            Text = text,
            TooltipText = tooltip,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            FocusMode = Control.FocusModeEnum.None,
        };
        button.Pressed += () =>
        {
            action();
            _host.CanvasGrabFocus();
        };
        return button;
    }

    private Button CreateCanvasToolButton(string name, string text, CanvasRegionTool tool)
    {
        var button = new Button
        {
            Name = name,
            Text = text,
            ToggleMode = true,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            FocusMode = Control.FocusModeEnum.None,
            TooltipAutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
            TooltipText = string.Format(TranslationServer.Translate("切换到{0}工具"), TranslationServer.Translate(text))
        };
        _canvasToolButtonKeys[button] = text;
        button.Pressed += () =>
        {
            _host.SetActiveRegionTool(tool);
            _host.UpdateCanvasToolStatus();
            _host.CanvasGrabFocus();
        };
        return button;
    }

    private sealed record ToolbarBuildResult(
        Control Root,
        Label TitleLabel,
        Label StatusLabel,
        Button UndoButton,
        Button RedoButton,
        Label AiTaskCountLabel,
        OptionButton AiProfileSelector,
        OptionButton AiQualitySelector,
        SpinBox GridSize,
        CheckBox SnapEnabled);

    private sealed record CenterPanelBuildResult(
        Control Root,
        CanvasWorkspace Canvas,
        AGAS.UI.LayerTabs LayerTabs,
        Button SelectionToolButton,
        Button ReplacementToolButton,
        Button EditToolButton,
        Label CanvasSelectionStatusLabel,
        Label CanvasContextStatusLabel,
        Button CanvasZoomButton);

    private sealed record BottomDockBuildResult(
        Control Root,
        AGAS.UI.PromptPanel PromptPanel,
        AGAS.UI.ToolboxPanel ToolboxPanel,
        AiGenerationPanel AiGenerationPanel,
        TabContainer BottomTabs);

    private sealed record CanvasToolStatusBuildResult(
        Control Root,
        Button SelectionToolButton,
        Button ReplacementToolButton,
        Button EditToolButton,
        Label CanvasSelectionStatusLabel,
        Label CanvasContextStatusLabel,
        Button CanvasZoomButton);
}

/// <summary>
/// Callback surface the shell builder uses to wire events without owning domain logic.
/// </summary>
internal sealed class MainWorkspaceShellHost
{
    internal required ToolCatalog ImageTools { get; init; }
    internal required Action OnWorkspaceSplitterDragEnded { get; init; }
    internal required Action OnCenterRightSplitDragStarted { get; init; }
    internal required Action ToggleLeftPanel { get; init; }
    internal required Action ToggleRightPanel { get; init; }
    internal required Action ToggleBottomDock { get; init; }
    internal required Func<bool> SaveProject { get; init; }
    internal required Func<bool> UndoActiveDocument { get; init; }
    internal required Func<bool> RedoActiveDocument { get; init; }
    internal required Action<int> SetGridSize { get; init; }
    internal required Action<bool> OnSnapToggled { get; init; }
    internal required Action CloseProject { get; init; }
    internal required Action<long> OnAiProfileSelected { get; init; }
    internal required Action<long> OnAiQualitySelected { get; init; }
    internal required Action<bool, bool> UpdateUndoRedoButtons { get; init; }
    internal required Action<string, bool> SetStatus { get; init; }
    internal Action<string, bool, object[]>? SetStatusTemplate { get; init; }
    internal required Action<string> SwitchScene { get; init; }
    internal required Action<string> SetSceneReadme { get; init; }
    internal required Action AddScene { get; init; }
    internal required Action<string, string> RenameScene { get; init; }
    internal required Action<string> DeleteScene { get; init; }
    internal required Action<string> OnSceneExportRequested { get; init; }
    internal required Action<string> ImportScenePackage { get; init; }
    internal required Action<VBoxContainer> BuildDocumentTabs { get; init; }
    internal required Action<Control> AddCanvasDocumentChild { get; init; }
    internal required Action<AgasLayerResource> SetCurrentLayer { get; init; }
    internal required Action<AgasLayerResource, bool> SetLayerVisible { get; init; }
    internal required Action<AgasLayerResource, bool> SetLayerLocked { get; init; }
    internal required Action<AgasLayerResource, bool> SetLayerShared { get; init; }
    internal required Action AddLayer { get; init; }
    internal required Action<AgasLayerResource, string> RenameLayer { get; init; }
    internal required Action<AgasLayerResource> DeleteLayer { get; init; }
    internal required Action<AgasLayerResource, int> MoveLayer { get; init; }
    internal required Action<AgasLayerResource> OpenLayerPrompt { get; init; }
    internal required Func<string, bool> CanMutateProject { get; init; }
    internal required Func<bool> ProjectRequiresReadOnly { get; init; }
    internal required Action OnCanvasSelectionChanged { get; init; }
    internal required Action OnCanvasChanged { get; init; }
    internal required Action<float> OnCanvasViewChanged { get; init; }
    internal required Action OnCanvasRegionStateChanged { get; init; }
    internal required Action ShowCanvasAiContextMenu { get; init; }
    internal required Action ShowCanvasAiContextMenuFromToolbar { get; init; }
    internal required Action<AgasLayerResource> AddPromptFragment { get; init; }
    internal required Action<AgasLayerResource, PromptFragmentResource> DeletePromptFragment { get; init; }
    internal required Action<PromptFragmentResource, string> UpdatePromptFragmentText { get; init; }
    internal required Action<PromptFragmentResource, bool> SetPromptFragmentEnabled { get; init; }
    internal required Action<AgasLayerResource, PromptFragmentResource, int> MovePromptFragment { get; init; }
    internal required IAgasAiGenerationCoordinator AiGenerationCoordinator { get; init; }
    internal required IAgasAiGenerationWorkspaceBridge AiGenerationWorkspaceBridge { get; init; }
    internal required Action<System.Collections.Generic.IReadOnlyList<AiGenerationOverlayState>> OnAiGenerationOverlaysChanged { get; init; }
    internal required Action<string, bool> OnAiGenerationStatusChanged { get; init; }
    internal required Action SyncAiProfileSelector { get; init; }
    internal required Action CanvasZoomOut { get; init; }
    internal required Action CanvasZoomIn { get; init; }
    internal required Action CanvasFitVisibleContent { get; init; }
    internal required Action CanvasResetZoomTo100Percent { get; init; }
    internal required Action CanvasGrabFocus { get; init; }
    internal required Action<CanvasRegionTool> SetActiveRegionTool { get; init; }
    internal required Action UpdateCanvasToolStatus { get; init; }
}
