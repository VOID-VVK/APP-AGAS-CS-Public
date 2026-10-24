using AppAgasCs.Tests.Core;
using Xunit;

namespace AppAgasCs.Core.Tests.Application;

public sealed class MainMcpRuntimeCompositionArchitectureTests
{
    [Fact]
    public void Main_DelegatesMcpObjectGraphToRuntimeComposition()
    {
        string main = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/UI/Main.cs"));
        string composition = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/Controllers/MainMcpRuntimeComposition.cs"));

        Assert.Contains("MainMcpRuntimeComposition", main, StringComparison.Ordinal);
        Assert.Contains("BeginApplicationExit", main, StringComparison.Ordinal);
        Assert.Contains("TrySaveForApplicationExit", main, StringComparison.Ordinal);
        Assert.Contains("ShutdownMcpThenQuitAsync", main, StringComparison.Ordinal);
        Assert.Contains("ShowProjectPicker", main, StringComparison.Ordinal);
        Assert.Contains("OpenWorkspace", main, StringComparison.Ordinal);
        Assert.DoesNotContain("new AgasMcpProjectFacade", main, StringComparison.Ordinal);
        Assert.DoesNotContain("new AgasMcpHostFactory", main, StringComparison.Ordinal);
        Assert.DoesNotContain("new AgasMcpLifecycleService", main, StringComparison.Ordinal);
        Assert.DoesNotContain("new AgasMcpSerialToolExecutor", main, StringComparison.Ordinal);
        Assert.DoesNotContain("new AgasMcpToolRuntime", main, StringComparison.Ordinal);
        Assert.DoesNotContain("AgasMcpToolCatalog.Create", main, StringComparison.Ordinal);
        Assert.DoesNotContain("private void InitializeMcp()", main, StringComparison.Ordinal);
        Assert.DoesNotContain("private async Task DisposeMcpAsync()", main, StringComparison.Ordinal);

        Assert.Contains("internal sealed class MainMcpRuntimeComposition", composition, StringComparison.Ordinal);
        Assert.Contains("new AgasMcpProjectFacade", composition, StringComparison.Ordinal);
        Assert.Contains("new AgasMcpHostFactory", composition, StringComparison.Ordinal);
        Assert.Contains("new AgasMcpLifecycleService", composition, StringComparison.Ordinal);
        Assert.Contains("new AgasMcpSerialToolExecutor", composition, StringComparison.Ordinal);
        Assert.Contains("new AgasMcpToolRuntime", composition, StringComparison.Ordinal);
        Assert.Contains("AgasMcpToolCatalog.Create", composition, StringComparison.Ordinal);
        Assert.Contains("RegisterSource", composition, StringComparison.Ordinal);
        Assert.Contains("ApplyEnabledAsync", composition, StringComparison.Ordinal);
        Assert.Contains("EnsureDisposedAsync", composition, StringComparison.Ordinal);
        Assert.Contains("ReleaseDispatcherOnMainThread", composition, StringComparison.Ordinal);
    }
}
