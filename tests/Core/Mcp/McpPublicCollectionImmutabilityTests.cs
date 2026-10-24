using System.Collections;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Contracts.Metadata;
using Xunit;

namespace AppAgasCs.Core.Tests.Mcp;

public sealed class McpPublicCollectionImmutabilityTests
{
    [Fact]
    public void ToolResponse_DefensivelyCopiesWarnings()
    {
        var warnings = new List<string> { "before" };
        AgasMcpToolResponse response = AgasMcpToolResponse.Success(false, true, new { }, warnings);

        warnings[0] = "after";

        Assert.Equal("before", Assert.Single(response.Warnings));
        AssertReadOnly(response.Warnings);
    }

    [Fact]
    public void ContractMetadataAttributes_DefensivelyCopyParamsArrays()
    {
        string[] allowed = { "one" };
        string[] required = { "required" };
        string[] candidates = { "candidate" };
        var items = new McpItemsAllowedValuesAttribute(allowed);
        var whenRequired = new McpWhenEqualsRequiredAttribute("kind", "value", required);
        var whenAny = new McpWhenEqualsAnyRequiredAttribute("kind", "value", candidates);

        allowed[0] = "mutated";
        required[0] = "mutated";
        candidates[0] = "mutated";

        Assert.Equal("one", Assert.Single(items.Values));
        Assert.Equal("required", Assert.Single(whenRequired.RequiredProperties));
        Assert.Equal("candidate", Assert.Single(whenAny.CandidateProperties));
        AssertReadOnly(items.Values);
        AssertReadOnly(whenRequired.RequiredProperties);
        AssertReadOnly(whenAny.CandidateProperties);
    }

    private static void AssertReadOnly<T>(IReadOnlyList<T> values)
    {
        IList list = Assert.IsAssignableFrom<IList>(values);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Clear());
    }
}
