#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AGAS.UI;
using Agas.Features.Canvas;
using AppAgasCs.Application;
using AppAgasCs.Application.Contracts;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Prompting;
using AppAgasCs.Features.Tools.Execution;
using AppAgasCs.Features.Tools.Storage;
using Godot;

namespace AppAgasCs.Tests.Godot;

/// <summary>Exports the real selected scene and exercises its ZIP through the workspace import flow.</summary>
public partial class Tile47ScenePackagePublicationAcceptance : Node
{
    private string _artifacts = "";
    private string _target = "";

    public override async void _Ready()
    {
        int exit = 0;
        try { await Verify(); GD.Print("[AGAS][TILE47-PUBLICATION][PASS]"); }
        catch (Exception error) { exit = 1; GD.PushError(error.ToString()); }
        finally { GetTree().Quit(exit); }
    }

    private async Task Verify()
    {
        string sourceRoot = System.Environment.GetEnvironmentVariable("AGAS_PUBLICATION_SOURCE")
            ?? throw new InvalidOperationException("AGAS_PUBLICATION_SOURCE is required.");
        string sceneId = System.Environment.GetEnvironmentVariable("AGAS_PUBLICATION_SCENE_ID")
            ?? throw new InvalidOperationException("AGAS_PUBLICATION_SCENE_ID is required.");
        _artifacts = System.Environment.GetEnvironmentVariable("AGAS_PUBLICATION_ARTIFACTS")
            ?? throw new InvalidOperationException("AGAS_PUBLICATION_ARTIFACTS is required.");
        Directory.CreateDirectory(_artifacts);
        string sourcePath = AgasPaths.SceneFile(sourceRoot, sceneId);
        string sourceHash = Hash(File.ReadAllBytes(sourcePath));
        string projectHash = Hash(File.ReadAllBytes(AgasPaths.ProjectFile(sourceRoot)));
        AgasSceneResource original = ResourceLoader.Load<AgasSceneResource>(sourcePath,
            cacheMode: ResourceLoader.CacheMode.Ignore) ?? throw new Exception("Real source scene did not load.");
        AgasSceneResource published = (AgasSceneResource)original.Duplicate(true);
        Require(published.LayersSnapshot.Count == 3, "Expected exactly three scene layers.");
        AgasLayerResource[] sourceLayers = original.LayersSnapshot.OrderBy(layer => layer.Order).ToArray();
        Require(sourceLayers[0].AiStyleReference && sourceLayers[0].Locked && sourceLayers[1].Locked,
            "Source style/template roles and locks are incorrect.");
        Require(sourceLayers[2].PromptFragmentsSnapshot.Count == 7, "Source must contain seven prompt fragments.");
        string[] sourceImageHashes = sourceLayers.Select(layer => Hash(File.ReadAllBytes(
            Path.Combine(sourceRoot, layer.ImagesSnapshot.Single().ImagePath)))).ToArray();
        foreach (AgasLayerResource layer in published.LayersSnapshot) layer.Shared = true;

        // Seed from shipped definitions, then retain only this scene's two unmodified Tile47 presets.
        var sourceTools = new ToolCatalog(Path.Combine(_artifacts, "source-tools-home"));
        var definitions = sourceTools.ExportSnapshot().Tools.Where(tool => tool.Id.StartsWith("tile47-", StringComparison.Ordinal)).ToArray();
        Require(definitions.Length == 2, "Expected two shipped Tile47 presets.");
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        File.WriteAllText(sourceTools.DocumentPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, tools = definitions, chains = Array.Empty<object>(),
        }, jsonOptions));
        Require(sourceTools.LoadTool("tile47-standard-reverse").Source == ToolPresetSources.Tile47StandardReverse,
            "Reverse tool source changed.");
        ScenePackageExportResult exported = new ScenePackageStore(sourceTools).Export(sourceRoot, published, _artifacts);
        Require(exported.Success && exported.PackagePath != null, "Actual export failed: " + exported.Message);
        string packagePath = exported.PackagePath!;
        ScenePackageDocument manifest;
        using (ZipArchive archive = ZipFile.OpenRead(packagePath))
        {
            Require(archive.Entries.Count == 4, "Package must contain only scene.json and three image files.");
            using var reader = new StreamReader(archive.GetEntry(AgasPaths.ScenePackageManifestFileName)!.Open());
            manifest = ScenePackageCodec.Deserialize(reader.ReadToEnd());
            ScenePackageCodec.ValidateForTransport(manifest);
            Require(manifest.Toolbox?.Tools.Count == 2 && manifest.Toolbox.Chains.Count == 0,
                "Unexpected tools in publication package.");
            Require(manifest.Layers.Single(layer => layer.AiStyleReference).Images?.Count == 1,
                "Style image is missing from package.");
        }
        GD.Print("[AGAS][TILE47-PUBLICATION] Real scene exported; three images, seven prompts, two original presets.");

        _target = Path.Combine(_artifacts, "roundtrip-project");
        Directory.CreateDirectory(_target);
        Require(new AgasProjectInitializer().EnsureInitialized(_target).Success, "Target initialization failed.");
        System.Environment.SetEnvironmentVariable("AGAS_HOME", Path.Combine(_artifacts, "target-home"));
        GetTree().Root.Size = new Vector2I(1600, 900);
        var workspace = new MainWorkspace { CustomMinimumSize = new Vector2(1600, 900) };
        AddChild(workspace);
        workspace.OpenProject(_target);
        await Frames();
        string beforeId = workspace.CurrentScene!.Id;
        Find<PopupMenu>(workspace, "ScenesContextMenu").EmitSignal(PopupMenu.SignalName.IdPressed, 4L);
        await Frames();
        FileDialog dialog = Find<FileDialog>(workspace, "ScenePackageImportDialog");
        dialog.Hide();
        dialog.EmitSignal(FileDialog.SignalName.FileSelected, packagePath);
        await Frames();
        AgasSceneResource imported = workspace.CurrentScene ?? throw new Exception("No imported scene.");
        Require(imported.Id != beforeId && imported.Id != published.Id && imported.Name == published.Name,
            "Workspace import did not activate a new scene.");
        string importedId = imported.Id;
        VerifyRestoredScene(imported, sourceLayers, sourceImageHashes);
        Require((bool)Invoke(workspace, "SaveProject")!, "Workspace save failed.");
        workspace.QueueFree();
        await Frames();

        workspace = new MainWorkspace { CustomMinimumSize = new Vector2(1600, 900) };
        AddChild(workspace);
        workspace.OpenProject(_target);
        await Frames();
        imported = workspace.CurrentScene ?? throw new Exception("Reopened scene missing.");
        Require(imported.Id == importedId, "Reopen did not restore imported active scene.");
        VerifyRestoredScene(imported, sourceLayers, sourceImageHashes);
        CanvasWorkspace canvas = Find<CanvasWorkspace>(workspace, "CanvasWorkspaceHost");
        Require(canvas.IsVisibleInTree(), "Imported canvas is not visible.");
        canvas.SetGridSizePx(64, undoable: false);
        Require(canvas.TrySetRegions(new CanvasGridRegion(0, 0, 16, 8)), "Could not set Q region.");
        Require(canvas.ReplacementRegion == null, "W must be absent.");
        AgasLayerResource[] restored = imported.LayersSnapshot.OrderBy(layer => layer.Order).ToArray();
        string prompt = PromptComposer.Compose(restored[2].PromptFragmentsSnapshot, null, PromptCompositionMode.ReferenceEdit);
        var context = (AiGenerationCanvasContext)Invoke(workspace, "CreateAiGenerationContext", CanvasPatchLayerScope.AllVisibleLayers)!;
        var profile = new AiGenerationProviderProfile("verification", "Local verification", "test", "test", "test",
            "verification-only", "https://example.test", AiGenerationAdapterCapabilities.ReferenceEdit);
        var input = new AiGenerationPreparationInput(profile, context, AiGenerationTarget.AddToCurrentLayer, prompt);
        var prepared = (AiGenerationPreparationResult)Invoke(workspace, "PrepareAiGenerationRequest", input)!;
        Require(prepared.Success && prepared.Request != null, "Real workspace prepare failed: " + prepared.Message);
        var request = prepared.Request!;
        Require(request.Width == 1024 && request.Height == 512 && request.ReferenceImages.Count == 2,
            "Output dimensions or reference count are incorrect.");
        using Image style = Image.LoadFromFile(Path.Combine(_target, restored[0].ImagesSnapshot.Single().ImagePath));
        Require(request.ReferenceImages[0].Bytes.SequenceEqual(style.SavePngToBuffer()), "First attachment is not the full style image.");
        using Image layout = Image.LoadFromFile(Path.Combine(_target, restored[1].ImagesSnapshot.Single().ImagePath));
        using Image captured = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        Require(captured.LoadPngFromBuffer(request.ReferenceImages[1].Bytes) == Error.Ok, "Second attachment failed to decode.");
        layout.Convert(Image.Format.Rgba8); captured.Convert(Image.Format.Rgba8);
        Require(layout.GetSize() == captured.GetSize() && SameVisiblePixels(layout.GetData(), captured.GetData()),
            "Second attachment is not the unchanged template; style/result leaked into Q.");
        string importedStylePath = Path.Combine(_target, restored[0].ImagesSnapshot.Single().ImagePath);
        byte[] originalStyleBytes = File.ReadAllBytes(importedStylePath);
        try
        {
            using Image changed = Image.CreateEmpty(3, 2, false, Image.Format.Rgba8);
            changed.Fill(Colors.Magenta);
            Require(changed.SavePng(importedStylePath) == Error.Ok, "Cannot stage fingerprint check.");
            var changedPreparation = (AiGenerationPreparationResult)Invoke(workspace, "PrepareAiGenerationRequest", input)!;
            Require(changedPreparation.Success && changedPreparation.PatchCapture!.ContentFingerprint != prepared.PatchCapture!.ContentFingerprint,
                "Changing the imported style outside Q did not invalidate the capture fingerprint.");
        }
        finally { File.WriteAllBytes(importedStylePath, originalStyleBytes); }

        string resultPath = restored[2].ImagesSnapshot.Single().ImagePath;
        var importedTools = new ToolCatalog();
        Require(importedTools.LoadTool("tile47-standard-reverse").Source == ToolPresetSources.Tile47StandardReverse,
            "Imported reverse preset is not the original source.");
        ImageToolRunResult reversed = new ImageToolRunner().RunTool(_target, resultPath,
            importedTools.LoadTool("tile47-standard-reverse"), "assets/publication-atlas.png");
        Require(reversed.Success && reversed.Width == 768 && reversed.Height == 256, "Imported reverse preset failed: " + reversed.Message);
        using Image atlas = Image.LoadFromFile(Path.Combine(_target, reversed.RelativePath));
        for (int y = 64; y < 128; y++)
            for (int x = 640; x < 704; x++) Require(atlas.GetPixel(x, y).A == 0, "Reserved atlas slot is not transparent.");
        File.Copy(Path.Combine(_target, resultPath), Path.Combine(_artifacts, "generated.png"), true);
        File.Copy(Path.Combine(_target, restored[0].ImagesSnapshot.Single().ImagePath), Path.Combine(_artifacts, "style.png"), true);
        File.Copy(Path.Combine(_target, restored[1].ImagesSnapshot.Single().ImagePath), Path.Combine(_artifacts, "template.png"), true);
        File.Copy(Path.Combine(_target, reversed.RelativePath), Path.Combine(_artifacts, "atlas.png"), true);
        File.WriteAllText(Path.Combine(_artifacts, "prompts.txt"), string.Join("\n\n", restored[2].PromptFragmentsSnapshot.OrderBy(p => p.Order).Select(p => p.Text)) + "\n");
        Require(sourceHash == Hash(File.ReadAllBytes(sourcePath)) && projectHash == Hash(File.ReadAllBytes(AgasPaths.ProjectFile(sourceRoot))),
            "Original scene or project changed during verification.");
        for (int i = 0; i < sourceLayers.Length; i++)
            Require(sourceImageHashes[i] == Hash(File.ReadAllBytes(Path.Combine(sourceRoot, sourceLayers[i].ImagesSnapshot.Single().ImagePath))),
                "An original image changed during verification.");
        File.WriteAllText(Path.Combine(_artifacts, "verification.json"), JsonSerializer.Serialize(new
        {
            verifiedAtUtc = DateTime.UtcNow.ToString("O"), status = "passed", sceneName = imported.Name,
            packageFile = Path.GetFileName(packagePath), packageSha256 = Hash(File.ReadAllBytes(packagePath)),
            originalSceneUnchanged = true, originalProjectUnchanged = true, originalImagesUnchanged = true,
            layers = 3, images = 3, promptFragments = 7, shippedTile47Tools = 2,
            actualExport = "ScenePackageStore.Export", actualImport = "MainWorkspace ScenePackageImportDialog.FileSelected",
            savedAndReopened = true, styleRolePreserved = true, activeResultLayerPreserved = true,
            referenceOrder = new[] { "style", "layout-template" }, outputPixels = new[] { 1024, 512 },
            templateVisiblePixelsAndAlphaUnchanged = true, styleOutsideQFingerprintChecked = true,
            importedReverseToolOutputPixels = new[] { 768, 256 }, transparentSlot = new[] { 10, 1 },
            sourceImageSha256 = sourceImageHashes, paidGenerationCalled = false,
            publicationOnlyAdjustment = "Set result layer Shared=true on the in-memory export copy to include the existing generated image.",
        }, jsonOptions));
        workspace.QueueFree();
        await Frames();
    }

    private void VerifyRestoredScene(AgasSceneResource scene, AgasLayerResource[] source, string[] hashes)
    {
        AgasLayerResource[] actual = scene.LayersSnapshot.OrderBy(layer => layer.Order).ToArray();
        Require(actual.Length == source.Length && scene.ActiveLayerId == actual[2].Id, "Restored layer selection mismatch.");
        for (int i = 0; i < actual.Length; i++)
        {
            Require(actual[i].Name == source[i].Name && actual[i].Visible && actual[i].Locked == source[i].Locked
                && actual[i].AiStyleReference == source[i].AiStyleReference && actual[i].Shared,
                "Layer roles, visibility, sharing or locks changed.");
            ImageInstanceResource image = actual[i].ImagesSnapshot.Single();
            ImageInstanceResource prior = source[i].ImagesSnapshot.Single();
            Require(image.X == prior.X && image.Y == prior.Y && image.ScaleX == prior.ScaleX && image.ScaleY == prior.ScaleY
                && image.RotationDegrees == prior.RotationDegrees && image.Opacity == prior.Opacity, "Image placement changed.");
            Require(Hash(File.ReadAllBytes(Path.Combine(_target, image.ImagePath))) == hashes[i], "Imported image bytes changed.");
            var prompts = actual[i].PromptFragmentsSnapshot.OrderBy(p => p.Order).Select(p => (p.Text, p.Enabled, p.Order, p.Role));
            Require(prompts.SequenceEqual(source[i].PromptFragmentsSnapshot.OrderBy(p => p.Order).Select(p => (p.Text, p.Enabled, p.Order, p.Role))),
                "Prompt fragment content, order or role changed.");
            using Image decoded = Image.LoadFromFile(Path.Combine(_target, image.ImagePath));
            Require(!decoded.IsEmpty(), "Imported visible image failed to decode.");
        }
    }

    private async Task Frames() { for (int i = 0; i < 6; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
    private static bool SameVisiblePixels(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index += 4)
        {
            if (left[index + 3] != right[index + 3]) return false;
            // Alpha compositing legitimately clears hidden RGB in fully transparent template pixels.
            if (left[index + 3] == 0) continue;
            for (int channel = 0; channel < 3; channel++)
                if (left[index + channel] != right[index + channel]) return false;
        }
        return true;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static T Find<T>(Node root, string name) where T : Node => root.FindChild(name, true, false) as T ?? throw new Exception("Missing UI node: " + name);
    private static object? Invoke(object value, string method, params object[] args) => value.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
