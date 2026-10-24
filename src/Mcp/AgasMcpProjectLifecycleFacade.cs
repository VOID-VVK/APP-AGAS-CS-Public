#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Application;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Contracts.Output;
using AppAgasCs.Mcp.Contracts.Runtime;
using AppAgasCs.Mcp.Runtime;

namespace AppAgasCs.Mcp;

public sealed class AgasMcpProjectLifecycleFacade
{
    private readonly IAgasMcpProjectLifecycleBridge _bridge;
    private readonly ProjectLifecycleRecentRefBroker _recentRefs;
    private readonly ProjectLifecycleCloseService _closeService;

    public AgasMcpProjectLifecycleFacade(
        IAgasMcpProjectLifecycleBridge bridge,
        ProjectLifecycleCloseService closeService,
        ProjectLifecycleRecentRefBroker? recentRefs = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _closeService = closeService ?? throw new ArgumentNullException(nameof(closeService));
        _recentRefs = recentRefs ?? new ProjectLifecycleRecentRefBroker();
    }

    public AgasMcpToolResponse Recent(AgasProjectsRecentInput input)
    {
        if (!TryValidate(input, out input, out AgasMcpToolResponse? error)) return error!;
        IReadOnlyList<ProjectLifecycleRecentProject> projects = _recentRefs.Rotate(
            _bridge.CaptureRecentProjects(), _bridge.CurrentProjectRoot, input.Limit);
        var data = new AgasMcpRecentProjectsDataDto(projects.Select(item => new AgasMcpRecentProjectDto(
            item.ProjectRef,
            SafeName(item.Name),
            item.LastOpenedUnixTime,
            item.Available,
            item.IsOpen)).ToImmutableArray());
        return AgasMcpToolResponse.Success(saved: true, dirty: false, data);
    }

