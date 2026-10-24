using AppAgasCs.Tests.Core;
using Xunit;

namespace AppAgasCs.Core.Tests.Application;

public sealed class MainWorkspaceShellBuilderArchitectureTests
{
    [Fact]
    public void MainWorkspace_DelegatesShellUiConstructionToBuilder()
    {
        string workspace = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/MainWorkspace.cs"));
        string builder = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/Controllers/MainWorkspaceShellBuilder.cs"));

        Assert.Contains("MainWorkspaceShellBuilder _shellBuilder", workspace, StringComparison.Ordinal);
        Assert.Contains("_shellBuilder.BuildUi(this)", workspace, StringComparison.Ordinal);
        Assert.Contains("ApplyShellUi(shell)", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("private Control CreateToolbar()", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("private MenuButton CreateViewMenu()", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("private static PanelContainer BuildDock(", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("private Control BuildLeftPanel()", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("private Control BuildCenterPanel()", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("private Control BuildBottomDock()", workspace, StringComparison.Ordinal);

        Assert.Contains("internal sealed class MainWorkspaceShellBuilder", builder, StringComparison.Ordinal);
        Assert.Contains("internal MainWorkspaceShellUi BuildUi(Control rootHost)", builder, StringComparison.Ordinal);
        Assert.Contains("CreateToolbar()", builder, StringComparison.Ordinal);
        Assert.Contains("CreateViewMenu()", builder, StringComparison.Ordinal);
        Assert.Contains("BuildDock(", builder, StringComparison.Ordinal);
        Assert.Contains("BuildLeftPanel()", builder, StringComparison.Ordinal);
        Assert.Contains("BuildCenterPanel()", builder, StringComparison.Ordinal);
        Assert.Contains("BuildBottomDock(", builder, StringComparison.Ordinal);
        Assert.Contains("internal sealed class MainWorkspaceShellHost", builder, StringComparison.Ordinal);
    }
}
