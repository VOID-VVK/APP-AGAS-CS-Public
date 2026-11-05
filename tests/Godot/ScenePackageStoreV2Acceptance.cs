#nullable enable

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Application;
using AppAgasCs.Features.Tools.Storage;
using AppAgasCs.Features.Tools.Execution;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class ScenePackageStoreV2Acceptance : Node
{
    private const string Prefix = "[AGAS][SCENE-PACKAGE-V2]";
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    public override void _Ready()
    {
        int exitCode = 0;
        try
        {
            VerifyImageRoundTripAndTamperRejection();
            GD.Print($"{Prefix}[PASS]");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {exception}");
        }
        finally
        {
            GetTree().Quit(exitCode);
        }
    }

    private static void VerifyImageRoundTripAndTamperRejection()
    {
        string root = Path.Combine(Path.GetTempPath(), "agas-scene-store-" + Guid.NewGuid().ToString("N"));
        string sourceProject = Path.Combine(root, "source");
        string targetProject = Path.Combine(root, "target");
        try
        {
            Directory.CreateDirectory(Path.Combine(sourceProject, "assets"));
            Directory.CreateDirectory(targetProject);
            File.WriteAllBytes(Path.Combine(sourceProject, "assets", "source.png"), Png);

            var scene = new AgasSceneResource
            {
                Id = "scene-one",
                Name = "Scene One",
                ActiveLayerId = "layer-one",
            };
            var layer = new AgasLayerResource
            {
                Id = "layer-one",
                Name = "Layer One",
                Visible = true,
                Shared = true,
            };
            layer.AddImage(new ImageInstanceResource
            {
                Id = "image-one",
                ImagePath = "assets/source.png",
                Order = 3,
                X = 12.5,
                Y = -4.25,
                ScaleX = 1.5,
                ScaleY = -0.75,
                RotationDegrees = 33,
                Opacity = 0.6,
            });
            scene.AddLayer(layer);

            const string customSource = "extends RefCounted\nfunc process(image: Image) -> Dictionary:\n\tvar result: Image = image.duplicate()\n\tresult.resize(17, 19, Image.INTERPOLATE_NEAREST)\n\treturn {\"image\": result}\n";
            var sourceTools = new ToolCatalog(Path.Combine(root, "source-home"));
            sourceTools.CreateTool("custom-size", "Custom size", customSource);
            sourceTools.CreateTool("unused-custom", "Unused custom", customSource);
            sourceTools.CreateChain("custom-chain", "Custom chain", ["resize-256", "custom-size"]);
            var targetTools = new ToolCatalog(Path.Combine(root, "target-home"));
            targetTools.CreateTool("custom-size", "Existing tool", customSource + "# existing\n");
            var store = new ScenePackageStore(sourceTools);
            ScenePackageExportResult exported = store.Export(sourceProject, scene, Path.Combine(root, "downloads"));
            Require(exported.Success && exported.PackagePath != null, exported.Message);
            string archive = exported.PackagePath!;
            string extracted = Path.Combine(root, "extracted");
            ZipFile.ExtractToDirectory(archive, extracted);
            ScenePackageDocument manifest = ScenePackageCodec.Deserialize(File.ReadAllText(
                AgasPaths.ScenePackageManifestPath(extracted)));
            ScenePackageCodec.ValidateForTransport(manifest);
            Require(manifest.Integrity?.Count == 1
                && manifest.Integrity[0].Type == ScenePackageFileType.CanvasImage,
                "Export did not include the canvas image integrity entry.");
            Require(manifest.Toolbox?.Tools.Single(tool => tool.Id == "custom-size").Source == customSource
                && manifest.Toolbox.Tools.Any(tool => tool.Id == "unused-custom")
                && manifest.Toolbox.Chains.Single().ToolIds.SequenceEqual(new[] { "resize-256", "custom-size" }),
                "Export did not include the complete tool catalog and custom source.");
            var capture = new AgasScenePackageStagingService().CaptureExport(scene, "pkg-tool-fixture", sourceTools);
            Require(capture.Success, "MCP export capture failed.");
            ScenePackageDocument captured = ScenePackageCodec.Deserialize(System.Text.Encoding.UTF8.GetString(capture.ManifestUtf8.Span));
            Require(captured.Toolbox?.Tools.Single(tool => tool.Id == "custom-size").Source == customSource,
                "MCP export did not include the custom source.");

            var targetStore = new ScenePackageStore(targetTools);
            ScenePackageImportResult imported = targetStore.Import(targetProject, archive);
            Require(imported.Success && imported.Scene != null, imported.Message);
            ImageInstanceResource importedImage = imported.Scene!.LayersSnapshot
                .Single().ImagesSnapshot.Single();
            Require(imported.Scene.Id != scene.Id && importedImage.Id != "image-one",
                "Import did not remap the scene and image IDs.");
            Require(importedImage.X == 12.5 && importedImage.Y == -4.25
                && importedImage.ScaleX == 1.5 && importedImage.ScaleY == -0.75
                && importedImage.RotationDegrees == 33 && importedImage.Opacity == 0.6,
                "Import changed the canvas image transform.");
            Require(File.Exists(Path.Combine(targetProject, importedImage.ImagePath)),
                "Imported image was not committed to the target project.");
            Require(File.Exists(AgasPaths.SceneFile(targetProject, imported.Scene.Id)),
                "Imported scene was not saved before completing the import.");
            var chain = targetTools.LoadChain("custom-chain");
            Require(chain.ToolIds[1] != "custom-size" && targetTools.LoadTool(chain.ToolIds[1]).Source == customSource,
                "Import did not remap a conflicting tool while preserving custom source.");
            ImageToolRunResult run = new ImageToolRunner().RunChain(
                targetProject, importedImage.ImagePath, chain, targetTools, "assets/restored-tool-output.png");
            Require(run.Success && run.Width == 17 && run.Height == 19,
                "Imported custom tool chain did not execute: " + run.Message);

            string imagePath = Path.Combine(extracted, "images", "layer-one", "image-one.png");
            byte[] bytes = File.ReadAllBytes(imagePath);
            bytes[^2] ^= 1;
            File.WriteAllBytes(imagePath, bytes);
            string tamperedArchive = Path.Combine(root, "tampered.zip");
            ZipFile.CreateFromDirectory(extracted, tamperedArchive);
            ScenePackageImportResult tampered = targetStore.Import(targetProject, tamperedArchive);
            Require(!tampered.Success, "A tampered image was imported.");

            VerifyImportFailureCleanup(root, archive, targetTools);
            VerifyImportSymlinkRejection(root, archive, targetTools);
            VerifyMcpActiveLayer(root, manifest);

            byte[] originalArchive = File.ReadAllBytes(archive);
            File.WriteAllText(Path.Combine(sourceProject, "assets", "source.png"), "invalid PNG");
            ScenePackageExportResult damaged = store.Export(sourceProject, scene, Path.Combine(root, "downloads"));
            Require(!damaged.Success && File.ReadAllBytes(archive).SequenceEqual(originalArchive),
                "Invalid source image export must fail while preserving the previous archive.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void VerifyImportFailureCleanup(string root, string archive, ToolCatalog tools)
    {
        byte[] existingCatalog = File.ReadAllBytes(tools.DocumentPath);
        string blockedSceneProject = Path.Combine(root, "blocked-scene-project");
        Directory.CreateDirectory(Path.Combine(blockedSceneProject, ".agas"));
        string blockedSceneDirectory = AgasPaths.ScenesDirectory(blockedSceneProject);
        File.WriteAllText(blockedSceneDirectory, "existing scene directory sentinel");
        var store = new ScenePackageStore(tools);
        ScenePackageImportResult sceneFailure = store.Import(blockedSceneProject, archive);
        Require(!sceneFailure.Success
            && File.ReadAllText(blockedSceneDirectory) == "existing scene directory sentinel"
            && File.ReadAllBytes(tools.DocumentPath).SequenceEqual(existingCatalog)
            && !Directory.EnumerateFiles(blockedSceneProject, "*.png", SearchOption.AllDirectories).Any(),
            "Scene-save failure left imported images/tools or changed existing files.");

        string blockedToolProject = Path.Combine(root, "blocked-tool-project");
        Directory.CreateDirectory(AgasPaths.ScenesDirectory(blockedToolProject));
        string oldScene = AgasPaths.SceneFile(blockedToolProject, "existing");
        File.WriteAllText(oldScene, "existing scene sentinel");
        var blockedTools = new ToolCatalog(Path.Combine(root, "blocked-tool-home"));
        Directory.CreateDirectory(blockedTools.DocumentPath);
        ScenePackageImportResult toolFailure = new ScenePackageStore(blockedTools).Import(blockedToolProject, archive);
        Require(!toolFailure.Success
            && Directory.GetFiles(AgasPaths.ScenesDirectory(blockedToolProject)).SequenceEqual(new[] { oldScene })
            && File.ReadAllText(oldScene) == "existing scene sentinel"
            && !Directory.EnumerateFiles(blockedToolProject, "*.png", SearchOption.AllDirectories).Any()
            && Directory.Exists(blockedTools.DocumentPath),
            "Tool-save failure left new scene/images or changed existing files.");
    }

    private static void VerifyImportSymlinkRejection(string root, string archive, ToolCatalog tools)
    {
        if (OperatingSystem.IsWindows()) return;
        byte[] originalCatalog = File.ReadAllBytes(tools.DocumentPath);
        foreach (string linkedDirectory in new[] { "assets", ".agas" })
        {
            string project = Path.Combine(root, "linked-project-" + linkedDirectory.TrimStart('.'));
            string outside = Path.Combine(root, "outside-" + linkedDirectory.TrimStart('.'));
            Directory.CreateDirectory(project);
            Directory.CreateDirectory(outside);
            Directory.CreateSymbolicLink(Path.Combine(project, linkedDirectory), outside);
            ScenePackageImportResult result = new ScenePackageStore(tools).Import(project, archive);
            Require(!result.Success && !Directory.EnumerateFileSystemEntries(outside).Any()
                && File.ReadAllBytes(tools.DocumentPath).SequenceEqual(originalCatalog),
                "Import followed a target directory symlink outside the project.");
        }
    }

    private static void VerifyMcpActiveLayer(string root, ScenePackageDocument document)
    {
        ScenePackageDocument multipleLayers = document with
        {
            ActiveLayerId = "layer-second",
            Layers = document.Layers.Concat(new[]
            {
                document.Layers.Single() with
                {
                    Id = "layer-second", Name = "Second", Order = 1,
                    Images = Array.Empty<ScenePackageImage>(),
                    ReferenceImages = Array.Empty<ScenePackageReferenceImage>(),
                },
            }).ToArray(),
        };
        var prepared = new AgasScenePackagePreparedImport(true, string.Empty, "op-active-layer", "scene-restored",
            ScenePackageCodec.Serialize(multipleLayers),
            new[] { new AgasScenePackageAssetMapping(document.Layers.Single().Images!.Single().FilePath, "assets/restored.png") });
        AgasScenePackagePreparedSceneResult built = new AgasScenePackageStagingService().BuildSceneFromPreparedImport(prepared);
        Require(built.Success && built.Scene?.ActiveLayerId == "layer-second",
            "MCP import must preserve the exported active layer.");
        foreach (string? active in new string?[] { null, "", " " })
        {
            var fallback = new AgasScenePackagePreparedImport(true, string.Empty, "op-active-layer", "scene-restored",
                ScenePackageCodec.Serialize(multipleLayers with { ActiveLayerId = active }), prepared.AssetMappings);
            AgasScenePackagePreparedSceneResult result = new AgasScenePackageStagingService().BuildSceneFromPreparedImport(fallback);
            Require(result.Success && result.Scene?.ActiveLayerId == document.Layers.Single().Id,
                "MCP import must fall back to the first layer when the active layer is blank.");
        }
        var caseFolded = new AgasScenePackagePreparedImport(true, string.Empty, "op-active-layer", "scene-restored",
            ScenePackageCodec.Serialize(multipleLayers with { ActiveLayerId = "LAYER-SECOND" }), prepared.AssetMappings);
        Require(new AgasScenePackageStagingService().BuildSceneFromPreparedImport(caseFolded).Scene?.ActiveLayerId == "layer-second",
            "MCP import must restore the actual layer ID after a case-insensitive match.");
        ScenePackageDocument legacy = multipleLayers with
        {
            SchemaVersion = ScenePackageCodec.LegacySchemaVersion, ActiveLayerId = "missing", Toolbox = null, Integrity = null,
            Layers = multipleLayers.Layers.Select(layer => layer with { Images = Array.Empty<ScenePackageImage>() }).ToArray(),
        };
        var legacyPrepared = new AgasScenePackagePreparedImport(true, string.Empty, "op-active-layer", "scene-restored",
            ScenePackageCodec.Serialize(legacy), prepared.AssetMappings);
        Require(new AgasScenePackageStagingService().BuildSceneFromPreparedImport(legacyPrepared).Scene?.ActiveLayerId == document.Layers.Single().Id,
            "Legacy MCP import must fall back when its active layer is missing.");
        ScenePackageDocument legacyCasing = legacy with
        {
            ActiveLayerId = "a",
            Layers = new[] { legacy.Layers[0] with { Id = "A" }, legacy.Layers[1] with { Id = "a" } },
        };
        var legacyCasingPrepared = new AgasScenePackagePreparedImport(true, string.Empty, "op-active-layer", "scene-restored",
            ScenePackageCodec.Serialize(legacyCasing), prepared.AssetMappings);
        Require(new AgasScenePackageStagingService().BuildSceneFromPreparedImport(legacyCasingPrepared).Scene?.ActiveLayerId == "a",
            "Legacy MCP import must prefer an exact active layer match over a case-insensitive match.");
        string legacyPackage = Path.Combine(root, "legacy-casing-package");
        string legacyTarget = Path.Combine(root, "legacy-casing-target");
        Directory.CreateDirectory(legacyPackage);
        Directory.CreateDirectory(legacyTarget);
        File.WriteAllText(AgasPaths.ScenePackageManifestPath(legacyPackage), ScenePackageCodec.Serialize(legacyCasing));
        var legacyStore = new ScenePackageStore(new ToolCatalog(Path.Combine(root, "legacy-home")));
        ScenePackageImportResult importedLegacy = legacyStore.Import(legacyTarget, legacyPackage);
        Require(importedLegacy.Success && importedLegacy.Scene?.Layers.Single(layer => layer.Id == importedLegacy.Scene.ActiveLayerId).Name == "Second",
            "Manual legacy import must retain layers with different ID casing and activate the exact matching layer.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
