using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Runtime;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp;

public sealed class AgasMcpHandlerRegistryTests
{
    [Fact]
    public void ThirtyOneFakeDescriptorsBindExactlyOnceInDescriptorOrder()
    {
        string[] names = Enumerable.Range(1, 31).Select(index => $"agas.fake.{index:D2}").ToArray();
        var registry = new AgasMcpPerToolHandlerRegistry(names);

        foreach (string name in names)
            registry.Register(name, SuccessHandler);

        IReadOnlyDictionary<string, AgasMcpPerToolHandler> handlers = registry.FreezeAndValidateComplete();

        Assert.Equal(names, registry.DescriptorNames);
        Assert.Equal(31, handlers.Count);
        Assert.Equal(names, handlers.Keys);
    }

    [Fact]
    public void DuplicateDescriptorIsRejected()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            new AgasMcpPerToolHandlerRegistry(new[] { "agas.fake", "agas.fake" }));

        Assert.Contains("Duplicate MCP descriptor", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownAndDuplicateHandlersAreRejected()
    {
        var registry = new AgasMcpPerToolHandlerRegistry(new[] { "agas.fake" });

        Assert.Throws<KeyNotFoundException>(() => registry.Register("agas.unknown", SuccessHandler));
        registry.Register("agas.fake", SuccessHandler);
        Assert.Throws<InvalidOperationException>(() => registry.Register("agas.fake", SuccessHandler));
        Assert.Throws<KeyNotFoundException>(() => registry.GetRequired("agas.unknown"));
    }

    [Fact]
    public void MissingHandlerPreventsFreezeAndLateRegistrationIsRejected()
    {
        var registry = new AgasMcpPerToolHandlerRegistry(new[] { "agas.one", "agas.two" });
        registry.Register("agas.one", SuccessHandler);

        InvalidOperationException missing = Assert.Throws<InvalidOperationException>(
            registry.FreezeAndValidateComplete);
        Assert.Contains("agas.two", missing.Message, StringComparison.Ordinal);

        registry.Register("agas.two", SuccessHandler);
        registry.FreezeAndValidateComplete();

        Assert.Throws<InvalidOperationException>(() => registry.Register("agas.one", SuccessHandler));
    }

    [Fact]
    public async Task RequiredHandlerReceivesSharedExecutionContext()
    {
        var registry = new AgasMcpPerToolHandlerRegistry(new[] { "agas.fake" });
        AgasMcpToolExecutionContext? observed = null;
        registry.Register("agas.fake", (_, context, _) =>
        {
            observed = context;
            return ValueTask.FromResult(AgasMcpToolResponse.Success(false, false, new { value = 1 }));
        });
        var execution = new AgasMcpCommitExecution(AgasMcpCommitPolicy.StartedMainThreadCompletion);
        var context = new AgasMcpToolExecutionContext("agas.fake", execution);

        AgasMcpToolResponse response = await registry.GetRequired("agas.fake")(
            null,
            context,
            CancellationToken.None);

        Assert.True(response.Ok);
        Assert.Same(context, observed);
        Assert.Same(execution, observed?.CommitExecution);
    }

    [Fact]
    public void ProjectCloseBindingFailurePreservesTypedReceipt()
    {
        string source = File.ReadAllText(Path.Combine(
            ProjectRoot.Path,
            "src/Mcp/AgasMcpDescriptorHandlerRegistry.cs"));
        int start = source.IndexOf("Register(registry, AgasMcpToolNames.ProjectClose", StringComparison.Ordinal);
        int end = source.IndexOf("RegisterMain<AgasProjectSnapshotInput>", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "project.close handler block was not found.");
        string closeHandler = source[start..end];

        Assert.Contains("AgasMcpProjectCloseDataDto receipt = lifecycle.LastCloseReceipt(tracker);", closeHandler, StringComparison.Ordinal);
        Assert.Contains("data: receipt", closeHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("BindAsync<AgasProjectCloseInput>", closeHandler, StringComparison.Ordinal);
    }

    private static ValueTask<AgasMcpToolResponse> SuccessHandler(
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? arguments,
        AgasMcpToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        _ = arguments;
        _ = context;
        _ = cancellationToken;
        return ValueTask.FromResult(AgasMcpToolResponse.Success(false, false, new { value = 1 }));
    }
}