    public async ValueTask<AgasMcpToolResponse> OpenAsync(
        AgasProjectOpenInput input,
        AgasMcpCommitExecution execution,
        CancellationToken cancellationToken)
    {
        if (!TryValidate(input, out input, out AgasMcpToolResponse? error)) return error!;
        if (execution.Policy != AgasMcpCommitPolicy.StartedMainThreadCompletion)
            throw new ArgumentException("Project open requires StartedMainThreadCompletion.", nameof(execution));
        if (_bridge.CurrentProjectRoot != null) return Failure(ProjectLifecycleFailureReason.ProjectAlreadyOpen);
        IReadOnlyList<ProjectLifecycleRecentEntry> entries = _bridge.CaptureRecentProjects();
        if (!_recentRefs.TryResolve(input.ProjectRef, entries, out ProjectLifecycleResolvedRecentProject? resolved)
            || resolved == null)
            return Failure(ProjectLifecycleFailureReason.ProjectRefInvalid);

        using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
            static state => ((AgasMcpCommitExecution)state!).TryCancel(), execution);
        ProjectLifecycleOpenResult result = await _bridge.OpenResolvedAsync(
            resolved, execution, cancellationToken).ConfigureAwait(false);
        if (!result.Success) return Failure(result.FailureReason);
        var data = new AgasMcpProjectOpenDataDto(input.ProjectRef, SafeName(result.Name), true);
        return AgasMcpToolResponse.Success(saved: true, dirty: false, data);
    }

    public async ValueTask<AgasMcpToolResponse> CreateAsync(
        AgasProjectCreateInput input,
        AgasMcpCommitExecution execution,
        CancellationToken cancellationToken)
    {
        if (!TryValidate(input, out input, out AgasMcpToolResponse? error)) return error!;
        if (execution.Policy != AgasMcpCommitPolicy.StartedMainThreadCompletion)
            throw new ArgumentException("Project create requires StartedMainThreadCompletion.", nameof(execution));
        if (_bridge.CurrentProjectRoot != null) return Failure(ProjectLifecycleFailureReason.ProjectAlreadyOpen);
        using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
            static state => ((AgasMcpCommitExecution)state!).TryCancel(), execution);
        ProjectLifecycleOpenResult result = await _bridge.CreateAndOpenAsync(
            input.Name, execution, cancellationToken).ConfigureAwait(false);
        if (!result.Success) return Failure(result.FailureReason);

        IReadOnlyList<ProjectLifecycleRecentProject> rotated = _recentRefs.Rotate(
            _bridge.CaptureRecentProjects(), result.ProjectRoot, ProjectLifecycleRecentRefBroker.MaximumActiveReferences);
        ProjectLifecycleRecentProject? created = rotated.FirstOrDefault(item => item.IsOpen);
        if (created == null)
            return Failure(ProjectLifecycleFailureReason.CreateFailed);
        var data = new AgasMcpProjectCreateDataDto(created.ProjectRef, SafeName(result.Name), true);
        return AgasMcpToolResponse.Success(saved: true, dirty: false, data);
    }

    public async ValueTask<AgasMcpToolResponse> CloseAsync(
        AgasProjectCloseInput input,
        AgasMcpCommitExecution execution,
        ProjectLifecycleCloseReceiptTracker receiptTracker,
        CancellationToken cancellationToken)
    {
        if (!TryValidate(input, out input, out AgasMcpToolResponse? error)) return error!;
        ProjectLifecycleCloseResult result = await _closeService.CloseAsync(
            input.SaveIfDirty,
            input.CancelAiJobs,
            execution,
            receiptTracker,
            cancellationToken).ConfigureAwait(false);
        AgasMcpProjectCloseDataDto data = CloseData(result.Receipt);
        if (result.Success)
            return AgasMcpToolResponse.Success(saved: data.Saved, dirty: data.ProjectStillOpen && !data.Saved, data);
        AgasMcpToolResponse mapped = Failure(result.FailureReason);
        return new AgasMcpToolResponse(
            ok: false,
            saved: data.Saved,
            dirty: data.ProjectStillOpen && !data.Saved,
            data,
            Array.Empty<string>(),
            mapped.Error);
    }

    public AgasMcpProjectCloseDataDto LastCloseReceipt(ProjectLifecycleCloseReceiptTracker tracker) =>
        CloseData((tracker ?? throw new ArgumentNullException(nameof(tracker))).Current);

    private static AgasMcpProjectCloseDataDto CloseData(ProjectLifecycleCloseReceipt receipt) => new(
        receipt.ProjectStillOpen,
        receipt.Saved,
        receipt.AiCancellationRequested,
        receipt.SaveState switch
        {
            ProjectLifecycleSaveState.Partial => "partial",
            ProjectLifecycleSaveState.Complete => "complete",
            _ => "none",
        });

    private static bool TryValidate<T>(T input, out T validated, out AgasMcpToolResponse? error) where T : class
    {
        AgasMcpContractBindResult<T> result = AgasMcpContractRegistry.ValidateInstance(input);
        validated = result.Value!;
        error = result.Success && result.Value != null
            ? null
            : InvalidArguments();
        return error == null;
    }

    private static string SafeName(string? name)
    {
        string value = string.IsNullOrWhiteSpace(name) ? "AGAS Project" : name.Trim();
        return value.Length <= 120 ? value : value[..120];
    }

    private static AgasMcpToolResponse InvalidArguments() => AgasMcpToolResponse.Failure(
        false, false, AgasMcpErrorCodes.InvalidOperation,
        "Tool arguments did not match the expected JSON shape.",
        "Check the documented lifecycle tool input schema.");

    private static AgasMcpToolResponse Failure(ProjectLifecycleFailureReason reason)
    {
        (string code, string message, string suggestion) = reason switch
        {
            ProjectLifecycleFailureReason.NoOpenProject => (AgasMcpErrorCodes.NoOpenProject, "No AGAS project is currently open.", "Open a project before closing it."),
            ProjectLifecycleFailureReason.ProjectAlreadyOpen => (AgasMcpErrorCodes.ProjectAlreadyOpen, "An AGAS project is already open.", "Close the current project before opening or creating another."),
            ProjectLifecycleFailureReason.ProjectRefInvalid => (AgasMcpErrorCodes.ProjectRefInvalid, "Project reference is invalid or expired.", "Call agas.projects.recent again and use the newly issued projectRef."),
            ProjectLifecycleFailureReason.ProjectDirty => (AgasMcpErrorCodes.ProjectDirty, "Project has unsaved changes.", "Retry with saveIfDirty=true after reviewing the project."),
            ProjectLifecycleFailureReason.AiJobsActive => (AgasMcpErrorCodes.AiJobsActive, "Project has active AI jobs.", "Wait for jobs or retry with cancelAiJobs=true."),
            ProjectLifecycleFailureReason.ToolBusy => (AgasMcpErrorCodes.ToolBusy, "A tool is running.", "Wait for the tool to finish before closing."),
            ProjectLifecycleFailureReason.SaveFailed => (AgasMcpErrorCodes.SaveFailed, "Project save did not complete.", "Resolve save errors and retry close."),
            ProjectLifecycleFailureReason.PathRejected => (AgasMcpErrorCodes.PathRejected, "Managed project path was rejected.", "Use a valid project name and the configured managed project root."),
            ProjectLifecycleFailureReason.CanceledBeforeCommit => (AgasMcpErrorCodes.Timeout, "Project lifecycle operation timed out before commit.", "Retry after the workspace becomes idle."),
            ProjectLifecycleFailureReason.WorkspaceBusy => (AgasMcpErrorCodes.Busy, "Workspace lifecycle mutation is busy.", "Retry after the current workspace operation completes."),
            ProjectLifecycleFailureReason.WorkspaceChanged => (AgasMcpErrorCodes.InvalidOperation, "Workspace changed during lifecycle validation.", "Refresh project state and retry."),
            ProjectLifecycleFailureReason.QuiescenceFailed => (AgasMcpErrorCodes.Timeout, "AI jobs did not quiesce before the deadline.", "Wait for AI runners to stop and retry."),
            ProjectLifecycleFailureReason.OpenFailed => (AgasMcpErrorCodes.InvalidOperation, "Project could not be opened safely.", "Refresh recent projects and retry."),
            ProjectLifecycleFailureReason.CreateFailed => (AgasMcpErrorCodes.InvalidOperation, "Project could not be created safely.", "Choose another project name and retry."),
            _ => (AgasMcpErrorCodes.InternalError, "Project lifecycle operation failed.", "Retry after restarting AGAS."),
        };
        return AgasMcpToolResponse.Failure(false, false, code, message, suggestion);
    }
}
