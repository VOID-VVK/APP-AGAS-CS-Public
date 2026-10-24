#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AppAgasCs.Features.Tools.Model;
using AppAgasCs.Features.Tools.Storage;
using Xunit;

namespace AppAgasCs.Core.Tests.Tools;

public sealed class ToolCatalogTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "agas-tool-catalog-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void EmptyLibrarySeedsEightSingleImageToolsWithoutLegacyDocuments()
    {
        var catalog = new ToolCatalog(_home);

        ToolDefinition[] tools = catalog.ListTools().ToArray();

        Assert.Equal(8, tools.Length);
        Assert.Equal(6, tools.Count(tool => tool.IsReadOnly && tool.Kind == ToolImplementationKind.BuiltIn));
        Assert.Equal(2, tools.Count(tool => !tool.IsReadOnly && tool.Kind == ToolImplementationKind.Gdscript));
        Assert.All(tools, tool =>
        {
            Assert.Equal("image", tool.InputPort);
            Assert.Equal("image", tool.OutputPort);
        });
        Assert.Empty(catalog.ListChains());
        Assert.True(File.Exists(catalog.DocumentPath));
        Assert.False(Directory.Exists(Path.Combine(catalog.RootDirectory, "drafts")));
        Assert.False(Directory.Exists(Path.Combine(catalog.RootDirectory, "published")));
        Assert.Empty(Directory.EnumerateFiles(catalog.RootDirectory, "*.tmp"));
    }

    [Fact]
    public void CustomToolAndChainPersistAcrossFreshCatalogInstances()
    {
        var first = new ToolCatalog(_home);
        ToolDefinition custom = first.CreateTool("my-filter", "自定义", Source);
        ToolChainDefinition chain = first.CreateChain("my-chain", "组合", ["tile47-standard-reverse", custom.Id, "tile47-isometric"]);

        var reopened = new ToolCatalog(_home);

        Assert.Equal(Source, reopened.LoadTool(custom.Id).Source);
        Assert.Equal(1, reopened.LoadTool(custom.Id).Revision);
        Assert.Equal(chain.ToolIds, reopened.LoadChain(chain.Id).ToolIds);
        Assert.Equal(9, reopened.ListTools().Count);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(reopened.DocumentPath));
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void UpdateRenameAndDeleteRequireCurrentRevision()
    {
        var catalog = new ToolCatalog(_home);
        ToolDefinition tool = catalog.CreateTool("editable", "初版", Source);
        ToolDefinition updated = catalog.UpdateTool(tool.Id, 1, "修改后", Source + "\n# changed");
        Assert.Equal(2, updated.Revision);
        Assert.Throws<InvalidOperationException>(() => catalog.UpdateTool(tool.Id, 1, "stale", Source));

        ToolDefinition renamed = catalog.RenameTool(tool.Id, 2, "重命名");
        Assert.Equal(3, renamed.Revision);
        Assert.Equal("重命名", new ToolCatalog(_home).LoadTool(tool.Id).Name);
        Assert.Throws<InvalidOperationException>(() => catalog.DeleteTool(tool.Id, 2));
        catalog.DeleteTool(tool.Id, 3);
        Assert.Throws<KeyNotFoundException>(() => catalog.LoadTool(tool.Id));
    }

    [Fact]
    public void ChainPreservesOrderAndCannotReferenceAnotherChain()
    {
        var catalog = new ToolCatalog(_home);
        ToolChainDefinition chain = catalog.CreateChain("chain-a", "首链", ["crop-128", "resize-256", "crop-128"]);
        Assert.Equal(new[] { "crop-128", "resize-256", "crop-128" }, chain.ToolIds);
        Assert.Throws<InvalidDataException>(() => catalog.CreateChain("chain-b", "嵌套", ["crop-128", "chain-a"]));

        ToolChainDefinition updated = catalog.UpdateChain("chain-a", 1, "倒序", ["resize-256", "crop-128"]);
        Assert.Equal(2, updated.Revision);
        Assert.Equal(new[] { "resize-256", "crop-128" }, new ToolCatalog(_home).LoadChain("chain-a").ToolIds);
        Assert.Throws<InvalidOperationException>(() => catalog.DeleteChain("chain-a", 1));
        catalog.DeleteChain("chain-a", 2);
        Assert.Empty(catalog.ListChains());
    }

    [Fact]
    public void ReferencedToolMustBeRemovedFromChainsBeforeDeletion()
    {
        var catalog = new ToolCatalog(_home);
        catalog.CreateTool("referenced", "过滤", Source);
        catalog.CreateChain("chain", "组合", ["referenced", "crop-128"]);

        Assert.Throws<InvalidOperationException>(() => catalog.DeleteTool("referenced", 1));
        catalog.DeleteChain("chain", 1);
        catalog.DeleteTool("referenced", 1);
    }

    [Fact]
    public void ShippedToolsStayReadOnlyWhileTile47PresetsAreEditable()
    {
        var catalog = new ToolCatalog(_home);
        ToolDefinition builtIn = catalog.LoadTool("resize-256");
        Assert.Throws<InvalidOperationException>(() => catalog.UpdateTool(builtIn.Id, 1, "changed", Source));
        Assert.Throws<InvalidOperationException>(() => catalog.RenameTool(builtIn.Id, 1, "changed"));
        Assert.Throws<InvalidOperationException>(() => catalog.DeleteTool(builtIn.Id, 1));

        foreach (string id in new[] { "tile47-isometric", "tile47-standard-reverse" })
        {
            ToolDefinition tile = catalog.LoadTool(id);
            Assert.False(tile.IsReadOnly);
            Assert.Equal(ToolImplementationKind.Gdscript, tile.Kind);
            Assert.Contains("func process(image", tile.Source);
            Assert.Equal(2, catalog.UpdateTool(id, tile.Revision, tile.Name, tile.Source!).Revision);
        }
    }

    [Fact]
    public void DeletedTile47CustomPresetDoesNotReappearAfterRestart()
    {
        var catalog = new ToolCatalog(_home);
        Assert.Equal(8, catalog.ListTools().Count);

        catalog.DeleteTool("tile47-standard-reverse", 1);

        var reopened = new ToolCatalog(_home);
        Assert.Equal(7, reopened.ListTools().Count);
        Assert.Throws<KeyNotFoundException>(() => reopened.LoadTool("tile47-standard-reverse"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".hidden")]
    [InlineData("UPPER")]
    public void UnsafeIdsAreRejectedBeforeAnyWrite(string id)
    {
        var catalog = new ToolCatalog(_home);
        Assert.Throws<ArgumentException>(() => catalog.CreateTool(id, "name", Source));
        Assert.False(Directory.Exists(catalog.RootDirectory));
    }

    [Fact]
    public void ExistingEmptyCatalogIsSeededAndMalformedCatalogIsRejected()
    {
        var catalog = new ToolCatalog(_home);
        Directory.CreateDirectory(catalog.RootDirectory);
        File.WriteAllText(catalog.DocumentPath, "{\"schemaVersion\":1,\"tools\":[],\"chains\":[]}");
        Assert.Equal(8, catalog.ListTools().Count);

        File.WriteAllText(catalog.DocumentPath, "{\"schemaVersion\":999,\"tools\":[],\"chains\":[]}");
        Assert.Throws<InvalidDataException>(() => catalog.ListTools());
    }

    [Fact]
    public void LegacyDraftsAreIgnoredRatherThanImported()
    {
        var catalog = new ToolCatalog(_home);
        string oldDraft = Path.Combine(catalog.RootDirectory, "drafts", "items", "old-tool");
        Directory.CreateDirectory(oldDraft);
        File.WriteAllText(Path.Combine(oldDraft, "tool.json"), "not-json");

        Assert.Equal(8, catalog.ListTools().Count);
        Assert.DoesNotContain(catalog.ListTools(), tool => tool.Id == "old-tool");
        Assert.True(File.Exists(Path.Combine(oldDraft, "tool.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private const string Source = "extends RefCounted\nfunc process(image: Image) -> Dictionary:\n\treturn {\"image\": image}\n";
}
