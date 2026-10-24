using System;
using System.Threading;
using System.Threading.Tasks;

namespace AppAgasCs.Mcp;

internal sealed class AgasMcpUiRefreshScope
{
    public static AgasMcpUiRefreshScope Empty { get; } = new();

    public AgasMcpMainThreadDispatcher? Dispatcher { get; init; }
    public Action<string, bool>? SetStatus { get; init; }
    public Action<string, bool>? MarkMcpDirty { get; init; }
    public Action<string, bool>? MarkDirty { get; init; }
    public Action<AgasMcpDirtySnapshot>? SyncDirtyState { get; init; }
    public Action? RefreshProject { get; init; }
    public Action? RefreshCanvas { get; init; }

    public void NotifyDirtyState(AgasMcpDirtySnapshot snapshot)
    {
        QueueUiCallback(() => SyncDirtyState?.Invoke(snapshot));
    }

    public void NotifyMutation(string message, bool error = false)
    {
        QueueUiCallback(() =>
        {
            if (MarkMcpDirty != null)
            {
                MarkMcpDirty.Invoke(message, error);
            }
            else
            {
                MarkDirty?.Invoke(message, error);
            }
            RefreshProject?.Invoke();
            RefreshCanvas?.Invoke();
            SetStatus?.Invoke(message, error);
        });
    }

    public void NotifyStatus(string message, bool error = false)
    {
        QueueUiCallback(() => SetStatus?.Invoke(message, error));
    }

    private void QueueUiCallback(Action callback)
    {
        if (Dispatcher == null)
        {
            callback();
            return;
        }

        Task dispatch;
        try
        {
            dispatch = Dispatcher.InvokeAsync(_ => callback());
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
}
