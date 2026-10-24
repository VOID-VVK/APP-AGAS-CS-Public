using AppAgasCs.Tests.Core;
using Xunit;

namespace AppAgasCs.Core.Tests.Application;

public sealed class MainWorkspaceDockChromeControllerArchitectureTests
{
    [Fact]
    public void MainWorkspaceDockChromeController_GuardsDeferredLayoutAfterWorkspaceRelease()
    {
        string workspace = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/MainWorkspace.cs"));
        string chrome = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/Controllers/MainWorkspaceDockChromeController.cs"));

        Assert.Contains("_dockChrome.NotifyWorkspaceReleased();", workspace, StringComparison.Ordinal);
        Assert.Contains("internal void NotifyWorkspaceReleased()", chrome, StringComparison.Ordinal);
        Assert.Contains("private bool TryGetWorkspaceWidth(out float workspaceWidth)", chrome, StringComparison.Ordinal);
        Assert.Contains("catch (ObjectDisposedException)", chrome, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(chrome, "_workspaceWidth()"));
    }

    [Fact]
    public void MainWorkspace_DelegatesDockChromeLayoutToController()
    {
        string workspace = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/MainWorkspace.cs"));
        string chrome = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/Controllers/MainWorkspaceDockChromeController.cs"));

        Assert.Contains("MainWorkspaceDockChromeController _dockChrome", workspace, StringComparison.Ordinal);
        Assert.Contains("=> _dockChrome.ApplyLayoutToUi();", workspace, StringComparison.Ordinal);
        Assert.Contains("=> _dockChrome.CaptureLayoutFromUi();", workspace, StringComparison.Ordinal);
        Assert.Contains("=> _dockChrome.QueueApplyLayoutOffsetsAfterLayout();", workspace, StringComparison.Ordinal);
        Assert.Contains("=> _dockChrome.OpenPromptDock();", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("private void SetLeftDockCollapsed(", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("private void SetRightDockCollapsed(", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("private void SetBottomDockCollapsed(", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("private void ToggleLeftPanel()", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("private void UpdateResponsiveLayout()", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("private static void UpdateDockHeader(", workspace, StringComparison.Ordinal);

        Assert.Contains("internal sealed class MainWorkspaceDockChromeController", chrome, StringComparison.Ordinal);
        Assert.Contains("_layoutController.CapturePanelSizes(", chrome, StringComparison.Ordinal);
        Assert.Contains("_layoutController.ApplyOuterSplitLayout(", chrome, StringComparison.Ordinal);
        Assert.Contains("_layoutController.ApplyCenterRightLayout(", chrome, StringComparison.Ordinal);
        Assert.Contains("_layoutController.ApplyResponsiveMinimums(", chrome, StringComparison.Ordinal);
        Assert.Contains("internal void SetLeftDockCollapsed(", chrome, StringComparison.Ordinal);
        Assert.Contains("internal void ToggleBottomDock()", chrome, StringComparison.Ordinal);
        Assert.Contains("internal static void UpdateDockHeader(", chrome, StringComparison.Ordinal);
        Assert.DoesNotContain("private static int ClampPaneSize", chrome, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
