using AppAgasCs.Application;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Contracts.Output;
using AppAgasCs.Mcp.Runtime;
using Xunit;

namespace AppAgasCs.Core.Tests.Mcp;

public sealed class AgasMcpProjectLifecycleFacadeWave23Tests
{
    [Fact]
    public async Task CleanCloseSuccessAndFinalizeFailureReturnAccurateTypedReceipts()
    {
        AgasMcpToolResponse success = await Close(finalize: true);
        AgasMcpToolResponse failure = await Close(finalize: false);

        AgasMcpProjectCloseDataDto successData = Assert.IsType<AgasMcpProjectCloseDataDto>(success.Data);
        Assert.True(success.Ok);
        Assert.False(successData.ProjectStillOpen);
        Assert.True(successData.Saved);
        Assert.Equal("none", successData.SaveState);

        AgasMcpProjectCloseDataDto failureData = Assert.IsType<AgasMcpProjectCloseDataDto>(failure.Data);
        Assert.False(failure.Ok);
        Assert.Equal(AgasMcpErrorCodes.InternalError, failure.Error?.Code);
        Assert.True(failureData.ProjectStillOpen);
        Assert.True(failureData.Saved);
        Assert.Equal("none", failureData.SaveState);
    }

    [Fact]
    public async Task LifecycleFailureVariantsUseCentralStableCodes()
    {
        var alreadyOpen = Facade(new MainPort(true), currentRoot: "/open");
        AgasMcpToolResponse open = await alreadyOpen.OpenAsync(
            new AgasProjectOpenInput("projectref-0123456789abcdef0123456789abcdef"),
            Execution(),
            CancellationToken.None);
        AgasMcpToolResponse create = await alreadyOpen.CreateAsync(
            new AgasProjectCreateInput("Project"),
            Execution(),
            CancellationToken.None);

        var closed = Facade(new MainPort(false), currentRoot: null);
        AgasMcpToolResponse invalidRef = await closed.OpenAsync(
            new AgasProjectOpenInput("projectref-0123456789abcdef0123456789abcdef"),
            Execution(),
            CancellationToken.None);
        AgasMcpToolResponse noProject = await closed.CloseAsync(
            new AgasProjectCloseInput(),
            Execution(),
            new ProjectLifecycleCloseReceiptTracker(),
            CancellationToken.None);

        Assert.Equal(AgasMcpErrorCodes.ProjectAlreadyOpen, open.Error?.Code);
        Assert.Equal(AgasMcpErrorCodes.ProjectAlreadyOpen, create.Error?.Code);
        Assert.Equal(AgasMcpErrorCodes.ProjectRefInvalid, invalidRef.Error?.Code);
        Assert.Equal(AgasMcpErrorCodes.NoOpenProject, noProject.Error?.Code);
        Assert.IsType<AgasMcpProjectCloseDataDto>(noProject.Data);
    }

    [Fact]
    public async Task RunningToolBlocksCloseWithToolBusyCode()
    {
        var port = new MainPort(true) { ToolRunning = true };
        AgasMcpToolResponse response = await Facade(port, "/open").CloseAsync(
            new AgasProjectCloseInput(),
            Execution(),
            new ProjectLifecycleCloseReceiptTracker(),
            CancellationToken.None);

        Assert.False(response.Ok);
        Assert.Equal(AgasMcpErrorCodes.ToolBusy, response.Error?.Code);
        Assert.IsType<AgasMcpProjectCloseDataDto>(response.Data);
    }

    private static async Task<AgasMcpToolResponse> Close(bool finalize)
    {
        var port = new MainPort(true) { Finalize = finalize };
        return await Facade(port, "/open").CloseAsync(
            new AgasProjectCloseInput(),
            Execution(),
            new ProjectLifecycleCloseReceiptTracker(),
            CancellationToken.None);
    }

    private static AgasMcpProjectLifecycleFacade Facade(MainPort port, string? currentRoot) => new(
        new Bridge(currentRoot),
        new ProjectLifecycleCloseService(
            new ProjectLifecycleWorkspaceLeaseManager(), port, new Quiescence()));

    private static AgasMcpCommitExecution Execution() =>
        new(AgasMcpCommitPolicy.StartedMainThreadCompletion);

    private sealed class Bridge(string? currentRoot) : IAgasMcpProjectLifecycleBridge
    {
        public string? CurrentProjectRoot => currentRoot;
        public IReadOnlyList<ProjectLifecycleRecentEntry> CaptureRecentProjects() => Array.Empty<ProjectLifecycleRecentEntry>();
        public ValueTask<ProjectLifecycleOpenResult> OpenResolvedAsync(ProjectLifecycleResolvedRecentProject project, AgasMcpCommitExecution execution, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.OpenFailed));
        public ValueTask<ProjectLifecycleOpenResult> CreateAndOpenAsync(string name, AgasMcpCommitExecution execution, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ProjectLifecycleOpenResult.Fail(ProjectLifecycleFailureReason.CreateFailed));
    }

    private sealed class MainPort(bool hasProject) : IProjectLifecycleMainThreadPort
    {
        public bool Finalize { get; init; } = true;
        public bool ToolRunning { get; init; }
        private ProjectLifecycleWorkspaceSnapshot Snapshot => new(
            hasProject,
            hasProject ? AgasMcpAiProjectScope.Create(Path.Combine(Path.GetTempPath(), "agas-close"), "project") : default,
            1,
            false,
            ToolRunning,
            false);
        public ValueTask<ProjectLifecycleWorkspaceSnapshot> CaptureAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Snapshot);
        public ValueTask<ProjectLifecycleWorkspaceSnapshot> RecheckAsync(ProjectLifecycleWorkspaceLease lease, CancellationToken cancellationToken) => ValueTask.FromResult(Snapshot);
        public ValueTask<ProjectLifecycleSaveOutcome> ControlledSaveAsync(ProjectLifecycleWorkspaceLease lease, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Clean close must not save.");
        public ValueTask<bool> FinalizeCloseAsync(ProjectLifecycleWorkspaceLease lease, CancellationToken cancellationToken) => ValueTask.FromResult(Finalize);
    }

    private sealed class Quiescence : IProjectLifecycleQuiescencePort
    {
        public void RequestProjectCancellation(AgasMcpAiProjectScope projectScope) { }
        public ValueTask WaitForProjectQuiescenceAsync(AgasMcpAiProjectScope projectScope, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
