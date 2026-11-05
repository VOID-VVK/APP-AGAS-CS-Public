#nullable enable
using System;
using System.IO;
using System.Linq;
using Agas.Features.Canvas;
using AppAgasCs.Application;
using AppAgasCs.Application.Contracts;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Generation;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class CanvasStyleReferenceAcceptance : Node
{
    public override void _Ready()
    {
        string root = Path.Combine(Path.GetTempPath(), "agas-style-reference-" + Guid.NewGuid().ToString("N"));
        int exit = 0;
        try
        {
            Directory.CreateDirectory(root);
            SaveImage(root, "style.png", 32, 24, Colors.Green);
            SaveImage(root, "template.png", 1024, 512, Colors.Blue);
            SaveImage(root, "old-result.png", 1024, 512, Colors.Red);
            var scene = new AgasSceneResource { Id = "scene-style", Name = "Style", ActiveLayerId = "result" };
            var style = new AgasLayerResource { Id = "style", Name = "Style", AiStyleReference = true, Locked = true, Shared = true };
            var template = new AgasLayerResource { Id = "template", Name = "Template", Order = 1, Locked = true, Shared = true };
            var result = new AgasLayerResource { Id = "result", Name = "Result", Order = 2 };
            scene.AddLayer(style); scene.AddLayer(template); scene.AddLayer(result);
            template.AddImage(new ImageInstanceResource { Id = "template-image", ImagePath = "template.png" });
            result.AddImage(new ImageInstanceResource { Id = "result-image", ImagePath = "old-result.png" });
            var service = new CanvasPatchCaptureService();
            CanvasPatchCapture Capture() => service.Capture(scene, root, null, "__layer__", CanvasPatchLayerScope.AllVisibleLayers,
                result.Id, new CanvasGridRegion(0, 0, 16, 8), new CanvasGridRegion(0, 0, 16, 8), 64,
                CanvasPatchGenerationMode.FullRegionGeneration);
            try { Capture(); throw new Exception("Missing style was accepted."); }
            catch (CanvasStyleReferenceException) { }
            style.AddImage(new ImageInstanceResource { Id = "style-image", ImagePath = "style.png", X = 2048 });
            CanvasPatchCapture captured = Capture();
            using (Image png = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8))
            {
                Require(png.LoadPngFromBuffer(captured.ReplacementPatchPng) == Error.Ok, "Template PNG decode");
                Require(png.GetSize() == new Vector2I(1024, 512), "Q output dimensions");
                Require(png.GetPixel(0, 0) == Colors.Blue, "Current result must not overwrite template");
            }
            var profile = new AiGenerationProviderProfile("test", "test", "test", "test", "test", "key", "https://example.test", AiGenerationAdapterCapabilities.ReferenceEdit);
            var context = new AiGenerationCanvasContext(root, "project", scene.Id, result.Id, false, null, true, 64, null);
            var prepared = new AiGenerationRequestPreparationService().Prepare(
                new AiGenerationPreparationInput(profile, context, AiGenerationTarget.AddToCurrentLayer, "style first; template second"), captured);
            Require(prepared.Success && prepared.Request!.ReferenceImages.Count == 2, "Two references required");
            using (Image png = Image.LoadFromFile(Path.Combine(root, "style.png")))
                Require(prepared.Request!.ReferenceImages[0].Bytes.SequenceEqual(png.SavePngToBuffer()), "First input is actual style file");
            Require(prepared.Request!.ReferenceImages[1].Bytes.SequenceEqual(captured.ReplacementPatchPng), "Second input is Q template");
            SaveImage(root, "style.png", 32, 24, Colors.Yellow);
            Require(Capture().ContentFingerprint != captured.ContentFingerprint, "Style changes outside Q invalidate capture");
            style.ImagesSnapshot[0].X = 0;
            Require(Capture().ReplacementPatchPng.SequenceEqual(captured.ReplacementPatchPng), "Style over Q never paints into template");
            string scenePath = Path.Combine(root, "scene.tres");
            Require(ResourceSaver.Save(scene, scenePath) == Error.Ok, "Save scene");
            var loaded = ResourceLoader.Load<AgasSceneResource>(scenePath, cacheMode: ResourceLoader.CacheMode.Ignore);
            Require(loaded.LayersSnapshot.Single(layer => layer.Id == "style").AiStyleReference, "Style flag .tres persistence");
            ScenePackageDocument package = ScenePackageCodec.Deserialize(ScenePackageCodec.Serialize(ScenePackageCodec.FromScene(scene)));
            Require(package.Layers.Single(layer => layer.Id == "style").AiStyleReference, "Style flag package persistence");
            style.AiStyleReference = false;
            CanvasPatchCapture legacy = Capture();
            Require(legacy.StyleReferencePng == null, "Ordinary scenes remain single-image");
            using (Image png = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8))
            {
                png.LoadPngFromBuffer(legacy.ReplacementPatchPng);
                Require(png.GetPixel(0, 0) == Colors.Red, "Ordinary all-visible-layer composition preserved");
            }
            GD.Print("[AGAS][STYLE-REFERENCE][PASS] Missing style blocked; actual files captured style→template; Q dimensions, persistence, fingerprint and legacy behavior verified.");
        }
        catch (Exception ex) { exit = 1; GD.PushError(ex.ToString()); }
        finally { Directory.Delete(root, true); GetTree().Quit(exit); }
    }

    private static void SaveImage(string root, string name, int width, int height, Color color)
    {
        using Image image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        image.Fill(color);
        Require(image.SavePng(Path.Combine(root, name)) == Error.Ok, "Write fixture");
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
