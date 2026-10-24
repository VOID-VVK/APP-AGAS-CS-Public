using System;
using System.IO;
using System.Threading.Tasks;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Mcp;
using AppAgasCs.UI.Shared;
using AGAS.UI.Controllers;
using AGAS.UI.Dialogs;
using Godot;

namespace AGAS.UI;

public partial class Main : Control
{
    private readonly RecentProjectsStore _recentProjectsStore;
    private readonly AgasResourceRepository _repository = new();
    private readonly AgasProjectInitializer _initializer;
    private readonly AgasUiAppearanceSettingsStore _appearanceSettingsStore = new();
    private readonly AgasAiGenerationCoordinator _aiGenerationCoordinator = new();
    private readonly bool _disableMcpRuntimeForTesting;

    private const int SettingsMenuMcpId = 1;
    private const int SettingsMenuGeneralId = 2;

    private ProjectPicker? _projectPicker;
    private MainWorkspace? _workspace;
    private MenuBar? _appMenuBar;
    private McpSettingsDialog? _mcpSettingsDialog;
    private SettingsDialog? _settingsDialog;
    private MainMcpRuntimeComposition? _mcpRuntime;
    private bool _applicationExitInProgress;
    private bool _applicationQuitIssued;
    private int _uiFontSize = AgasUiAppearanceSettings.DefaultFontSize;
    private string? _projectOpenErrorTemplate;
    private object[] _projectOpenErrorArguments = Array.Empty<object>();

    // Acceptance tests replace only the final SceneTree.Quit call so they can
    // observe the real WM-close shutdown path without terminating the tree
    // before isolated user files have been restored. Production leaves this
    // null and always performs the normal SceneTree.Quit.
    internal Action? ApplicationQuitOverrideForTesting { get; set; }
    internal bool ApplicationQuitIssuedForTesting => _applicationQuitIssued;
    internal bool McpDisposedForTesting => _mcpRuntime?.IsDisposed ?? true;
    internal AgasMcpAiJobScheduler? McpAiJobSchedulerForTesting => _mcpRuntime?.AiJobScheduler;
    internal AgasMcpAiJobStore? McpAiJobStoreForTesting => _mcpRuntime?.AiJobStore;

    public Main() : this(new RecentProjectsStore(), disableMcpRuntimeForTesting: false)
    {
    }

    internal Main(RecentProjectsStore recentProjectsStore, bool disableMcpRuntimeForTesting)
    {
        _recentProjectsStore = recentProjectsStore ?? throw new ArgumentNullException(nameof(recentProjectsStore));
        _disableMcpRuntimeForTesting = disableMcpRuntimeForTesting;
        _initializer = new AgasProjectInitializer(_repository);
    }

    public override void _EnterTree()
    {
        AgasUiAppearanceSettings settings = _appearanceSettingsStore.Load();
        _uiFontSize = settings.FontSize;
        AppLocale.Apply(settings.UiLocale);
    }

    public override void _Ready()
    {
        Name = "AGASMain";
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Theme = AgasIdeTheme.Create(_uiFontSize);
        GetTree().AutoAcceptQuit = false;
        if (!_disableMcpRuntimeForTesting)
        {
            _mcpRuntime = new MainMcpRuntimeComposition(this, _aiGenerationCoordinator);
            _mcpRuntime.BindDialogSources(() => _mcpSettingsDialog, () => _settingsDialog);
            _mcpRuntime.Initialize();
        }
        BuildAppMenuBar();
        ShowProjectPicker();
        if (!_disableMcpRuntimeForTesting)
        {
            _mcpRuntime?.ApplyPersistedSettings();
        }
        AgasIdeTheme.ApplyScaledFontOverrides(this, _uiFontSize);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged)
        {
            RefreshAppMenuTranslation();
            RefreshProjectOpenErrorTranslation();
        }

        if (what == NotificationResized)
        {
            EnsureActivePageFullRect();
        }

