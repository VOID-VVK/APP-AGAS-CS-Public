#nullable enable

using System;
using System.IO;
using System.Threading.Tasks;
using AGAS.UI;
using Agas.Features.Canvas;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class CanvasViewportZoomAcceptance : Node
{
    private const string Prefix = "[AGAS][CANVAS-VIEWPORT-ZOOM]";
    private string _root = string.Empty;

    public override async void _Ready()
    {
        int exitCode = 0;
        try
        {
            await RunAllAsync();
            GD.Print($"{Prefix}[PASS]");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {exception}");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(_root))
            {
                try { Directory.Delete(_root, recursive: true); } catch { }
            }
            GetTree().Quit(exitCode);
        }
    }

    private async Task RunAllAsync()
    {
        GetWindow().Size = new Vector2I(1200, 760);
        _root = Path.Combine(Path.GetTempPath(), "agas-canvas-viewport-zoom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Require(new AgasProjectInitializer().EnsureInitialized(_root).Success, "Project fixture initialization failed.");
        string imagePath = Path.Combine(_root, "assets", "zoom.png");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        using (Image image = Image.CreateEmpty(16, 16, false, Image.Format.Rgba8))
        {
            image.Fill(Colors.White);
            Require(image.SavePng(imagePath) == Error.Ok, "Zoom image fixture could not be saved.");
        }

        var workspace = new MainWorkspace { CustomMinimumSize = new Vector2(1200, 760) };
        AddChild(workspace);
        await Frames(2);
        workspace.OpenProject(_root);
        await Frames(4);

        CanvasWorkspace canvas = FindRequired<CanvasWorkspace>(workspace, "CanvasWorkspaceHost");
        Button zoomOut = FindRequired<Button>(workspace, "CanvasZoomOutButton");
        Button zoomPercent = FindRequired<Button>(workspace, "CanvasZoomPercentButton");
        Button zoomIn = FindRequired<Button>(workspace, "CanvasZoomInButton");
        Button zoomFit = FindRequired<Button>(workspace, "CanvasZoomFitButton");
        Require(zoomOut.IsVisibleInTree() && zoomPercent.IsVisibleInTree()
                && zoomIn.IsVisibleInTree() && zoomFit.IsVisibleInTree(),
            "Canvas-only zoom controls were not visible on the scene surface.");

        Press(zoomIn);
        Require(Mathf.IsEqualApprox(canvas.GetZoom(), 1.1f) && zoomPercent.Text == "110%",
            "Zoom-in did not update the viewport and percentage.");
        Press(zoomOut);
        Require(Mathf.IsEqualApprox(canvas.GetZoom(), 1.0f) && zoomPercent.Text == "100%",
            "Zoom-out did not update the viewport and percentage.");
        Press(zoomIn);
        Press(zoomPercent);
        Require(Mathf.IsEqualApprox(canvas.GetZoom(), 1.0f) && zoomPercent.Text == "100%",
            "Percentage control did not reset the viewport to 100%.");

        Require(canvas.TryAddImageFromProjectRelativePath("assets/zoom.png", new Vector2(48, 96)),
            "Zoom image fixture could not be added to the Canvas.");
        ImageInstanceResource selected = canvas.GetSelectedImage() as ImageInstanceResource
            ?? throw new InvalidOperationException("Zoom image fixture was not selected.");
        selected.ScaleX = 200;
        selected.ScaleY = 200;
        Press(zoomFit);
        Require(canvas.GetZoom() < 1.0f && zoomPercent.Text != "100%",
            "Fit-visible did not reduce oversized content to the viewport.");
        float visibleFitZoom = canvas.GetZoom();
        Vector2 visibleFitCamera = canvas.GetCameraWorld();

        Require(canvas.TryAddImageFromProjectRelativePath("assets/zoom.png", new Vector2(1_000_000, 1_000_000)),
            "Transparent fit-exclusion fixture could not be added to the Canvas.");
        ImageInstanceResource transparent = canvas.GetSelectedImage() as ImageInstanceResource
            ?? throw new InvalidOperationException("Transparent fit-exclusion fixture was not selected.");
        transparent.Opacity = 0;
        Press(zoomFit);
        Require(Mathf.IsEqualApprox(canvas.GetZoom(), visibleFitZoom)
                && canvas.GetCameraWorld().IsEqualApprox(visibleFitCamera),
            "A fully transparent distant image changed visible-content fitting.");

        workspace.QueueFree();
        await Frames(4);
    }

    private async Task Frames(int count)
    {
        for (int i = 0; i < count; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static T FindRequired<T>(Node root, string name) where T : Node =>
        root.FindChild(name, recursive: true, owned: false) as T
        ?? throw new InvalidOperationException($"Required node '{name}' was not found.");

    private static void Press(Button button) => button.EmitSignal(BaseButton.SignalName.Pressed);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
