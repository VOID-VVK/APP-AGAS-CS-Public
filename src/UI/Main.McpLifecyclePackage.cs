#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AGAS.UI.Controllers;
using AppAgasCs.Application;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Runtime;

namespace AGAS.UI;

public partial class Main
{
    private string? _mcpCurrentProjectRootSnapshot;
    private readonly ProjectLifecycleWorkspaceLeaseManager _mcpLifecycleWorkspaceLeaseManager = new();

    internal ProjectLifecycleWorkspaceLeaseManager McpLifecycleWorkspaceLeaseManager =>
        _mcpLifecycleWorkspaceLeaseManager;

    internal MainMcpProjectLifecycleBridgePorts CreateMcpProjectLifecycleBridgePorts() => new(
        CaptureMcpRecentProjects,
        () => Volatile.Read(ref _mcpCurrentProjectRootSnapshot),
        OpenMcpResolvedProjectAsync,
        CreateAndOpenMcpProjectAsync);

    internal MainMcpScenePackageBridgePorts CreateMcpScenePackageBridgePorts() => new(
        sceneId => _workspace?.CaptureMcpScenePackageExport(sceneId)
            ?? AgasMcpScenePackageExportCapture.Fail(AppAgasCs.Mcp.Contracts.AgasMcpErrorCodes.NoOpenProject),
        (prepared, execution, token) => _workspace?.PublishMcpPreparedScenePackageAsync(prepared, execution, token)
            ?? ValueTask.FromResult(AgasMcpScenePackagePublishResult.Fail(AppAgasCs.Mcp.Contracts.AgasMcpErrorCodes.NoOpenProject)));

    internal MainMcpProjectLifecycleMainThreadPort CreateMcpLifecycleMainThreadPort()
    {
        AgasMcpMainThreadDispatcher dispatcher = _mcpRuntime?.Dispatcher
            ?? throw new InvalidOperationException("MCP main-thread dispatcher is unavailable.");
        return new MainMcpProjectLifecycleMainThreadPort(
            dispatcher,
            CaptureMcpLifecycleWorkspace,
            () => _workspace?.SaveForMcpLifecycle()
                ?? new ProjectLifecycleSaveOutcome(false, false, ProjectLifecycleSaveState.None),
            () => _workspace?.FinalizeMcpLifecycleClose() ?? true);
    }

    internal IProjectLifecycleQuiescencePort CreateMcpLifecycleQuiescencePort()
    {
        AgasMcpAiJobScheduler scheduler = _mcpRuntime?.AiJobScheduler
            ?? throw new InvalidOperationException("MCP AI scheduler is unavailable.");
        return new ProjectLifecycleAiJobSchedulerQuiescencePort(scheduler, TimeSpan.FromSeconds(30));
    }

    private IReadOnlyList<ProjectLifecycleRecentEntry> CaptureMcpRecentProjects()
    {
        var identities = new FileSystemProjectAccessIdentityProvider();
        var rows = new List<ProjectLifecycleRecentEntry>();
        foreach (RecentProjectEntryResource entry in _recentProjectsStore.LoadEntries())
        {
            try
            {
                string root = Path.GetFullPath(entry.ProjectPath);
                ProjectAccessPathIdentity identity = identities.Capture(root);
                bool available = identity.Exists
                    && identity.IsDirectory
                    && !identity.IsLinkOrReparse
                    && File.Exists(AgasPaths.ProjectFile(root));
                rows.Add(new ProjectLifecycleRecentEntry(
                    root,
                    identity.IdentityToken,
                    string.IsNullOrWhiteSpace(entry.ProjectName) ? Path.GetFileName(root) : entry.ProjectName,
                    entry.LastOpenedUnixTime,
                    available));
            }
            catch
            {
                // Unsafe/unreadable rows are not issued as capabilities.
            }
        }
        return Array.AsReadOnly(rows
            .OrderByDescending(item => item.LastOpenedUnixTime)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray());
    }

