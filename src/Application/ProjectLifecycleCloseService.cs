#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Runtime;

namespace AppAgasCs.Application;

public sealed class ProjectLifecycleCloseService
{
    private readonly ProjectLifecycleWorkspaceLeaseManager _leaseManager;
    private readonly IProjectLifecycleMainThreadPort _mainThread;
    private readonly IProjectLifecycleQuiescencePort _quiescence;

    public ProjectLifecycleCloseService(
        ProjectLifecycleWorkspaceLeaseManager leaseManager,
        IProjectLifecycleMainThreadPort mainThread,
        IProjectLifecycleQuiescencePort quiescence)
    {
        _leaseManager = leaseManager ?? throw new ArgumentNullException(nameof(leaseManager));
        _mainThread = mainThread ?? throw new ArgumentNullException(nameof(mainThread));
        _quiescence = quiescence ?? throw new ArgumentNullException(nameof(quiescence));
    }

    public async ValueTask<ProjectLifecycleCloseResult> CloseAsync(
        bool saveIfDirty,
        bool cancelAiJobs,
        AgasMcpCommitExecution execution,
        CancellationToken cancellationToken)
    {
        return await CloseAsync(
            saveIfDirty,
            cancelAiJobs,
            execution,
            new ProjectLifecycleCloseReceiptTracker(),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ProjectLifecycleCloseResult> CloseAsync(
        bool saveIfDirty,
        bool cancelAiJobs,
        AgasMcpCommitExecution execution,
        ProjectLifecycleCloseReceiptTracker receiptTracker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(receiptTracker);
        if (execution.Policy != AgasMcpCommitPolicy.StartedMainThreadCompletion)
            throw new ArgumentException("Project close requires StartedMainThreadCompletion.", nameof(execution));

        ProjectLifecycleCloseReceipt receipt = ProjectLifecycleCloseReceipt.Initial;
        receiptTracker.Reset();
        void Publish(ProjectLifecycleCloseReceipt next)
        {
            receipt = next;
            receiptTracker.Publish(next);
        }
        using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
            static state => ((AgasMcpCommitExecution)state!).TryCancel(),
            execution);

        ProjectLifecycleWorkspaceSnapshot capture;
        try
        {
            capture = await _mainThread.CaptureAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ProjectLifecycleCloseResult.Fail(
                ProjectLifecycleFailureReason.CanceledBeforeCommit,
                receipt);
        }

        if (!capture.HasOpenProject)
            return ProjectLifecycleCloseResult.Fail(ProjectLifecycleFailureReason.NoOpenProject, receipt);
        if (!_leaseManager.TryAcquire(capture, out ProjectLifecycleWorkspaceLease? lease) || lease is null)
            return ProjectLifecycleCloseResult.Fail(ProjectLifecycleFailureReason.WorkspaceBusy, receipt);

        using (lease)
        {
            ProjectLifecycleFailureReason gateFailure = ProjectLifecycleReadOnlyGate.EvaluateClose(
                capture,
                saveIfDirty,
                cancelAiJobs);
            if (gateFailure != ProjectLifecycleFailureReason.None)
                return ProjectLifecycleCloseResult.Fail(gateFailure, receipt);

            if (cancelAiJobs)
            {
                try
                {
                    _quiescence.RequestProjectCancellation(capture.ProjectScope);
                    Publish(receipt with { AiCancellationRequested = true });
                    await _quiescence.WaitForProjectQuiescenceAsync(
                        capture.ProjectScope,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    execution.TryCancel();
                    return ProjectLifecycleCloseResult.Fail(
                        ProjectLifecycleFailureReason.CanceledBeforeCommit,
                        receipt);
                }
                catch
                {
                    return ProjectLifecycleCloseResult.Fail(
                        ProjectLifecycleFailureReason.QuiescenceFailed,
                        receipt);
                }
            }

            ProjectLifecycleWorkspaceSnapshot recheck;
            try
            {
                recheck = await _mainThread.RecheckAsync(lease, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                execution.TryCancel();
                return ProjectLifecycleCloseResult.Fail(
                    ProjectLifecycleFailureReason.CanceledBeforeCommit,
                    receipt);
            }

            if (!lease.Matches(recheck))
                return ProjectLifecycleCloseResult.Fail(ProjectLifecycleFailureReason.WorkspaceChanged, receipt);

            ProjectLifecycleFailureReason recheckFailure = ProjectLifecycleReadOnlyGate.EvaluateClose(
                recheck,
                saveIfDirty,
                cancelAiJobs);
            if (recheckFailure != ProjectLifecycleFailureReason.None)
                return ProjectLifecycleCloseResult.Fail(recheckFailure, receipt);

            bool requiresSave = recheck.IsDirty;
            if (!execution.TryBeginMainThreadCompletion())
            {
                return ProjectLifecycleCloseResult.Fail(
                    ProjectLifecycleFailureReason.CanceledBeforeCommit,
                    receipt);
            }

            try
            {
                if (requiresSave)
                {
                    ProjectLifecycleSaveOutcome save;
                    try
                    {
                        save = await _mainThread.ControlledSaveAsync(lease, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        return ProjectLifecycleCloseResult.Fail(
                            ProjectLifecycleFailureReason.SaveFailed,
                            receipt);
                    }

                    Publish(receipt with
                    {
                        Saved = save.Success && save.IsClean,
                        SaveState = save.SaveState,
                    });
                    if (!save.Success || !save.IsClean)
                    {
                        return ProjectLifecycleCloseResult.Fail(
                            ProjectLifecycleFailureReason.SaveFailed,
                            receipt);
                    }

                    ProjectLifecycleWorkspaceSnapshot postSave;
                    try
                    {
                        postSave = await _mainThread.RecheckAsync(lease, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        return ProjectLifecycleCloseResult.Fail(
                            ProjectLifecycleFailureReason.WorkspaceChanged,
                            receipt);
                    }

                    if (!lease.Matches(postSave))
                    {
                        return ProjectLifecycleCloseResult.Fail(
                            ProjectLifecycleFailureReason.WorkspaceChanged,
                            receipt);
                    }
                    if (postSave.IsDirty)
                    {
                        Publish(receipt with { Saved = false });
                        return ProjectLifecycleCloseResult.Fail(
                            ProjectLifecycleFailureReason.SaveFailed,
                            receipt);
                    }
                    if (postSave.IsToolBusy || postSave.HasActiveAiJobs)
                    {
                        return ProjectLifecycleCloseResult.Fail(
                            postSave.IsToolBusy
                                ? ProjectLifecycleFailureReason.ToolBusy
                                : ProjectLifecycleFailureReason.AiJobsActive,
                            receipt);
                    }
                }
                else
                {
                    // A clean project is already fully durable even though this close performs
                    // no save write. Preserve SaveState.None while reporting saved=true.
                    Publish(receipt with { Saved = true });
                }

                bool finalized;
                try
                {
                    finalized = await _mainThread.FinalizeCloseAsync(lease, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    Publish(await ResolveFinalizeFailureReceiptAsync(
                        capture.ProjectScope,
                        receipt).ConfigureAwait(false));
                    return ProjectLifecycleCloseResult.Fail(
                        ProjectLifecycleFailureReason.FinalizeFailed,
                        receipt);
                }

                if (!finalized)
                {
                    Publish(await ResolveFinalizeFailureReceiptAsync(
                        capture.ProjectScope,
                        receipt).ConfigureAwait(false));
                    return ProjectLifecycleCloseResult.Fail(
                        ProjectLifecycleFailureReason.FinalizeFailed,
                        receipt);
                }

                Publish(receipt with { ProjectStillOpen = false, Saved = true });
                return ProjectLifecycleCloseResult.Ok(receipt);
            }
            finally
            {
                execution.CompleteCommit();
            }
        }
    }

    private async ValueTask<ProjectLifecycleCloseReceipt> ResolveFinalizeFailureReceiptAsync(
        AgasMcpAiProjectScope originalScope,
        ProjectLifecycleCloseReceipt receipt)
    {
        try
        {
            ProjectLifecycleWorkspaceSnapshot current = await _mainThread.CaptureAsync(CancellationToken.None)
                .ConfigureAwait(false);
            bool originalProjectStillOpen = current.HasOpenProject
                && current.ProjectScope == originalScope;
            return receipt with { ProjectStillOpen = originalProjectStillOpen };
        }
        catch
        {
            // Preserve the last proven state when recapture itself is unavailable.
            return receipt;
        }
    }

}
