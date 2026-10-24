#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AGAS.UI;
using Agas.Features.Canvas;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class ProjectMutationIntegrationAcceptance : Node
{
    private const string Prefix = "[AGAS][PROJECT-MUTATION-INTEGRATION]";
    private string? _root;

    public override async void _Ready()
    {
        int exitCode = 0;
        try
        {
            await RunAsync();
            GD.Print($"{Prefix}[PASS]");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {exception}");
        }
        finally
        {
            try { if (_root != null && Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
            GetTree().Quit(exitCode);
        }
    }

    private async Task RunAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "agas-project-mutation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var repository = new AgasResourceRepository();
        Require(new AgasProjectInitializer(repository).EnsureInitialized(_root).Success, "Fixture initialization failed.");

        var workspace = new MainWorkspace();
        AddChild(workspace);
        workspace.OpenProject(_root);
        await Frames(3);

        int originalSceneCount = workspace.Project!.Scenes.Count;
        InvokePrivate(workspace, "AddScene");
        await Frames(2);
        string sceneId = workspace.CurrentScene!.Id;
        Require(workspace.Project.Scenes.Count == originalSceneCount + 1, "Application service did not add the scene.");

        InvokePrivate(workspace, "RenameScene", sceneId, "Unified Scene");
        Require(workspace.CurrentScene.Name == "Unified Scene"
                && workspace.Project.Scenes.Single(item => item.Id == sceneId).Name == "Unified Scene",
            "Scene rename did not update index and loaded scene through one mutation.");

        CanvasWorkspace canvas = FindRequired<CanvasWorkspace>(workspace, "CanvasWorkspaceHost");
        InvokePrivate(workspace, "AddLayer");
        await Frames(1);
        Require(workspace.CurrentScene.Layers.Count == 2 && canvas.CanUndo(), "Layer add did not enter Canvas undo policy.");
        Require(canvas.Undo() && workspace.CurrentScene.Layers.Count == 1, "Layer add undo failed.");
        Require(canvas.Redo() && workspace.CurrentScene.Layers.Count == 2, "Layer add redo failed.");

        AgasLayerResource added = workspace.CurrentScene.Layers.Single(item => item.Id == workspace.CurrentScene.ActiveLayerId);
        string oldName = added.Name;
        InvokePrivate(workspace, "RenameLayer", added, "Unified Layer");
        Require(added.Name == "Unified Layer" && canvas.Undo() && added.Name == oldName && canvas.Redo(), "Layer rename undo/redo failed.");

        InvokePrivate(workspace, "SetLayerVisible", added, false);
        Require(!added.Visible && canvas.Undo() && added.Visible, "Layer visibility mutation/undo failed.");
        InvokePrivate(workspace, "SetLayerLocked", added, true);
        Require(added.Locked && canvas.Undo() && !added.Locked, "Layer lock mutation/undo failed.");

        int beforeOrder = added.Order;
        InvokePrivate(workspace, "MoveLayer", added, -1);
        Require(added.Order != beforeOrder && canvas.Undo() && added.Order == beforeOrder, "Layer move mutation/undo failed.");

        InvokePrivate(workspace, "DeleteLayer", added);
        Require(workspace.CurrentScene.Layers.Count == 1 && canvas.Undo() && workspace.CurrentScene.Layers.Count == 2,
            "Layer delete mutation/undo failed.");

        canvas.ClearUndoRedoHistory();
        AgasLayerResource canvasLayer = workspace.CurrentScene.Layers.Single(item => item.Id == workspace.CurrentScene.ActiveLayerId);
        Require(canvas.SetLayerVisible(canvasLayer, false) && !canvasLayer.Visible && canvas.CanUndo(),
            "Canvas visibility API did not flow through ProjectMutationService and undo policy.");
        Require(canvas.Undo() && canvasLayer.Visible,
            "Canvas visibility API undo did not restore the application mutation.");
        Require(canvas.SetLayerLocked(canvasLayer, true) && canvasLayer.Locked && canvas.CanUndo(),
            "Canvas lock API did not flow through ProjectMutationService and undo policy.");
        Require(canvas.Undo() && !canvasLayer.Locked,
            "Canvas lock API undo did not restore the application mutation.");
        canvas.ClearUndoRedoHistory();

        string imagePath = Path.Combine(_root, "assets", "mutation.png");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        using (var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8))
        {
            image.Fill(Colors.White);
            Require(image.SavePng(imagePath) == Error.Ok, "Image fixture save failed.");
        }

        Require(canvas.TryAddImageFromProjectRelativePath("assets/mutation.png", new Vector2(32, 64)),
            "Canvas image add did not use the application mutation service.");
        ImageInstanceResource canvasImage = canvas.GetSelectedImage()
            ?? throw new InvalidOperationException("Image mutation did not select the added image.");
        double originalImageX = canvasImage.X;
        double originalImageOpacity = canvasImage.Opacity;
        Require(canvasLayer.Images.Contains(canvasImage) && canvas.Undo() && !canvasLayer.Images.Contains(canvasImage),
            "Image add did not enter Canvas undo policy.");
        Require(canvas.Redo() && canvasLayer.Images.Contains(canvasImage), "Image add redo failed.");
        Require(canvas.UpdateSelectedImageProperties(x: 96, opacity: 0.5) && canvasImage.X == 96 && canvasImage.Opacity == 0.5,
            "Image update did not flow through ProjectMutationService.");
        Require(canvas.Undo() && canvasImage.X == originalImageX && canvasImage.Opacity == originalImageOpacity,
            "Image update undo failed.");

        string resultImagePath = Path.Combine(_root, "assets", "mutation-result.png");
        using (var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8))
        {
            image.Fill(Colors.Black);
            Require(image.SavePng(resultImagePath) == Error.Ok, "Replacement image fixture save failed.");
        }

        Require(canvas.ReplaceImagePath(
                canvasImage.Id,
                "assets/mutation-result.png")
            && canvasImage.ImagePath == "assets/mutation-result.png",
            "Image path replacement failed.");
        Require(canvas.Undo()
                && canvasImage.ImagePath == "assets/mutation.png",
            "Image path replacement undo failed.");
        Require(canvas.Redo()
                && canvasImage.ImagePath == "assets/mutation-result.png",
            "Image path replacement redo failed.");

        Require(canvas.CopySelectedToClipboard() && canvas.PasteClipboard(),
            "Copy/paste of an image failed.");
        ImageInstanceResource pastedImage = canvas.GetSelectedImage()
            ?? throw new InvalidOperationException("Pasted image was not selected.");
        Require(canvas.Undo() && !canvasLayer.Images.Contains(pastedImage),
            "Undo did not remove the pasted image.");

        PromptFragmentResource prompt = canvas.AddPromptFragment(canvasLayer, "before")
            ?? throw new InvalidOperationException("Prompt add mutation failed.");
        Require(canvasLayer.PromptFragments.Contains(prompt) && canvas.UpdatePromptFragment(prompt, "after", false),
            "Prompt add/update did not flow through ProjectMutationService.");
        Require(prompt.Text == "after" && !prompt.Enabled && canvas.Undo() && prompt.Text == "before" && prompt.Enabled,
            "Prompt update undo did not restore one atomic snapshot.");

        canvas.ClearUndoRedoHistory();

        int initialGridSize = workspace.Project.Grid.SizePx;
        bool initialSnap = workspace.Project.Grid.SnapEnabled;
        bool initialGridVisible = workspace.Project.Grid.Visible;
        Require(canvas.SetGridSizePx(initialGridSize + 16)
                && workspace.Project.Grid.SizePx == initialGridSize + 16
                && canvas.CanUndo(),
            "Canvas grid-size API did not flow through ProjectMutationService and undo policy.");
        Require(canvas.Undo() && workspace.Project.Grid.SizePx == initialGridSize,
            "Canvas grid-size undo failed.");
        Require(canvas.Redo() && workspace.Project.Grid.SizePx == initialGridSize + 16,
            "Canvas grid-size redo failed.");
        Require(canvas.SetSnapEnabled(!initialSnap)
                && workspace.Project.Grid.SnapEnabled == !initialSnap
                && canvas.Undo()
                && workspace.Project.Grid.SnapEnabled == initialSnap,
            "Canvas grid-snap mutation/undo failed.");
        Require(canvas.SetGridVisible(!initialGridVisible)
                && workspace.Project.Grid.Visible == !initialGridVisible
                && canvas.Undo()
                && workspace.Project.Grid.Visible == initialGridVisible,
            "Canvas grid-visibility mutation/undo failed.");

        canvas.ClearUndoRedoHistory();
        InvokePrivate(workspace, "SetGridSize", initialGridSize + 32);
        Require(workspace.Project.Grid.SizePx == initialGridSize + 32 && canvas.CanUndo(),
            "MainWorkspace grid-size route did not use the Canvas application mutation boundary.");
        Require(canvas.Undo() && workspace.Project.Grid.SizePx == initialGridSize + 16,
            "MainWorkspace grid-size route did not share Canvas undo.");

        canvas.ClearUndoRedoHistory();
        SetPrivateField(workspace, "_projectRequiresReadOnly", true);
        int readOnlyGridSize = workspace.Project.Grid.SizePx;
        Require(!canvas.SetGridSizePx(readOnlyGridSize + 1)
                && workspace.Project.Grid.SizePx == readOnlyGridSize
                && !canvas.CanUndo(),
            "Read-only workspace allowed a grid mutation or polluted undo history.");
        SetPrivateField(workspace, "_projectRequiresReadOnly", false);

        canvas.ClearUndoRedoHistory();
        canvas.SetView(new Vector2(12, 34), 1.5f);
        Require(workspace.CurrentScene.ViewState.CameraX == 12
                && workspace.CurrentScene.ViewState.CameraY == 34
                && workspace.CurrentScene.ViewState.Zoom == 1.5,
            "Canvas view did not flow through ProjectMutationService.");
        Require(!canvas.CanUndo(), "View mutation must follow the existing non-undoable UI strategy.");

        Require(workspace.TrySaveForApplicationExit(), "Unified mutation state did not save.");
        AgasLoadResult<AgasSceneResource> reopened = repository.Load<AgasSceneResource>(AgasPaths.SceneFile(_root, sceneId));
        Require(reopened.Success && reopened.Resource?.Name == "Unified Scene" && reopened.Resource.Layers.Count == 2,
            "Saved scene did not preserve unified Scene/Layer mutations.");
        AgasSceneResource reopenedScene = reopened.Resource
            ?? throw new InvalidOperationException("Saved scene resource was null after successful load.");
        ImageInstanceResource reopenedImage = reopenedScene.Layers
            .SelectMany(layer => layer.Images)
            .Single(image => image.Id == canvasImage.Id);
        Require(reopenedImage.ImagePath == "assets/mutation-result.png",
            "Saved scene did not preserve the image path.");

        workspace.QueueFree();
        await Frames(2);
    }

    private async Task Frames(int count)
    {
        for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static void InvokePrivate(object target, string name, params object[] args)
    {
        MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().FullName, name);
        method.Invoke(target, args);
    }

    private static void SetPrivateField(object target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, name);
        field.SetValue(target, value);
    }

    private static T FindRequired<T>(Node root, string name) where T : Node =>
        root.FindChild(name, recursive: true, owned: false) as T
        ?? throw new InvalidOperationException($"Missing {name} ({typeof(T).Name}).");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
