using System;
using System.Collections.Generic;
using System.IO;
using Agas.Features.Canvas;
using AppAgasCs.Core.Resources;
using Godot;

public partial class CanvasRegionAndReplaceAcceptance : Node
{
    private const string Prefix = "[AGAS][CANVAS-REGION-REPLACE]";
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
            foreach (string root in _tempRoots)
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
            GetTree().Quit(exitCode);
        }
    }

    private async System.Threading.Tasks.Task RunAll()
    {
        string root = Path.Combine(Path.GetTempPath(), "agas-canvas-region-" + Guid.NewGuid().ToString("N"));
        _tempRoots.Add(root);
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        SaveImage(Path.Combine(root, "assets", "before.png"), Colors.Blue);
        SaveImage(Path.Combine(root, "assets", "after.png"), Colors.Green);
        SaveImage(Path.Combine(root, "assets", "overlay.png"), Colors.Red);

        var layer = new AgasLayerResource { Id = "layer-1", Name = "Layer", Visible = true, Locked = false };
        var scene = new AgasSceneResource { Id = "scene-1", Name = "Scene", ActiveLayerId = layer.Id };
        scene.Layers.Add(layer);
        var project = new AgasProjectResource { ProjectId = "project-1", Name = "Project" };
        project.Grid.SizePx = 48;

        var canvas = new CanvasWorkspace { Name = "Canvas", Size = new Vector2(640, 480) };
        AddChild(canvas);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        canvas.SetWorkspace(project, scene, root);

        var overlay = new CanvasAiGenerationOverlay(
            new Rect2(96, 144, 48, 96),
            "AI 生成中 00:00",
            CanvasAiGenerationOverlayTone.Generating);
        canvas.SetAiGenerationOverlay(overlay);
        Assert.NotNull(canvas.AiGenerationOverlay);
        Assert.Equal(overlay, canvas.AiGenerationOverlay!);
        Rect2 expectedOverlayScreen = new(
            canvas.WorldToScreen(overlay.WorldRect.Position),
            canvas.WorldToScreen(overlay.WorldRect.End) - canvas.WorldToScreen(overlay.WorldRect.Position));
        Assert.Equal(expectedOverlayScreen, canvas.AiGenerationOverlayScreenRect!.Value);
        canvas.SetView(new Vector2(24, 48), 2.0f);
        Rect2 expectedZoomedOverlay = new(
            canvas.WorldToScreen(overlay.WorldRect.Position),
            canvas.WorldToScreen(overlay.WorldRect.End) - canvas.WorldToScreen(overlay.WorldRect.Position));
        Assert.Equal(expectedZoomedOverlay, canvas.AiGenerationOverlayScreenRect!.Value);
        canvas.SetScene(scene);
        Assert.Null(canvas.AiGenerationOverlay);
        canvas.SetView(Vector2.Zero, 1.0f);

        Vector2 zoomPointer = new(500, 350);
        Vector2 worldUnderPointer = canvas.ScreenToWorld(zoomPointer);
        canvas._GuiInput(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.WheelUp,
            Pressed = true,
            Position = zoomPointer,
        });
        Assert.True(Mathf.IsEqualApprox(canvas.GetZoom(), 1.1f));
        Assert.True(canvas.ScreenToWorld(zoomPointer).IsEqualApprox(worldUnderPointer));
        canvas._GuiInput(new InputEventMagnifyGesture { Position = zoomPointer, Factor = 2.0f });
        Assert.True(Mathf.IsEqualApprox(canvas.GetZoom(), 2.2f));
        Assert.True(canvas.ScreenToWorld(zoomPointer).IsEqualApprox(worldUnderPointer));
        canvas.SetView(Vector2.Zero, 100.0f);
        Assert.True(Mathf.IsEqualApprox(canvas.GetZoom(), canvas.MaxZoom));
        canvas.SetView(Vector2.Zero, 0.001f);
        Assert.True(Mathf.IsEqualApprox(canvas.GetZoom(), canvas.MinZoom));
        canvas.ResetZoomTo100Percent();
        Assert.True(Mathf.IsEqualApprox(canvas.GetZoom(), 1.0f));
        canvas.ClearUndoRedoHistory();
        canvas.SetView(new Vector2(11, 22), 1.25f);
        Assert.Equal(11.0, scene.ViewState.CameraX);
        Assert.Equal(22.0, scene.ViewState.CameraY);
        Assert.Equal(1.25, scene.ViewState.Zoom);
        Assert.True(!canvas.CanUndo());
        canvas.SetView(Vector2.Zero, 1.0f);

        string droppedImagePath = Path.Combine(root, "assets", "before.png");
        Assert.True(canvas._CanDropData(new Vector2(96, 144), droppedImagePath));
        Assert.True(!canvas._CanDropData(new Vector2(96, 144), "../outside.png"));
        canvas._DropData(new Vector2(96, 144), droppedImagePath);
        Assert.Equal(1, layer.Images.Count);
        Assert.True(canvas.Undo());
        Assert.Equal(0, layer.Images.Count);

        Assert.True(canvas.TryAddImageFromProjectRelativePath("assets/before.png", new Vector2(96, 144)));
        var image = (ImageInstanceResource?)canvas.GetSelectedImage();
        Assert.NotNull(image);
        image!.ScaleX = 1.5;
        image.ScaleY = 0.75;
        image.RotationDegrees = 22;
        image.Opacity = 0.6;
        string id = image.Id;
        int order = image.Order;

        image.ScaleX = 100;
        image.ScaleY = 80;
        Assert.True(canvas.FitVisibleContent());
        Assert.True(canvas.GetZoom() < 1.0f);
        foreach (Vector2 corner in ImageWorldCorners(image, new Vector2(8, 8)))
        {
            Vector2 screen = canvas.WorldToScreen(corner);
            Assert.True(screen.X >= 47 && screen.X <= canvas.Size.X - 47);
            Assert.True(screen.Y >= 47 && screen.Y <= canvas.Size.Y - 47);
        }
        image.ScaleX = 1.5;
        image.ScaleY = 0.75;
        canvas.ResetZoomTo100Percent();

        int changed = 0;
        canvas.CanvasChanged += () => changed++;
        Assert.True(canvas.ReplaceSelectedImagePath("assets/after.png"));
        Assert.Equal("assets/after.png", image.ImagePath);
        Assert.Same(image, canvas.GetSelectedImage());
        Assert.Equal(id, image.Id);
        Assert.Equal(order, image.Order);
        Assert.Equal(1.5, image.ScaleX);
        Assert.Equal(0.75, image.ScaleY);
        Assert.Equal(22, image.RotationDegrees);
        Assert.Equal(0.6, image.Opacity);
        Assert.True(changed > 0);

        Assert.True(canvas.Undo());
        Assert.Equal("assets/before.png", image.ImagePath);
        Assert.Same(image, canvas.GetSelectedImage());
        Assert.True(canvas.Redo());
        Assert.Equal("assets/after.png", image.ImagePath);

        canvas.ClearUndoRedoHistory();
        PromptFragmentResource prompt = canvas.AddPromptFragment(layer, "before")
            ?? throw new InvalidOperationException("Prompt add failed.");
        Assert.True(layer.PromptFragments.Contains(prompt));
        Assert.True(canvas.UpdatePromptFragment(prompt, "after", enabled: false));
        Assert.Equal("after", prompt.Text);
        Assert.True(!prompt.Enabled);
        Assert.True(canvas.Undo());
        Assert.Equal("before", prompt.Text);
        Assert.True(prompt.Enabled);
        Assert.True(canvas.Redo());
        Assert.Equal("after", prompt.Text);
        Assert.True(!prompt.Enabled);
        Assert.True(canvas.SetPromptFragmentRole(prompt, (int)PromptFragmentRole.Environment));
        Assert.Equal(PromptFragmentRole.Environment, prompt.Role);
        Assert.True(canvas.Undo());
        Assert.Equal(PromptFragmentRole.General, prompt.Role);
        canvas.ClearUndoRedoHistory();

        canvas._GuiInput(new InputEventKey { Keycode = Key.Q, Pressed = true });
        Assert.Equal(CanvasRegionTool.Selection, canvas.ActiveRegionTool);
        Drag(canvas, new Vector2(500, 400), new Vector2(300, 200));
        Assert.NotNull(canvas.SelectionRegion);
        CanvasGridRegion expectedSelection = CanvasGridRegion.FromWorldDrag(
            canvas.ScreenToWorld(new Vector2(500, 400)),
            canvas.ScreenToWorld(new Vector2(300, 200)),
            48);
        Assert.Equal(expectedSelection, canvas.SelectionRegion!.Value);

        canvas._GuiInput(new InputEventKey { Keycode = Key.W, Pressed = true });
        Assert.Equal(CanvasRegionTool.Replacement, canvas.ActiveRegionTool);
        Drag(canvas, new Vector2(360, 260), new Vector2(410, 310));
        Assert.NotNull(canvas.ReplacementRegion);
        Assert.Equal(Colors.Yellow, canvas.SelectionRegionColor);
        Assert.Equal(Colors.Red, canvas.ReplacementRegionColor);

        image.ScaleX = 1;
        image.ScaleY = 1;
        image.RotationDegrees = 0;
        image.X = 96;
        image.Y = 144;
        Vector2 originalPosition = new((float)image.X, (float)image.Y);
        canvas.SetView(Vector2.Zero, 2.0f);
        Vector2 imageHit = canvas.WorldToScreen(originalPosition + new Vector2(4, 4));

        canvas._GuiInput(new InputEventKey { Keycode = Key.Q, Pressed = true });
        Drag(canvas, imageHit, imageHit + new Vector2(96, 0));
        Assert.Equal(originalPosition.X, (float)image.X);
        Assert.Equal(originalPosition.Y, (float)image.Y);

        canvas._GuiInput(new InputEventKey { Keycode = Key.W, Pressed = true });
        Drag(canvas, imageHit, imageHit + new Vector2(96, 0));
        Assert.Equal(originalPosition.X, (float)image.X);
        Assert.Equal(originalPosition.Y, (float)image.Y);

        canvas._GuiInput(new InputEventKey { Keycode = Key.Escape, Pressed = true });
        Assert.Equal(CanvasRegionTool.Edit, canvas.ActiveRegionTool);
        Drag(canvas, imageHit, imageHit + new Vector2(96, 0));
        Assert.Equal(originalPosition.X + 48, (float)image.X);
        Assert.Equal(originalPosition.Y, (float)image.Y);
        Assert.True(canvas.Undo());
        Assert.Equal(originalPosition.X, (float)image.X);
        Assert.Equal(originalPosition.Y, (float)image.Y);
        Assert.True(canvas.Redo());
        Assert.Equal(originalPosition.X + 48, (float)image.X);

        Vector2 positionBeforeCanceledDrag = new((float)image.X, (float)image.Y);
        Vector2 cancelDragStart = canvas.WorldToScreen(positionBeforeCanceledDrag + new Vector2(4, 4));
        canvas._GuiInput(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            Position = cancelDragStart,
        });
        canvas._GuiInput(new InputEventMouseMotion
        {
            Position = cancelDragStart + new Vector2(96, 0),
            Relative = new Vector2(96, 0),
            ButtonMask = MouseButtonMask.Left,
        });
        Assert.Equal(positionBeforeCanceledDrag.X, (float)image.X);
        Assert.Equal(positionBeforeCanceledDrag.Y, (float)image.Y);
        canvas.SetActiveRegionTool(CanvasRegionTool.Selection);
        Assert.Equal(positionBeforeCanceledDrag.X, (float)image.X);
        Assert.Equal(positionBeforeCanceledDrag.Y, (float)image.Y);

        canvas._GuiInput(new InputEventKey { Keycode = Key.Q, Pressed = true });
        canvas._GuiInput(new InputEventKey { Keycode = Key.E, Pressed = true });
        Assert.Equal(CanvasRegionTool.Edit, canvas.ActiveRegionTool);

        var overlayLayer = new AgasLayerResource { Id = "layer-overlay", Name = "Overlay", Order = 1, Visible = true, Locked = false };
        scene.Layers.Add(overlayLayer);
        var foreignLayerWithSameId = new AgasLayerResource { Id = overlayLayer.Id, Name = "Foreign" };
        Assert.True(!canvas.SetCurrentLayer(foreignLayerWithSameId));
        Assert.Equal(layer.Id, scene.ActiveLayerId);
        Assert.True(canvas.SetCurrentLayer(overlayLayer));
        Assert.True(canvas.TryAddImageFromProjectRelativePath("assets/overlay.png", new Vector2(144, 144)));
        Assert.True(canvas.SetCurrentLayer(layer));
        canvas.SetActiveRegionTool(CanvasRegionTool.Replacement);
        Drag(canvas, canvas.WorldToScreen(new Vector2(145, 145)), canvas.WorldToScreen(new Vector2(191, 191)));
        CanvasPatchCapture currentLayerCapture = canvas.CaptureCurrentPatch(
            null,
            CanvasPatchLayerScope.CurrentLayer,
            layer.Id);
        CanvasPatchCapture allVisibleCapture = canvas.CaptureCurrentPatch(
            null,
            CanvasPatchLayerScope.AllVisibleLayers,
            layer.Id);
        Assert.True(currentLayerCapture.ContentFingerprint != allVisibleCapture.ContentFingerprint);
        using Image currentLayerPatch = LoadPng(currentLayerCapture.ReplacementPatchPng);
        using Image allVisiblePatch = LoadPng(allVisibleCapture.ReplacementPatchPng);
        Assert.True(currentLayerPatch.GetPixel(2, 2).G > 0.8f);
        Assert.True(allVisiblePatch.GetPixel(2, 2).R > 0.8f);
    }

    private static void Drag(CanvasWorkspace canvas, Vector2 start, Vector2 end)
    {
        canvas._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = start });
        canvas._GuiInput(new InputEventMouseMotion { Position = end, Relative = end - start, ButtonMask = MouseButtonMask.Left });
        canvas._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = end });
    }

    private static IEnumerable<Vector2> ImageWorldCorners(ImageInstanceResource image, Vector2 pixelSize)
    {
        Vector2 position = new((float)image.X, (float)image.Y);
        float rotation = Mathf.DegToRad((float)image.RotationDegrees);
        Vector2 right = new Vector2(pixelSize.X * (float)image.ScaleX, 0).Rotated(rotation);
        Vector2 down = new Vector2(0, pixelSize.Y * (float)image.ScaleY).Rotated(rotation);
        return new[] { position, position + right, position + right + down, position + down };
    }

    private static void SaveImage(string path, Color color)
    {
        Image image = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
        image.Fill(color);
        Error error = image.SavePng(path);
        if (error != Error.Ok)
        {
            throw new InvalidOperationException($"Failed to save fixture image: {error}");
        }
    }

    private static Image LoadPng(byte[] png)
    {
        var image = new Image();
        if (image.LoadPngFromBuffer(png) != Error.Ok)
        {
            image.Dispose();
            throw new InvalidOperationException("Failed to load capture PNG.");
        }
        return image;
    }

    private static class Assert
    {
        public static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
        public static void Null(object? value) { if (value != null) throw new InvalidOperationException("Expected null."); }
        public static void NotNull(object? value) { if (value == null) throw new InvalidOperationException("Expected non-null."); }
        public static void Same(object expected, object? actual) { if (!ReferenceEquals(expected, actual)) throw new InvalidOperationException("Expected same instance."); }
        public static void Equal<T>(T expected, T actual) where T : notnull { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected '{expected}', got '{actual}'."); }
    }
}