    private async ValueTask<ProjectLifecycleOpenResult> OpenMcpResolvedProjectAsync(
        ProjectLifecycleResolvedRecentProject project,
        AgasMcpCommitExecution execution,
        CancellationToken cancellationToken)
    {
        AgasMcpMainThreadDispatcher? dispatcher = _mcpRuntime?.Dispatcher;
        if (dispatcher == null) return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.OpenFailed);
        return await dispatcher.InvokeAsync(dispatchToken =>
        {
            if (Volatile.Read(ref _mcpCurrentProjectRootSnapshot) != null)
                return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.ProjectAlreadyOpen);
            ProjectLifecycleWorkspaceSnapshot snapshot = CaptureMcpLifecycleWorkspace();
            if (!_mcpLifecycleWorkspaceLeaseManager.TryAcquire(snapshot, out ProjectLifecycleWorkspaceLease? lease)
                || lease == null)
                return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.WorkspaceBusy);
            using (lease)
            {
            string? name = TryGetOpenableProjectName(project.ProjectRoot, createIfMissing: false, out string ignoredOpenError);
            if (name == null) return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.OpenFailed);
            if (!execution.TryBeginMainThreadCompletion())
                return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.CanceledBeforeCommit);
            try
            {
                OpenWorkspace(project.ProjectRoot, createIfMissing: false);
                if (_workspace == null || !_workspace.TryGetCurrentProjectIdentity(out string root, out string ignoredProjectId))
                    return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.OpenFailed);
                Volatile.Write(ref _mcpCurrentProjectRootSnapshot, root);
                return ProjectLifecycleOpenResult.Ok(root, name);
            }
            finally
            {
                execution.CompleteCommit();
            }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ProjectLifecycleOpenResult> CreateAndOpenMcpProjectAsync(
        string name,
        AgasMcpCommitExecution execution,
        CancellationToken cancellationToken)
    {
        AgasMcpMainThreadDispatcher? dispatcher = _mcpRuntime?.Dispatcher;
        if (dispatcher == null) return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.CreateFailed);
        return await dispatcher.InvokeAsync(_ => CreateAndOpenMcpProjectOnMain(name, execution), cancellationToken)
            .ConfigureAwait(false);
    }

    private ProjectLifecycleOpenResult CreateAndOpenMcpProjectOnMain(
        string name,
        AgasMcpCommitExecution execution)
    {
        if (Volatile.Read(ref _mcpCurrentProjectRootSnapshot) != null)
            return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.ProjectAlreadyOpen);
        ProjectLifecycleWorkspaceSnapshot workspaceSnapshot = CaptureMcpLifecycleWorkspace();
        if (!_mcpLifecycleWorkspaceLeaseManager.TryAcquire(workspaceSnapshot, out ProjectLifecycleWorkspaceLease? workspaceLease)
            || workspaceLease == null)
            return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.WorkspaceBusy);
        using (workspaceLease)
        {
        var guard = new ProjectAccessPathGuard();
        ProjectAccessValidationResult validation = guard.PrepareCreateTarget(name);
        if (!validation.Success || validation.Target == null)
            return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.PathRejected);
        ProjectAccessCreateTarget target = validation.Target;
        var identityProvider = new FileSystemProjectAccessIdentityProvider();
        var markerStore = new ProjectAccessOwnerMarkerStore();
        string staging = Path.Combine(target.ManagedRoot, $".agas-create-{Guid.NewGuid():N}");
        ProjectAccessOwnerMarker? marker = null;
        bool published = false;
        try
        {
            Directory.CreateDirectory(staging);
            ProjectAccessPathIdentity stagingIdentity = identityProvider.Capture(staging);
            marker = markerStore.Write(staging, target.TargetPath, stagingIdentity);
            AgasPersistenceResult initialized = _initializer.EnsureInitialized(staging);
            if (!initialized.Success) return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.CreateFailed);
            AgasLoadResult<AgasProjectResource> loaded = _repository.LoadFresh<AgasProjectResource>(AgasPaths.ProjectFile(staging));
            if (!loaded.Success || loaded.Resource == null)
                return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.CreateFailed);
            loaded.Resource.Name = target.DisplayName;
            if (!_repository.Save(loaded.Resource, AgasPaths.ProjectFile(staging)).Success)
                return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.CreateFailed);
            if (!guard.IdentityStillMatches(target.ManagedRootIdentity)
                || File.Exists(target.TargetPath) || Directory.Exists(target.TargetPath)
                || !markerStore.IsOwnedBy(staging, marker, identityProvider.Capture(staging)))
                return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.PathRejected);
            if (!execution.TryBeginMainThreadCompletion())
                return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.CanceledBeforeCommit);
            Directory.Move(staging, target.TargetPath);
            published = true;
            ProjectAccessPathIdentity targetIdentity = identityProvider.Capture(target.TargetPath);
            if (!markerStore.IsOwnedBy(target.TargetPath, marker, targetIdentity))
                return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.CreateFailed);
            OpenWorkspace(target.TargetPath, createIfMissing: false);
            if (_workspace == null || !_workspace.TryGetCurrentProjectIdentity(out string root, out _))
            {
                RollbackOwnedCreate(target.TargetPath, marker, identityProvider, markerStore);
                return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.OpenFailed);
            }
            File.Delete(Path.Combine(target.TargetPath, ProjectAccessOwnerMarkerStore.MarkerFileName));
            Volatile.Write(ref _mcpCurrentProjectRootSnapshot, root);
            return ProjectLifecycleOpenResult.Ok(root, target.DisplayName);
        }
        catch
        {
            if (published && marker != null)
                RollbackOwnedCreate(target.TargetPath, marker, identityProvider, markerStore);
            return ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.CreateFailed);
        }
        finally
        {
            if (execution.State == AgasMcpCommitExecutionState.MainThreadCompleting)
                execution.CompleteCommit();
            if (!published && marker != null)
                RollbackOwnedCreate(staging, marker, identityProvider, markerStore);
        }
        }
    }

    private static void RollbackOwnedCreate(
        string directory,
        ProjectAccessOwnerMarker marker,
        IProjectAccessIdentityProvider identityProvider,
        ProjectAccessOwnerMarkerStore markerStore)
    {
        try
        {
            ProjectAccessPathIdentity identity = identityProvider.Capture(directory);
            if (markerStore.IsOwnedBy(directory, marker, identity)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Fail closed: preserve evidence when ownership cannot be proven.
        }
    }

    private ProjectLifecycleWorkspaceSnapshot CaptureMcpLifecycleWorkspace()
    {
        if (_workspace == null) return new(false, default, 0, false, false, false);
        bool activeJobs = false;
        if (_workspace.TryGetCurrentProjectIdentity(out string root, out string projectId)
            && _mcpRuntime?.AiJobScheduler is { } scheduler)
        {
            AgasMcpAiProjectScope scope = AgasMcpAiProjectScope.Create(root, projectId);
            activeJobs = !scheduler.WaitForProjectQuiescenceAsync(scope, CancellationToken.None).IsCompletedSuccessfully;
        }
        return _workspace.CaptureMcpLifecycleSnapshot(activeJobs);
    }
}
