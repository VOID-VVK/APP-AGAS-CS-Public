#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AGAS.UI;
using Agas.Features.Canvas;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Tools.Storage;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class CanvasTile47ToolRunAcceptance : Node
{
    private const string Prefix = "[AGAS][CANVAS-TILE47-REVERSE-RUN]";
    private string? _root;
    private string? _previousHome;

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
            System.Environment.SetEnvironmentVariable("AGAS_HOME", _previousHome);
            if (_root != null) { try { Directory.Delete(_root, recursive: true); } catch { } }
            GetTree().Quit(exitCode);
        }
    }

    private async Task RunAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "agas-canvas-tile47-reverse-" + Guid.NewGuid().ToString("N"));
        string projectRoot = Path.Combine(_root, "project");
        string agasHome = Path.Combine(_root, "home");
        Directory.CreateDirectory(Path.Combine(projectRoot, "assets"));
        _previousHome = System.Environment.GetEnvironmentVariable("AGAS_HOME");
        System.Environment.SetEnvironmentVariable("AGAS_HOME", agasHome);

        var repository = new AgasResourceRepository();
        Require(new AgasProjectInitializer(repository).EnsureInitialized(projectRoot).Success, "Project initialization failed.");
        string inputPath = Path.Combine(projectRoot, "assets", "template-16x8.png");
        using (Image fixture = CreateFixture())
            Require(fixture.SavePng(inputPath) == Error.Ok, "Fixture PNG save failed.");

        var workspace = new MainWorkspace { CustomMinimumSize = new Vector2(1200, 760) };
        AddChild(workspace);
        workspace.OpenProject(projectRoot);
        await Frames(6);

        CanvasWorkspace canvas = FindRequired<CanvasWorkspace>(workspace, "CanvasWorkspaceHost");
        Require(canvas.TryAddImageFromProjectRelativePath("assets/template-16x8.png", new Vector2(48, 48)),
            "Template image could not be added to the canvas.");
        ImageInstanceResource source = canvas.GetSelectedImage() as ImageInstanceResource
            ?? throw new InvalidOperationException("Template image was not selected.");
        AgasLayerResource layer = workspace.CurrentScene!.Layers.Single(item => item.Id == workspace.CurrentScene.ActiveLayerId);

        canvas._GuiInput(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Right,
            Pressed = true,
            Position = new Vector2(320, 240),
        });
        await Frames(2);
        PopupMenu rootMenu = FindRequired<PopupMenu>(workspace, "CanvasAiContextMenu");
        PopupMenu toolMenu = FindRequired<PopupMenu>(rootMenu, "CanvasToolMenu");
        Require(rootMenu.Visible, "Right-click did not display the canvas menu.");
        string toolName = new ToolCatalog().LoadTool("tile47-standard-reverse").Name;
        int index = Enumerable.Range(0, toolMenu.ItemCount)
            .Single(item => toolMenu.GetItemText(item) == toolName);
        toolMenu.EmitSignal(PopupMenu.SignalName.IdPressed, (long)toolMenu.GetItemId(index));

        Label status = FindRequired<Label>(workspace, "SaveStatusLabel");
        await WaitUntil(
            () => layer.Images.Count == 2,
            180,
            () => $"Timed out waiting for the generated canvas image. Status: {status.Text}");
        ImageInstanceResource output = layer.Images.Single(item => item.Id != source.Id);
        Require(source.ImagePath == "assets/template-16x8.png", "Running the tool replaced the source image.");
        Require(output.X == source.X && output.Y > source.Y, "Generated output was not placed below the source image.");
        Require(output.ImagePath.StartsWith("agas/tools/", StringComparison.Ordinal),
            "Tile47 tool output was not saved under agas/tools.");
        string outputAbsolute = Path.Combine(projectRoot, output.ImagePath);
        Require(File.Exists(outputAbsolute), "Generated output PNG was not saved in the project.");
        using Image decoded = Image.LoadFromFile(outputAbsolute);
        Require(decoded.GetWidth() == 768 && decoded.GetHeight() == 256,
            $"Generated output size mismatch: {decoded.GetWidth()}x{decoded.GetHeight()}.");

        Require(canvas.Undo(), "Generated output add was not undoable.");
        Require(layer.Images.Count == 1 && layer.Images.Single().Id == source.Id,
            "Undo did not remove only the generated output.");
    }

    private static Image CreateFixture()
    {
        Image image = Image.CreateEmpty(1024, 512, false, Image.Format.Rgba8);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                image.FillRect(
                    new Rect2I(x * 64, y * 64, 64, 64),
                    Color.Color8((byte)(x * 13 + 1), (byte)(y * 29 + 2), (byte)(x * 7 + y * 11 + 3), 255));
            }
        }
        return image;
    }

    private async Task WaitUntil(
        Func<bool> predicate,
        int maximumFrames,
        Func<string>? timeoutMessage = null)
    {
        for (int frame = 0; frame < maximumFrames; frame++)
        {
            if (predicate()) return;
            await Frames(1);
        }
        throw new InvalidOperationException(timeoutMessage?.Invoke() ?? "Timed out waiting for the generated canvas image.");
    }

    private async Task Frames(int count)
    {
        for (int index = 0; index < count; index++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static T FindRequired<T>(Node root, string name) where T : Node
    {
        T? found = FindDescendants<T>(root).FirstOrDefault(node => node.Name == name);
        return found ?? throw new InvalidOperationException($"Missing {typeof(T).Name}: {name}");
    }

    private static System.Collections.Generic.IEnumerable<T> FindDescendants<T>(Node root) where T : Node
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is T typed) yield return typed;
            foreach (T descendant in FindDescendants<T>(child)) yield return descendant;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
