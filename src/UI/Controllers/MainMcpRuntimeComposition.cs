using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Application;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using AGAS.UI.Dialogs;
using Godot;
using ModelContextProtocol.Server;

namespace AGAS.UI.Controllers;

/// <summary>
/// Owns the app-root MCP object graph and runtime lifecycle (context provider, dirty state,
/// facades, host, dispose). <see cref="Main"/> remains the shell: picker, workspace page,
/// menus, dialog chrome, and quit orchestration.
/// </summary>
internal sealed class MainMcpRuntimeComposition
{
    private readonly Node _host;
    private readonly Main _main;
    private readonly AgasAiGenerationCoordinator _aiGenerationCoordinator;
    private readonly AgasMcpSettingsStore _settingsStore = new();
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly object _disposeLock = new();

    private Func<McpSettingsDialog?> _getMcpSettingsDialog = static () => null;
    private Func<SettingsDialog?> _getSettingsDialog = static () => null;

    private AgasMcpSettingsResource _settings = new();
    private AgasMcpProjectContextProvider? _contextProvider;
    private AgasMcpDirtyState? _dirtyState;
    private AgasMcpPathValidator? _pathValidator;
    private AgasMcpProjectFacade? _projectFacade;
    private AgasMcpAiJobStore? _aiJobStore;
    private AgasMcpAiJobScheduler? _aiJobScheduler;
    private AgasMcpAiJobUiSynchronizer? _aiJobUiSynchronizer;
    private AgasMcpAiFacade? _aiFacade;
    private AgasMcpAiSettingsFacade? _aiSettingsFacade;
    private AgasMcpProjectLifecycleFacade? _projectLifecycleFacade;
    private AgasMcpImportFacade? _importFacade;
    private IAiGenerationRunner? _aiRunner;
    private AgasMcpMainThreadDispatcher? _dispatcher;
    private AgasMcpSerialToolExecutor? _serialExecutor;
    private AgasMcpToolRuntime? _toolRuntime;
    private IReadOnlyList<McpServerTool> _tools = Array.Empty<McpServerTool>();
    private AgasMcpHostFactory? _hostFactory;
    private AgasMcpLifecycleService? _lifecycle;
    private Task? _disposeTask;
    private bool _disposing;
    private bool _disposed;
    private AgasAiEffectiveProfileSelection? _effectiveAiSelection;

