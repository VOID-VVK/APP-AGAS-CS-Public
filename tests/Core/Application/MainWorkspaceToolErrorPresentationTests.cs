using AppAgasCs.Tests.Core;
using Xunit;

namespace AppAgasCs.Core.Tests.Application;

public sealed class MainWorkspaceToolErrorPresentationTests
{
    [Fact]
    public void ToolMenuAndRunErrors_LogFullExceptionsButSanitizeStatusText()
    {
        string source = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/UI/MainWorkspace.cs"));

        Assert.Contains("GD.PushWarning($\"Tool menu list failed: {exception}\")", source, StringComparison.Ordinal);
        Assert.Contains("GD.PushWarning($\"Tool run failed: {exception}\")", source, StringComparison.Ordinal);
        Assert.Contains("SetStatusTemplate(\"工具列表读取失败：{0}\", true, ToolboxPanel.FormatErrorMessageForUi(exception.Message))", source,
            StringComparison.Ordinal);
        Assert.Contains("SetStatusTemplate(\"工具执行失败：{0}\", true, ToolboxPanel.FormatErrorMessageForUi(exception.Message))", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("工具列表读取失败：{exception.Message}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("工具执行失败：{exception.Message}", source, StringComparison.Ordinal);
    }
}
