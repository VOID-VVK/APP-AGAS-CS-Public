#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Application;
using AppAgasCs.Mcp.Runtime;
using AppAgasCs.Mcp;

namespace AGAS.UI.Controllers;

/// <summary>
/// Construction ports owned by Main. Delegates may orchestrate worker IO, but every delegate
/// named Capture/Publish/Finalize must project or mutate Godot state only on the main thread.
/// </summary>
internal sealed record MainMcpProjectLifecycleBridgePorts(
    Func<IReadOnlyList<ProjectLifecycleRecentEntry>> CaptureRecentProjects,
    Func<string?> ReadCurrentProjectRoot,
    Func<ProjectLifecycleResolvedRecentProject, AgasMcpCommitExecution, CancellationToken, ValueTask<ProjectLifecycleOpenResult>> OpenResolvedAsync,
    Func<string, AgasMcpCommitExecution, CancellationToken, ValueTask<ProjectLifecycleOpenResult>> CreateAndOpenAsync);

internal sealed class MainMcpProjectLifecycleBridgeAdapter : IAgasMcpProjectLifecycleBridge
{
    private readonly int _mainThreadId;
    private readonly MainMcpProjectLifecycleBridgePorts _ports;

    internal MainMcpProjectLifecycleBridgeAdapter(
        int mainThreadId,
        MainMcpProjectLifecycleBridgePorts ports)
    {
        if (mainThreadId <= 0) throw new ArgumentOutOfRangeException(nameof(mainThreadId));
        _mainThreadId = mainThreadId;
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
        ArgumentNullException.ThrowIfNull(ports.CaptureRecentProjects);
        ArgumentNullException.ThrowIfNull(ports.ReadCurrentProjectRoot);
        ArgumentNullException.ThrowIfNull(ports.OpenResolvedAsync);
        ArgumentNullException.ThrowIfNull(ports.CreateAndOpenAsync);
    }

    public IReadOnlyList<ProjectLifecycleRecentEntry> CaptureRecentProjects()
    {
        EnsureMainThread();
        ProjectLifecycleRecentEntry[] frozen = System.Linq.Enumerable.ToArray(
            _ports.CaptureRecentProjects());
        return Array.AsReadOnly(frozen);
    }

    // This delegate must expose a thread-safe scalar snapshot maintained by Main; it must not
    // walk Controls/Resources because lifecycle facades can inspect it before dispatch.
    public string? CurrentProjectRoot => _ports.ReadCurrentProjectRoot();

    public ValueTask<ProjectLifecycleOpenResult> OpenResolvedAsync(
        ProjectLifecycleResolvedRecentProject project,
        AgasMcpCommitExecution execution,
        CancellationToken cancellationToken) =>
        _ports.OpenResolvedAsync(project, execution, cancellationToken);

    public ValueTask<ProjectLifecycleOpenResult> CreateAndOpenAsync(
        string name,
        AgasMcpCommitExecution execution,
        CancellationToken cancellationToken) =>
        _ports.CreateAndOpenAsync(name, execution, cancellationToken);

    private void EnsureMainThread()
    {
        if (Environment.CurrentManagedThreadId != _mainThreadId)
            throw new InvalidOperationException("Recent project Resource projection must run on the Main-owned thread.");
    }
}

internal sealed record MainMcpScenePackageBridgePorts(
    Func<string, AgasMcpScenePackageExportCapture> CaptureExport,
    Func<AgasScenePackagePreparedImport, AgasMcpCommitExecution, CancellationToken, ValueTask<AgasMcpScenePackagePublishResult>> PublishPreparedImportAsync);

internal sealed class MainMcpScenePackageBridgeAdapter : IAgasMcpScenePackageBridge
{
    private readonly int _mainThreadId;
    private readonly MainMcpScenePackageBridgePorts _ports;

    internal MainMcpScenePackageBridgeAdapter(
        int mainThreadId,
        MainMcpScenePackageBridgePorts ports)
    {
        if (mainThreadId <= 0) throw new ArgumentOutOfRangeException(nameof(mainThreadId));
        _mainThreadId = mainThreadId;
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
        ArgumentNullException.ThrowIfNull(ports.CaptureExport);
        ArgumentNullException.ThrowIfNull(ports.PublishPreparedImportAsync);
    }

    public AgasMcpScenePackageExportCapture CaptureExport(string sceneId)
    {
        EnsureMainThread("Scene package Resource projection");
        return _ports.CaptureExport(sceneId);
    }

    public ValueTask<AgasMcpScenePackagePublishResult> PublishPreparedImportAsync(
        AgasScenePackagePreparedImport prepared,
        AgasMcpCommitExecution execution,
        CancellationToken cancellationToken)
    {
        EnsureMainThread("Scene package session publish");
        return _ports.PublishPreparedImportAsync(prepared, execution, cancellationToken);
    }

    private void EnsureMainThread(string operation)
    {
        if (Environment.CurrentManagedThreadId != _mainThreadId)
            throw new InvalidOperationException($"{operation} must run on the Main-owned thread.");
    }
}

/// <summary>Dispatcher-backed main-thread port used by ProjectLifecycleCloseService.</summary>
internal sealed class MainMcpProjectLifecycleMainThreadPort : IProjectLifecycleMainThreadPort
{
    private readonly AgasMcpMainThreadDispatcher _dispatcher;
    private readonly Func<ProjectLifecycleWorkspaceSnapshot> _capture;
    private readonly Func<ProjectLifecycleSaveOutcome> _save;
    private readonly Func<bool> _finalize;

    internal MainMcpProjectLifecycleMainThreadPort(
        AgasMcpMainThreadDispatcher dispatcher,
        Func<ProjectLifecycleWorkspaceSnapshot> capture,
        Func<ProjectLifecycleSaveOutcome> save,
        Func<bool> finalize)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _finalize = finalize ?? throw new ArgumentNullException(nameof(finalize));
    }

    public async ValueTask<ProjectLifecycleWorkspaceSnapshot> CaptureAsync(CancellationToken cancellationToken) =>
        await _dispatcher.InvokeAsync(_ => _capture(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<ProjectLifecycleWorkspaceSnapshot> RecheckAsync(
        ProjectLifecycleWorkspaceLease lease,
        CancellationToken cancellationToken) =>
        await _dispatcher.InvokeAsync(_ => _capture(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<ProjectLifecycleSaveOutcome> ControlledSaveAsync(
        ProjectLifecycleWorkspaceLease lease,
        CancellationToken cancellationToken) =>
        await _dispatcher.InvokeAsync(_ => _save(), cancellationToken).ConfigureAwait(false);

    public async ValueTask<bool> FinalizeCloseAsync(
        ProjectLifecycleWorkspaceLease lease,
        CancellationToken cancellationToken) =>
        await _dispatcher.InvokeAsync(_ => _finalize(), cancellationToken).ConfigureAwait(false);
}
