using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

namespace AppAgasCs.Mcp;

public sealed class AgasMcpLifecycleService : IAsyncDisposable, IDisposable
{
    private readonly AgasMcpHostFactory _hostFactory;
    private readonly IEnumerable<McpServerTool> _tools;
    private readonly IAgasMcpToolExecutor? _toolExecutor;
    private readonly AgasMcpAiJobScheduler? _aiJobScheduler;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IHost? _host;
    private bool _disposed;
    private AgasMcpRuntimeStatus _status;

    public event Action<AgasMcpRuntimeStatus>? StatusChanged;

    public AgasMcpRuntimeStatus Status
    {
        get => _status;
        private set
        {
            _status = value;
            StatusChanged?.Invoke(value);
        }
    }

    public AgasMcpLifecycleService(
        AgasMcpHostFactory hostFactory,
        IEnumerable<McpServerTool>? tools = null,
        IAgasMcpToolExecutor? toolExecutor = null,
        AgasMcpAiJobScheduler? aiJobScheduler = null)
    {
        _hostFactory = hostFactory;
        _tools = tools ?? Array.Empty<McpServerTool>();
        _toolExecutor = toolExecutor;
        _aiJobScheduler = aiJobScheduler;
        _status = StatusFor(AgasMcpRuntimeState.Disabled);
    }

    public async Task ApplySettingsAsync(AgasMcpSettingsResource settings, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(settings);

        await ApplyEnabledAsync(settings.Enabled, cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (enabled)
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await StopAsync(cancellationToken).ConfigureAwait(false);
            Status = StatusFor(AgasMcpRuntimeState.Disabled);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_host != null)
            {
                _toolExecutor?.AllowNewWork();
                _aiJobScheduler?.AllowNewWork();
                Status = StatusFor(AgasMcpRuntimeState.Running);
                return;
            }

            _toolExecutor?.BeginRejectingNewWork();
            Status = StatusFor(AgasMcpRuntimeState.Starting);
            IHost host = _hostFactory.Create(_tools, _toolExecutor);
            try
            {
                await host.StartAsync(cancellationToken);
                _host = host;
                _toolExecutor?.AllowNewWork();
                _aiJobScheduler?.AllowNewWork();
                Status = StatusFor(AgasMcpRuntimeState.Running);
            }
            catch (Exception ex) when (IsPortConflict(ex))
            {
                await DisposeHostAsync(host);
                Status = StatusFor(
                    AgasMcpRuntimeState.Error,
                    $"端口 {AgasMcpSettingsResource.FixedHost}:{_hostFactory.Port} 已被占用。");
            }
            catch (Exception ex)
            {
                await DisposeHostAsync(host);
                Status = StatusFor(AgasMcpRuntimeState.Error, ex.Message);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_toolExecutor != null)
            {
                await _toolExecutor.RejectNewWorkAndCancelActiveAndPendingAsync().ConfigureAwait(false);
            }
            if (_aiJobScheduler != null)
            {
                await _aiJobScheduler.StopAsync().ConfigureAwait(false);
            }

            if (_host == null)
            {
                Status = StatusFor(AgasMcpRuntimeState.Stopped);
                return;
            }

            Status = StatusFor(AgasMcpRuntimeState.Stopping);
            IHost host = _host;
            _host = null;
            try
            {
                await host.StopAsync(cancellationToken);
            }
            finally
            {
                await DisposeHostAsync(host);
                if (_toolExecutor != null)
                {
                    await _toolExecutor.RejectNewWorkAndCancelActiveAndPendingAsync().ConfigureAwait(false);
                }

                Status = StatusFor(AgasMcpRuntimeState.Stopped);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            IHost? host = _host;
            _host = null;
            if (_toolExecutor != null)
            {
                await _toolExecutor.RejectNewWorkAndCancelActiveAndPendingAsync().ConfigureAwait(false);
            }
            if (_aiJobScheduler != null)
            {
                await _aiJobScheduler.StopAsync().ConfigureAwait(false);
            }

            if (host != null)
            {
                await host.StopAsync();
                await DisposeHostAsync(host);
            }
            if (_toolExecutor != null)
            {
                await _toolExecutor.RejectNewWorkAndCancelActiveAndPendingAsync().ConfigureAwait(false);
            }

            _gate.Dispose();
            _disposed = true;
            Status = StatusFor(AgasMcpRuntimeState.Stopped);
        }
        finally
        {
            if (!_disposed)
            {
                _gate.Release();
            }
        }
    }

    private static async ValueTask DisposeHostAsync(IHost host)
    {
        if (host is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            host.Dispose();
        }
    }

    private static bool IsPortConflict(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is SocketException socketException
                && socketException.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                return true;
            }
        }

        return false;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private AgasMcpRuntimeStatus StatusFor(AgasMcpRuntimeState state, string? error = null) =>
        new(state, _hostFactory.Endpoint, error);
}
