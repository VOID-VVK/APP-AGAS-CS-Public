using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AGAS.UI.Dialogs;
using AGAS.UI.Controllers;
using AppAgasCs.Application;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Prompting;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Features.Ai.UI;
using AppAgasCs.Features.Ai.Workspace;
using AppAgasCs.Features.Tools.Storage;
using AppAgasCs.Mcp;
using AppAgasCs.UI.Shared;
using Agas.Features.Canvas;
using Godot;

namespace AGAS.UI;

public partial class MainWorkspace : Control
{
    public event Action? ProjectClosed;

    private const int CanvasAiSendCurrentLayer = 1;
    private const int CanvasAiSendAllVisibleLayers = 2;
    private const int CanvasAiSendCustomCurrentLayer = 3;
    private const int CanvasAiSendCustomAllVisibleLayers = 4;
    private const int CanvasMergeVisibleLayers = 5;
    private const int CanvasAiSendOtherVisibleLayers = 6;
    private const string CanvasAiSendCurrentLayerText = "发送 AI 填充";
    private const string CanvasAiSendOtherVisibleLayersText = "仅其他可见图层发送";
    private const string CanvasAiSendAllVisibleLayersText = "所有图层发送";
    private const string CanvasAiSendCustomCurrentLayerText = "自定义提示词发送";
    private const string CanvasAiSendCustomAllVisibleLayersText = "自定义 + 所有图层";
    private const string CanvasMergeVisibleLayersText = "合并可见图层到当前层";
    private const string CanvasAiSendingText = "发送中…";
    internal const string DefaultCanvasAiFillPrompt =
        "[生图要求]\n" + PromptComposer.ReferenceEditTask;
    private readonly AgasResourceRepository _repository = new();
    private readonly AgasMcpPathValidator _mcpPathValidator = new();
    private readonly IAgasAiGenerationCoordinator _aiGenerationCoordinator;
    /// <summary>Single live project session shared by UI mutations, canvas, and MCP.</summary>
    private readonly ProjectSession _projectSession = new();
    private readonly WorkspaceDirtyAggregator _dirtyAggregator = new();
    private readonly WorkspaceLayoutController _layoutController = new();
    private readonly MainWorkspaceMcpCoordinator _mcpCoordinator;
    private readonly MainWorkspaceMcpSurfaceAdapter _mcpSurfaceAdapter;
    private readonly MainWorkspaceLayerPromptController _layerPromptController;
    private readonly MainWorkspaceProjectSessionController _projectSessionController;
    private readonly MainWorkspaceAiApplicationController _aiApplicationController;
    private readonly MainWorkspaceAiGenerationBridge _aiGenerationWorkspaceBridge;
    private readonly MainWorkspaceDockChromeController _dockChrome;
    private readonly MainWorkspaceShellBuilder _shellBuilder;
    private readonly MainWorkspaceSaveController _saveController;
    private readonly CanvasToolRunController _canvasToolRunController;
    private readonly ToolCatalog _imageTools = new();
    private Guid _canvasToolProjectSessionId = Guid.NewGuid();

    private string _projectRoot => _projectSession.ProjectRoot;
    private AgasProjectResource? _project => _projectSession.ProjectOrNull;
    private WorkspaceLayoutResource? _layout;
    private AgasSceneResource? _currentScene => _projectSession.CurrentScene;
    private bool _projectRequiresReadOnly;
    private string _projectReadOnlyReasonCode = string.Empty;
    private AgasMcpMainThreadDispatcher? _mcpMainThreadDispatcher;

    private Label? _titleLabel;
    private Label? _statusLabel;
    private string _statusMessage = string.Empty;
    private bool _statusError;
    private object[]? _statusArguments;
    private bool _statusFromAiPanel;
    private string _sceneNoticeMessage = string.Empty;
    private object[]? _sceneNoticeArguments;
    private Button? _undoButton;
    private Button? _redoButton;
    private Button? _selectionToolButton;
    private Button? _replacementToolButton;
    private Button? _editToolButton;
    private Label? _canvasSelectionStatusLabel;
    private Label? _canvasContextStatusLabel;
    private Button? _canvasZoomButton;
    private Label? _aiTaskCountLabel;
    private int _aiTaskCountRefreshQueued;
    private OptionButton? _aiProfileSelector;
    private Button? _aiModelSyncButton;
    private OptionButton? _aiQualitySelector;
    private bool _syncingAiProfileSelector;
    private SpinBox? _gridSize;
    private CheckBox? _snapEnabled;
    private FilesScenesPanel? _filesScenesPanel;
    private AcceptDialog? _sceneExportResultDialog;
    private Button? _sceneExportOpenFolderButton;
    private string _lastExportedScenePackageOpenDirectory = string.Empty;
    private LayerTabs? _layerTabs;
    private PropertiesPanel? _propertiesPanel;
    private PromptPanel? _promptPanel;
    private ToolboxPanel? _toolboxPanel;
    private AiGenerationPanel? _aiGenerationPanel;
    private CanvasWorkspace? _canvas;
    private PopupMenu? _canvasAiContextMenu;
    private PopupMenu? _canvasToolMenu;
    private AcceptDialog? _aiGenerationBlockedDialog;
    private Label? _aiGenerationBlockedMessage;
    private string _aiGenerationBlockedKey = string.Empty;
    private object[] _aiGenerationBlockedArguments = Array.Empty<object>();
    private readonly Dictionary<int, CanvasToolSelection> _canvasToolSelections = new();
    private bool _canvasAiMenuLoading;
    private bool _canvasToolRunning;
    private TextInputDialog? _aiCustomPromptDialog;
    private CanvasPatchLayerScope _aiGenerationLayerScope = CanvasPatchLayerScope.AllVisibleLayers;
    private TabContainer? _bottomTabs;
    private readonly HashSet<string> _uiPendingDeletedSceneIds = new(StringComparer.Ordinal);

    public MainWorkspace()
        : this(new AgasAiGenerationCoordinator())
    {
    }

    internal MainWorkspace(IAgasAiGenerationCoordinator aiGenerationCoordinator)
    {
        _aiGenerationCoordinator = aiGenerationCoordinator
            ?? throw new ArgumentNullException(nameof(aiGenerationCoordinator));
        // Keep the toolbar task counter in sync whenever any UI/MCP permit is
        // released, not only when generation overlays change. Permit release
        // can happen on an MCP/AI worker thread, so the Node-bound Label update
        // must be deferred back to Godot's main thread.
        _aiGenerationCoordinator.CapacityAvailable += QueueAiTaskCountRefresh;
        var initializer = new AgasProjectInitializer(_repository);
        var projectSaveCoordinator = new ProjectSaveCoordinator(
            new ValidatedProjectSavePersistence(_repository, _mcpPathValidator));
        _mcpCoordinator = new MainWorkspaceMcpCoordinator(_repository, _mcpPathValidator);
        _mcpSurfaceAdapter = new MainWorkspaceMcpSurfaceAdapter(this);
        // Local UI dirty + external contributors (MCP / tools). Presentation asks
        // only IsDirty — not each origin (Law of Demeter / least knowledge).
        _dirtyAggregator.RegisterContributor("mcp", () => IsMcpDirtyForCurrentProject);
        _dockChrome = new MainWorkspaceDockChromeController(
            _layoutController,
            () => _layout,
            () => Size.X,
            MarkDirty,
            UpdateDocumentVisibility,
            () =>
            {
                if (_propertiesPanel != null) _propertiesPanel.Visible = false;
            });
        _canvasToolRunController = new CanvasToolRunController(
            CaptureCanvasToolContext,
            ApplyCanvasToolResult,
            _imageTools);
        _saveController = new MainWorkspaceSaveController(
            projectSaveCoordinator,
            _uiPendingDeletedSceneIds,
            () => _projectRoot,
            () => _project,
            () => _layout,
            () => _currentScene,
            () => _projectSession.LoadedScenes,
            () => _projectRequiresReadOnly,
            () => _projectReadOnlyReasonCode,
            () => IsWorkspaceDirty,
            () => _mcpCoordinator.GetCurrentSnapshot(_projectRoot, _project),
            CaptureLayoutFromUi,
            () =>
            {
                if (_currentScene != null)
                {
                    _projectSession.CacheScene(_currentScene);
                }
            },
            () =>
            {
                if (_project != null)
                {
                    _mcpCoordinator.ClearSavedProject(_projectRoot, _project);
                }
            },
            ClearUiDirty,
            SetStatus,
            SetStatusTemplate);
        _layerPromptController = new MainWorkspaceLayerPromptController(
            () => _projectSession,
            () => _currentScene,
            () => _canvas,
            action => CanMutateProject(action),
            SetStatus,
            MarkDirty,
            RefreshManagementUi,
            UpdateAiGenerationContext,
            UpdateCanvasToolStatus,
            scene => _layerTabs?.SetScene(scene),
            layer => _promptPanel?.SetLayer(layer),
            OpenPromptDock,
            SetStatusTemplate,
            MarkDirtyTemplate);
        _projectSessionController = new MainWorkspaceProjectSessionController(
            _repository,
            initializer,
            _mcpPathValidator,
            _layoutController,
            _projectSession,
            _uiPendingDeletedSceneIds,
            () => _projectSession,
            () => _projectRoot,
            () => _project,
            () => _currentScene,
            () => _projectRequiresReadOnly,
            action => CanMutateProject(action),
            ResetProjectStateForOpen,
            AcceptLoadedProject,
            scene => _projectSession.SetCurrentScene(scene, notify: false),
            CompleteProjectOpen,
            () => SaveCurrentScene(silent: true),
            scene => _canvas?.SetScene(scene),
            sceneId => _filesScenesPanel?.SetActiveScene(sceneId),
            scene => _layerTabs?.SetScene(scene),
            layer => _promptPanel?.SetLayer(layer),
            () => _propertiesPanel?.SetSelectedImage(null),
            RefreshManagementUi,
            SetStatus,
            MarkDirty,
            SetStatusTemplate,
            MarkDirtyTemplate);
        // Disk load + cache lives on the session controller; session.LoadScene delegates here.
        _projectSession.Configure(
            loadScene: sceneId => _projectSessionController.LoadScene(sceneId),
            currentSceneChanged: OnProjectSessionCurrentSceneChanged);
        _aiApplicationController = new MainWorkspaceAiApplicationController(
            () => _canvas,
            () => _projectRoot,
            () => _project,
            () => _currentScene,
            GetActiveLayer,
            () => HasOpenProject,
            () => _projectRequiresReadOnly,
            () => _projectReadOnlyReasonCode,
            action => CanMutateProject(action),
            RefreshProjectAssetTree,
            RefreshToolbox,
            UpdateAiGenerationContext,
            SaveProject,
            () => _projectSession,
            scene => _saveController.SaveScene(scene).Success);
        _aiGenerationWorkspaceBridge = new MainWorkspaceAiGenerationBridge(
            CreateAiGenerationContext,
            () => _aiGenerationPanel,
            () => GetActiveLayer()?.PromptFragmentsSnapshot,
            PrepareAiGenerationRequest,
            ApplyAiGeneratedImage,
            DiscardAiGeneratedImage,
            RefreshAiTaskCount,
            ReflectMcpPromptToUi);
        // Shell host captures bridge/session controllers; construct after they exist.
        _shellBuilder = new MainWorkspaceShellBuilder(CreateShellHost());
    }

