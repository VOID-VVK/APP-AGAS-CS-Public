using AppAgasCs.Application;
using AppAgasCs.Mcp;
using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class ProjectLifecycleWorkspaceLeaseTests
{
    [Fact]
    public void LeaseIsExclusiveAndMatchesOnlyFrozenScopeAndGeneration()
    {
        var manager = new ProjectLifecycleWorkspaceLeaseManager();
        var snapshot = new ProjectLifecycleWorkspaceSnapshot(true, Scope(), 4, false, false, false);

        Assert.True(manager.TryAcquire(snapshot, out ProjectLifecycleWorkspaceLease? lease));
        Assert.NotNull(lease);
        Assert.False(manager.TryAcquire(snapshot, out _));
        Assert.True(lease.Matches(snapshot));
        Assert.False(lease.Matches(snapshot with { ProjectScope = Scope("scope-b") }));
        Assert.False(lease.Matches(snapshot with { Generation = 5 }));

        lease.Dispose();
        Assert.True(manager.TryAcquire(snapshot, out ProjectLifecycleWorkspaceLease? replacement));
        replacement!.Dispose();
    }

    [Fact]
    public void NoProjectLeaseSupportsOpenAndCreatePreflight()
    {
        var manager = new ProjectLifecycleWorkspaceLeaseManager();
        var noProject = new ProjectLifecycleWorkspaceSnapshot(false, default, 8, false, false, false);

        Assert.True(manager.TryAcquire(noProject, out ProjectLifecycleWorkspaceLease? lease));
        Assert.NotNull(lease);
        Assert.False(lease.HadOpenProject);
        Assert.True(lease.Matches(noProject));
        Assert.False(lease.Matches(noProject with { HasOpenProject = true, ProjectScope = Scope() }));
        lease.Dispose();
    }

    [Fact]
    public void OpenAndCreateGateRequiresNoOpenProject()
    {
        var noProject = new ProjectLifecycleWorkspaceSnapshot(false, default, 1, false, false, false);
        var openProject = noProject with { HasOpenProject = true, ProjectScope = Scope() };

        Assert.Equal(
            ProjectLifecycleFailureReason.None,
            ProjectLifecycleReadOnlyGate.EvaluateOpenOrCreate(noProject));
        Assert.Equal(
            ProjectLifecycleFailureReason.ProjectAlreadyOpen,
            ProjectLifecycleReadOnlyGate.EvaluateOpenOrCreate(openProject));
    }

    private static AgasMcpAiProjectScope Scope(string projectId = "scope-a") =>
        AgasMcpAiProjectScope.Create(
            Path.Combine(Path.GetTempPath(), "agas-project-lifecycle-lease-tests", projectId),
            projectId);
}
