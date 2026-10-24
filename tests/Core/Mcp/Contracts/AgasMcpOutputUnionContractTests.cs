using System.Text.Json;
using System.Text.Json.Serialization;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Contracts.Output;
using AppAgasCs.Mcp.Contracts.Runtime;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp.Contracts;

public sealed class AgasMcpOutputUnionContractTests
{
    [Fact]
    public void BuilderAndRuntime_AcceptExactTopLevelAndNestedUnionVariants()
    {
        AgasMcpOutputContract direct = AgasMcpOutputSchemaBuilder.Build([typeof(IResult)]);
        AgasMcpOutputContract list = AgasMcpOutputSchemaBuilder.Build([typeof(ResultList)]);
        var queued = new QueuedResult("Queued", "job-1");

        Assert.True(AgasMcpOutputContractRegistry.Validate(
            direct,
            AgasMcpToolResponse.Success(false, false, queued)).Success);
        Assert.True(AgasMcpOutputContractRegistry.Validate(
            list,
            AgasMcpToolResponse.Success(false, false, new ResultList([queued]))).Success);

        JsonElement directSchema = direct.OutputSchema.GetProperty("oneOf")[0]
            .GetProperty("properties").GetProperty("data");
        JsonElement nestedSchema = list.OutputSchema.GetProperty("oneOf")[0]
            .GetProperty("properties").GetProperty("data")
            .GetProperty("properties").GetProperty("items").GetProperty("items");
        Assert.Equal(2, directSchema.GetProperty("oneOf").GetArrayLength());
        Assert.Equal(2, nestedSchema.GetProperty("oneOf").GetArrayLength());

        JsonElement serialized = AgasMcpOutputContractRegistry.SerializeToElement(
            AgasMcpToolResponse.Success(false, false, new ResultList([queued])));
        Assert.Equal("Queued", serialized.GetProperty("data").GetProperty("items")[0].GetProperty("state").GetString());
        Assert.Equal("job-1", serialized.GetProperty("data").GetProperty("items")[0].GetProperty("jobId").GetString());
    }

    [Fact]
    public void Runtime_RejectsUnregisteredDerivedUnionVariant()
    {
        AgasMcpOutputContract contract = AgasMcpOutputSchemaBuilder.Build([typeof(IResult)]);
        AgasMcpOutputValidationResult validation = AgasMcpOutputContractRegistry.Validate(
            contract,
            AgasMcpToolResponse.Success(false, false, new UnknownResult("Unknown")));

        Assert.False(validation.Success);
        Assert.Equal("data-variant", validation.ContractFailure);
    }

    [JsonPolymorphic]
    [JsonDerivedType(typeof(QueuedResult))]
    [JsonDerivedType(typeof(FailedResult))]
    [AgasMcpOutputUnion(typeof(QueuedResult), typeof(FailedResult))]
    private interface IResult;

    [AgasMcpOutputObject]
    private sealed record QueuedResult(
        [property: AgasMcpOutputConst("Queued")] string State,
        string JobId) : IResult;

    [AgasMcpOutputObject]
    private sealed record FailedResult(
        [property: AgasMcpOutputConst("Failed")] string State,
        string Message) : IResult;

    [AgasMcpOutputObject]
    private sealed record ResultList(IReadOnlyList<IResult> Items);

    [AgasMcpOutputObject]
    private sealed record UnknownResult(string State) : IResult;
}
