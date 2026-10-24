using System.Text.Json;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Runtime;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp;

public sealed class AgasMcpWave23RuntimeAssertionTests
{
    [Fact]
    public async Task AsyncOrchestratedRuntime_AcceptsTypedSerializedOutput_AndFailsClosedWrongDto()
    {
        await using var executor = new AgasMcpSerialToolExecutor(TimeSpan.FromSeconds(2));
        executor.AllowNewWork();

        AgasMcpToolResponse typed = AgasMcpToolResponse.Success(
            true,
            false,
            new AgasMcpAiProfilesDataDto(Array.Empty<AgasMcpAiProfileRowDto>(), 0));
        AgasMcpToolResponse accepted = await Execute(executor, typed);
        Assert.True(accepted.Ok);
        JsonElement wire = AppAgasCs.Mcp.Contracts.Runtime.AgasMcpOutputContractRegistry.SerializeToElement(accepted);
        Assert.Equal(0, wire.GetProperty("data").GetProperty("providerConfigurationErrorCount").GetInt32());
        Assert.Equal(JsonValueKind.Array, wire.GetProperty("data").GetProperty("profiles").ValueKind);

        AgasMcpToolResponse rejected = await Execute(
            executor,
            AgasMcpToolResponse.Success(true, false, new { profiles = Array.Empty<object>() }));
        Assert.False(rejected.Ok);
        Assert.Equal(AgasMcpErrorCodes.InternalError, rejected.Error?.Code);
        Assert.Null(rejected.Data);
    }

    private static async Task<AgasMcpToolResponse> Execute(
        IAgasMcpToolExecutor executor,
        AgasMcpToolResponse response)
    {
        var registry = new AgasMcpPerToolHandlerRegistry(AgasMcpToolNames.All);
        foreach (string name in AgasMcpToolNames.All)
        {
            AgasMcpToolResponse current = name == AgasMcpToolNames.AiProfilesList
                ? response
                : AgasMcpToolResponse.Failure(false, false, AgasMcpErrorCodes.Stopped, "stopped", "retry");
            registry.Register(name, (_, _, _) => new ValueTask<AgasMcpToolResponse>(current));
        }
        var runtime = new AgasMcpToolRuntime(executor, null!, registry.FreezeAndValidateComplete());
        return await runtime.ExecuteToolAsync(
            "session",
            AgasMcpToolNames.AiProfilesList,
            new Dictionary<string, JsonElement>(),
            CancellationToken.None);
    }
}
