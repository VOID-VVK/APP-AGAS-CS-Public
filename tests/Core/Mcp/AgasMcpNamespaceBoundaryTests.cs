using AppAgasCs.Tests.Core;
using Xunit;

namespace AppAgasCs.Core.Tests.Mcp;

public sealed class AgasMcpNamespaceBoundaryTests
{
    [Fact]
    public void RootMcpNamespace_DoesNotDependOnToolsNamespace()
    {
        string mcpRoot = Path.Combine(ProjectRoot.Path, "src/Mcp");
        string rootSources = string.Join('\n', Directory
            .GetFiles(mcpRoot, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllText));
        string toolAdapter = File.ReadAllText(Path.Combine(
            mcpRoot, "Tools", "AgasMcpServerToolBase.cs"));

        Assert.DoesNotContain("using AppAgasCs.Mcp.Tools", rootSources, StringComparison.Ordinal);
        Assert.DoesNotContain("AppAgasCs.Mcp.Tools.", rootSources, StringComparison.Ordinal);
        Assert.Contains("namespace AppAgasCs.Mcp;", toolAdapter, StringComparison.Ordinal);
    }

    [Fact]
    public void DescriptorCatalog_HasNoLegacyConcreteToolImplementations()
    {
        string toolsRoot = Path.Combine(ProjectRoot.Path, "src/Mcp/Tools");
        string[] legacyFiles =
        {
            "AgasProjectTools.cs",
            "AgasAiTools.cs",
            "AgasBlueprintTools.cs",
            "AgasAssetsTools.cs",
            "AgasImageTools.cs",
            "AgasLayerTools.cs",
            "AgasPromptTools.cs",
            "AgasSceneTools.cs",
            "AgasSelectionTools.cs",
            "AgasViewTools.cs",
        };

        Assert.All(legacyFiles, file => Assert.False(File.Exists(Path.Combine(toolsRoot, file)), file));
        string catalog = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/Mcp/AgasMcpToolCatalog.cs"));
        Assert.Contains("new AgasDescriptorServerTool(runtime, descriptor)", catalog, StringComparison.Ordinal);
    }
}
