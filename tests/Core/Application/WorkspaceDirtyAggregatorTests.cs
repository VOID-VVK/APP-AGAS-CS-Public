using AppAgasCs.Application;
using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class WorkspaceDirtyAggregatorTests
{
    [Fact]
    public void LocalDirtyAndContributors_AggregateWithoutCallersKnowingEachSource()
    {
        var dirty = new WorkspaceDirtyAggregator();
        bool mcpDirty = false;
        bool blueprintDirty = false;
        dirty.RegisterContributor("mcp", () => mcpDirty);
        dirty.RegisterContributor("blueprint", () => blueprintDirty);

        Assert.False(dirty.IsDirty);

        dirty.MarkLocalDirty();
        Assert.True(dirty.IsDirty);
        Assert.True(dirty.IsLocalDirty);

        dirty.ClearLocalDirty();
        Assert.False(dirty.IsDirty);

        mcpDirty = true;
        Assert.True(dirty.IsDirty);

        mcpDirty = false;
        blueprintDirty = true;
        Assert.True(dirty.IsDirty);

        dirty.UnregisterContributor("blueprint");
        Assert.False(dirty.IsDirty);
    }

    [Fact]
    public void RegisterContributor_ReplacesSameName()
    {
        var dirty = new WorkspaceDirtyAggregator();
        dirty.RegisterContributor("mcp", () => false);
        dirty.RegisterContributor("mcp", () => true);
        Assert.True(dirty.IsDirty);
    }
}
