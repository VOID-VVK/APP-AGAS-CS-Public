using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Features.Tools.Model;
using AppAgasCs.Features.Tools.Storage;
using Xunit;

namespace AppAgasCs.Core.Tests.Tools;

public sealed class ToolCatalogSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agas-tool-snapshot-" + Guid.NewGuid().ToString("N"));
    private const string Source = "extends RefCounted\nfunc process(image: Image) -> Dictionary:\n\treturn {\"image\": image}\n";

    [Fact]
    public void SameContentWithDistinctOriginalIdsRemainsIndependentAcrossConflictsAndRepeatedImports()
    {
        string prefix = "tool-" + new string('x', 50);
        var source = new ToolCatalog(Path.Combine(_root, "source"));
        var target = new ToolCatalog(Path.Combine(_root, "target"));
        string[] ids = ["tool-a", "tool-b", prefix + "a", prefix + "b"];
        foreach (string id in ids)
        {
            source.CreateTool(id, "Same name", Source);
            target.CreateTool(id, "Local tool", Source + "# local\n");
        }
        source.CreateChain("chain-a", "Same chain", ["tool-a"]);
        source.CreateChain("chain-b", "Same chain", ["tool-a"]);
        target.CreateChain("chain-a", "Local chain", ["resize-256"]);
        target.CreateChain("chain-b", "Local chain", ["resize-256"]);

        ToolCatalogSnapshot snapshot = source.ExportSnapshot();
        IReadOnlyDictionary<string, string> imported = target.ImportSnapshot(snapshot);
        Assert.Equal(ids.Length, ids.Select(id => imported[id]).Distinct().Count());
        Assert.NotEqual(imported["chain-a"], imported["chain-b"]);
        IReadOnlyDictionary<string, string> repeated = target.ImportSnapshot(snapshot);
        Assert.All(imported, item => Assert.Equal(item.Value, repeated[item.Key]));
        target.UpdateTool(imported["tool-a"], 1, "Edited", Source + "# edited\n");
        Assert.Equal(Source, target.LoadTool(imported["tool-b"]).Source);
    }

    [Fact]
    public void ImportRemapsConflictingToolsAndChainsWithoutReplacingLocalCodeOrDuplicatingRepeatedImports()
    {
        var source = new ToolCatalog(Path.Combine(_root, "source"));
        source.CreateTool("custom", "Custom", Source);
        source.CreateTool("unused", "Unused", Source + "# unused\n");
        source.CreateChain("chain", "Portable chain", ["custom", "crop-128", "custom"]);
        var target = new ToolCatalog(Path.Combine(_root, "target"));
        string localSource = Source + "# local\n";
        target.CreateTool("custom", "Local", localSource);
        target.CreateChain("chain", "Local chain", ["resize-256"]);

        IReadOnlyDictionary<string, string> mapped = target.ImportSnapshot(source.ExportSnapshot());
        var reopened = new ToolCatalog(Path.Combine(_root, "target"));
        Assert.Equal(localSource, reopened.LoadTool("custom").Source);
        Assert.Equal(Source, reopened.LoadTool(mapped["custom"]).Source);
        Assert.Equal(new[] { mapped["custom"], "crop-128", mapped["custom"] }, reopened.LoadChain(mapped["chain"]).ToolIds);
        Assert.Equal(Source + "# unused\n", reopened.LoadTool("unused").Source);
        int tools = reopened.ListTools().Count;
        int chains = reopened.ListChains().Count;
        IReadOnlyDictionary<string, string> repeated = reopened.ImportSnapshot(source.ExportSnapshot());
        Assert.Equal(mapped["custom"], repeated["custom"]);
        Assert.Equal(mapped["chain"], repeated["chain"]);
        Assert.Equal(tools, reopened.ListTools().Count);
        Assert.Equal(chains, reopened.ListChains().Count);
    }

    [Fact]
    public void InvalidSnapshotCannotPartiallyWriteToolsBeforeMissingChainDependencyIsRejected()
    {
        var target = new ToolCatalog(Path.Combine(_root, "target"));
        target.ListTools();
        string previous = File.ReadAllText(target.DocumentPath);
        var snapshot = new ToolCatalogSnapshot(
            [new ToolDefinition { Id = "custom", Name = "Custom", Kind = ToolImplementationKind.Gdscript, Source = Source }],
            [new ToolChainDefinition { Id = "chain", Name = "Broken", ToolIds = ["missing"] }]);

        Assert.Throws<InvalidDataException>(() => target.ImportSnapshot(snapshot));
        Assert.Equal(previous, File.ReadAllText(target.DocumentPath));
    }

    [Fact]
    public void SceneCodecRoundTripsLargeLegalCatalogAndRetainsEveryScriptAndChainStep()
    {
        var source = new ToolCatalog(Path.Combine(_root, "source"));
        for (int index = 0; index < 3; index++)
            source.CreateTool($"large-{index}", $"Large {index}", Source + "#" + new string('x', 800_000));
        source.CreateChain("chain", "Large chain", ["large-2", "large-0", "large-1"]);
        ScenePackageDocument document = EmptyScene() with { Toolbox = source.ExportSnapshot() };

        string json = ScenePackageCodec.Serialize(document);
        Assert.True(Encoding.UTF8.GetByteCount(json) > 2 * 1024 * 1024);
        ScenePackageDocument decoded = ScenePackageCodec.Deserialize(json);
        ScenePackageCodec.ValidateForTransport(decoded);
        Assert.Equal(source.LoadTool("large-2").Source, decoded.Toolbox!.Tools.Single(tool => tool.Id == "large-2").Source);
        Assert.Equal(source.LoadChain("chain").ToolIds, Assert.Single(decoded.Toolbox.Chains).ToolIds);
        Assert.Equal(ScenePackageSafetyLimits.MaximumManifestBytes, ScenePackageCodec.MaximumManifestBytes);

        string packageDirectory = Path.Combine(_root, "package");
        Directory.CreateDirectory(packageDirectory);
        File.WriteAllText(Path.Combine(packageDirectory, "scene.json"), json);
        string archive = Path.Combine(_root, "large-scene.zip");
        ScenePackageArchive.Create(packageDirectory, archive);
        string extracted = ScenePackageArchive.ExtractToTemporaryDirectory(archive);
        try
        {
            ScenePackageDocument transported = ScenePackageCodec.Deserialize(File.ReadAllText(Path.Combine(extracted, "scene.json")));
            Assert.Equal(source.LoadTool("large-0").Source, transported.Toolbox!.Tools.Single(tool => tool.Id == "large-0").Source);
        }
        finally { Directory.Delete(extracted, recursive: true); }
    }

    [Fact]
    public void SnapshotOverCatalogByteBudgetIsRejectedEvenWithinManifestBudget()
    {
        ToolDefinition[] tools = Enumerable.Range(0, 10).Select(index => new ToolDefinition
        {
            Id = $"large-{index}", Name = $"Large {index}", Kind = ToolImplementationKind.Gdscript,
            Source = Source + "#" + new string('x', 900_000),
        }).ToArray();
        var snapshot = new ToolCatalogSnapshot(tools, []);

        Assert.Throws<InvalidDataException>(() => ScenePackageCodec.Serialize(EmptyScene() with { Toolbox = snapshot }));
    }

    [Fact]
    public void ExistingV2WithoutToolboxRemainsCompatibleAndLegacyCannotCarryToolbox()
    {
        ScenePackageDocument document = EmptyScene();
        string json = ScenePackageCodec.Serialize(document);
        Assert.DoesNotContain("toolbox", json, StringComparison.Ordinal);
        Assert.Null(ScenePackageCodec.Deserialize(json).Toolbox);
        Assert.Throws<InvalidDataException>(() => ScenePackageCodec.Serialize(document with
        {
            SchemaVersion = ScenePackageCodec.LegacySchemaVersion,
            Toolbox = new ToolCatalogSnapshot([], []),
        }));
    }

    private static ScenePackageDocument EmptyScene() => new(
        ScenePackageCodec.CurrentSchemaVersion, "scene-a", "Portable scene", "layer-a",
        [new ScenePackageLayer("layer-a", "Layer", 0, true, false, [], [])]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