    public string ProjectRoot => _projectRoot;
    public string CurrentProjectRoot => _projectRoot;
    internal AgasProjectResource? Project => _project;
    internal WorkspaceLayoutResource? Layout => _layout;
    internal AgasSceneResource? CurrentScene => _currentScene;
    public ProjectSessionSnapshot SessionSnapshot => ProjectSessionSnapshot.Capture(
        _projectRoot,
        _project,
        _layout,
        _currentScene,
        _projectSession.LoadedScenes);
    public ProjectSnapshot? ReadOnlyProject => SessionSnapshot.Project;
    public WorkspaceLayoutSnapshot? ReadOnlyLayout => SessionSnapshot.Layout;
    public IReadOnlyList<LoadedSceneSnapshot> LoadedSceneSnapshots => SessionSnapshot.LoadedScenes;
    public bool HasOpenProject => _project != null && _layout != null && !string.IsNullOrWhiteSpace(_projectRoot);
    public string CurrentProjectId => _project?.ProjectId ?? string.Empty;
    public bool HasUnsavedChanges => IsWorkspaceDirty;
    public bool ProjectRequiresReadOnly => _projectRequiresReadOnly;
    public string ProjectReadOnlyReasonCode => _projectReadOnlyReasonCode;
    public IAgasAiGenerationWorkspaceBridge AiGenerationWorkspaceBridge => _aiGenerationWorkspaceBridge;

    /// <summary>Shared mutation session for the open project (UI / canvas / MCP).</summary>
    internal ProjectSession ProjectSession => _projectSession;

    /// <summary>MCP project/context port — prefer this over treating the Control as an MCP surface.</summary>
    public IAgasMcpProjectContextSource McpProjectContextSource => _mcpSurfaceAdapter;

    public bool TrySaveForApplicationExit()
        => SaveProjectOnceForShutdown();

    public void SetMcpMainThreadDispatcher(AgasMcpMainThreadDispatcher? dispatcher)
    {
        _mcpMainThreadDispatcher = dispatcher;
    }

    public void SetMcpDirtyState(AgasMcpDirtyState dirtyState)
    {
        _mcpCoordinator.SetDirtyState(dirtyState);

        if (!HasOpenProject)
        {
            return;
        }

        if (TryBindMcpDirtyState(out string errorMessage))
        {
            _mcpCoordinator.RefreshSnapshot(_projectRoot, _project);
            RefreshStatus("MCP dirty state 已绑定。", false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            SetStatus(errorMessage, true);
        }
    }

    public bool TryGetCurrentProjectIdentity(out string projectRoot, out string projectId)
    {
        return _mcpCoordinator.TryGetProjectIdentity(
            _projectRoot,
            _project,
            HasOpenProject,
            out projectRoot,
            out projectId);
    }

    public bool TryBindMcpDirtyState(out string errorMessage)
    {
        return _mcpCoordinator.TryBindProject(
            _projectRoot,
            _project,
            HasOpenProject,
            out errorMessage);
    }

    public void RefreshProjectUi()
    {
        RefreshManagementUi();
    }

    public void RefreshContextUi()
    {
        RefreshManagementUi();
        RefreshCanvasFromMcp();
    }

    public AgasMcpProjectContext? GetCurrentMcpProjectContext()
    {
        return _mcpCoordinator.CreateContext(new MainWorkspaceMcpContextRequest(
            _projectSession,
            _layout,
            _projectRequiresReadOnly,
            _projectReadOnlyReasonCode,
            _canvas,
            AiGenerationWorkspaceBridge,
            _mcpMainThreadDispatcher,
            SetStatus,
            SyncMcpDirtyState,
            RefreshManagementUi,
            RefreshCanvasFromMcp));
    }

    public AgasMcpProjectAccessState GetCurrentMcpProjectAccessState() =>
        _mcpCoordinator.GetAccessState(
            HasOpenProject,
            _projectRequiresReadOnly,
            _projectReadOnlyReasonCode);

    public override void _Ready()
    {
        Name = "MainWorkspace";
        SetAnchorsPreset(LayoutPreset.FullRect);
        BuildUi();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady())
        {
            UpdateCanvasToolStatus();
            RenderStatus();
            _shellBuilder.RefreshTranslations();
            _dockChrome.RefreshTranslations();
            RefreshAiTaskCount();
            UpdateAiProfileSelectorTooltip();
            RenderAiGenerationBlockedMessage();
            _dockChrome.UpdateResponsiveLayout();
            _dockChrome.QueueApplyLayoutOffsetsAfterLayout();
            if (_project == null && _titleLabel != null) _titleLabel.Text = Tr("AGAS - 未打开项目");
            if (_sceneExportResultDialog != null)
                _sceneExportResultDialog.DialogText = _sceneNoticeArguments == null
                    ? Tr(_sceneNoticeMessage)
                    : string.Format(Tr(_sceneNoticeMessage), _sceneNoticeArguments);
        }
        if (what == NotificationResized && IsNodeReady() && _layout != null)
        {
            _dockChrome.UpdateResponsiveLayout();
            _dockChrome.QueueApplyLayoutOffsetsAfterLayout();
        }
    }

    private MainWorkspaceShellHost CreateShellHost() => new()
    {
        ImageTools = _imageTools,
        OnWorkspaceSplitterDragEnded = () => _dockChrome.OnWorkspaceSplitterDragEnded(),
        OnCenterRightSplitDragStarted = () => _dockChrome.OnCenterRightSplitDragStarted(),
        ToggleLeftPanel = () => _dockChrome.ToggleLeftPanel(),
        ToggleRightPanel = () => _dockChrome.ToggleRightPanel(),
        ToggleBottomDock = () => _dockChrome.ToggleBottomDock(),
        SaveProject = SaveProject,
        UndoActiveDocument = UndoActiveDocument,
        RedoActiveDocument = RedoActiveDocument,
        SetGridSize = SetGridSize,
        OnSnapToggled = enabled =>
        {
            if (_canvas?.SetSnapEnabled(enabled) != true)
            {
                SyncGridControls();
            }
        },
        CloseProject = CloseProject,
        OnAiProfileSelected = OnAiProfileSelected,
        SynchronizeAiModels = () => _aiGenerationPanel?.SynchronizeModels(),
        OnAiQualitySelected = OnAiQualitySelected,
        UpdateUndoRedoButtons = UpdateUndoRedoButtons,
        SetStatus = SetStatus,
        SetStatusTemplate = SetStatusTemplate,
        SwitchScene = SelectSceneFromList,
        SetSceneReadme = SetSceneReadme,
        AddScene = AddScene,
        RenameScene = RenameScene,
        DeleteScene = DeleteScene,
        OnSceneExportRequested = OnSceneExportRequested,
        ImportScenePackage = path => _projectSessionController.ImportScenePackage(path),
        BuildDocumentTabs = BuildDocumentTabs,
        AddCanvasDocumentChild = AddCanvasDocumentChild,
        SetCurrentLayer = SetCurrentLayer,
        SetLayerVisible = SetLayerVisible,
        SetLayerLocked = SetLayerLocked,
        SetLayerShared = SetLayerShared,
        AddLayer = AddLayer,
        RenameLayer = RenameLayer,
        DeleteLayer = DeleteLayer,
        MoveLayer = MoveLayer,
        OpenLayerPrompt = OpenLayerPrompt,
        CanMutateProject = action => CanMutateProject(action),
        ProjectRequiresReadOnly = () => _projectRequiresReadOnly,
        OnCanvasSelectionChanged = OnCanvasSelectionChanged,
        OnCanvasChanged = OnCanvasChanged,
        OnCanvasViewChanged = OnCanvasViewChanged,
        OnCanvasRegionStateChanged = OnCanvasRegionStateChanged,
        ShowCanvasAiContextMenu = ShowCanvasAiContextMenu,
        ShowCanvasAiContextMenuFromToolbar = ShowCanvasAiContextMenuFromToolbar,
        AddPromptFragment = AddPromptFragment,
        DeletePromptFragment = DeletePromptFragment,
        UpdatePromptFragmentText = UpdatePromptFragmentText,
        SetPromptFragmentEnabled = SetPromptFragmentEnabled,
        MovePromptFragment = MovePromptFragment,
        AiGenerationCoordinator = _aiGenerationCoordinator,
        AiGenerationWorkspaceBridge = _aiGenerationWorkspaceBridge,
        OnAiGenerationOverlaysChanged = OnAiGenerationOverlaysChanged,
        OnAiGenerationStatusChanged = OnAiGenerationStatusChanged,
        SyncAiProfileSelector = SyncAiProfileSelector,
        CanvasZoomOut = () => _canvas?.ZoomOut(),
        CanvasZoomIn = () => _canvas?.ZoomIn(),
        CanvasFitVisibleContent = () => _canvas?.FitVisibleContent(),
        CanvasResetZoomTo100Percent = () => _canvas?.ResetZoomTo100Percent(),
        CanvasGrabFocus = () => _canvas?.GrabFocus(),
        SetActiveRegionTool = tool => _canvas?.SetActiveRegionTool(tool),
        UpdateCanvasToolStatus = UpdateCanvasToolStatus,
    };

