using Agas.Features.Canvas;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Generation;
using Godot;
using System;
using System.Collections.Generic;
using System.IO;

public partial class CanvasPatchSecurityAcceptance : Node
{
    private const string Prefix = "[AGAS][CANVAS-PATCH-SECURITY]";
    private readonly List<string> _tempRoots = new();

    public override async void _Ready()
    {
        int exitCode = 0;
        try
        {
            await RunAll();
            GD.Print($"{Prefix}[PASS]");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {exception}");
        }
        finally
        {
            foreach (string path in _tempRoots)
            {
                try { Directory.Delete(path, recursive: true); } catch { }
            }
            GetTree().Quit(exitCode);
        }
    }

    private async System.Threading.Tasks.Task RunAll()
    {
        string projectRoot = Temp("project");
        string externalRoot = Temp("external");
        Directory.CreateDirectory(Path.Combine(projectRoot, "assets"));
        Directory.CreateDirectory(Path.Combine(projectRoot, ".agas", "scenes"));
        SaveImage(Path.Combine(projectRoot, "assets", "safe.png"), Colors.Red);
        string outsideImage = Path.Combine(externalRoot, "outside.png");
        SaveImage(outsideImage, Colors.Blue);

        var project = new AgasProjectResource { ProjectId = "security-project", Name = "Security" };
        project.Grid.SizePx = 4;
        AgasSceneResource maliciousScene = SceneWithImage("../" + Path.GetFileName(externalRoot) + "/outside.png");
        string maliciousTres = Path.Combine(projectRoot, ".agas", "scenes", "malicious.tres");
        Equal(Error.Ok, ResourceSaver.Save(maliciousScene, maliciousTres), "malicious tres fixture save");
        var repository = new AgasResourceRepository();
        AgasLoadResult<AgasSceneResource> loaded = repository.Load<AgasSceneResource>(maliciousTres);
        True(loaded.Success && loaded.Resource != null, "malicious tres fixture load");

        var canvas = new CanvasWorkspace { Size = new Vector2(320, 240) };
        AddChild(canvas);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        canvas.SetWorkspace(project, loaded.Resource, projectRoot);
        SetQReferenceRegion(canvas, new Rect2(0, 0, 4, 4));
        Throws(() => canvas.CaptureCurrentPatch("image-1"), "traversal ImagePath from tres must be rejected");

        loaded.Resource!.Layers[0].Images[0].ImagePath = outsideImage;
        Throws(() => canvas.CaptureCurrentPatch("image-1"), "absolute ImagePath from tres must be rejected");

        loaded.Resource.Layers[0].Images[0].ImagePath = "assets/safe.png";
        CanvasPatchCapture safeCapture = canvas.CaptureCurrentPatch("image-1");
        True(safeCapture.SourceCanvasPng.Length > 0, "safe project-relative image should capture");

        if (TryCreateFileSymlink(Path.Combine(projectRoot, "assets", "outside-link.png"), outsideImage))
        {
            loaded.Resource.Layers[0].Images[0].ImagePath = "assets/outside-link.png";
            Throws(() => canvas.CaptureCurrentPatch("image-1"), "image symlink must be rejected before capture read");
        }

        string outsideDirectory = Path.Combine(externalRoot, "linked-directory");
        Directory.CreateDirectory(outsideDirectory);
        SaveImage(Path.Combine(outsideDirectory, "nested.png"), Colors.Green);
        if (TryCreateDirectorySymlink(Path.Combine(projectRoot, "assets", "linked-directory"), outsideDirectory))
        {
            loaded.Resource.Layers[0].Images[0].ImagePath = "assets/linked-directory/nested.png";
            Throws(() => canvas.CaptureCurrentPatch("image-1"), "parent directory symlink must be rejected before capture read");
        }

        VerifyGeneratedAssetStoreRejectsLinks(projectRoot, externalRoot, safeCapture.SourceCanvasPng);
        canvas.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static void VerifyGeneratedAssetStoreRejectsLinks(string projectRoot, string externalRoot, byte[] png)
    {
        string generated = Path.Combine(projectRoot, "assets", "generated");
        Directory.CreateDirectory(generated);
        string outsideCandidate = Path.Combine(externalRoot, "candidate.png");
        SaveImage(outsideCandidate, Colors.Blue);
        string candidateLink = Path.Combine(generated, "candidate-link.png");
        if (TryCreateFileSymlink(candidateLink, outsideCandidate))
        {
            False(CanvasPatchAssetStore.TryReadGenerated(projectRoot, "assets/generated/candidate-link.png", out _, out _),
                "generated candidate symlink must not be read for preview/upload");
            CanvasPatchAssetStore.DeleteGeneratedCandidate(projectRoot, "assets/generated/candidate-link.png");
            True(File.Exists(outsideCandidate), "rejected delete must not remove external symlink target");
        }

        string linkedProject = Path.Combine(Path.GetDirectoryName(projectRoot)!, "linked-output-" + Guid.NewGuid().ToString("N"));
        string externalGenerated = Path.Combine(externalRoot, "generated-target");
        try
        {
            string generatedRoot = Path.Combine(linkedProject, AgasGeneratedImagePathPolicy.RootDirectoryName);
            Directory.CreateDirectory(generatedRoot);
            Directory.CreateDirectory(externalGenerated);
            if (TryCreateDirectorySymlink(Path.Combine(generatedRoot, "Forest"), externalGenerated))
            {
                False(CanvasPatchAssetStore.TrySavePng(
                        linkedProject,
                        png,
                        new AiGenerationStorageScope("scene-forest", "Forest", "layer-ground", "Ground"),
                        out _,
                        out _),
                    "scoped generated directory symlink must reject composed image save/upload");
                Equal(
                    0,
                    Directory.GetFileSystemEntries(externalGenerated, "*", SearchOption.AllDirectories).Length,
                    "external generated target must remain completely untouched");
            }
        }
        finally
        {
            try { Directory.Delete(linkedProject, recursive: true); } catch { }
            try { Directory.Delete(externalGenerated, recursive: true); } catch { }
        }
    }

    private static AgasSceneResource SceneWithImage(string imagePath)
    {
        var image = new ImageInstanceResource
        {
            Id = "image-1",
            ImagePath = imagePath,
            Order = 0,
            X = 0,
            Y = 0,
            ScaleX = 1,
            ScaleY = 1,
            Opacity = 1
        };
        var layer = new AgasLayerResource { Id = "layer-1", Name = "Layer", Visible = true, Order = 0 };
        layer.Images.Add(image);
        var scene = new AgasSceneResource { Id = "scene-1", Name = "Scene", ActiveLayerId = layer.Id };
        scene.Layers.Add(layer);
        return scene;
    }

    private string Temp(string name)
    {
        string path = Path.Combine(Path.GetTempPath(), $"agas-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempRoots.Add(path);
        return path;
    }

    private static void SetQReferenceRegion(CanvasWorkspace canvas, Rect2 worldRect)
    {
        canvas.SetActiveRegionTool(CanvasRegionTool.Selection);
        Vector2 start = canvas.WorldToScreen(worldRect.Position + Vector2.One * 0.1f);
        Vector2 end = canvas.WorldToScreen(worldRect.End - Vector2.One * 0.1f);
        canvas._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = start });
        canvas._GuiInput(new InputEventMouseMotion { Position = end, Relative = end - start, ButtonMask = MouseButtonMask.Left });
        canvas._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = end });
        True(canvas.SelectionRegion.HasValue, "Q reference region must be set through the Selection tool");
        False(canvas.ReplacementRegion.HasValue, "Q-only security capture must not fabricate a W replacement region");
    }

    private static bool TryCreateFileSymlink(string link, string target)
    {
        try { File.CreateSymbolicLink(link, target); return true; }
        catch (PlatformNotSupportedException exception)
        {
            GD.PushWarning($"{Prefix}[SKIP][SYMLINK-UNSUPPORTED] file symlink assertions were not covered: {exception.Message}");
            return false;
        }
    }

    private static bool TryCreateDirectorySymlink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (PlatformNotSupportedException exception)
        {
            GD.PushWarning($"{Prefix}[SKIP][SYMLINK-UNSUPPORTED] directory symlink assertions were not covered: {exception.Message}");
            return false;
        }
    }

    private static void SaveImage(string path, Color color)
    {
        using Image image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        image.Fill(color);
        Equal(Error.Ok, image.SavePng(path), "fixture image save");
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch { return; }
        throw new InvalidOperationException(message);
    }

    private static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void False(bool value, string message) => True(!value, message);

    private static void Equal<T>(T expected, T actual, string message) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }
}
