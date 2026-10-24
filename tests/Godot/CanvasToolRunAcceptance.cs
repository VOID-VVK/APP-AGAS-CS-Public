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
using AppAgasCs.Features.Tools.Storage;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class CanvasToolRunAcceptance : Node
{
    private const string Prefix = "[AGAS][CANVAS-TOOL-RUN]";
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
        catch (Exception error)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {error}");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("AGAS_HOME", _previousHome);
            if (_root != null) { try { Directory.Delete(_root, true); } catch { } }
            GetTree().Quit(exitCode);
        }
    }

    private async Task RunAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "agas-canvas-tool-" + Guid.NewGuid().ToString("N"));
        string projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        _previousHome = System.Environment.GetEnvironmentVariable("AGAS_HOME");
        System.Environment.SetEnvironmentVariable("AGAS_HOME",
            Path.Combine(_root, "home"));

        Require(new AgasProjectInitializer().EnsureInitialized(projectRoot).Success, "Project initialization failed.");
        Directory.CreateDirectory(Path.Combine(projectRoot, "assets"));
        using (Image image = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8))
        {
            image.Fill(Colors.CornflowerBlue);
            Require(image.SavePng(Path.Combine(projectRoot, "assets", "selected.png")) == Error.Ok,
                "Fixture PNG save failed.");
        }

        var tools = new ToolCatalog();
        const string passThrough = "extends RefCounted\nfunc process(image: Image) -> Dictionary:\n    return {\"image\": image.duplicate()}\n";
        tools.CreateTool("canvas_tool_a", "测试工具 A", passThrough);
        tools.CreateTool("canvas_tool_b", "测试工具 B", passThrough);
        tools.CreateTool("canvas_fail", "测试失败工具",
            "extends RefCounted\nfunc process(_image: Image) -> Dictionary:\n    return {\"error\": \"intentional failure\"}\n");
        tools.CreateChain("canvas_chain", "测试工具链", ["canvas_tool_a", "canvas_tool_b"]);

        var workspace = new MainWorkspace { CustomMinimumSize = new Vector2(1100, 720) };
        AddChild(workspace);
        workspace.OpenProject(projectRoot);
        await Frames(5);
        CanvasWorkspace canvas = FindRequired<CanvasWorkspace>(workspace, "CanvasWorkspaceHost");
        Require(canvas.TryAddImageFromProjectRelativePath("assets/selected.png", new Vector2(48, 48)),
            "Fixture image could not be selected.");
        ImageInstanceResource source = canvas.GetSelectedImage()
            ?? throw new InvalidOperationException("Selected image is missing.");
        AgasLayerResource layer = workspace.CurrentScene!.Layers
            .Single(item => item.Id == workspace.CurrentScene.ActiveLayerId);

        canvas._GuiInput(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Right,
            Pressed = true,
            Position = new Vector2(160, 120),
        });
        await Frames(2);
        PopupMenu rootMenu = FindRequired<PopupMenu>(workspace, "CanvasAiContextMenu");
        PopupMenu menu = FindRequired<PopupMenu>(rootMenu, "CanvasToolMenu");
        Require(rootMenu.Visible, "Canvas right click did not open its context menu.");
        Require(Enumerable.Range(0, rootMenu.ItemCount).Any(index => rootMenu.GetItemText(index) == "工具箱"),
            "Canvas context menu did not offer the toolbox.");
        string[] names = Enumerable.Range(0, menu.ItemCount).Select(menu.GetItemText).ToArray();
        Require(names.Contains("测试工具 A") && names.Any(name => name.Contains("测试工具链", StringComparison.Ordinal)),
            "Tool and chain were not listed in the context menu.");

        int toolIndex = Array.IndexOf(names, "测试工具 A");
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, (long)menu.GetItemId(toolIndex));
        await WaitUntil(() => layer.Images.Count == 2);
        ImageInstanceResource firstOutput = layer.Images.Single(item => item.Id != source.Id);
        Require(source.ImagePath == "assets/selected.png" && firstOutput.Y > source.Y,
            "Tool run replaced its input or did not place output below it.");
        Require(firstOutput.ImagePath.StartsWith("agas/tools/", StringComparison.Ordinal),
            "Tool output was not saved under agas/tools.");
        Require(File.Exists(Path.Combine(projectRoot, firstOutput.ImagePath)),
            "Tool output PNG was not saved.");

        Require(canvas.TrySelectImage(source.Id), "Could not reselect source image for the chain run.");
        ToolboxPanel toolbox = FindRequired<ToolboxPanel>(workspace, "ToolboxPanel");
        Require(toolbox.SelectItem("canvas_chain"), "Chain was missing from the bottom toolbox.");
        FindRequired<Button>(toolbox, "ToolboxRun").EmitSignal(BaseButton.SignalName.Pressed);
        await WaitUntil(() => layer.Images.Count == 3);
        ImageInstanceResource chainOutput = layer.Images.Single(item =>
            item.Id != source.Id && item.Id != firstOutput.Id);
        Require(chainOutput.ImagePath.StartsWith("agas/tools/", StringComparison.Ordinal),
            "Tool-chain output was not saved under agas/tools.");
        Require(File.Exists(Path.Combine(projectRoot, chainOutput.ImagePath)),
            "Chain output PNG was not saved.");

        Require(canvas.Undo() && layer.Images.Count == 2, "Chain output was not one undo step.");
        Require(canvas.Redo() && layer.Images.Count == 3, "Chain output could not be redone.");
        Require(canvas.TrySelectImage(source.Id), "Could not reselect source image for failure check.");
        string[] filesBeforeFailure = Directory.GetFiles(Path.Combine(projectRoot, "agas", "tools"), "*.png");
        Require(toolbox.SelectItem("canvas_fail"), "Failing tool was missing from the toolbox.");
        FindRequired<Button>(toolbox, "ToolboxRun").EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(2);
        Require(layer.Images.Count == 3
                && Directory.GetFiles(Path.Combine(projectRoot, "agas", "tools"), "*.png").Length == filesBeforeFailure.Length,
            "Failed tool run left a canvas image or half-finished PNG.");
        Require(FindRequired<Label>(workspace, "SaveStatusLabel").Text.Contains("TOOL_SCRIPT_FAILED", StringComparison.Ordinal),
            "Failed tool run did not show its actionable error code.");
        Require(typeof(MainWorkspace).GetMethod("SaveProject", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(workspace, null) is true, "Project save failed after tool execution.");
        string sourceId = source.Id;
        string firstOutputId = firstOutput.Id;
        string firstOutputPath = firstOutput.ImagePath;
        string chainOutputId = chainOutput.Id;
        string chainOutputPath = chainOutput.ImagePath;
        workspace.QueueFree();
        await Frames(3);
        var reopened = new MainWorkspace { CustomMinimumSize = new Vector2(1100, 720) };
        AddChild(reopened);
        reopened.OpenProject(projectRoot);
        await Frames(5);
        ImageInstanceResource[] persisted = reopened.CurrentScene!.LayersSnapshot
            .SelectMany(item => item.ImagesSnapshot)
            .Where(item => item.Id == sourceId || item.Id == firstOutputId || item.Id == chainOutputId)
            .ToArray();
        Require(persisted.Length == 3, "Source, tool output, or chain output was lost after reopen.");
        Require(persisted.Single(item => item.Id == sourceId).ImagePath == "assets/selected.png",
            "Source image path changed after reopen.");
        Require(persisted.Single(item => item.Id == firstOutputId).ImagePath == firstOutputPath
                && persisted.Single(item => item.Id == chainOutputId).ImagePath == chainOutputPath,
            "Generated image paths changed after reopen.");
        reopened.QueueFree();
        await Frames(2);
    }

    private async Task WaitUntil(Func<bool> predicate)
    {
        for (int index = 0; index < 180; index++)
        {
            if (predicate()) return;
            await Frames(1);
        }
        throw new InvalidOperationException("Timed out waiting for tool execution.");
    }

    private async Task Frames(int count)
    {
        for (int index = 0; index < count; index++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static T FindRequired<T>(Node root, string name) where T : Node =>
        root.FindChild(name, recursive: true, owned: false) as T
        ?? throw new InvalidOperationException($"Missing {typeof(T).Name}: {name}");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
