using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AGAS.UI;
using Agas.Features.Canvas;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Settings;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using Godot;

public partial class McpSelectionAcceptance : Node
{
    private const string Prefix = "[AGAS][MCP-SELECTION]";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private string? _root;
    private string? _home;
    private string? _previousHome;

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
            System.Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, _previousHome);
            Cleanup(_root);
            Cleanup(_home);
            GetTree().Quit(exitCode);
        }
    }

    private async System.Threading.Tasks.Task RunAll()
    {
        _root = Path.Combine(Path.GetTempPath(), "agas-mcp-selection-" + Guid.NewGuid().ToString("N"));
        _home = Path.Combine(Path.GetTempPath(), "agas-mcp-selection-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        Directory.CreateDirectory(_home);
        SaveImage(Path.Combine(_root, "assets", "selected.png"));
        _previousHome = System.Environment.GetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable);
        System.Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, _home);

        var dirtyState = new AgasMcpDirtyState();
        var workspace = new MainWorkspace { CustomMinimumSize = new Vector2(1200, 760) };
        workspace.SetMcpDirtyState(dirtyState);
        AddChild(workspace);
        await Frames(2);
        workspace.OpenProject(_root);
        await Frames(4);
        Require(workspace.TryBindMcpDirtyState(out string bindError), "MCP dirty state bind failed: " + bindError);

        CanvasWorkspace canvas = FindRequired<CanvasWorkspace>(workspace, "CanvasWorkspaceHost");
        Label imageStatus = FindRequired<Label>(workspace, "CanvasSelectionStatusLabel");
        Label contextStatus = FindRequired<Label>(workspace, "CanvasContextStatusLabel");
        AgasSceneResource scene = workspace.CurrentScene ?? throw new InvalidOperationException("Current scene missing.");
        AgasLayerResource layer = scene.Layers[0];
        var image = new ImageInstanceResource
        {
            Id = "image-selection",
            ImagePath = "assets/selected.png",
            X = 48,
            Y = 96,
        };
        layer.Images.Add(image);
        canvas.RefreshCanvasState();

        var provider = new AgasMcpProjectContextProvider();
        provider.RegisterSource(workspace.McpProjectContextSource);
        var facade = new AgasMcpProjectFacade(provider, dirtyState);
        var q = new AgasMcpGridRegionDto(-2, 3, 4, 5);
        var w = new AgasMcpGridRegionDto(-1, 4, 2, 1);

        AgasMcpToolResponse setRegions = facade.SelectionSet(new AgasSelectionSetInput("setRegions", scene.Id, q, w));
        Require(setRegions.Ok && !setRegions.Dirty && setRegions.Saved, "setRegions must be successful and non-dirty.");
        Require(canvas.SelectionRegion == new CanvasGridRegion(-2, 3, 4, 5), "Canvas Q region did not update.");
        Require(canvas.ReplacementRegion == new CanvasGridRegion(-1, 4, 2, 1), "Canvas W region did not update.");
        Require(canvas.ActiveRegionTool == CanvasRegionTool.Replacement, "W tool should be active after setting Q/W.");
        Require(contextStatus.Text.Contains("Q 发送/写回：(-2,3) 4×5", StringComparison.Ordinal)
                && contextStatus.Text.Contains("W 透明洞：(-1,4) 2×1", StringComparison.Ordinal),
            "Workspace status did not refresh from Canvas region signals.");

        JsonElement state = Data(facade.SelectionGet(new AgasSelectionGetInput()));
        Require(state.GetProperty("sceneId").GetString() == scene.Id, "selection.get scene mismatch.");
        Require(state.GetProperty("activeRegionTool").GetString() == "Replacement", "selection.get tool mismatch.");
        Require(state.GetProperty("effectiveGenerationRegion").GetProperty("width").GetInt32() == 2, "selection.get effective region mismatch.");
        Require(!dirtyState.IsDirty && !workspace.HasUnsavedChanges, "Selection-only changes must not mark either dirty model.");
        workspace.RefreshContextUi();
        Require(canvas.SelectionRegion == new CanvasGridRegion(-2, 3, 4, 5)
                && canvas.ReplacementRegion == new CanvasGridRegion(-1, 4, 2, 1),
            "Same-scene MCP UI refresh must not clear the live Q/W state.");

        AgasMcpToolResponse viewSet = facade.ViewSet(new AgasViewSetInput(scene.Id, 321.5, -88.25, 2.5));
        Require(viewSet.Ok, viewSet.Error?.Message ?? "view.set failed.");
        Require(canvas.GetCameraWorld().IsEqualApprox(new Vector2(321.5f, -88.25f))
                && Mathf.IsEqualApprox(canvas.GetZoom(), 2.5f),
            "Same-scene MCP view.set must reload live camera state from Scene.ViewState.");
        Require(canvas.SelectionRegion == new CanvasGridRegion(-2, 3, 4, 5)
                && canvas.ReplacementRegion == new CanvasGridRegion(-1, 4, 2, 1),
            "Reloading resource-backed view state must preserve live Q/W regions.");

        AgasMcpToolResponse selectImage = facade.SelectionSet(new AgasSelectionSetInput(
            "selectImage",
            scene.Id,
            LayerId: layer.Id,
            ImageId: image.Id));
        Require(selectImage.Ok && ReferenceEquals(canvas.GetSelectedImage(), image), "selectImage did not select the live image instance.");
        Require(imageStatus.Text.Contains("selected.png", StringComparison.Ordinal), "Image selection signal did not refresh UI status.");
        state = Data(facade.SelectionGet(new AgasSelectionGetInput()));
        Require(state.GetProperty("selectedImageId").GetString() == image.Id
                && state.GetProperty("selectedLayerId").GetString() == layer.Id,
            "selection.get selected image ownership mismatch.");

        AgasMcpToolResponse clearImage = facade.SelectionSet(new AgasSelectionSetInput("clearImageSelection", scene.Id));
        Require(clearImage.Ok && canvas.GetSelectedImage() == null, "clearImageSelection failed.");
        Require(imageStatus.Text.Contains("未选中", StringComparison.Ordinal), "Clear image signal did not refresh UI status.");

        layer.Locked = true;
        AgasMcpToolResponse locked = facade.SelectionSet(new AgasSelectionSetInput(
            "selectImage",
            scene.Id,
            LayerId: layer.Id,
            ImageId: image.Id));
        Require(!locked.Ok && locked.Error?.Code == AgasMcpErrorCodes.LayerNotEditable, "Locked layer must use AGAS_LAYER_NOT_EDITABLE.");
        layer.Locked = false;
        layer.Visible = false;
        AgasMcpToolResponse hidden = facade.SelectionSet(new AgasSelectionSetInput(
            "selectImage",
            scene.Id,
            LayerId: layer.Id,
            ImageId: image.Id));
        Require(!hidden.Ok && hidden.Error?.Code == AgasMcpErrorCodes.LayerNotEditable, "Hidden layer must use AGAS_LAYER_NOT_EDITABLE.");
        layer.Visible = true;

        AgasMcpToolResponse invalidShape = facade.SelectionSet(new AgasSelectionSetInput(
            "setRegions",
            scene.Id,
            new AgasMcpGridRegionDto(0, 0, 0, 1)));
        Require(!invalidShape.Ok && invalidShape.Error?.Code == AgasMcpErrorCodes.SelectionInvalid,
            "Non-positive region dimensions must use AGAS_SELECTION_INVALID.");

        AgasMcpToolResponse invalidEndpoint = facade.SelectionSet(new AgasSelectionSetInput(
            "setRegions",
            scene.Id,
            new AgasMcpGridRegionDto(int.MaxValue, 0, 2, 1)));
        Require(!invalidEndpoint.Ok && invalidEndpoint.Error?.Code == AgasMcpErrorCodes.SelectionInvalid,
            "Overflowing region endpoint must use AGAS_SELECTION_INVALID.");
        Require(canvas.SelectionRegion == new CanvasGridRegion(-2, 3, 4, 5), "Rejected region must not change live Q.");

        AgasMcpToolResponse clearRegions = facade.SelectionSet(new AgasSelectionSetInput("clearRegions", scene.Id));
        Require(clearRegions.Ok
                && canvas.SelectionRegion == null
                && canvas.ReplacementRegion == null
                && canvas.ActiveRegionTool == CanvasRegionTool.Edit,
            "clearRegions must clear Q/W and restore E.");

        workspace.QueueFree();
        await Frames(2);
    }

    private async System.Threading.Tasks.Task Frames(int count)
    {
        for (int i = 0; i < count; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static JsonElement Data(AgasMcpToolResponse response)
    {
        Require(response.Ok, response.Error?.Message ?? "MCP response failed.");
        return JsonSerializer.SerializeToElement(response.Data, JsonOptions);
    }

    private static T FindRequired<T>(Node root, string name) where T : Node
    {
        return root.FindChild(name, recursive: true, owned: false) as T
            ?? throw new InvalidOperationException($"Missing node '{name}' ({typeof(T).Name}).");
    }

    private static void SaveImage(string path)
    {
        using Image image = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
        image.Fill(Colors.CornflowerBlue);
        Error error = image.SavePng(path);
        if (error != Error.Ok) throw new InvalidOperationException("Fixture image save failed: " + error);
    }

    private static void Cleanup(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { Directory.Delete(path, recursive: true); } catch { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
