#nullable enable

using System;

namespace AppAgasCs.Application;

public static class ProjectLifecycleReadOnlyGate
{
    public static ProjectLifecycleFailureReason EvaluateOpenOrCreate(
        ProjectLifecycleWorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.HasOpenProject
            ? ProjectLifecycleFailureReason.ProjectAlreadyOpen
            : ProjectLifecycleFailureReason.None;
    }

    public static ProjectLifecycleFailureReason EvaluateClose(
        ProjectLifecycleWorkspaceSnapshot snapshot,
        bool saveIfDirty,
        bool cancelAiJobs)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.HasOpenProject)
            return ProjectLifecycleFailureReason.NoOpenProject;
        if (snapshot.IsToolBusy)
            return ProjectLifecycleFailureReason.ToolBusy;
        if (snapshot.IsDirty && !saveIfDirty)
            return ProjectLifecycleFailureReason.ProjectDirty;
        if (snapshot.HasActiveAiJobs && !cancelAiJobs)
            return ProjectLifecycleFailureReason.AiJobsActive;
        return ProjectLifecycleFailureReason.None;
    }
}
