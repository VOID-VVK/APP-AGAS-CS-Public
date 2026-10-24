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

public partial class CanvasVisibleLayerMergeAcceptance : Node
{
    private const string Prefix = "[AGAS][CANVAS-VISIBLE-LAYER-MERGE]";
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
        _root = Path.Combine(Path.GetTempPath(), "agas-visible-layer-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        Require(new AgasProjectInitializer().EnsureInitialized(_root).Success, "Fixture initialization failed.");
        SaveSolidPng(Path.Combine(_root, "assets", "ground.png"), Colors.SaddleBrown);
        SaveSolidPng(Path.Combine(_root, "assets", "grass.png"), Colors.ForestGreen);
        SaveSolidPng(Path.Combine(_root, "assets", "hidden.png"), Colors.Red);

        var workspace = new MainWorkspace { CustomMinimumSize = new Vector2(1200, 760) };
        AddChild(workspace);
        workspace.OpenProject(_root);
        await Frames(4);

        CanvasWorkspace canvas = FindRequired<CanvasWorkspace>(workspace, "CanvasWorkspaceHost");
        AgasSceneResource scene = workspace.CurrentScene
            ?? throw new InvalidOperationException("Open project must load a scene.");
        AgasLayerResource ground = scene.Layers.Single(item => item.Id == scene.ActiveLayerId);
        Require(canvas.TryAddImageFromProjectRelativePath("assets/ground.png", Vector2.Zero), "Ground image must add.");
        await Frames(1);

        InvokePrivate(workspace, "AddLayer");
        await Frames(1);
        AgasLayerResource grass = scene.Layers.Single(item => item.Id == scene.ActiveLayerId);
        Require(!ReferenceEquals(ground, grass), "AddLayer must switch to the new layer.");
        Require(canvas.TryAddImageFromProjectRelativePath("assets/grass.png", Vector2.Zero), "Grass image must add.");
        await Frames(1);

        InvokePrivate(workspace, "AddLayer");
        await Frames(1);
        AgasLayerResource hidden = scene.Layers.Single(item => item.Id == scene.ActiveLayerId);
        Require(canvas.TryAddImageFromProjectRelativePath("assets/hidden.png", Vector2.Zero), "Hidden-layer image must add.");
        Require(canvas.SetLayerVisible(hidden, false) && !hidden.Visible, "Hidden source layer must stay excluded.");
        await Frames(1);

        InvokePrivate(workspace, "AddLayer");
        await Frames(1);
        AgasLayerResource composite = scene.Layers.Single(item => item.Id == scene.ActiveLayerId);
        Require(composite.ImagesSnapshot.Count == 0, "Composite layer starts empty.");
        Require(canvas.CanEditCurrentLayer(), "Composite layer must be writable.");

        Require(
            canvas.TryMergeVisibleLayersIntoCurrent(composite.Id, out CanvasVisibleLayerMergeResult merged) && merged.Success,
            "Merge must succeed: " + merged.Message);
        Require(merged.SourceLayerIds.Count == 2, "Hidden layer must not be a merge source.");
        Require(merged.SourceLayerIds.Contains(ground.Id) && merged.SourceLayerIds.Contains(grass.Id),
            "Ground and Grass must be merged.");
        Require(!merged.SourceLayerIds.Contains(hidden.Id) && !merged.SourceLayerIds.Contains(composite.Id),
            "Current and hidden layers must be excluded.");
        Require(composite.ImagesSnapshot.Count == 1, "Merged PNG must land on the current layer.");
        Require(composite.ImagesSnapshot[0].X == 0 && composite.ImagesSnapshot[0].Y == 0,
            "Merged image must keep the source origin.");
        Require(!string.IsNullOrWhiteSpace(merged.RelativePath) && merged.RelativePath.Replace('\\', '/').StartsWith("agas/", StringComparison.Ordinal),
            "Merge output must be an AGAS-generated PNG.");

        PopupMenu menu = FindRequired<PopupMenu>(workspace, "CanvasAiContextMenu");
        canvas._GuiInput(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Right,
            Pressed = true,
            Position = new Vector2(80, 80),
        });
        await Frames(2);
        Require(Enumerable.Range(0, menu.ItemCount).Any(index => menu.GetItemText(index) == "合并可见图层到当前层"),
            "Canvas context menu must expose merge-visible-layers.");
    }

    private static void SaveSolidPng(string path, Color color)
    {
        using Image image = Image.CreateEmpty(64, 64, false, Image.Format.Rgba8);
        image.Fill(color);
        if (image.SavePng(path) != Error.Ok)
        {
            throw new InvalidOperationException("Could not write " + path);
        }
    }

    private static T FindRequired<T>(Node root, string name) where T : Node =>
        root.FindChild(name, owned: false) as T
        ?? throw new InvalidOperationException($"Missing {typeof(T).Name} '{name}'.");

    private static void InvokePrivate(object target, string name, params object[] args)
    {
        MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing method " + name);
        method.Invoke(target, args);
    }

    private async Task Frames(int count)
    {
        for (int i = 0; i < count; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
