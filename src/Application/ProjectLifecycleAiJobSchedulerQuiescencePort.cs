#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Mcp;

namespace AppAgasCs.Application;

public sealed class ProjectLifecycleAiJobSchedulerQuiescencePort : IProjectLifecycleQuiescencePort
{
    private readonly AgasMcpAiJobScheduler _scheduler;
    private readonly TimeSpan _timeout;

    public ProjectLifecycleAiJobSchedulerQuiescencePort(
        AgasMcpAiJobScheduler scheduler,
        TimeSpan timeout)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive or infinite.");
        _timeout = timeout;
    }

    public void RequestProjectCancellation(AgasMcpAiProjectScope projectScope) =>
        _scheduler.CancelProject(projectScope);

    public async ValueTask WaitForProjectQuiescenceAsync(
        AgasMcpAiProjectScope projectScope,
        CancellationToken cancellationToken)
    {
        await _scheduler.WaitForProjectQuiescenceAsync(
            projectScope,
            _timeout,
            cancellationToken).ConfigureAwait(false);
    }
}
