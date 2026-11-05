#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AGAS.UI;
using AGAS.UI.Controllers;
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
        const string incrementRed = "extends RefCounted\nfunc process(image: Image) -> Dictionary:\n    var result = image.duplicate()\n    var pixel = result.get_pixel(0, 0)\n    pixel.r += 0.1\n    result.set_pixel(0, 0, pixel)\n    return {\"image\": result}\n";
        const string incrementGreen = "extends RefCounted\nfunc process(image: Image) -> Dictionary:\n    var result = image.duplicate()\n    var pixel = result.get_pixel(0, 0)\n    pixel.g += 0.1\n    result.set_pixel(0, 0, pixel)\n    return {\"image\": result}\n";
        tools.CreateTool("canvas_tool_a", "测试工具 A", incrementRed);
        tools.CreateTool("canvas_tool_b", "测试工具 B", incrementGreen);
        tools.CreateTool("canvas_fail", "测试失败工具",
            "extends RefCounted\nfunc process(_image: Image) -> Dictionary:\n    return {\"error\": \"intentional failure\"}\n");

        bool appliedSingleTool = false;
        var guardController = new CanvasToolRunController(
            () => null,
            (_, _) => appliedSingleTool = true,
            tools);
        CanvasToolRunResult blocked = guardController.Run(
            new CanvasToolSelection("canvas_tool_a", "测试工具 A", false, 1));
        Require(!blocked.Success && blocked.Code == CanvasToolRunErrorCodes.ChainRequired && !appliedSingleTool,
            "The canvas controller must reject single-tool dispatch before execution.");

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
        Require(names.Length == 1 && menu.IsItemDisabled(0)
                && names[0].Contains("尚无工具链", StringComparison.Ordinal),
            "The empty chain menu must be disabled and must not list individual tools.");
        rootMenu.Hide();
        ToolboxPanel toolbox = FindRequired<ToolboxPanel>(workspace, "ToolboxPanel");
        FindRequired<Button>(workspace, "CanvasToolboxMenuButton").EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(2);
        Require(!rootMenu.Visible && toolbox.IsVisibleInTree()
                && FindRequired<TabContainer>(workspace, "BottomDockTabs").CurrentTab == toolbox.GetIndex(),
            $"The toolbar toolbox action must open management without offering execution (menu={rootMenu.Visible}, toolbox={toolbox.IsVisibleInTree()}, current={FindRequired<TabContainer>(workspace, "BottomDockTabs").CurrentTab}, expected={toolbox.GetIndex()}).");
        Require(toolbox.FindChild("ToolboxRun", recursive: true, owned: false) == null,
            "The toolbox management panel must not expose an execution button.");

        tools.CreateChain("canvas_single_chain", "测试单步工具链", ["canvas_tool_a"]);
        tools.CreateChain("canvas_chain", "测试工具链", ["canvas_tool_a", "canvas_tool_b", "canvas_tool_a"]);
        tools.CreateChain("canvas_fail_chain", "测试失败工具链", ["canvas_fail"]);
        await OpenMenu(canvas);
        names = Enumerable.Range(0, menu.ItemCount).Select(menu.GetItemText).ToArray();
        Require(names.Length == 3 && !names.Contains("测试工具 A") && !names.Contains("测试工具 B")
                && !names.Contains("测试失败工具") && names.Contains("测试工具链"),
            "Only user-assembled tool chains must appear in the canvas toolbox menu.");

        int toolIndex = Array.IndexOf(names, "测试单步工具链");
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, (long)menu.GetItemId(toolIndex));
        await WaitUntil(() => layer.Images.Count == 2);
        ImageInstanceResource firstOutput = layer.Images.Single(item => item.Id != source.Id);
        Require(source.ImagePath == "assets/selected.png" && firstOutput.Y > source.Y,
            "Tool run replaced its input or did not place output below it.");
        Require(firstOutput.ImagePath == "agas/tools/Tools 1.png",
            "The first tool output must use the shared Tools 1.png sequence.");
        Require(File.Exists(Path.Combine(projectRoot, firstOutput.ImagePath)),
            "Tool output PNG was not saved.");
        using (Image singleResult = Image.LoadFromFile(Path.Combine(projectRoot, firstOutput.ImagePath)))
        {
            Color pixel = singleResult.GetPixel(0, 0);
            Require(Mathf.Abs(pixel.R - Colors.CornflowerBlue.R - 0.1f) < 0.01f
                    && Mathf.Abs(pixel.G - Colors.CornflowerBlue.G) < 0.01f,
                "The single-step chain did not execute exactly its selected tool.");
        }

        Require(canvas.TrySelectImage(source.Id), "Could not reselect source image for the chain run.");
        await OpenMenu(canvas);
        int chainIndex = Enumerable.Range(0, menu.ItemCount).Single(index => menu.GetItemText(index) == "测试工具链");
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, (long)menu.GetItemId(chainIndex));
        await WaitUntil(() => layer.Images.Count == 3);
        ImageInstanceResource chainOutput = layer.Images.Single(item =>
            item.Id != source.Id && item.Id != firstOutput.Id);
        Require(chainOutput.ImagePath == "agas/tools/Tools 2.png",
            "A different tool chain must continue the same Tools sequence.");
        Require(File.Exists(Path.Combine(projectRoot, chainOutput.ImagePath)),
            "Chain output PNG was not saved.");
        using (Image repeatedResult = Image.LoadFromFile(Path.Combine(projectRoot, chainOutput.ImagePath)))
        {
            Color pixel = repeatedResult.GetPixel(0, 0);
            Require(Mathf.Abs(pixel.R - Colors.CornflowerBlue.R - 0.2f) < 0.01f
                    && Mathf.Abs(pixel.G - Colors.CornflowerBlue.G - 0.1f) < 0.01f,
                "The repeated-step chain must execute each occurrence and preserve separate tool effects.");
        }

        Require(canvas.Undo() && layer.Images.Count == 2, "Chain output was not one undo step.");
        Require(canvas.Redo() && layer.Images.Count == 3, "Chain output could not be redone.");
        Require(canvas.TrySelectImage(source.Id), "Could not reselect source image for failure check.");
        string[] filesBeforeFailure = Directory.GetFiles(Path.Combine(projectRoot, "agas", "tools"), "*.png");
        await OpenMenu(canvas);
        int failIndex = Enumerable.Range(0, menu.ItemCount).Single(index => menu.GetItemText(index) == "测试失败工具链");
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, (long)menu.GetItemId(failIndex));
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
        CanvasWorkspace reopenedCanvas = FindRequired<CanvasWorkspace>(reopened, "CanvasWorkspaceHost");
        Require(reopenedCanvas.TrySelectImage(sourceId), "Could not reselect source after reopening.");
        await OpenMenu(reopenedCanvas);
        PopupMenu reopenedMenu = FindRequired<PopupMenu>(reopened, "CanvasToolMenu");
        int reopenedChain = Enumerable.Range(0, reopenedMenu.ItemCount)
            .Single(index => reopenedMenu.GetItemText(index) == "测试单步工具链");
        reopenedMenu.EmitSignal(PopupMenu.SignalName.IdPressed, (long)reopenedMenu.GetItemId(reopenedChain));
        await WaitUntil(() => reopened.CurrentScene!.LayersSnapshot.SelectMany(item => item.ImagesSnapshot)
            .Any(item => item.ImagePath == "agas/tools/Tools 3.png"));
        Require(File.Exists(Path.Combine(projectRoot, "agas/tools/Tools 3.png")),
            "Reopening the project must continue at Tools 3 without reusing an existing filename.");
        Require(Directory.GetFiles(Path.Combine(projectRoot, "agas/tools"), "*.reserve").Length == 0,
            "Successful and failed runs must release their numbering reservations.");
        reopened.QueueFree();
        await Frames(2);
    }

    private async Task OpenMenu(CanvasWorkspace canvas)
    {
        canvas._GuiInput(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Right,
            Pressed = true,
            Position = new Vector2(160, 120),
        });
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
