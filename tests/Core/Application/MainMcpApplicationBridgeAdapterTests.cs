using AppAgasCs.Application;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Runtime;
using AGAS.UI.Controllers;
using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class MainMcpApplicationBridgeAdapterTests
{
    [Fact]
    public async Task LifecycleRecentCaptureRejectsWorkerThreadAndFreezesRows()
    {
        var rows = new List<ProjectLifecycleRecentEntry>
        {
            new("/managed/project", "identity", "Project", 1, true),
        };
        var adapter = new MainMcpProjectLifecycleBridgeAdapter(
            Environment.CurrentManagedThreadId,
            new MainMcpProjectLifecycleBridgePorts(
                () => rows,
                () => null,
                (project, execution, token) => ValueTask.FromResult(ProjectLifecycleOpenResult.Ok(project.ProjectRoot, project.Name)),
                (name, execution, token) => ValueTask.FromResult(ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.CreateFailed))));

        IReadOnlyList<ProjectLifecycleRecentEntry> captured = adapter.CaptureRecentProjects();
        rows.Clear();

        Assert.Single(captured);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => adapter.CaptureRecentProjects()));
    }

    [Fact]
    public async Task PackageCaptureAndPublishRejectWorkerThreadBeforeDelegateTouchesGodotState()
    {
        int calls = 0;
        var adapter = new MainMcpScenePackageBridgeAdapter(
            Environment.CurrentManagedThreadId,
            new MainMcpScenePackageBridgePorts(
                sceneId =>
                {
                    calls++;
                    return AgasMcpScenePackageExportCapture.Fail(AgasMcpErrorCodes.SceneNotFound);
                },
                (prepared, execution, token) =>
                {
                    calls++;
                    return ValueTask.FromResult(AgasMcpScenePackagePublishResult.Fail(AgasMcpErrorCodes.PackageInvalid));
                }));

        _ = adapter.CaptureExport("scene-1");
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(() => adapter.CaptureExport("scene-1")));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Task.Run(async () => await adapter.PublishPreparedImportAsync(
                AgasScenePackagePreparedImport.Fail(AgasMcpErrorCodes.PackageInvalid),
                new AgasMcpCommitExecution(AgasMcpCommitPolicy.StartedMainThreadCompletion),
                CancellationToken.None)));
        Assert.Equal(1, calls);
    }

}