    public override void _Input(InputEvent @event)
    {
        // Handle Cmd/Ctrl shortcuts in _Input (before GUI) so focused controls like the
        // scenes ItemList cannot consume the key for incremental search / navigation.
        // Previously Cmd+S reached ItemList first → switched scenes whose names start with S,
        // then also saved via _UnhandledKeyInput.
        if (@event is not InputEventKey keyEvent || !keyEvent.Pressed || keyEvent.Echo)
        {
            return;
        }

        if (!(keyEvent.CommandOrControlAutoremap || keyEvent.CtrlPressed || keyEvent.MetaPressed))
        {
            return;
        }

        bool handled = keyEvent.Keycode switch
        {
            Key.S => SaveProject(),
            Key.Z when keyEvent.ShiftPressed && !HasTextEditorFocus() => RedoActiveDocument(),
            Key.Z when !HasTextEditorFocus() => UndoActiveDocument(),
            Key.Y when !HasTextEditorFocus() => RedoActiveDocument(),
            _ => false,
        };
        if (handled)
        {
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (_canvas == null || @event is not InputEventKey keyEvent || !keyEvent.Pressed || keyEvent.Echo)
        {
            return;
        }

        // Leave plain letter keys here so typing in LineEdit/TextEdit is unaffected.
        if (keyEvent.CommandOrControlAutoremap || keyEvent.CtrlPressed || keyEvent.MetaPressed)
        {
            return;
        }

        CanvasRegionTool? requestedTool = keyEvent.Keycode switch
        {
            Key.Q => CanvasRegionTool.Selection,
            Key.W => CanvasRegionTool.Replacement,
            Key.E => CanvasRegionTool.Edit,
            Key.Escape => CanvasRegionTool.Edit,
            _ => null
        };
        if (!requestedTool.HasValue)
        {
            return;
        }

        _canvas.SetActiveRegionTool(requestedTool.Value);
        UpdateCanvasToolStatus();
        GetViewport().SetInputAsHandled();
    }

    public override void _ExitTree()
    {
        _aiGenerationCoordinator.CapacityAvailable -= QueueAiTaskCountRefresh;
        if (_aiGenerationPanel != null)
        {
            _aiGenerationPanel.GenerationOverlaysChanged = null;
            _aiGenerationPanel.StatusChanged -= OnAiGenerationStatusChanged;
            _aiGenerationPanel.ProfilesChanged -= SyncAiProfileSelector;
        }
        if (_canvas != null)
        {
            _canvas.ContextMenuRequested = null;
            _canvas.ClearAiGenerationOverlays();
        }
        if (_canvasAiContextMenu != null)
        {
            _canvasAiContextMenu.IdPressed -= OnCanvasAiContextMenuPressed;
        }
        if (_canvasToolMenu != null)
        {
            _canvasToolMenu.IdPressed -= OnCanvasToolMenuPressed;
        }
        if (_toolboxPanel != null)
        {
            _toolboxPanel.RunRequested -= OnToolboxRunRequested;
        }
        _canvasToolRunController.Invalidate();
        try
        {
            SaveProjectOnceForShutdown();
        }
        finally
        {
            _dockChrome.NotifyWorkspaceReleased();
        }
    }

    public void OpenProject(string projectRoot)
        => _projectSessionController.OpenProject(projectRoot);

    private void ResetProjectStateForOpen(string projectRoot)
    {
        _canvasToolRunController.Invalidate();
        _saveController.ResetFinalSaveAttempt();
        _mcpCoordinator.ResetProjectSnapshot();
        _layout = null;
        _canvasToolProjectSessionId = Guid.NewGuid();
        _projectRequiresReadOnly = false;
        _projectReadOnlyReasonCode = string.Empty;
        _projectSession.Reset(projectRoot);
        if (_canvas != null)
        {
            _canvas.MutationSession = null;
        }
    }

    private void AcceptLoadedProject(
        AgasProjectResource project,
        WorkspaceLayoutResource layout,
        bool requiresReadOnly,
        string readOnlyReasonCode)
    {
        _projectSession.AcceptProject(project);
        _layout = layout;
        _projectRequiresReadOnly = requiresReadOnly;
        _projectReadOnlyReasonCode = readOnlyReasonCode;
    }

    private void CompleteProjectOpen()
    {
        ApplyProjectToUi();
        ApplyLayoutToUi();
        // Opening a project constructs a fresh workspace shell; ensure the compact
        // model selector is populated from ~/.agas even if the hidden AI panel's
        // deferred _Ready has not finished yet.
        ReloadAiProfiles();
        ClearUiDirty("项目已打开。", false);
        if (_projectRequiresReadOnly)
            SetStatusTemplate("项目以只读模式打开：{0}", true, _projectReadOnlyReasonCode);
        if (_mcpCoordinator.HasInjectedDirtyState && !TryBindMcpDirtyState(out string bindError))
        {
            SetStatus(bindError, true);
        }
        else if (_mcpCoordinator.HasInjectedDirtyState)
        {
            _mcpCoordinator.RefreshSnapshot(_projectRoot, _project);
        }
    }

    private void BuildUi()
    {
        MainWorkspaceShellUi shell = _shellBuilder.BuildUi(this);
        ApplyShellUi(shell);
        _dockChrome.BindShell(shell);
        _dockChrome.SetBottomDockCollapsed(true, notify: false);
        UpdateCanvasToolStatus();
        // AiGenerationPanel may fire ProfilesChanged during its deferred _Ready before
        // ApplyShellUi assigns field refs; re-sync now, then reload once after ready so
        // the compact toolbar selector never stays empty while profiles exist on disk.
        SyncAiProfileSelector();
        Callable.From(() =>
        {
            ReloadAiProfiles();
            _dockChrome.UpdateResponsiveLayout();
        }).CallDeferred();
    }

    private void ApplyShellUi(MainWorkspaceShellUi shell)
    {
        _titleLabel = shell.TitleLabel;
        _statusLabel = shell.StatusLabel;
        _undoButton = shell.UndoButton;
        _redoButton = shell.RedoButton;
        _selectionToolButton = shell.SelectionToolButton;
        _replacementToolButton = shell.ReplacementToolButton;
        _editToolButton = shell.EditToolButton;
        _canvasSelectionStatusLabel = shell.CanvasSelectionStatusLabel;
        _canvasContextStatusLabel = shell.CanvasContextStatusLabel;
        _canvasZoomButton = shell.CanvasZoomButton;
        _aiTaskCountLabel = shell.AiTaskCountLabel;
        _aiProfileSelector = shell.AiProfileSelector;
        _aiModelSyncButton = shell.AiModelSyncButton;
        _aiQualitySelector = shell.AiQualitySelector;
        _gridSize = shell.GridSize;
        _snapEnabled = shell.SnapEnabled;
        _filesScenesPanel = shell.FilesScenesPanel;
        _layerTabs = shell.LayerTabs;
        _propertiesPanel = shell.PropertiesPanel;
        _promptPanel = shell.PromptPanel;
        _toolboxPanel = shell.ToolboxPanel;
        if (_toolboxPanel != null)
        {
            _toolboxPanel.RunRequested += OnToolboxRunRequested;
        }
        _aiGenerationPanel = shell.AiGenerationPanel;
        _canvas = shell.Canvas;
        _bottomTabs = shell.BottomTabs;
    }

    private void UpdateCanvasToolStatus()
    {
        CanvasRegionTool activeTool = _canvas?.ActiveRegionTool ?? CanvasRegionTool.Edit;
        SetToolButtonActive(_selectionToolButton, activeTool == CanvasRegionTool.Selection);
        SetToolButtonActive(_replacementToolButton, activeTool == CanvasRegionTool.Replacement);
        SetToolButtonActive(_editToolButton, activeTool == CanvasRegionTool.Edit);

        if (_canvasSelectionStatusLabel != null)
        {
            if (_canvas?.GetSelectedImage() is ImageInstanceResource selected)
            {
                string imageName = Path.GetFileName(selected.ImagePath);
                _canvasSelectionStatusLabel.Text = string.Format(Tr("图片：{0}"), string.IsNullOrWhiteSpace(imageName) ? selected.Id : imageName);
                _canvasSelectionStatusLabel.TooltipText = string.IsNullOrWhiteSpace(selected.ImagePath)
                    ? selected.Id
                    : selected.ImagePath;
            }
            else
            {
                _canvasSelectionStatusLabel.Text = Tr("未选中图片");
                _canvasSelectionStatusLabel.TooltipText = Tr("E 编辑模式下单击图片可选中并拖拽");
            }
        }

        if (_canvasContextStatusLabel != null)
        {
            string layerName = GetActiveLayer()?.Name ?? Tr("无");
            string replacementStatus = _canvas?.ReplacementRegion is CanvasGridRegion replacement
                ? FormatCanvasRegion(replacement)
                : Tr("未设置");
            _canvasContextStatusLabel.Text = string.Format(Tr("图层：{0} · Q 发送/写回：{1} · W 透明洞：{2}"), layerName, FormatCanvasRegion(_canvas?.SelectionRegion), replacementStatus);
            _canvasContextStatusLabel.TooltipText = _canvasContextStatusLabel.Text + "\n" + Tr("Q 是最终完整画布：已有非透明内容保持不变，全部透明或空白区域会被补全；W 仅用于把 Q 内旧问题区先抠成透明");
        }

        OnCanvasViewChanged(_canvas?.GetZoom() ?? 1.0f);
    }

    private static void SetToolButtonActive(Button? button, bool active)
    {
        if (button != null)
        {
            button.ButtonPressed = active;
        }
    }

    private string FormatCanvasRegion(CanvasGridRegion? region) => region.HasValue
        ? $"({region.Value.Column},{region.Value.Row}) {region.Value.Width}×{region.Value.Height}"
        : Tr("未设置");

    public void ReloadAiProfiles(string? preferredProfileId = null)
    {
        _aiGenerationPanel?.ReloadProfiles(preferredProfileId);
        // Always re-sync the compact toolbar after a reload, even if ProfilesChanged
        // was dropped earlier due to missing field refs during shell construction.
        SyncAiProfileSelector(preferredProfileId);
    }

    private void SyncAiProfileSelector() => SyncAiProfileSelector(preferredProfileId: null);

    private void SyncAiProfileSelector(string? preferredProfileId)
    {
        if (_aiModelSyncButton != null)
        {
            bool syncing = _aiGenerationPanel?.IsRefreshingModels == true;
            _aiModelSyncButton.Disabled = _aiGenerationPanel == null || syncing;
            _aiModelSyncButton.Text = syncing ? "同步中…" : "↻ 同步模型";
        }
        if (_aiProfileSelector == null)
        {
            return;
        }

        _syncingAiProfileSelector = true;
        try
        {
            _aiProfileSelector.Clear();

            // The compact selector is model-only. Connection activation is owned
            // by Settings and must never leak connection names into this menu.
            IReadOnlyList<AiGenerationProfileOption> options =
                _aiGenerationPanel?.ProfileOptions ?? Array.Empty<AiGenerationProfileOption>();
            string? selectedId = _aiGenerationPanel?.ActiveModelName;
            if (options.Count == 0)
            {
                try
                {
                    AgasAiSettingsStore store = new();
                    AgasAiProfilesDocument document = store.LoadProfiles();
                    AgasAiModelSnapshot snapshot = new AgasAiConnectionModelAccess(store).Snapshot();
                    options = snapshot.Models.Where(model => snapshot.IsReady && model.IsAvailable)
                        .Select(model => new AiGenerationProfileOption(model.ModelName, model.DisplayName))
                        .ToArray();
                    selectedId ??= document.ActiveModelName;
                }
                catch (Exception exception)
                {
                    GD.PushWarning($"AI profile toolbar sync could not read settings: {exception.Message}");
                }
            }

            int selectedIndex = -1;
            for (int index = 0; index < options.Count; index++)
            {
                AiGenerationProfileOption option = options[index];
                _aiProfileSelector.AddItem(option.DisplayName);
                _aiProfileSelector.SetItemAutoTranslateMode(index, AutoTranslateModeEnum.Disabled);
                _aiProfileSelector.SetItemMetadata(index, option.Id);
                if (string.Equals(option.Id, selectedId, StringComparison.Ordinal))
                {
                    selectedIndex = index;
                }
            }

            // Godot OptionButton shows blank text when ItemCount > 0 but Selected == -1.
            // Always pin a visible selection when profiles exist.
            if (options.Count > 0)
            {
                _aiProfileSelector.Select(selectedIndex >= 0 ? selectedIndex : 0);
            }

            _aiProfileSelector.Disabled = options.Count == 0;
            UpdateAiProfileSelectorTooltip();
            UpdateAiQualitySelectorVisibility();
        }
        finally
        {
            _syncingAiProfileSelector = false;
        }
    }

    private void OnAiProfileSelected(long index)
    {
        if (_syncingAiProfileSelector
            || _aiProfileSelector == null
            || _aiGenerationPanel == null
            || index < 0
            || index >= _aiProfileSelector.ItemCount)
        {
            return;
        }

        string modelName = _aiProfileSelector.GetItemMetadata((int)index).AsString();
        if (_aiGenerationPanel.SelectModelByName(modelName))
        {
            UpdateAiProfileSelectorTooltip();
            UpdateAiQualitySelectorVisibility();
            SetStatusTemplate("生图模型已切换为 {0}；进行中的任务不受影响。", false, modelName);
            return;
        }

        SyncAiProfileSelector();
    }

    private void OnAiQualitySelected(long index)
    {
        if (_aiQualitySelector == null
            || _aiGenerationPanel == null
            || index < 0
            || index >= _aiQualitySelector.ItemCount)
        {
            return;
        }

        var quality = (AiGenerationQuality)_aiQualitySelector.GetItemId((int)index);
        _aiGenerationPanel.SelectQuality(quality);
        SetStatusTemplate("Grok Imagine Image 2.0 质量已切换为 {0}；进行中的任务不受影响。", false, (StringName)_aiQualitySelector.GetItemText((int)index));
    }

    private void UpdateAiQualitySelectorVisibility()
    {
        if (_aiQualitySelector == null)
        {
            return;
        }

        string modelName = _aiGenerationPanel?.ActiveModelName ?? string.Empty;
        _aiQualitySelector.Visible = AiGenerationQualityPolicy.Supports(modelName);
        _aiQualitySelector.Disabled = !_aiQualitySelector.Visible;
    }

    private void UpdateAiProfileSelectorTooltip()
    {
        if (_aiProfileSelector == null) return;
        int selected = _aiProfileSelector.Selected;
        string selectedName = selected >= 0 && selected < _aiProfileSelector.ItemCount
            ? _aiProfileSelector.GetItemText(selected)
            : Tr("未选择模型");
        _aiProfileSelector.TooltipText = string.Format(Tr("当前生图模型：{0}\n选择生图模型"), selectedName);
        _aiProfileSelector.AccessibilityName = string.Format(Tr("当前生图模型：{0}"), selectedName);
    }

    private void OnAiGenerationStatusChanged(string message, bool error)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            _statusMessage = message;
            _statusError = error;
            _statusArguments = null;
            _statusFromAiPanel = true;
            RenderStatus();
        }
    }

    private void ShowCanvasAiContextMenu()
    {
        if (_canvas == null)
        {
            return;
        }
        EnsureCanvasAiContextMenu();
        if (_canvasAiContextMenu == null)
        {
            return;
        }
        if (_canvasAiMenuLoading)
        {
            return;
        }
        RestoreCanvasAiMenuItems();
        SetCanvasAiMenuEnabled(_canvas.SelectionRegion.HasValue);
        UpdateMergeVisibleLayersMenuItem();
        RefreshCanvasToolMenu();
        Vector2 globalPosition = _canvas.GetScreenPosition() + _canvas.LastContextMenuPosition;
        _canvasAiContextMenu.Position = (Vector2I)globalPosition;
        _canvasAiContextMenu.Popup();
    }

    private void ShowCanvasAiContextMenuFromToolbar()
    {
        _canvas?.RequestContextMenuAt(new Vector2(16, 16));
    }

    private void EnsureCanvasAiContextMenu()
    {
        if (_canvasAiContextMenu != null && IsInstanceValid(_canvasAiContextMenu))
        {
            return;
        }
        _canvasAiContextMenu = new PopupMenu
        {
            Name = "CanvasAiContextMenu",
            HideOnItemSelection = false,
        };
        _canvasToolMenu = new PopupMenu
        {
            Name = "CanvasToolMenu",
        };
        _canvasToolMenu.IdPressed += OnCanvasToolMenuPressed;
        _canvasAiContextMenu.AddSubmenuNodeItem("工具箱", _canvasToolMenu);
        _canvasAiContextMenu.AddSeparator();
        _canvasAiContextMenu.AddItem(CanvasAiSendCurrentLayerText, CanvasAiSendCurrentLayer);
        _canvasAiContextMenu.SetItemTooltip(
            _canvasAiContextMenu.GetItemIndex(CanvasAiSendCurrentLayer),
            "使用当前活动图层作为参考并发送到 AI 生图");
        _canvasAiContextMenu.AddItem(CanvasAiSendOtherVisibleLayersText, CanvasAiSendOtherVisibleLayers);
        _canvasAiContextMenu.SetItemTooltip(
            _canvasAiContextMenu.GetItemIndex(CanvasAiSendOtherVisibleLayers),
            "仅使用当前层之外的可见图层作为参考，生成结果仍写入当前层");
        _canvasAiContextMenu.AddItem(CanvasAiSendAllVisibleLayersText, CanvasAiSendAllVisibleLayers);
        _canvasAiContextMenu.SetItemTooltip(
            _canvasAiContextMenu.GetItemIndex(CanvasAiSendAllVisibleLayers),
            "使用所有可见图层作为参考并发送到 AI 生图");
        _canvasAiContextMenu.AddSeparator();
        _canvasAiContextMenu.AddItem(CanvasAiSendCustomCurrentLayerText, CanvasAiSendCustomCurrentLayer);
        _canvasAiContextMenu.SetItemTooltip(
            _canvasAiContextMenu.GetItemIndex(CanvasAiSendCustomCurrentLayer),
            "使用当前活动图层并追加自定义提示词");
        _canvasAiContextMenu.AddItem(CanvasAiSendCustomAllVisibleLayersText, CanvasAiSendCustomAllVisibleLayers);
        _canvasAiContextMenu.SetItemTooltip(
            _canvasAiContextMenu.GetItemIndex(CanvasAiSendCustomAllVisibleLayers),
            "使用所有可见图层并追加自定义提示词");
        _canvasAiContextMenu.AddSeparator();
        _canvasAiContextMenu.AddItem(CanvasMergeVisibleLayersText, CanvasMergeVisibleLayers);
        _canvasAiContextMenu.SetItemTooltip(
            _canvasAiContextMenu.GetItemIndex(CanvasMergeVisibleLayers),
            "把其他可见图层合成一张图并放到当前图层；隐藏图层和当前图层自己不参与合并");
        _canvasAiContextMenu.IdPressed += OnCanvasAiContextMenuPressed;
        AddChild(_canvasAiContextMenu);
        ConfigureCanvasContextMenuLayout(_canvasAiContextMenu);
        ConfigureCanvasContextMenuLayout(_canvasToolMenu);
    }

    private static void ConfigureCanvasContextMenuLayout(PopupMenu menu)
    {
        AgasPopupMenuAppearance.Apply(menu);
        var panel = (StyleBox)AgasIdeTheme.Create().GetStylebox("panel", "PopupMenu").Duplicate();
        panel.ContentMarginLeft = 12;
        panel.ContentMarginRight = 12;
        panel.ContentMarginTop = 10;
        panel.ContentMarginBottom = 10;
        menu.AddThemeStyleboxOverride("panel", panel);
        menu.AddThemeConstantOverride("item_start_padding", 8);
        menu.AddThemeConstantOverride("item_end_padding", 8);
        menu.AddThemeConstantOverride("v_separation", 12);
        menu.MinSize = new Vector2I(360, 0);
        menu.WrapControls = true;
        menu.ShrinkWidth = false;
        menu.AboutToPopup += () =>
        {
            menu.MinSize = new Vector2I(360, 0);
            menu.ResetSize();
        };
    }

    private void RefreshCanvasToolMenu()
    {
        if (_canvasToolMenu == null) return;
        _canvasToolMenu.Clear();
        _canvasToolSelections.Clear();
        try
        {
            int nextId = 1;
            foreach (CanvasToolSelection item in _canvasToolRunController.ListAvailable())
            {
                _canvasToolSelections.Add(nextId, item);
                _canvasToolMenu.AddItem(item.Name, nextId);
                _canvasToolMenu.SetItemAutoTranslateMode(_canvasToolMenu.ItemCount - 1, AutoTranslateModeEnum.Disabled);
                nextId++;
            }
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Tool menu list failed: {exception}");
            SetStatusTemplate("工具列表读取失败：{0}", true, ToolboxPanel.FormatErrorMessageForUi(exception.Message));
        }
        if (_canvasToolSelections.Count == 0)
        {
            _canvasToolMenu.AddItem("请先选中可编辑图片", 0);
            _canvasToolMenu.SetItemDisabled(0, true);
        }
        if (_canvasToolRunning)
        {
            for (int index = 0; index < _canvasToolMenu.ItemCount; index++)
                _canvasToolMenu.SetItemDisabled(index, true);
        }
    }

    private void OnCanvasToolMenuPressed(long id)
    {
        int command = (int)id;
        if (!_canvasToolSelections.TryGetValue(command, out CanvasToolSelection? selection))
        {
            return;
        }
        RunImageTool(selection);
    }

    private void OnToolboxRunRequested(string toolId)
    {
        try
        {
            CanvasToolSelection? selection = _canvasToolRunController.ListAvailable()
                .FirstOrDefault(candidate => candidate.Id == toolId);
            if (selection == null)
            {
                SetStatus("请先选中可编辑图层中的图片，或刷新工具列表。", true);
                return;
            }
            RunImageTool(selection);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Tool list failed: {exception}");
            SetStatus($"工具列表读取失败：{ToolboxPanel.FormatErrorMessageForUi(exception.Message)}", true);
        }
    }

    private void RunImageTool(CanvasToolSelection selection)
    {
        if (_canvasToolRunning) return;
        try
        {
            _canvasToolRunning = true;
            _canvasToolMenu?.Hide();
            _canvasAiContextMenu?.Hide();
            SetStatus("正在执行工具…", false);
            CanvasToolRunResult result = _canvasToolRunController.Run(selection);
            if (!result.Success)
            {
                SetCanvasToolFailureStatus(result);
                return;
            }
            RefreshProjectAssetTree();
            _propertiesPanel?.SetSelectedImage(_canvas?.GetSelectedImage());
            SetStatus("工具已执行，生成图片已添加到当前图层；可撤销。", false);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Tool run failed: {exception}");
            SetStatusTemplate("工具执行失败：{0}", true, ToolboxPanel.FormatErrorMessageForUi(exception.Message));
        }
        finally
        {
            _canvasToolRunning = false;
        }
    }

    private void SetCanvasToolFailureStatus(CanvasToolRunResult result)
    {
        object detail = ToolboxPanel.FormatErrorMessageForUi(result.Message);
        if ((result.Code, result.Message) is
            (CanvasToolRunErrorCodes.ContextUnavailable, "请先选中可编辑图层中的图片。") or
            (CanvasToolRunErrorCodes.ToolChanged, "所选工具在执行期间发生变化，请刷新后重试。") or
            (CanvasToolRunErrorCodes.ContextStale, "执行期间项目、场景、图层或所选图片已变化，结果未应用。") or
            (CanvasToolRunErrorCodes.ApplyFailed, "画布目标已变化或不可编辑，工具结果未应用。"))
        {
            detail = (StringName)result.Message;
        }
        else if (result.Code == CanvasToolRunErrorCodes.ContextUnavailable
            && result.Message.StartsWith("项目为只读状态（", StringComparison.Ordinal)
            && result.Message.EndsWith("）。", StringComparison.Ordinal))
        {
            string reason = result.Message.Substring("项目为只读状态（".Length, result.Message.Length - "项目为只读状态（".Length - "）。".Length);
            detail = (Func<string>)(() => AppAgasCs.UI.Shared.AppLocale.Format("项目为只读状态（{0}）。", reason));
        }
        SetStatusTemplate("工具执行失败（{0}）：{1}", true, result.Code, detail);
    }

    private CanvasToolRunContext? CaptureCanvasToolContext()
    {
        ImageInstanceResource? image = _canvas?.GetSelectedImage();
        AgasLayerResource? layer = GetActiveLayer();
        if (_project == null || _currentScene == null || image == null || layer == null)
            return null;
        bool belongsToLayer = layer.ImagesSnapshot.Any(candidate =>
            ReferenceEquals(candidate, image)
            || string.Equals(candidate.Id, image.Id, StringComparison.Ordinal));
        return new CanvasToolRunContext(
            _canvasToolProjectSessionId,
            _projectRoot,
            _project.ProjectId,
            _currentScene.Id,
            layer.Id,
            image.Id,
            image.ImagePath,
            belongsToLayer
                && _canvas!.CanEditCurrentLayer()
                && CanMutateProject("运行工具", reportError: false),
            _projectReadOnlyReasonCode);
    }

    private bool ApplyCanvasToolResult(
        CanvasToolRunContext expected,
        string relativePath)
    {
        CanvasToolRunContext? current = CaptureCanvasToolContext();
        if (current == null
            || current.ProjectSessionId != expected.ProjectSessionId
            || !string.Equals(current.ProjectId, expected.ProjectId, StringComparison.Ordinal)
            || !string.Equals(current.SceneId, expected.SceneId, StringComparison.Ordinal)
            || !string.Equals(current.LayerId, expected.LayerId, StringComparison.Ordinal)
            || !string.Equals(current.ImageId, expected.ImageId, StringComparison.Ordinal)
            || !string.Equals(current.ImagePath, expected.ImagePath, StringComparison.Ordinal))
        {
            return false;
        }
        return _canvas?.TryAddToolOutputBelowImage(
            expected.ImageId,
            relativePath) == true;
    }

    private void SetCanvasAiMenuEnabled(bool enabled)
    {
        if (_canvasAiContextMenu == null)
        {
            return;
        }
        foreach (int id in new[]
        {
            CanvasAiSendCurrentLayer,
            CanvasAiSendOtherVisibleLayers,
            CanvasAiSendAllVisibleLayers,
            CanvasAiSendCustomCurrentLayer,
            CanvasAiSendCustomAllVisibleLayers,
        })
        {
            int index = _canvasAiContextMenu.GetItemIndex(id);
            if (index >= 0)
            {
                _canvasAiContextMenu.SetItemDisabled(index, !enabled);
                _canvasAiContextMenu.SetItemTooltip(index, enabled
                    ? _canvasAiContextMenu.GetItemText(index)
                    : "请先框选 Q 参考区域");
            }
        }

        int otherLayersIndex = _canvasAiContextMenu.GetItemIndex(CanvasAiSendOtherVisibleLayers);
        if (otherLayersIndex >= 0 && enabled && !HasOtherVisibleLayers())
        {
            _canvasAiContextMenu.SetItemDisabled(otherLayersIndex, true);
            _canvasAiContextMenu.SetItemTooltip(otherLayersIndex, "没有可作为参考的其他可见图层");
        }
    }

    private void OnCanvasAiContextMenuPressed(long id)
    {
        int command = (int)id;
        if (command == CanvasMergeVisibleLayers)
        {
            MergeVisibleLayersIntoCurrent();
            _canvasAiContextMenu?.Hide();
            return;
        }

        if ((command is CanvasAiSendCurrentLayer
                or CanvasAiSendOtherVisibleLayers
                or CanvasAiSendAllVisibleLayers
                or CanvasAiSendCustomCurrentLayer
                or CanvasAiSendCustomAllVisibleLayers)
            && !TryEnsureAiTargetLayerEditable())
        {
            FinishCanvasAiMenuLoading();
            return;
        }

        if (command is CanvasAiSendCustomCurrentLayer or CanvasAiSendCustomAllVisibleLayers)
        {
            FinishCanvasAiMenuLoading();
            switch (command)
            {
                case CanvasAiSendCustomCurrentLayer:
                    RequestCustomCanvasPrompt(CanvasPatchLayerScope.CurrentLayer);
                    break;
                case CanvasAiSendCustomAllVisibleLayers:
                    RequestCustomCanvasPrompt(CanvasPatchLayerScope.AllVisibleLayers);
                    break;
            }
            return;
        }

        if (_canvasAiMenuLoading)
        {
            return;
        }

        BeginCanvasAiMenuLoading(command);
        switch (command)
        {
            case CanvasAiSendCurrentLayer:
                SendCanvasToAi(CanvasPatchLayerScope.CurrentLayer, null);
                break;
            case CanvasAiSendOtherVisibleLayers:
                SendCanvasToAi(CanvasPatchLayerScope.VisibleLayersExceptCurrent, null);
                break;
            case CanvasAiSendAllVisibleLayers:
                SendCanvasToAi(CanvasPatchLayerScope.AllVisibleLayers, null);
                break;
        }

        HideCanvasAiMenuWhenPrepareSettles();
    }

    private void BeginCanvasAiMenuLoading(int commandId)
    {
        if (_canvasAiContextMenu == null)
        {
            return;
        }

        _canvasAiMenuLoading = true;
        foreach (int id in new[]
        {
            CanvasAiSendCurrentLayer,
            CanvasAiSendOtherVisibleLayers,
            CanvasAiSendAllVisibleLayers,
            CanvasAiSendCustomCurrentLayer,
            CanvasAiSendCustomAllVisibleLayers,
        })
        {
            int index = _canvasAiContextMenu.GetItemIndex(id);
            if (index < 0)
            {
                continue;
            }

            _canvasAiContextMenu.SetItemDisabled(index, true);
            if (id == commandId)
            {
                _canvasAiContextMenu.SetItemText(index, CanvasAiSendingText);
            }
        }
    }

    private async void HideCanvasAiMenuWhenPrepareSettles()
    {
        if (IsInstanceValid(this) && IsInsideTree())
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        while (IsInstanceValid(this) && _aiGenerationPanel?.IsPreparingGeneration == true)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        if (IsInstanceValid(this))
        {
            FinishCanvasAiMenuLoading();
        }
    }

    private void FinishCanvasAiMenuLoading()
    {
        _canvasAiMenuLoading = false;
        RestoreCanvasAiMenuItems();
        _canvasAiContextMenu?.Hide();
    }

    private void RestoreCanvasAiMenuItems()
    {
        if (_canvasAiContextMenu == null)
        {
            return;
        }

        SetCanvasAiMenuItem(CanvasAiSendCurrentLayer, CanvasAiSendCurrentLayerText, "使用当前活动图层作为参考并发送到 AI 生图");
        SetCanvasAiMenuItem(CanvasAiSendOtherVisibleLayers, CanvasAiSendOtherVisibleLayersText, "仅使用当前层之外的可见图层作为参考，生成结果仍写入当前层");
        SetCanvasAiMenuItem(CanvasAiSendAllVisibleLayers, CanvasAiSendAllVisibleLayersText, "使用所有可见图层作为参考并发送到 AI 生图");
        SetCanvasAiMenuItem(CanvasAiSendCustomCurrentLayer, CanvasAiSendCustomCurrentLayerText, "使用当前活动图层并追加自定义提示词");
        SetCanvasAiMenuItem(CanvasAiSendCustomAllVisibleLayers, CanvasAiSendCustomAllVisibleLayersText, "使用所有可见图层并追加自定义提示词");
        UpdateMergeVisibleLayersMenuItem();
    }

    private bool HasOtherVisibleLayers()
    {
        AgasLayerResource? currentLayer = GetActiveLayer();
        return currentLayer != null
            && _currentScene?.Layers.Any(layer =>
                layer.Visible
                && !string.Equals(layer.Id, currentLayer.Id, StringComparison.Ordinal)) == true;
    }

    private void UpdateMergeVisibleLayersMenuItem()
    {
        if (_canvasAiContextMenu == null)
        {
            return;
        }

        int index = _canvasAiContextMenu.GetItemIndex(CanvasMergeVisibleLayers);
        if (index < 0)
        {
            return;
        }

        bool canMerge = _canvas != null
            && _canvas.CanEditCurrentLayer()
            && CanMutateProject("合并可见图层", reportError: false);
        _canvasAiContextMenu.SetItemDisabled(index, !canMerge);
        _canvasAiContextMenu.SetItemText(index, CanvasMergeVisibleLayersText);
        _canvasAiContextMenu.SetItemTooltip(
            index,
            canMerge
                ? "把其他可见图层合成一张图并放到当前图层；隐藏图层和当前图层自己不参与合并"
                : "当前图层隐藏、锁定或项目只读时无法合并");
    }

    private void MergeVisibleLayersIntoCurrent()
    {
        if (_canvas == null || !CanMutateProject("合并可见图层"))
        {
            return;
        }

        if (!_canvas.TryMergeVisibleLayersIntoCurrent(null, out CanvasVisibleLayerMergeResult result) || !result.Success)
        {
            SetStatus(string.IsNullOrWhiteSpace(result.Message) ? "可见图层合并失败。" : result.Message, true);
            return;
        }

        RefreshProjectAssetTree();
        _propertiesPanel?.SetSelectedImage(_canvas.GetSelectedImage());
        SetStatus(result.Message, false);
    }

    private void SetCanvasAiMenuItem(int id, string text, string tooltip)
    {
        if (_canvasAiContextMenu == null)
        {
            return;
        }

        int index = _canvasAiContextMenu.GetItemIndex(id);
        if (index < 0)
        {
            return;
        }

        _canvasAiContextMenu.SetItemText(index, text);
        _canvasAiContextMenu.SetItemTooltip(index, tooltip);
        _canvasAiContextMenu.SetItemDisabled(index, false);
    }

    private void RequestCustomCanvasPrompt(CanvasPatchLayerScope scope)
    {
        if (!TryEnsureAiTargetLayerEditable()) return;
        if (!CanMutateProject("发送 AI 请求")) return;
        _aiCustomPromptDialog ??= new TextInputDialog { Name = "CanvasAiCustomPromptDialog" };
        _aiCustomPromptDialog.Open(
            this,
            "追加自定义提示词",
            string.Empty,
            "输入要追加到当前图层提示词后的内容",
            customPrompt =>
            {
                if (!TryEnsureAiTargetLayerEditable()) return;
                SendCanvasToAi(scope, customPrompt, requireCustomPrompt: true);
            });
    }

    private void SendCanvasToAi(CanvasPatchLayerScope scope, string? customPrompt, bool requireCustomPrompt = false)
    {
        if (!TryEnsureAiTargetLayerEditable()) return;
        if (!CanMutateProject("发送 AI 请求")) return;
        _aiGenerationLayerScope = scope;
        AiGenerationCanvasContext? context = CreateAiGenerationContext(scope);
        if (context == null || _aiGenerationPanel == null)
        {
            SetStatus("当前项目、场景或图层不可用，未发送 AI 请求。", true);
            return;
        }

        string trimmedCustom = customPrompt?.Trim() ?? string.Empty;
        string prompt = AiGenerationWorkspaceBridge.ResolvePrompt(trimmedCustom);
        string? preparationMessage = requireCustomPrompt && string.IsNullOrWhiteSpace(trimmedCustom)
            ? "自定义提示词为空，当前未发送请求。"
            : null;
        _aiGenerationPanel.BeginCanvasGeneration(
            context,
            prompt,
            submit: preparationMessage == null,
            preparationMessage: preparationMessage,
            target: AiGenerationTarget.AddToCurrentLayer);
    }

    private bool TryEnsureAiTargetLayerEditable()
    {
        AgasLayerResource? layer = GetActiveLayer();
        if (layer == null)
        {
            ShowAiGenerationBlocked("当前没有可用的目标图层，请先选择一个图层；未发送生图请求。");
            return false;
        }
        if (!layer.Visible && layer.Locked)
        {
            ShowAiGenerationBlocked("当前图层“{0}”已隐藏且已锁定，请点击图层旁的眼睛图标将它显示，并点击锁图标解锁后再生成；未发送生图请求。", layer.Name);
            return false;
        }
        if (!layer.Visible)
        {
            ShowAiGenerationBlocked("当前图层“{0}”已隐藏，请点击图层旁的眼睛图标将它显示后再生成；未发送生图请求。", layer.Name);
            return false;
        }
        if (layer.Locked)
        {
            ShowAiGenerationBlocked("当前图层“{0}”已锁定，请点击图层旁的锁图标解锁后再生成；未发送生图请求。", layer.Name);
            return false;
        }

        return true;
    }

    private void ShowAiGenerationBlocked(string message, params object[] arguments)
    {
        _aiGenerationBlockedKey = message;
        _aiGenerationBlockedArguments = arguments;
        SetStatusTemplate(message, true, arguments);
        if (_aiCustomPromptDialog?.Visible == true)
        {
            _aiCustomPromptDialog.Hide();
        }
        if (_aiGenerationBlockedDialog == null || !IsInstanceValid(_aiGenerationBlockedDialog))
        {
            _aiGenerationBlockedDialog = new AcceptDialog
            {
                Name = "AiGenerationBlockedDialog",
                Title = "无法开始生图",
                Exclusive = true,
                DialogAutowrap = true,
            };

            // AcceptDialog's built-in label has no content padding. Keep its DialogText
            // contract, but render the message in the same margin layout as other dialogs.
            _aiGenerationBlockedDialog.GetLabel().Visible = false;
            _aiGenerationBlockedDialog.GetLabel().AutoTranslateMode = AutoTranslateModeEnum.Disabled;
            MarginContainer contentMargin = AgasMarginContainerFactory.Create(
                20,
                16,
                20,
                16,
                "AiGenerationBlockedContentMargin");
            contentMargin.CustomMinimumSize = new Vector2(0, 96);
            var messageScroll = new ScrollContainer
            {
                Name = "AiGenerationBlockedMessageScroll",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            };
            _aiGenerationBlockedMessage = new Label
            {
                AutoTranslateMode = AutoTranslateModeEnum.Disabled,
                Name = "AiGenerationBlockedMessage",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                VerticalAlignment = VerticalAlignment.Top,
            };
            _aiGenerationBlockedMessage.AddThemeColorOverride("font_color", AgasIdeTheme.MutedTextColor);
            _aiGenerationBlockedMessage.Resized += RefreshAiGenerationBlockedMessageHeight;
            messageScroll.AddChild(_aiGenerationBlockedMessage);
            contentMargin.AddChild(messageScroll);
            _aiGenerationBlockedDialog.AddChild(contentMargin);
            AddChild(_aiGenerationBlockedDialog);

            Button okButton = _aiGenerationBlockedDialog.GetOkButton();
            okButton.Text = "知道了";
        }

        RenderAiGenerationBlockedMessage();
        Vector2 available = GetViewport().GetVisibleRect().Size - new Vector2(48, 48);
        int bodyFontSize = _aiGenerationBlockedMessage!.GetThemeFontSize("font_size");
        int desiredHeight = Math.Clamp(164 + (bodyFontSize * 4), 220, 360);
        var popupSize = new Vector2I(
            Math.Max(1, Math.Min(560, (int)available.X)),
            Math.Max(1, Math.Min(desiredHeight, (int)available.Y)));
        _aiGenerationBlockedDialog.MaxSize = popupSize;
        _aiGenerationBlockedDialog.PopupCentered(popupSize);
        Callable.From(RefreshAiGenerationBlockedMessageHeight).CallDeferred();
    }

    private void RefreshAiGenerationBlockedMessageHeight()
    {
        if (_aiGenerationBlockedMessage == null || !IsInstanceValid(_aiGenerationBlockedMessage))
        {
            return;
        }

        int lineCount = Math.Max(1, _aiGenerationBlockedMessage.GetLineCount());
        float requiredHeight = Math.Max(80, lineCount * _aiGenerationBlockedMessage.GetLineHeight(-1));
        if (!Mathf.IsEqualApprox(_aiGenerationBlockedMessage.CustomMinimumSize.Y, requiredHeight))
        {
            _aiGenerationBlockedMessage.CustomMinimumSize = new Vector2(0, requiredHeight);
        }
    }

    private void RenderAiGenerationBlockedMessage()
    {
        if (_aiGenerationBlockedDialog == null || _aiGenerationBlockedMessage == null) return;
        string message = string.Format(Tr(_aiGenerationBlockedKey), _aiGenerationBlockedArguments);
        _aiGenerationBlockedDialog.DialogText = message;
        _aiGenerationBlockedMessage.Text = message;
        RefreshAiGenerationBlockedMessageHeight();
    }

    private string BuildActiveLayerPrompt()
    {
        return AiGenerationWorkspaceBridge.ResolvePrompt();
    }

    private void UpdateAiGenerationContext()
    {
        if (_aiGenerationPanel == null || _canvas == null || !HasOpenProject)
        {
            _aiGenerationPanel?.SetCanvasContext(null);
            return;
        }

        AiGenerationCanvasContext? context = CreateAiGenerationContext(_aiGenerationLayerScope);
        _aiGenerationPanel.SetCanvasContext(context);
    }

    private void OnAiGenerationOverlaysChanged(IReadOnlyList<AiGenerationOverlayState> states)
    {
        if (_statusFromAiPanel && _aiGenerationPanel?.FindChild("AiGenerationStatus", true, false) is Label panelStatus)
        {
            _statusMessage = panelStatus.Text;
            RenderStatus();
        }
        RefreshAiTaskCount();
        if (_canvas == null)
        {
            return;
        }

        CanvasAiGenerationOverlay[] overlays = states
            .Where(state => MatchesGenerationBoundary(CreateAiGenerationContext(state.Context.LayerScope), state.Context))
            .Select(state => new CanvasAiGenerationOverlay(
                state.RunId,
                state.WorldRect,
                state.Text,
                state.Phase switch
                {
                    AiGenerationOverlayPhase.Pending => CanvasAiGenerationOverlayTone.Pending,
                    AiGenerationOverlayPhase.Failure => CanvasAiGenerationOverlayTone.Failure,
                    AiGenerationOverlayPhase.Canceled => CanvasAiGenerationOverlayTone.Canceled,
                    _ => CanvasAiGenerationOverlayTone.Generating,
                }))
            .ToArray();
        _canvas.SetAiGenerationOverlays(overlays);
    }

    private void RefreshAiTaskCount()
    {
        if (_aiTaskCountLabel != null)
        {
            _aiTaskCountLabel.Text = string.Format(Tr("任务 {0}/{1}"), AiGenerationPanel.MaximumConcurrentGenerations, _aiGenerationCoordinator.ActiveCount);
        }
    }

    private void QueueAiTaskCountRefresh()
    {
        if (Interlocked.Exchange(ref _aiTaskCountRefreshQueued, 1) != 0)
        {
            return;
        }

        Callable.From(RefreshQueuedAiTaskCount).CallDeferred();
    }

    private void RefreshQueuedAiTaskCount()
    {
        Interlocked.Exchange(ref _aiTaskCountRefreshQueued, 0);
        if (!IsInsideTree())
        {
            return;
        }

        RefreshAiTaskCount();
    }

    private static bool MatchesGenerationBoundary(AiGenerationCanvasContext? current, AiGenerationCanvasContext frozen)
    {
        return current != null
            && string.Equals(current.ProjectRoot, frozen.ProjectRoot, StringComparison.Ordinal)
            && string.Equals(current.ProjectId, frozen.ProjectId, StringComparison.Ordinal)
            && string.Equals(current.SceneId, frozen.SceneId, StringComparison.Ordinal)
            && string.Equals(current.LayerId, frozen.LayerId, StringComparison.Ordinal)
            && current.LayerScope == frozen.LayerScope;
    }

    private AiGenerationCanvasContext? CreateAiGenerationContext(CanvasPatchLayerScope scope)
        => _aiApplicationController.CreateContext(scope);

    private AiGenerationPreparationResult PrepareAiGenerationRequest(AiGenerationPreparationInput input)
        => _aiApplicationController.Prepare(input);

    private AiGeneratedImageApplyResult ApplyAiGeneratedImage(
        AiGeneratedImageApplication application,
        AiGenerationApplyPersistence persistence,
        AiGenerationApplyContextPolicy contextPolicy)
        => _aiApplicationController.Apply(application, persistence, contextPolicy);

    private void DiscardAiGeneratedImage(AiGeneratedImageApplication application)
        => _aiApplicationController.Discard(application);

    private AgasSceneResource? LoadScene(string sceneId)
        => _projectSessionController.LoadScene(sceneId);

    private void ApplyProjectToUi()
    {
        if (_project == null || _currentScene == null)
        {
            return;
        }

        string name = string.IsNullOrWhiteSpace(_project.Name)
            ? Path.GetFileName(_projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : _project.Name;
        if (_titleLabel != null)
        {
            _titleLabel.Text = $"AGAS - {name}";
            _titleLabel.TooltipText = _titleLabel.Text;
        }

        if (_canvas != null)
        {
            // Bind canvas mutations to the shared project session (full multi-scene semantics).
            _canvas.MutationSession = _projectSession;
            _canvas.ViewStatePersistenceEnabled = !_projectRequiresReadOnly;
            _canvas.ProcessMode = ProcessModeEnum.Inherit;
            _canvas.SetWorkspace(_project, _currentScene, _projectRoot);
        }
        if (_aiGenerationPanel != null)
        {
            _aiGenerationPanel.ProcessMode = _projectRequiresReadOnly ? ProcessModeEnum.Disabled : ProcessModeEnum.Inherit;
        }
        if (_propertiesPanel != null)
        {
            _propertiesPanel.ProcessMode = _projectRequiresReadOnly ? ProcessModeEnum.Disabled : ProcessModeEnum.Inherit;
            _propertiesPanel.Editable = !_projectRequiresReadOnly;
            _propertiesPanel.SetSceneReadme(_currentScene.Readme ?? string.Empty);
        }
        if (_snapEnabled != null)
        {
            _snapEnabled.Disabled = _projectRequiresReadOnly;
        }
        if (_gridSize != null)
        {
            _gridSize.Editable = !_projectRequiresReadOnly;
        }
        _filesScenesPanel?.OpenProject(_projectRoot, _project, _currentScene.Id);
        _layerTabs?.SetScene(_currentScene);
        _promptPanel?.SetLayer(GetActiveLayer());
        _propertiesPanel?.SetSelectedImage(_canvas?.GetSelectedImage());
        UpdateAiGenerationContext();
        UpdateCanvasToolStatus();
        SyncGridControls();
        RefreshCanvasUndoRedo();
    }

    private void ApplyLayoutToUi()
        => _dockChrome.ApplyLayoutToUi();

    private void CaptureLayoutFromUi()
        => _dockChrome.CaptureLayoutFromUi();

    private void QueueApplyLayoutOffsetsAfterLayout()
        => _dockChrome.QueueApplyLayoutOffsetsAfterLayout();

    private void RefreshManagementUi()
    {
        if (_project == null || _currentScene == null)
        {
            return;
        }

        _filesScenesPanel?.OpenProject(_projectRoot, _project, _currentScene.Id);
        RefreshToolbox();
        _layerTabs?.SetScene(_currentScene);
        _promptPanel?.SetLayer(GetActiveLayer());
        UpdateCanvasToolStatus();
        RefreshCanvasUndoRedo();
    }

    private void RefreshProjectAssetTree()
    {
        if (_project != null && _currentScene != null)
        {
            _filesScenesPanel?.OpenProject(_projectRoot, _project, _currentScene.Id);
        }
    }

    private void RefreshCanvasFromMcp()
    {
        if (_currentScene != null && !ReferenceEquals(_canvas?.GetSceneResource(), _currentScene))
        {
            _canvas?.SetScene(_currentScene);
        }
        else
        {
            _canvas?.RefreshCanvasStateFromResources();
        }
        _propertiesPanel?.SetSelectedImage(_canvas?.GetSelectedImage());
        UpdateAiGenerationContext();
        UpdateCanvasToolStatus();
    }

    private void OnProjectSessionCurrentSceneChanged(AgasSceneResource? scene)
    {
        // Keep ActiveSceneId aligned when mutations change current via the shared session.
        if (scene != null && _project != null
            && !string.Equals(_project.ActiveSceneId, scene.Id, StringComparison.Ordinal))
        {
            _project.ActiveSceneId = scene.Id;
        }
        _propertiesPanel?.SetSceneReadme(scene?.Readme ?? string.Empty);
    }

    private void SyncMcpDirtyState(AgasMcpDirtySnapshot snapshot)
    {
        if (!_mcpCoordinator.TryAcceptSnapshot(_projectRoot, _project, snapshot))
        {
            return;
        }

        RefreshStatus(snapshot.IsDirty ? "MCP 已修改项目。" : "MCP 变更已保存。", false);
    }

    private AgasLayerResource? GetActiveLayer()
    {
        if (_currentScene == null)
        {
            return null;
        }

        return _currentScene.Layers.FirstOrDefault(layer => layer.Id == _currentScene.ActiveLayerId)
            ?? _currentScene.Layers.OrderBy(layer => layer.Order).FirstOrDefault();
    }

    private void AddScene()
        => _projectSessionController.AddScene();

    private void RenameScene(string sceneId, string newName)
        => _projectSessionController.RenameScene(sceneId, newName);

    private void DeleteScene(string sceneId)
        => _projectSessionController.DeleteScene(sceneId);

    private void SwitchScene(string sceneId)
        => _projectSessionController.SwitchScene(sceneId);

    private void SelectSceneFromList(string sceneId)
    {
        SwitchScene(sceneId);
        if (_currentScene?.Id == sceneId)
        {
            _propertiesPanel?.ShowSceneReadme(_currentScene.Readme ?? string.Empty);
        }
        else if (_currentScene != null)
        {
            _filesScenesPanel?.SetActiveScene(_currentScene.Id);
        }
    }

    private void SetSceneReadme(string readme)
    {
        if (_currentScene == null || !CanMutateProject("修改场景说明"))
        {
            return;
        }

        ProjectMutationResult mutation = new ProjectMutationService().MutateScene(
            _projectSession,
            new SceneMutationCommand(SceneMutationKind.SetReadme, _currentScene.Id, Readme: readme));
        if (!mutation.Success)
        {
            SetStatusTemplate("更新场景说明失败：{0}", true, mutation.Message);
            return;
        }

        MarkDirty("已更新场景说明。", false);
    }

    private void AddLayer()
        => _layerPromptController.AddLayer();

    private void RenameLayer(AgasLayerResource layer, string newName)
        => _layerPromptController.RenameLayer(layer, newName);

    private void DeleteLayer(AgasLayerResource layer)
        => _layerPromptController.DeleteLayer(layer);

    private void MoveLayer(AgasLayerResource layer, int direction)
        => _layerPromptController.MoveLayer(layer, direction);

    private void OpenLayerPrompt(AgasLayerResource layer)
        => _layerPromptController.OpenLayerPrompt(layer);

    private void OpenPromptDock()
        => _dockChrome.OpenPromptDock();

    /// <summary>
    /// Writes an MCP-submitted prompt into the layer prompt dock (and keeps AI panel text
    /// in sync via <see cref="MainWorkspaceAiGenerationBridge.ReflectMcpPrompt"/>).
    /// Does not start generation; generation already uses the same prompt string.
    /// </summary>
    private void ReflectMcpPromptToUi(string prompt)
    {
        string text = prompt?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        AgasLayerResource? layer = GetActiveLayer();
        CanvasWorkspace? canvas = _canvas;
        if (layer == null || canvas == null)
        {
            return;
        }

        // Prefer updating an existing fragment so the bottom PromptPanel is not left empty
        // after MCP generate. MCP reflection is not undoable (agent-driven, not user edit).
        var ordered = layer.PromptFragments
            .OrderBy(fragment => fragment.Order)
            .ToList();
        PromptFragmentResource? target = ordered.FirstOrDefault(fragment => fragment.Enabled)
            ?? ordered.FirstOrDefault();

        bool ok;
        if (target != null)
        {
            ok = string.Equals(target.Text, text, StringComparison.Ordinal)
                || canvas.UpdatePromptFragmentText(target, text, undoable: false);
        }
        else
        {
            ok = canvas.AddPromptFragment(layer, text, enabled: true, undoable: false) != null;
        }

        if (!ok)
        {
            return;
        }

        // Show the dock and rebind the list so the operator sees the mirrored text immediately.
        OpenPromptDock();
        _promptPanel?.SetLayer(layer);
    }

    private void AddPromptFragment(AgasLayerResource layer)
        => _layerPromptController.AddPromptFragment(layer);

    private void DeletePromptFragment(AgasLayerResource layer, PromptFragmentResource fragment)
        => _layerPromptController.DeletePromptFragment(layer, fragment);

    private void UpdatePromptFragmentText(PromptFragmentResource fragment, string text)
        => _layerPromptController.UpdatePromptFragmentText(fragment, text);

    private void SetPromptFragmentEnabled(PromptFragmentResource fragment, bool enabled)
        => _layerPromptController.SetPromptFragmentEnabled(fragment, enabled);

    private void MovePromptFragment(AgasLayerResource layer, PromptFragmentResource fragment, int direction)
        => _layerPromptController.MovePromptFragment(layer, fragment, direction);

    private void SetCurrentLayer(AgasLayerResource layer)
        => _layerPromptController.SetCurrentLayer(layer);

    private void SetLayerVisible(AgasLayerResource layer, bool visible)
        => _layerPromptController.SetLayerVisible(layer, visible);

    private void SetLayerLocked(AgasLayerResource layer, bool locked)
        => _layerPromptController.SetLayerLocked(layer, locked);

    private void SetLayerShared(AgasLayerResource layer, bool shared)
        => _layerPromptController.SetLayerShared(layer, shared);

    private void OnCanvasChanged()
    {
        if (RejectProjectReadOnlyMutation("修改画布")) return;
        SyncGridControls();
        // Applying a generated image already refreshes assets. Re-entering
        // SetCanvasContext here retriggers parked write-backs and writes another Result N.png.
        if (!_aiGenerationWorkspaceBridge.SuppressCanvasContextRefresh)
        {
            RefreshManagementUi();
            UpdateAiGenerationContext();
        }
        if (!_aiGenerationWorkspaceBridge.SuppressUiDirty)
        {
            MarkDirty("画布已更新。", false);
        }
    }

    private void OnCanvasSelectionChanged()
    {
        _propertiesPanel?.SetSelectedImage(_canvas?.GetSelectedImage());
        UpdateAiGenerationContext();
        UpdateCanvasToolStatus();
        RefreshCanvasUndoRedo();
    }

    private void OnCanvasViewChanged(float zoom)
    {
        if (_canvasZoomButton == null)
        {
            return;
        }

        int percent = (int)Math.Round(Math.Clamp(zoom, 0.0f, 100.0f) * 100.0f);
        _canvasZoomButton.Text = $"{percent}%";
    }

    private void OnCanvasRegionStateChanged()
    {
        UpdateAiGenerationContext();
        UpdateCanvasToolStatus();
    }

    private void SetGridSize(int sizePx)
    {
        if (_canvas?.SetGridSizePx(sizePx) != true)
        {
            SyncGridControls();
            return;
        }

        int sanitized = _canvas.GetGridSizePx();
        if (_gridSize != null && (int)Math.Round(_gridSize.Value) != sanitized)
        {
            _gridSize.Value = sanitized;
        }
    }

    private void SyncGridControls()
    {
        if (_project == null)
        {
            return;
        }

        if (_gridSize != null)
        {
            _gridSize.Value = Math.Max(1, _project.Grid.SizePx);
        }
        if (_snapEnabled != null)
        {
            _snapEnabled.ButtonPressed = _project.Grid.SnapEnabled;
        }
    }

    private void OnSceneExportRequested(string sceneId)
    {
        ScenePackageExportResult exported = _projectSessionController.ExportScenePackage(sceneId);
        if (!exported.Success)
        {
            ShowScenePackageNotice(
                "导出场景包失败",
                string.IsNullOrWhiteSpace(exported.Message) ? "导出失败。" : exported.Message,
                packagePath: null);
            return;
        }

        string packagePath = exported.PackagePath ?? string.Empty;
        string relative = string.IsNullOrWhiteSpace(_projectRoot) || string.IsNullOrWhiteSpace(packagePath)
            ? packagePath
            : MakeDisplayRelativePath(_projectRoot, packagePath);
        ShowScenePackageNotice(
            "ZIP 场景包已导出",
            "已导出为单个 ZIP 场景包。\n\n"
            + $"文件：{relative}\n"
            + "内容：scene.json + 各图层提示词 + 参考图文件\n\n"
            + "导入时：场景列表右键 → 导入场景包… → 选择该 ZIP，程序会自动解压。",
            packagePath);
    }

    private void ShowScenePackageNotice(string title, string message, string? packagePath, object[]? arguments = null)
    {
        if (_sceneExportResultDialog == null)
        {
            _sceneExportResultDialog = new AcceptDialog
            {
                Name = "ScenePackageExportResultDialog",
                DialogHideOnOk = true,
                Exclusive = true,
            };
            _sceneExportOpenFolderButton = _sceneExportResultDialog.AddButton("打开所在文件夹", true, "open_folder");
            _sceneExportResultDialog.CustomAction += OnSceneExportDialogCustomAction;
            AddChild(_sceneExportResultDialog);
            _sceneExportResultDialog.GetLabel().AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        }

        _sceneExportResultDialog.Title = title;
        _sceneNoticeMessage = message;
        _sceneNoticeArguments = arguments;
        _sceneExportResultDialog.DialogText = arguments == null ? Tr(message) : string.Format(Tr(message), arguments);

        _lastExportedScenePackageOpenDirectory = GetScenePackageOpenDirectory(packagePath);
        bool canOpen = !string.IsNullOrWhiteSpace(_lastExportedScenePackageOpenDirectory);

        if (_sceneExportOpenFolderButton != null)
        {
            _sceneExportOpenFolderButton.Visible = canOpen;
        }

        _sceneExportResultDialog.PopupCenteredClamped(new Vector2I(560, 280));
    }

    private void OnSceneExportDialogCustomAction(StringName action)
    {
        if (action != "open_folder")
        {
            return;
        }

        string directory = _lastExportedScenePackageOpenDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            SetStatus("导出目录不存在，无法打开。", true);
            return;
        }

        Error openError = OS.ShellOpen(directory);
        if (openError != Error.Ok)
        {
            SetStatusTemplate("无法打开文件夹：{0}", true, openError);
        }
    }

    private static string GetScenePackageOpenDirectory(string? packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return string.Empty;
        }

        try
        {
            if (File.Exists(packagePath))
            {
                return Path.GetDirectoryName(Path.GetFullPath(packagePath)) ?? string.Empty;
            }

            return Directory.Exists(packagePath) ? Path.GetFullPath(packagePath) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string MakeDisplayRelativePath(string projectRoot, string absolutePath)
    {
        try
        {
            string root = Path.GetFullPath(projectRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(absolutePath);
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || full.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                return full[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');
            }
        }
        catch
        {
            // Fall through.
        }

        return absolutePath.Replace('\\', '/');
    }

    private bool SaveProject() => _saveController.SaveProject();

    private bool FailSaveForRetry(string message, bool partialSaved)
        => _saveController.FailSaveForRetry(message, partialSaved);

    private AgasPersistenceResult SaveCurrentScene(bool silent)
        => _saveController.SaveCurrentScene(silent);

    private void CloseProject()
    {
        if (!SaveProjectOnceForShutdown())
        {
            return;
        }

        ProjectClosed?.Invoke();
    }

    private bool SaveProjectOnceForShutdown()
        => _saveController.SaveProjectOnceForShutdown();

    private void MarkDirty(string message, bool error)
    {
        if (!CanMutateProject(message, reportError: false)) return;
        _dirtyAggregator.MarkLocalDirty();
        RefreshStatus(message, error);
    }

    private void MarkDirtyTemplate(string message, bool error, params object[] arguments)
    {
        if (!CanMutateProject(message, reportError: false)) return;
        _dirtyAggregator.MarkLocalDirty();
        SetStatusTemplate(message, error, arguments);
    }

    private bool RejectProjectReadOnlyMutation(string action)
    {
        return !CanMutateProject(action);
    }

    private bool CanMutateProject(string action, bool reportError = true)
    {
        if (!_projectRequiresReadOnly) return true;
        if (reportError) SetStatusTemplate("项目为只读状态（{0}），不能{1}。", true, _projectReadOnlyReasonCode, (StringName)action);
        return false;
    }

    private void ClearUiDirty(string message, bool error)
    {
        _dirtyAggregator.ClearLocalDirty();
        RefreshStatus(message, error);
    }

    private bool IsMcpDirtyForCurrentProject =>
        _mcpCoordinator.IsDirty(_projectRoot, _project);

    private bool IsWorkspaceDirty => _dirtyAggregator.IsDirty;

    private void RefreshStatus(string message, bool error)
    {
        SetStatus(message, error);
    }

    private void SetStatus(string message, bool error)
    {
        _statusMessage = message;
        _statusError = error;
        _statusArguments = null;
        _statusFromAiPanel = false;
        RenderStatus();
    }

    private void SetStatusTemplate(string message, bool error, params object[] arguments)
    {
        _statusMessage = message;
        _statusError = error;
        _statusArguments = arguments;
        _statusFromAiPanel = false;
        RenderStatus();
    }

    private void RenderStatus()
    {
        if (_statusLabel == null)
        {
            return;
        }

        bool dirty = IsWorkspaceDirty;
        string trimmed = (_statusFromAiPanel ? _statusMessage : _statusArguments == null ? Tr(_statusMessage) : string.Format(Tr(_statusMessage), _statusArguments.Select(argument => argument is StringName key ? Tr(key) : argument is Func<string> render ? render() : argument).ToArray())).Trim();
        // Avoid the broken "未保存 - " with an empty tail (e.g. a tool clears status
        // with Message="") when the workspace is still dirty.
        _statusLabel.Text = dirty
            ? (string.IsNullOrEmpty(trimmed) ? Tr("未保存") : string.Format(Tr("未保存 - {0}"), trimmed))
            : trimmed;
        _statusLabel.TooltipText = _statusLabel.Text;
        _statusLabel.AddThemeColorOverride("font_color", _statusError ? Colors.OrangeRed : dirty ? Colors.Gold : Colors.LightGreen);
    }

    private void UpdateUndoRedoButtons(bool canUndo, bool canRedo)
    {
        if (_undoButton != null)
        {
            _undoButton.Disabled = _projectRequiresReadOnly || !canUndo;
        }
        if (_redoButton != null)
        {
            _redoButton.Disabled = _projectRequiresReadOnly || !canRedo;
        }
    }
}
