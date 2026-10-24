using AppAgasCs.Mcp;
using AppAgasCs.Tests.Core;
using AppAgasCs.Features.Ai.Generation;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp;

[Collection(AgasMcpTestCollections.FixedPort)]
public sealed class AgasMcpSettingsAndLifecycleTests
{
    [Fact]
    public void Settings_ConstantsDeclareDisabledByDefaultAndFixedLoopbackEndpoint()
    {
        Assert.False(AgasMcpSettingsResource.DefaultEnabled);
        Assert.Equal("127.0.0.1", AgasMcpSettingsResource.FixedHost);
        Assert.Equal(54119, AgasMcpSettingsResource.FixedPort);
        Assert.Equal("/mcp", AgasMcpSettingsResource.FixedEndpointPath);
        Assert.Equal("http://127.0.0.1:54119/mcp", AgasMcpSettingsResource.FixedEndpoint);
        var defaultFactory = new AgasMcpHostFactory();
        Assert.Equal(AgasMcpSettingsResource.FixedPort, defaultFactory.Port);
        Assert.Equal(AgasMcpSettingsResource.FixedEndpoint, defaultFactory.Endpoint);
    }

    [Fact]
    public async Task ApplyEnabledAsyncFalse_DoesNotRequireGodotResourceOrStartHost()
    {
        var executor = new TrackingToolExecutor();
        var factory = AgasMcpHostFactory.CreateTestDynamicPort();
        await using var lifecycle = new AgasMcpLifecycleService(factory, tools: null, executor);
        var statuses = new List<AgasMcpRuntimeStatus>();
        lifecycle.StatusChanged += statuses.Add;

        Assert.Equal(AgasMcpRuntimeState.Disabled, lifecycle.Status.State);
        Assert.Equal(factory.Endpoint, lifecycle.Status.Endpoint);
        await lifecycle.ApplyEnabledAsync(false, TestTimeout());

        Assert.Equal(AgasMcpRuntimeState.Disabled, lifecycle.Status.State);
        Assert.Equal(factory.Endpoint, lifecycle.Status.Endpoint);
        Assert.Equal(
            new[]
            {
                (AgasMcpRuntimeState.Stopped, factory.Endpoint),
                (AgasMcpRuntimeState.Disabled, factory.Endpoint),
            },
            statuses.Select(status => (status.State, status.Endpoint)).ToArray());
        Assert.Equal(1, executor.RejectAndCancelCalls);
        Assert.Equal(0, executor.AllowCalls);
        Assert.Equal(0, executor.BeginRejectingCalls);
    }

    [Fact]
    public async Task Lifecycle_StartStopStartSerializesThroughGateAndReleasesIsolatedPort()
    {
        var executor = new TrackingToolExecutor();
        var factory = AgasMcpHostFactory.CreateTestDynamicPort();
        await using var lifecycle = new AgasMcpLifecycleService(factory, tools: null, executor);

        await lifecycle.StartAsync(TestTimeout());
        int firstPort = factory.Port;
        Assert.Equal(AgasMcpRuntimeState.Running, lifecycle.Status.State);
        Assert.Equal(factory.Endpoint, lifecycle.Status.Endpoint);
        Assert.True(await CanConnectToPortAsync(firstPort));
        Assert.Equal(1, executor.BeginRejectingCalls);
        Assert.Equal(1, executor.AllowCalls);

        await lifecycle.StopAsync(TestTimeout());
        Assert.Equal(AgasMcpRuntimeState.Stopped, lifecycle.Status.State);
        Assert.Equal(factory.Endpoint, lifecycle.Status.Endpoint);
        Assert.True(CanBindPort(firstPort));
        Assert.Equal(2, executor.RejectAndCancelCalls);

        Task startOne = lifecycle.StartAsync(TestTimeout());
        Task startTwo = lifecycle.StartAsync(TestTimeout());
        await Task.WhenAll(startOne, startTwo);

        int secondPort = factory.Port;
        Assert.Equal(AgasMcpRuntimeState.Running, lifecycle.Status.State);
        Assert.Equal(factory.Endpoint, lifecycle.Status.Endpoint);
        Assert.True(await CanConnectToPortAsync(secondPort));
        Assert.Equal(2, executor.BeginRejectingCalls);
        Assert.Equal(3, executor.AllowCalls);

        await lifecycle.StopAsync(TestTimeout());
        Assert.Equal(AgasMcpRuntimeState.Stopped, lifecycle.Status.State);
        Assert.Equal(factory.Endpoint, lifecycle.Status.Endpoint);
        Assert.True(CanBindPort(secondPort));
    }

