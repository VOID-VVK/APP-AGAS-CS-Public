using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

namespace AppAgasCs.Mcp;

public sealed class AgasMcpHostFactory
{
    public const long MaxRequestBodyBytes = 1024 * 1024;
    public const long MaxConcurrentConnections = 8;

    private readonly int _listenPort;

    public AgasMcpHostFactory()
        : this(AgasMcpSettingsResource.FixedPort, allowDynamicPort: false)
    {
    }

    /// <summary>
    /// Creates a loopback-only host factory on a caller-selected port. The parameterless
    /// production path remains pinned to <see cref="AgasMcpSettingsResource.FixedPort"/>;
    /// this overload lets isolated E2E fixtures coexist with a running AGAS instance.
    /// </summary>
    internal AgasMcpHostFactory(int port)
        : this(port, allowDynamicPort: false)
    {
    }

    private AgasMcpHostFactory(int port, bool allowDynamicPort)
    {
        if (port is < 0 or > 65535 || (port == 0 && !allowDynamicPort))
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        _listenPort = port;
        Port = port;
        Endpoint = $"http://{AgasMcpSettingsResource.FixedHost}:{port}{AgasMcpSettingsResource.FixedEndpointPath}";
    }

    public int Port { get; private set; }
    public string Endpoint { get; private set; }

    /// <summary>
    /// Test-only atomic port allocation. Kestrel owns the port-0 bind, so no
    /// probe-close-rebind race exists between the fixture and the real server.
    /// </summary>
    internal static AgasMcpHostFactory CreateTestDynamicPort() => new(0, allowDynamicPort: true);

    public IHost Create(IEnumerable<McpServerTool> tools, IAgasMcpToolExecutor? toolExecutor = null)
    {
        ArgumentNullException.ThrowIfNull(tools);

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ApplicationName = typeof(AgasMcpHostFactory).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, _listenPort);
            options.Limits.MaxConcurrentConnections = MaxConcurrentConnections;
            options.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
        });

        builder.Services.AddSingleton(new AgasMcpEndpointSecurity(_listenPort));
        if (toolExecutor != null)
        {
            builder.Services.AddSingleton(toolExecutor);
        }

        builder.Services
            .AddMcpServer()
            .WithHttpTransport(options => options.Stateless = false)
            .WithTools(tools);

        WebApplication app = builder.Build();
        app.UseMiddleware<AgasMcpEndpointSecurityMiddleware>();
        app.UseMiddleware<AgasMcpProtocolHeaderMiddleware>();
        if (toolExecutor != null)
        {
            app.UseMiddleware<AgasMcpSessionDeleteMiddleware>();
        }

        app.MapMcp(AgasMcpSettingsResource.FixedEndpointPath);

        return new EndpointObservingHost(app, UpdateBoundEndpoint);
    }

    private void UpdateBoundEndpoint(IServiceProvider services)
    {
        IServer server = services.GetRequiredService<IServer>();
        IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose its bound server addresses.");
        Uri[] bound = addresses.Addresses
            .Select(address => new Uri(address, UriKind.Absolute))
            .Where(uri => string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && IPAddress.TryParse(uri.Host, out IPAddress? ip)
                && ip.Equals(IPAddress.Loopback))
            .ToArray();
        if (bound.Length != 1 || bound[0].Port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"Expected exactly one IPv4 loopback MCP listener, actual: {string.Join(", ", addresses.Addresses)}");
        }

        Port = bound[0].Port;
        Endpoint = $"http://{AgasMcpSettingsResource.FixedHost}:{Port}{AgasMcpSettingsResource.FixedEndpointPath}";
    }

    private sealed class EndpointObservingHost : IHost, IAsyncDisposable
    {
        private readonly IHost _inner;
        private readonly Action<IServiceProvider> _onStarted;

        public EndpointObservingHost(IHost inner, Action<IServiceProvider> onStarted)
        {
            _inner = inner;
            _onStarted = onStarted;
        }

        public IServiceProvider Services => _inner.Services;

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await _inner.StartAsync(cancellationToken).ConfigureAwait(false);
            _onStarted(_inner.Services);
        }

        public Task StopAsync(CancellationToken cancellationToken = default) =>
            _inner.StopAsync(cancellationToken);

        public void Dispose() => _inner.Dispose();

        public ValueTask DisposeAsync() => _inner is IAsyncDisposable asyncDisposable
            ? asyncDisposable.DisposeAsync()
            : DisposeSynchronously();

        private ValueTask DisposeSynchronously()
        {
            _inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
