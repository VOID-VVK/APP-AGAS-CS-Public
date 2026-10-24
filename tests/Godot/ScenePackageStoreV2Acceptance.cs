#nullable enable

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
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

            var store = new ScenePackageStore();
            ScenePackageExportResult exported = store.Export(sourceProject, scene);
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

            ScenePackageImportResult imported = store.Import(targetProject, archive);
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

            string imagePath = Path.Combine(extracted, "images", "layer-one", "image-one.png");
            byte[] bytes = File.ReadAllBytes(imagePath);
            bytes[^2] ^= 1;
            File.WriteAllBytes(imagePath, bytes);
            string tamperedArchive = Path.Combine(root, "tampered.zip");
            ZipFile.CreateFromDirectory(extracted, tamperedArchive);
            ScenePackageImportResult tampered = store.Import(targetProject, tamperedArchive);
            Require(!tampered.Success, "A tampered image was imported.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
