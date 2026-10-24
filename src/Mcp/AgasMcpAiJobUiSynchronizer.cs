using System;
using System.Threading;
using System.Threading.Tasks;

namespace AppAgasCs.Mcp;

/// <summary>
/// Marshals scheduler job transitions onto the Godot main thread before they
/// reach the live workspace bridge. Worker callbacks never touch Node/Resource.
/// </summary>
public sealed class AgasMcpAiJobUiSynchronizer : IDisposable
{
    private readonly AgasMcpAiJobScheduler _scheduler;
    private readonly AgasMcpMainThreadDispatcher _dispatcher;
    private readonly AgasMcpProjectContextProvider _contextProvider;
    private int _disposed;

    public AgasMcpAiJobUiSynchronizer(
        AgasMcpAiJobScheduler scheduler,
        AgasMcpMainThreadDispatcher dispatcher,
        AgasMcpProjectContextProvider contextProvider)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _contextProvider = contextProvider ?? throw new ArgumentNullException(nameof(contextProvider));
        _scheduler.JobStateChanged += OnJobStateChanged;
        _scheduler.ActivityChanged += OnActivityChanged;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _scheduler.JobStateChanged -= OnJobStateChanged;
        _scheduler.ActivityChanged -= OnActivityChanged;
    }

    private void OnJobStateChanged(AgasMcpAiJobSnapshot job)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Task dispatch;
        try
        {
            dispatch = _dispatcher.InvokeAsync(_ => ReportOnMainThread(job));
        }
        catch
        {
            return;
        }

        _ = dispatch.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void OnActivityChanged()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        QueueMainThread(static project => project.AiGenerationWorkspace?.RefreshExternalJobActivity());
    }

    private void QueueMainThread(Action<AgasMcpProjectContext> action)
    {
        Task dispatch;
        try
        {
            dispatch = _dispatcher.InvokeAsync(_ =>
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                AgasMcpProjectContext? project = _contextProvider.GetCurrent();
                if (project != null) action(project);
            });
        }
        catch
        {
            return;
        }

        _ = dispatch.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ReportOnMainThread(AgasMcpAiJobSnapshot job)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        AgasMcpProjectContext? project = _contextProvider.GetCurrent();
        if (project?.AiGenerationWorkspace == null)
        {
            return;
        }

        AgasMcpAiProjectScope currentScope;
        try
        {
            currentScope = AgasMcpAiProjectScope.Create(project.ProjectRoot, project.ProjectId);
        }
        catch (ArgumentException)
        {
            return;
        }

        if (job.ProjectScope != currentScope)
        {
            return;
        }

        project.AiGenerationWorkspace.ReportExternalJobStatus(
            AgasMcpAiFacade.CreateExternalJobStatus(job));
    }
}