    internal MainMcpRuntimeComposition(Node host, AgasAiGenerationCoordinator aiGenerationCoordinator)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _main = host as Main ?? throw new ArgumentException("MCP runtime host must be Main.", nameof(host));
        _aiGenerationCoordinator = aiGenerationCoordinator
            ?? throw new ArgumentNullException(nameof(aiGenerationCoordinator));
    }

    internal AgasMcpProjectContextProvider? ContextProvider => _contextProvider;
    internal AgasMcpDirtyState? DirtyState => _dirtyState;
    internal AgasMcpPathValidator? PathValidator => _pathValidator;
    internal AgasMcpMainThreadDispatcher? Dispatcher => _dispatcher;
    internal AgasMcpLifecycleService? Lifecycle => _lifecycle;
    internal AgasMcpSettingsResource Settings => _settings;
    internal bool SettingsEnabled => _settings.Enabled;
    internal bool IsDisposed => _disposed;
    internal bool IsDisposing => _disposing;
    internal AgasMcpRuntimeStatus Status => _lifecycle?.Status ?? AgasMcpRuntimeStatus.Disabled;

    /// <summary>Testing/acceptance access to the real app-root AI job scheduler.</summary>
    internal AgasMcpAiJobScheduler? AiJobScheduler => _aiJobScheduler;

    /// <summary>Testing/acceptance access to the real app-root AI job store.</summary>
    internal AgasMcpAiJobStore? AiJobStore => _aiJobStore;

    internal void BindDialogSources(
        Func<McpSettingsDialog?> getMcpSettingsDialog,
        Func<SettingsDialog?> getSettingsDialog)
    {
        _getMcpSettingsDialog = getMcpSettingsDialog
            ?? throw new ArgumentNullException(nameof(getMcpSettingsDialog));
        _getSettingsDialog = getSettingsDialog
            ?? throw new ArgumentNullException(nameof(getSettingsDialog));
    }

    internal bool IsProtectedChild(Node child) => ReferenceEquals(child, _dispatcher);

    internal void ClearContext() => _contextProvider?.Clear();

    internal void RegisterContextSource(IAgasMcpProjectContextSource source)
    {
        _contextProvider?.RegisterSource(source);
    }

    internal void Initialize()
    {
        if (_lifecycle != null)
        {
            return;
        }

        _settings = _settingsStore.LoadOrCreate();
        _contextProvider = new AgasMcpProjectContextProvider();
        _dirtyState = new AgasMcpDirtyState();
        _pathValidator = new AgasMcpPathValidator();
        _projectFacade = new AgasMcpProjectFacade(_contextProvider, _dirtyState, _pathValidator);
        _aiJobStore = new AgasMcpAiJobStore();
        _aiRunner = new AiGenerationServiceRunner();
        _aiJobScheduler = new AgasMcpAiJobScheduler(
            _aiGenerationCoordinator,
            _aiJobStore,
            AgasMcpAiFacade.MapRunnerFailure);
        _aiFacade = new AgasMcpAiFacade(
            _contextProvider,
            _dirtyState,
            _aiJobStore,
            _aiJobScheduler,
            _aiRunner);
        _dispatcher = new AgasMcpMainThreadDispatcher
        {
            Name = "AgasMcpMainThreadDispatcher"
        };
        _host.AddChild(_dispatcher);
        SettingsDialog aiSettingsDialog = _main.EnsureAiSettingsDialogForMcp();
        RefreshEffectiveAiSelection(aiSettingsDialog);
        var aiSettingsStore = new AgasAiSettingsStore();
        _aiSettingsFacade = new AgasMcpAiSettingsFacade(
            new AgasAiProfileSettingsService(aiSettingsStore),
            aiSettingsDialog.McpAiMutationBridge,
            _dirtyState,
            () => Volatile.Read(ref _effectiveAiSelection));
        var lifecycleBridge = new MainMcpProjectLifecycleBridgeAdapter(
            System.Environment.CurrentManagedThreadId,
            _main.CreateMcpProjectLifecycleBridgePorts());
        var closeService = new ProjectLifecycleCloseService(
            _main.McpLifecycleWorkspaceLeaseManager,
            _main.CreateMcpLifecycleMainThreadPort(),
            _main.CreateMcpLifecycleQuiescencePort());
        _projectLifecycleFacade = new AgasMcpProjectLifecycleFacade(lifecycleBridge, closeService);
        var packageBridge = new MainMcpScenePackageBridgeAdapter(
            System.Environment.CurrentManagedThreadId,
            _main.CreateMcpScenePackageBridgePorts());
        _importFacade = new AgasMcpImportFacade(
            _contextProvider,
            _dirtyState,
            packageBridge);
        _aiJobUiSynchronizer = new AgasMcpAiJobUiSynchronizer(
            _aiJobScheduler,
            _dispatcher,
            _contextProvider);
        _hostFactory = new AgasMcpHostFactory();
        CreateExecutorRuntimeAndLifecycle();
    }

    private void CreateExecutorRuntimeAndLifecycle()
    {
        if (_dispatcher == null || _projectFacade == null || _projectLifecycleFacade == null
            || _importFacade == null || _aiSettingsFacade == null || _aiFacade == null)
        {
            throw new InvalidOperationException("MCP runtime dependencies are not initialized.");
        }

        _serialExecutor = new AgasMcpSerialToolExecutor();
        SettingsDialog aiSettingsDialog = _main.EnsureAiSettingsDialogForMcp();
        IReadOnlyDictionary<string, AppAgasCs.Mcp.Runtime.AgasMcpPerToolHandler> handlers =
            AgasMcpDescriptorHandlerRegistry.Create(
                _projectFacade,
                _projectLifecycleFacade,
                _importFacade,
                _aiSettingsFacade,
                _aiFacade,
                () => aiSettingsDialog.StageMcpAiMutation(_main.CurrentMcpWorkspaceId()),
                aiSettingsDialog.FinishMcpAiMutation,
                () => RefreshEffectiveAiSelection(aiSettingsDialog));
        _toolRuntime = new AgasMcpToolRuntime(
            _serialExecutor,
            _dispatcher,
            handlers);
        _tools = AgasMcpToolCatalog.Create(_toolRuntime);
        _hostFactory ??= new AgasMcpHostFactory();
        _lifecycle = new AgasMcpLifecycleService(
            _hostFactory,
            _tools,
            _serialExecutor,
            _aiJobScheduler);
        _lifecycle.StatusChanged += OnRuntimeStatusChanged;
    }

    private void RefreshEffectiveAiSelection(SettingsDialog dialog) =>
        Volatile.Write(ref _effectiveAiSelection, dialog.CaptureEffectiveAiSelection());

    internal void ApplyPersistedSettings()
    {
        UpdateDialogFromRuntimeStatus(_lifecycle?.Status ?? AgasMcpRuntimeStatus.Disabled);
        if (_settings.Enabled)
        {
            _ = ApplyPersistedSettingsAsync();
        }
    }

    internal async Task OnEnabledChangeRequestedAsync(bool enabled)
    {
        _settings.Enabled = enabled;
        Error saveResult = _settingsStore.Save(_settings);
        if (saveResult != Error.Ok)
        {
            throw new InvalidOperationException($"保存 MCP 设置失败：{saveResult}");
        }

        await ApplySettingsAsync();
    }

    internal async Task ApplySettingsAsync(CancellationToken cancellationToken = default)
    {
        await _settingsGate.WaitAsync(cancellationToken);
        try
        {
            AgasMcpLifecycleService? lifecycle = _lifecycle;
            if (lifecycle == null || _disposing)
            {
                return;
            }

            UpdateDialogFromRuntimeStatus(new AgasMcpRuntimeStatus(
                _settings.Enabled ? AgasMcpRuntimeState.Starting : AgasMcpRuntimeState.Stopping,
                AgasMcpSettingsResource.FixedEndpoint));
            await lifecycle.ApplyEnabledAsync(_settings.Enabled, cancellationToken);

            QueueDialogStatusUpdate(lifecycle.Status);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            QueueDialogStatusUpdate(new AgasMcpRuntimeStatus(
                AgasMcpRuntimeState.Error,
                AgasMcpSettingsResource.FixedEndpoint,
                exception.Message));
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    internal void TryClearDirtyProjectBindingIfNoUnsaved(MainWorkspace? workspace)
    {
        AgasMcpDirtyState? dirtyState = _dirtyState;
        if (dirtyState == null || workspace == null || !GodotObject.IsInstanceValid(workspace) || workspace.HasUnsavedChanges)
        {
            return;
        }

        if (workspace.TryGetCurrentProjectIdentity(out string projectRoot, out string projectId))
        {
            dirtyState.ClearForProject(projectRoot, projectId);
        }
    }

    internal void CancelAiJobsForWorkspace(MainWorkspace? workspace)
    {
        if (_aiJobScheduler == null
            || workspace == null
            || !GodotObject.IsInstanceValid(workspace)
            || !workspace.TryGetCurrentProjectIdentity(out string projectRoot, out string projectId))
        {
            return;
        }

        _aiJobScheduler.CancelProject(AgasMcpAiProjectScope.Create(projectRoot, projectId));
    }

    internal void BeginDisposeFallback()
    {
        if (_disposed)
        {
            return;
        }

        _ = EnsureDisposedAsync(workspace: null);
    }

    internal Task EnsureDisposedAsync(MainWorkspace? workspace)
    {
        lock (_disposeLock)
        {
            if (_disposeTask != null)
            {
                return _disposeTask;
            }

            _disposing = true;
            TryClearDirtyProjectBindingIfNoUnsaved(workspace);
            _disposeTask = DisposeAsync();
            return _disposeTask;
        }
    }

    private async Task DisposeAsync()
    {
        await _settingsGate.WaitAsync();
        try
        {
            AgasMcpLifecycleService? lifecycle = _lifecycle;
            AgasMcpSerialToolExecutor? executor = _serialExecutor;
            AgasMcpAiJobScheduler? aiJobScheduler = _aiJobScheduler;
            AgasMcpAiJobUiSynchronizer? aiJobUiSynchronizer = _aiJobUiSynchronizer;
            AgasMcpAiJobStore? aiJobStore = _aiJobStore;
            IAiGenerationRunner? aiRunner = _aiRunner;

            _lifecycle = null;
            _serialExecutor = null;
            _aiJobScheduler = null;
            _aiJobUiSynchronizer = null;
            _aiJobStore = null;
            _aiRunner = null;

            aiJobUiSynchronizer?.Dispose();

            if (lifecycle != null)
            {
                lifecycle.StatusChanged -= OnRuntimeStatusChanged;
                try
                {
                    await lifecycle.StopAsync();
                }
                catch
                {
                    // 退出阶段不把 host 停止异常重新抛回 Godot 主循环。
                }

                await lifecycle.DisposeAsync();
            }

            if (executor != null)
            {
                await executor.DisposeAsync();
            }

            if (aiJobScheduler != null)
            {
                await aiJobScheduler.DisposeAsync();
            }
            if (aiRunner is IDisposable disposableRunner)
            {
                disposableRunner.Dispose();
            }
            aiJobStore?.Dispose();

            _contextProvider?.Clear();
            _disposed = true;
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    internal void ReleaseDispatcherOnMainThread()
    {
        AgasMcpMainThreadDispatcher? dispatcher = _dispatcher;
        _dispatcher = null;
        if (dispatcher != null
            && GodotObject.IsInstanceValid(dispatcher)
            && dispatcher.GetParent() == _host)
        {
            _host.RemoveChild(dispatcher);
            dispatcher.QueueFree();
        }
    }

    internal void UpdateDialogFromRuntimeStatus(AgasMcpRuntimeStatus status)
    {
        McpSettingsStatus dialogStatus = ToDialogStatus(status.State);
        McpSettingsDialog? mcpDialog = _getMcpSettingsDialog();
        if (mcpDialog != null && GodotObject.IsInstanceValid(mcpDialog))
        {
            mcpDialog.UpdateState(dialogStatus, status.LastError, _settings.Enabled);
        }

        SettingsDialog? settingsDialog = _getSettingsDialog();
        if (settingsDialog != null && GodotObject.IsInstanceValid(settingsDialog))
        {
            settingsDialog.UpdateMcpState(dialogStatus, _settings.Enabled, status.LastError);
        }
    }

    internal static McpSettingsStatus ToDialogStatus(AgasMcpRuntimeState state)
    {
        return state switch
        {
            AgasMcpRuntimeState.Starting => McpSettingsStatus.Starting,
            AgasMcpRuntimeState.Running => McpSettingsStatus.Running,
            AgasMcpRuntimeState.Stopping => McpSettingsStatus.Stopping,
            AgasMcpRuntimeState.Stopped => McpSettingsStatus.Stopped,
            AgasMcpRuntimeState.Error => McpSettingsStatus.Error,
            _ => McpSettingsStatus.Disabled,
        };
    }

    private void OnRuntimeStatusChanged(AgasMcpRuntimeStatus status)
    {
        QueueDialogStatusUpdate(status);
    }

    private void QueueDialogStatusUpdate(AgasMcpRuntimeStatus status)
    {
        AgasMcpMainThreadDispatcher? dispatcher = _dispatcher;
        if (dispatcher == null || _disposing)
        {
            return;
        }

        try
        {
            Task updateTask = dispatcher.InvokeAsync(_ => UpdateDialogFromRuntimeStatus(status));
            _ = updateTask.ContinueWith(static completedTask =>
            {
                _ = completedTask.Exception;
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch
        {
            // 状态同步不应打断 MCP 生命周期动作。
        }
    }

    private async Task ApplyPersistedSettingsAsync()
    {
        try
        {
            await ApplySettingsAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            GD.PushWarning($"MCP persisted settings apply failed: {exception.Message}");
        }
    }
}