        if (what == NotificationWMCloseRequest)
        {
            BeginApplicationExit();
        }
    }

    public override void _ExitTree()
    {
        _mcpRuntime?.BeginDisposeFallback();
    }

    private void ShowProjectPicker()
    {
        _projectOpenErrorTemplate = null;
        _settingsDialog?.UpdateMcpAiMutationWorkspace(null);
        System.Threading.Volatile.Write(ref _mcpCurrentProjectRootSnapshot, null);
        _mcpRuntime?.TryClearDirtyProjectBindingIfNoUnsaved(_workspace);
        _mcpRuntime?.CancelAiJobsForWorkspace(_workspace);
        _mcpRuntime?.ClearContext();
        ClearChildren();
        _workspace = null;
        _projectPicker = new ProjectPicker();
        // Both recent-list open and folder picker use the same smart path:
        // existing AGAS project → open; ordinary folder → initialize.
        _projectPicker.ProjectOpened += OpenOrCreateWorkspace;
        _projectPicker.ProjectCreated += OpenOrCreateWorkspace;
        _projectPicker.SettingsRequested += OpenSettingsDialog;
        AddChild(_projectPicker);
        ApplyFullRect(_projectPicker);
        AgasIdeTheme.ApplyScaledFontOverrides(_projectPicker, _uiFontSize);
    }

    private void OpenOrCreateWorkspace(string projectRoot)
    {
        OpenWorkspace(projectRoot, createIfMissing: true);
    }

    private void OpenWorkspace(string projectRoot, bool createIfMissing)
    {
        string? projectName = TryGetOpenableProjectName(projectRoot, createIfMissing, out string openError);
        if (string.IsNullOrWhiteSpace(projectName))
        {
            _projectPicker?.ShowProjectOpenFailure(openError);
            return;
        }

        _mcpRuntime?.CancelAiJobsForWorkspace(_workspace);
        _mcpRuntime?.ClearContext();

        ClearChildren();
        _workspace = new MainWorkspace(_aiGenerationCoordinator);
        AgasMcpDirtyState? dirtyState = _mcpRuntime?.DirtyState;
        _workspace.SetMcpMainThreadDispatcher(_mcpRuntime?.Dispatcher);
        if (dirtyState != null)
        {
            _workspace.SetMcpDirtyState(dirtyState);
        }

        _workspace.ProjectClosed += ShowProjectPicker;
        AddChild(_workspace);
        ApplyFullRect(_workspace);
        AgasIdeTheme.ApplyScaledFontOverrides(_workspace, _uiFontSize);
        _workspace.OpenProject(projectRoot);
        if (!_workspace.TryGetCurrentProjectIdentity(out _, out string openedProjectId))
        {
            _mcpRuntime?.ClearContext();
            return;
        }
        _settingsDialog?.UpdateMcpAiMutationWorkspace(openedProjectId);
        System.Threading.Volatile.Write(ref _mcpCurrentProjectRootSnapshot, Path.GetFullPath(projectRoot));

        if (dirtyState != null && !_workspace.TryBindMcpDirtyState(out string bindError))
        {
            _mcpRuntime?.ClearContext();
            if (!string.IsNullOrWhiteSpace(bindError))
            {
                GD.PushWarning($"MCP dirty state bind failed: {bindError}");
            }
            return;
        }

        if (_workspace.GetCurrentMcpProjectContext() != null)
        {
            // Register the narrow MCP surface adapter, not the Godot Control itself (ISP).
            _mcpRuntime?.RegisterContextSource(_workspace.McpProjectContextSource);
        }

        _recentProjectsStore.RecordOpened(projectRoot, projectName);
    }

    internal string? TryGetOpenableProjectName(string projectRoot, bool createIfMissing, out string errorMessage)
    {
        _projectOpenErrorTemplate = null;
        errorMessage = string.Empty;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(projectRoot);
        }
        catch (Exception exception)
        {
            errorMessage = FormatProjectOpenError("项目目录无效：{0}", exception.Message);
            return null;
        }

        if (!createIfMissing && !File.Exists(AgasPaths.ProjectFile(fullPath)))
        {
            errorMessage = FormatProjectOpenError("无法打开：所选目录不是有效的 AGAS 项目。请使用“新建项目”选择该文件夹以初始化。");
            return null;
        }

        // createIfMissing=true: initialize when .agas/project.tres is absent; open when present.
        AgasPersistenceResult init = _initializer.EnsureInitialized(fullPath);
        bool rejectedAsTooOld = !init.Success
            && init.ErrorMessage?.StartsWith("PROJECT_VERSION_INVALID", StringComparison.Ordinal) == true;
        if (!init.Success && !rejectedAsTooOld)
        {
            errorMessage = string.IsNullOrWhiteSpace(init.ErrorMessage)
                ? FormatProjectOpenError(createIfMissing ? "打开或创建项目失败。" : "无法打开 AGAS 项目。")
                : FormatProjectOpenError("打开项目失败：{0}", init.ErrorMessage);
            return null;
        }

        var projectLoad = _repository.Load<AgasProjectResource>(AgasPaths.ProjectFile(fullPath));
        if (!projectLoad.Success || projectLoad.Resource == null)
        {
            errorMessage = string.IsNullOrWhiteSpace(projectLoad.ErrorMessage)
                ? FormatProjectOpenError("无法加载 AGAS 项目文件。")
                : FormatProjectOpenError("无法加载 AGAS 项目文件：{0}", projectLoad.ErrorMessage);
            return null;
        }

        AgasProjectResource project = projectLoad.Resource;
        if (!AgasMcpPathValidator.IsSafeIdentifier(project.ActiveSceneId))
        {
            errorMessage = FormatProjectOpenError("无法打开：项目的活动场景标识无效。");
            return null;
        }

        AgasMcpPathValidator pathValidator = _mcpRuntime?.PathValidator ?? new AgasMcpPathValidator();
        AgasMcpPathValidationResult scenePath = pathValidator.ValidateAgasSceneFile(fullPath, project.ActiveSceneId);
        if (!scenePath.Success)
        {
            errorMessage = string.IsNullOrWhiteSpace(scenePath.Message)
                ? FormatProjectOpenError("无法打开：活动场景路径无效。")
                : FormatProjectOpenError("无法打开活动场景：{0}", scenePath.Message);
            return null;
        }

        var sceneLoad = _repository.Load<AgasSceneResource>(scenePath.AbsolutePath);
        if (!sceneLoad.Success || sceneLoad.Resource == null)
        {
            errorMessage = string.IsNullOrWhiteSpace(sceneLoad.ErrorMessage)
                ? FormatProjectOpenError("无法加载项目的活动场景。")
                : FormatProjectOpenError("无法加载项目的活动场景：{0}", sceneLoad.ErrorMessage);
            return null;
        }

        return string.IsNullOrWhiteSpace(project.Name) ? Path.GetFileName(fullPath) : project.Name;
    }

    private void EnsureActivePageFullRect()
    {
        if (_projectPicker != null && IsInstanceValid(_projectPicker) && _projectPicker.GetParent() == this)
        {
            ApplyFullRect(_projectPicker);
        }

        if (_workspace != null && IsInstanceValid(_workspace) && _workspace.GetParent() == this)
        {
            ApplyFullRect(_workspace);
        }
    }

    private static void ApplyFullRect(Control control)
    {
        control.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        control.SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    private void BuildAppMenuBar()
    {
        if (_appMenuBar != null && IsInstanceValid(_appMenuBar))
        {
            return;
        }

        _appMenuBar = new MenuBar
        {
            Name = "AppMenuBar",
            PreferGlobalMenu = true
        };
        _appMenuBar.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
        _appMenuBar.GrowHorizontal = GrowDirection.Both;

        var settingsMenu = new PopupMenu
        {
            Name = "设置"
        };
        settingsMenu.AddItem("设置...", SettingsMenuGeneralId);
        settingsMenu.AddSeparator();
        settingsMenu.AddItem("MCP...", SettingsMenuMcpId);
        settingsMenu.IdPressed += OnSettingsMenuItemPressed;
        _appMenuBar.AddChild(settingsMenu);

        AddChild(_appMenuBar);
        MoveChild(_appMenuBar, 0);
        RefreshAppMenuTranslation();
    }

    private void OnSettingsMenuItemPressed(long id)
    {
        if ((int)id == SettingsMenuGeneralId)
        {
            OpenSettingsDialog();
        }
        else if ((int)id == SettingsMenuMcpId)
        {
            OpenMcpSettingsDialog();
        }
    }

    private void ClearChildren()
    {
        foreach (Node child in GetChildren())
        {
            if ((_mcpRuntime != null && _mcpRuntime.IsProtectedChild(child))
                || ReferenceEquals(child, _mcpSettingsDialog)
                || ReferenceEquals(child, _settingsDialog)
                || ReferenceEquals(child, _appMenuBar))
            {
                continue;
            }

            RemoveChild(child);
            child.QueueFree();
        }
    }

    private void OpenMcpSettingsDialog()
    {
        EnsureMcpSettingsDialog();
        _mcpRuntime?.UpdateDialogFromRuntimeStatus(_mcpRuntime.Status);
        _mcpSettingsDialog?.Open(this);
    }

    private void OpenSettingsDialog()
    {
        EnsureSettingsDialog();
        AgasMcpRuntimeStatus status = _mcpRuntime?.Status ?? AgasMcpRuntimeStatus.Disabled;
        bool enabled = _mcpRuntime?.SettingsEnabled ?? false;
        _settingsDialog?.UpdateMcpState(
            MainMcpRuntimeComposition.ToDialogStatus(status.State),
            enabled,
            status.LastError);
        _settingsDialog?.Open(this);
    }

    private void EnsureSettingsDialog()
    {
        if (_settingsDialog != null && IsInstanceValid(_settingsDialog))
        {
            return;
        }

        _settingsDialog = new SettingsDialog();
        _settingsDialog.McpSettingsRequested += OpenMcpSettingsDialog;
        _settingsDialog.ActiveProfileChanged += OnActiveAiProfileChanged;
        _settingsDialog.AppearanceFontSizeChanged += ApplyAppearanceFontSize;
        _settingsDialog.VisibilityChanged += OnSettingsDialogVisibilityChanged;
        AddChild(_settingsDialog);
    }

    private void OnActiveAiProfileChanged(string profileId)
    {
        if (_workspace != null && IsInstanceValid(_workspace))
        {
            _workspace.ReloadAiProfiles(profileId);
        }
    }

    private void OnSettingsDialogVisibilityChanged()
    {
        if (_settingsDialog?.Visible == false && _workspace != null && IsInstanceValid(_workspace))
        {
            _workspace.ReloadAiProfiles(_settingsDialog.ActiveProfileId);
        }
    }

    private void ApplyAppearanceFontSize(int fontSize)
    {
        _uiFontSize = AgasUiAppearanceSettings.IsValidFontSize(fontSize)
            ? fontSize
            : AgasUiAppearanceSettings.DefaultFontSize;
        Theme = AgasIdeTheme.Create(_uiFontSize);
        AgasIdeTheme.ApplyScaledFontOverrides(this, _uiFontSize);
    }

    private void EnsureMcpSettingsDialog()
    {
        if (_mcpSettingsDialog != null && IsInstanceValid(_mcpSettingsDialog))
        {
            return;
        }

        _mcpSettingsDialog = new McpSettingsDialog();
        _mcpSettingsDialog.EnabledChangeRequested += OnMcpEnabledChangeRequestedAsync;
        AddChild(_mcpSettingsDialog);
    }

    private Task OnMcpEnabledChangeRequestedAsync(bool enabled)
    {
        if (_mcpRuntime == null)
        {
            return Task.CompletedTask;
        }

        return _mcpRuntime.OnEnabledChangeRequestedAsync(enabled);
    }

    private void BeginApplicationExit()
    {
        if (_applicationQuitIssued || _applicationExitInProgress)
        {
            return;
        }

        if (_workspace != null
            && IsInstanceValid(_workspace)
            && !_workspace.TrySaveForApplicationExit())
        {
            return;
        }

        _applicationExitInProgress = true;
        _ = ShutdownMcpThenQuitAsync();
    }

    private async Task ShutdownMcpThenQuitAsync()
    {
        Exception? shutdownError = null;
        try
        {
            if (_mcpRuntime != null)
            {
                await _mcpRuntime.EnsureDisposedAsync(_workspace);
            }
        }
        catch (Exception exception)
        {
            shutdownError = exception;
        }

        AgasMcpMainThreadDispatcher? dispatcher = _mcpRuntime?.Dispatcher;
        if (dispatcher != null)
        {
            try
            {
                await dispatcher.InvokeAsync(_ => FinishApplicationExitOnMainThread(shutdownError));
                return;
            }
            catch
            {
                // 如果兜底阶段 dispatcher 已离树，下面走 Godot deferred 退出。
            }
        }

        Callable.From(() => FinishApplicationExitOnMainThread(shutdownError)).CallDeferred();
    }

    internal Task EnsureMcpDisposedForTestingAsync()
    {
        return _mcpRuntime?.EnsureDisposedAsync(_workspace) ?? Task.CompletedTask;
    }

    private void FinishApplicationExitOnMainThread(Exception? shutdownError = null)
    {
        if (_applicationQuitIssued)
        {
            return;
        }

        if (shutdownError != null)
        {
            GD.PushWarning($"Application exit MCP shutdown failed: {shutdownError.GetBaseException().Message}");
        }

        _applicationQuitIssued = true;
        _mcpRuntime?.ReleaseDispatcherOnMainThread();
        Action? quitOverride = ApplicationQuitOverrideForTesting;
        if (quitOverride != null)
        {
            quitOverride();
            return;
        }

        GetTree().Quit();
    }
}
