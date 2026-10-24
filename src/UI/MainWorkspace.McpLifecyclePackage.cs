#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Application;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Runtime;

namespace AGAS.UI;

public partial class MainWorkspace
{
    private readonly AgasScenePackageStagingService _mcpScenePackageStaging = new();

    internal ProjectLifecycleWorkspaceSnapshot CaptureMcpLifecycleSnapshot(bool hasActiveAiJobs)
    {
        if (!HasOpenProject || _project == null)
            return new(false, default, GetInstanceIdAsGeneration(), false, false, false);
        return new(
            true,
            AgasMcpAiProjectScope.Create(_projectRoot, _project.ProjectId),
            GetInstanceIdAsGeneration(),
            HasUnsavedChanges,
            _canvasToolRunning,
            hasActiveAiJobs);
    }

    internal ProjectLifecycleSaveOutcome SaveForMcpLifecycle() =>
        _saveController.SaveProjectForMcpLifecycle();

    internal bool FinalizeMcpLifecycleClose()
    {
        if (!HasOpenProject) return true;
        ProjectClosed?.Invoke();
        return true;
    }

    internal AgasMcpScenePackageExportCapture CaptureMcpScenePackageExport(string sceneId)
    {
        if (!HasOpenProject || _project == null || !AgasPaths.IsSafeId(sceneId))
            return AgasMcpScenePackageExportCapture.Fail(AgasMcpErrorCodes.SceneNotFound);
        SceneIndexResource? index = _project.Scenes.FirstOrDefault(item =>
            string.Equals(item.Id, sceneId, StringComparison.Ordinal));
        if (index == null)
            return AgasMcpScenePackageExportCapture.Fail(AgasMcpErrorCodes.SceneNotFound);

        AgasSceneResource? scene = _projectSession.GetCachedScene(sceneId);
        if (scene == null)
        {
            AgasLoadResult<AgasSceneResource> loaded = _repository.LoadFresh<AgasSceneResource>(
                AgasPaths.SceneFile(_projectRoot, sceneId));
            scene = loaded.Success ? loaded.Resource : null;
        }
        if (scene == null)
            return AgasMcpScenePackageExportCapture.Fail(AgasMcpErrorCodes.SceneNotFound);

        try
        {
            return _mcpScenePackageStaging.CaptureExport(scene, $"pkg-{Guid.NewGuid():N}");
        }
        catch
        {
            return AgasMcpScenePackageExportCapture.Fail(AgasMcpErrorCodes.PackageInvalid);
        }
    }

    internal ValueTask<AgasMcpScenePackagePublishResult> PublishMcpPreparedScenePackageAsync(
        AgasScenePackagePreparedImport prepared,
        AgasMcpCommitExecution execution,
        CancellationToken cancellationToken)
    {
        if (execution.Policy != AgasMcpCommitPolicy.StartedMainThreadCompletion)
            throw new ArgumentException("Package import publish requires StartedMainThreadCompletion.", nameof(execution));
        if (cancellationToken.IsCancellationRequested) execution.TryCancel();
        if (!execution.TryBeginMainThreadCompletion())
            return ValueTask.FromResult(AgasMcpScenePackagePublishResult.Fail(AgasMcpErrorCodes.Stopped));
        try
        {
            return ValueTask.FromResult(PublishMcpPreparedScenePackageCore(prepared));
        }
        finally
        {
            execution.CompleteCommit();
        }
    }

    private AgasMcpScenePackagePublishResult PublishMcpPreparedScenePackageCore(
        AgasScenePackagePreparedImport prepared)
    {
        if (!prepared.Success || !HasOpenProject || _project == null || _projectRequiresReadOnly)
            return AgasMcpScenePackagePublishResult.Fail(AgasMcpErrorCodes.PackageInvalid);
        try
        {
            AgasScenePackagePreparedSceneResult built =
                _mcpScenePackageStaging.BuildSceneFromPreparedImport(prepared);
            if (!built.Success || built.Scene == null)
                return AgasMcpScenePackagePublishResult.Fail(built.Code);
            AgasSceneResource scene = built.Scene;
            _project.Scenes.Add(new SceneIndexResource
            {
                Id = scene.Id,
                Name = scene.Name,
                RelativePath = $".agas/scenes/{scene.Id}.tres",
                Order = _project.Scenes.Select(item => item.Order).DefaultIfEmpty(-1).Max() + 1,
            });
            _project.ActiveSceneId = scene.Id;
            _projectSession.CacheScene(scene);
            _projectSession.ChangeCurrentScene(scene);
            AgasMcpDirtySnapshot? dirty = _mcpCoordinator.MarkPackageImported(_projectRoot, _project, scene.Id);
            _dirtyAggregator.MarkLocalDirty();
            if (dirty != null) SyncMcpDirtyState(dirty);
            RefreshManagementUi();
            RefreshCanvasFromMcp();
            RefreshToolbox();
            SetStatusTemplate("已通过 MCP 导入场景：{0}", false, scene.Name);
            return new AgasMcpScenePackagePublishResult(
                true, string.Empty, scene.Id, scene.Name,
                dirty?.DirtySceneIds ?? new[] { scene.Id });
        }
        catch
        {
            return AgasMcpScenePackagePublishResult.Fail(AgasMcpErrorCodes.PackageInvalid);
        }
    }

    private long GetInstanceIdAsGeneration() => unchecked((long)GetInstanceId());
}
