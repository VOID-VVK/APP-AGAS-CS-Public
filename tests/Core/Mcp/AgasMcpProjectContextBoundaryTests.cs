using System.Reflection;
using AppAgasCs.Mcp;
using Xunit;

namespace AppAgasCs.Core.Tests.Mcp;

public sealed class AgasMcpProjectContextBoundaryTests
{
    [Fact]
    public void Context_DoesNotExposeRepositoryCacheOrUiCallbacksAsPublicApi()
    {
        Type context = typeof(AgasMcpProjectContext);

        Assert.Empty(context.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(context.GetProperty("Repository", BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(context.GetProperty("SceneCache", BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(context.GetProperty("UiRefresh", BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(context.GetProperty("CurrentScene", BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(context.GetProperty("Project", BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(context.GetProperty("Layout", BindingFlags.Public | BindingFlags.Instance));
        Assert.False(typeof(AgasMcpUiRefreshScope).IsPublic);
    }
}