    [Fact]
    public async Task Lifecycle_StopStart_ReenablesAiSchedulerSubmissions()
    {
        var executor = new TrackingToolExecutor();
        var coordinator = new AgasAiGenerationCoordinator();
        using var store = new AgasMcpAiJobStore();
        await using var scheduler = new AgasMcpAiJobScheduler(coordinator, store);
        await using var lifecycle = new AgasMcpLifecycleService(
            AgasMcpHostFactory.CreateTestDynamicPort(),
            tools: null,
            executor,
            scheduler);
        var scope = AgasMcpAiProjectScope.Create(Path.GetTempPath(), "project-reenable");

        await lifecycle.StartAsync(TestTimeout());
        await lifecycle.StopAsync(TestTimeout());
        AgasMcpAiJobSubmitResult stopped = scheduler.Submit(new AgasMcpAiJobSubmission(
            scope,
            "TextToImage",
            token => Task.FromResult(new AgasMcpAiJobRunResult(
                new AgasMcpAiJobResultMetadata("assets/generated/stopped.png", null, "unused")))));
        Assert.False(stopped.Accepted);
        Assert.Equal(AgasMcpAiJobSubmitRejectReason.Stopped, stopped.RejectReason);

        await lifecycle.StartAsync(TestTimeout());
        AgasMcpAiJobSubmitResult restarted = scheduler.Submit(new AgasMcpAiJobSubmission(
            scope,
            "TextToImage",
            token => Task.FromResult(new AgasMcpAiJobRunResult(
                new AgasMcpAiJobResultMetadata("assets/generated/restarted.png", null, "completed")))));
        Assert.True(restarted.Accepted);
        await WaitUntilAsync(() =>
            store.TryGet(scope, restarted.Job!.JobId, out AgasMcpAiJobSnapshot? current)
            && current!.State == AgasMcpAiJobState.Succeeded);

        await lifecycle.StopAsync(TestTimeout());
        Assert.Equal(0, scheduler.ActiveCount);
        Assert.Equal(0, coordinator.ActiveCount);
    }

    private static bool CanBindPort(int port)
    {
        TcpListener listener = new(IPAddress.Loopback, port);
        try
        {
            listener.Start();
            return true;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<bool> CanConnectToPortAsync(int port)
    {
        using var client = new TcpClient();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        return client.Connected;
    }

    private static CancellationToken TestTimeout() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
    }

    private sealed class TrackingToolExecutor : IAgasMcpToolExecutor
    {
        public int BeginRejectingCalls { get; private set; }
        public int AllowCalls { get; private set; }
        public int RejectAndCancelCalls { get; private set; }

        public Task<T> ExecuteAsync<T>(
            string? sessionId,
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            return operation(cancellationToken);
        }

        public Task ExecuteAsync(
            string? sessionId,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            return operation(cancellationToken);
        }

        public void BeginRejectingNewWork() => BeginRejectingCalls++;

        public void AllowNewWork() => AllowCalls++;

        public Task RejectNewWorkAndCancelActiveAndPendingAsync(CancellationToken cancellationToken = default)
        {
            RejectAndCancelCalls++;
            return Task.CompletedTask;
        }

        public void Stop()
        {
        }
    }
}
