#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Mcp;

namespace AppAgasCs.Application;

public enum ProjectLifecycleFailureReason
{
    None,
    NoOpenProject,
    ProjectAlreadyOpen,
    ProjectDirty,
    AiJobsActive,
    ToolBusy,
    WorkspaceBusy,
    WorkspaceChanged,
    QuiescenceFailed,
    SaveFailed,
    FinalizeFailed,
    CanceledBeforeCommit,
    ProjectRefInvalid,
    PathRejected,
    OpenFailed,
    CreateFailed,
}

public enum ProjectLifecycleSaveState
{
    None,
    Partial,
    Complete,
}

public sealed record ProjectLifecycleCloseReceipt(
    bool ProjectStillOpen,
    bool Saved,
    bool AiCancellationRequested,
    ProjectLifecycleSaveState SaveState)
{
    public static ProjectLifecycleCloseReceipt Initial { get; } =
        new(true, false, false, ProjectLifecycleSaveState.None);
}

/// <summary>
/// Publishes the last immutable close receipt while a lifecycle handler is in
/// progress. Runtime fail-closed mapping may read <see cref="Current"/>; it must
/// never reconstruct side effects from mutable workspace state.
/// </summary>
public sealed class ProjectLifecycleCloseReceiptTracker
{
    private ProjectLifecycleCloseReceipt _current = ProjectLifecycleCloseReceipt.Initial;

    public ProjectLifecycleCloseReceipt Current => Volatile.Read(ref _current);

    internal void Reset() => Publish(ProjectLifecycleCloseReceipt.Initial);

    internal void Publish(ProjectLifecycleCloseReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        Interlocked.Exchange(ref _current, receipt);
    }
}

public sealed record ProjectLifecycleCloseResult(
    bool Success,
    ProjectLifecycleFailureReason FailureReason,
    ProjectLifecycleCloseReceipt Receipt)
{
    public static ProjectLifecycleCloseResult Ok(ProjectLifecycleCloseReceipt receipt) =>
        new(true, ProjectLifecycleFailureReason.None, receipt);

    public static ProjectLifecycleCloseResult Fail(
        ProjectLifecycleFailureReason reason,
        ProjectLifecycleCloseReceipt receipt) =>
        new(false, reason, receipt);
}

public sealed record ProjectLifecycleWorkspaceSnapshot(
    bool HasOpenProject,
    AgasMcpAiProjectScope ProjectScope,
    long Generation,
    bool IsDirty,
    bool IsToolBusy,
    bool HasActiveAiJobs);

public sealed record ProjectLifecycleSaveOutcome(
    bool Success,
    bool IsClean,
    ProjectLifecycleSaveState SaveState);

public interface IProjectLifecycleMainThreadPort
{
    ValueTask<ProjectLifecycleWorkspaceSnapshot> CaptureAsync(CancellationToken cancellationToken);

    ValueTask<ProjectLifecycleWorkspaceSnapshot> RecheckAsync(
        ProjectLifecycleWorkspaceLease lease,
        CancellationToken cancellationToken);

    ValueTask<ProjectLifecycleSaveOutcome> ControlledSaveAsync(
        ProjectLifecycleWorkspaceLease lease,
        CancellationToken cancellationToken);

    ValueTask<bool> FinalizeCloseAsync(
        ProjectLifecycleWorkspaceLease lease,
        CancellationToken cancellationToken);
}

public interface IProjectLifecycleQuiescencePort
{
    void RequestProjectCancellation(AgasMcpAiProjectScope projectScope);

    ValueTask WaitForProjectQuiescenceAsync(
        AgasMcpAiProjectScope projectScope,
        CancellationToken cancellationToken);
}

public sealed record ProjectLifecycleRecentEntry(
    string ProjectRoot,
    string ProjectIdentity,
    string Name,
    long LastOpenedUnixTime,
    bool Available);

public sealed record ProjectLifecycleRecentProject(
    string ProjectRef,
    string Name,
    long LastOpenedUnixTime,
    bool Available,
    bool IsOpen);

public sealed record ProjectLifecycleResolvedRecentProject(
    string ProjectRoot,
    string ProjectIdentity,
    string Name);

public sealed record ProjectLifecycleOpenResult(
    bool Success,
    ProjectLifecycleFailureReason FailureReason,
    string ProjectRoot,
    string Name)
{
    public static ProjectLifecycleOpenResult Ok(string projectRoot, string name) =>
        new(true, ProjectLifecycleFailureReason.None, projectRoot, name);

    public static ProjectLifecycleOpenResult Fail(ProjectLifecycleFailureReason reason) =>
        new(false, reason, string.Empty, string.Empty);
}

/// <summary>
/// Main-owned lifecycle surface. Implementations perform workspace lease/generation checks and
/// ensure Godot Resource/Node work remains on the dispatcher thread.
/// </summary>
public interface IAgasMcpProjectLifecycleBridge
{
    IReadOnlyList<ProjectLifecycleRecentEntry> CaptureRecentProjects();
    string? CurrentProjectRoot { get; }

    ValueTask<ProjectLifecycleOpenResult> OpenResolvedAsync(
        ProjectLifecycleResolvedRecentProject project,
        AppAgasCs.Mcp.Runtime.AgasMcpCommitExecution execution,
        CancellationToken cancellationToken);

    ValueTask<ProjectLifecycleOpenResult> CreateAndOpenAsync(
        string name,
        AppAgasCs.Mcp.Runtime.AgasMcpCommitExecution execution,
        CancellationToken cancellationToken);
}

public interface IProjectLifecycleClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemProjectLifecycleClock : IProjectLifecycleClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface IProjectLifecycleRefFactory
{
    string CreateProjectRef();
}

public sealed class RandomProjectLifecycleRefFactory : IProjectLifecycleRefFactory
{
    public string CreateProjectRef() => $"projectref-{Guid.NewGuid():N}";
}
