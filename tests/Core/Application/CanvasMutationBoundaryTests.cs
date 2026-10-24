using System.Reflection;
using Agas.Features.Canvas;
using AppAgasCs.Core.Resources;
using AppAgasCs.Tests.Core;
using Godot;
using Xunit;

namespace AppAgasCs.Core.Tests.Application;

public sealed class CanvasMutationBoundaryTests
{
    [Fact]
    public void CanvasWorkspace_DoesNotReturnMutableResourcesFromPublicDeclaredMethods()
    {
        MethodInfo[] publicMethods = typeof(CanvasWorkspace).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(publicMethods, method => typeof(Resource).IsAssignableFrom(method.ReturnType));
        Assert.DoesNotContain(publicMethods, method => method.Name is
            "GetProjectResource" or "GetSceneResource" or "GetSelectedImage");
    }

    [Fact]
    public void CurrentLayerAndDragPreview_UseControlledMutationBoundaries()
    {
        string canvas = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/Features/Canvas/CanvasWorkspace.cs"));
        string documentCommands = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/Features/Canvas/CanvasDocumentCommands.cs"));
        string input = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/Features/Canvas/CanvasInputController.cs"));

        Assert.Contains("LayerMutationKind.SetCurrent", documentCommands, StringComparison.Ordinal);
        Assert.Contains(
            "LayersSnapshot.Any(candidate => ReferenceEquals(candidate, layer))",
            documentCommands,
            StringComparison.Ordinal);
        Assert.Contains("_document.SetCurrentLayer", canvas, StringComparison.Ordinal);
        Assert.Contains("CommitImageMovePreview", input, StringComparison.Ordinal);
        Assert.DoesNotContain("SetImagePosition", input, StringComparison.Ordinal);
        Assert.DoesNotContain("imageInstance.X = position.X", input, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistedGridSettings_UseOneApplicationMutationBoundary()
    {
        string contracts = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/Application/ProjectMutationContracts.cs"));
        string service = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/Application/ProjectMutationService.cs"));
        string canvas = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/Features/Canvas/CanvasWorkspace.cs"));
        string workspace = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/MainWorkspace.cs"));

        Assert.Contains("enum GridMutationKind", contracts, StringComparison.Ordinal);
        Assert.Contains("record GridMutationCommand", contracts, StringComparison.Ordinal);
        Assert.Contains("ProjectMutationResult MutateGrid", service, StringComparison.Ordinal);
        string canvasEdits = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/Features/Canvas/CanvasEditApplication.cs"));
        Assert.Contains("ProjectMutationResult MutateGrid", canvas, StringComparison.Ordinal);
        Assert.Contains("MutateGrid(", canvasEdits, StringComparison.Ordinal);
        Assert.Contains("CanvasEditScope.Grid", canvasEdits, StringComparison.Ordinal);
        Assert.Contains("_document.SetGridSizePx", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("grid.SizePx =", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("grid.SnapEnabled =", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("grid.Visible =", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("_project.Grid.SizePx =", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("_project.Grid.SnapEnabled =", workspace, StringComparison.Ordinal);
        Assert.Contains("_canvas?.SetGridSizePx(sizePx)", workspace, StringComparison.Ordinal);
        Assert.Contains("_canvas?.SetSnapEnabled(enabled)", workspace, StringComparison.Ordinal);
    }

    [Fact]
    public void CanvasWorkspace_DelegatesDropAndImportPathProcessing()
    {
        string canvas = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/Features/Canvas/CanvasWorkspace.cs"));
        string importPaths = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/Features/Canvas/CanvasImageImportPathService.cs"));
        string dropHandlers = canvas[
            canvas.IndexOf("public override bool _CanDropData(", StringComparison.Ordinal)..
            canvas.IndexOf("internal void SetWorkspace(", StringComparison.Ordinal)];

        Assert.Contains("CanvasImageImportPathService _imageImportPaths", canvas, StringComparison.Ordinal);
        Assert.Contains("_imageImportPaths.ResolveImportPath", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("SupportedImageExtensions", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectImagePathResolver", dropHandlers, StringComparison.Ordinal);
        Assert.DoesNotContain("private static IEnumerable<string> ExtractDroppedPaths", canvas, StringComparison.Ordinal);
        Assert.Contains("internal sealed class CanvasImageImportPathService", importPaths, StringComparison.Ordinal);
        Assert.Contains("ProjectImagePathResolver _resolver", importPaths, StringComparison.Ordinal);
        Assert.Contains("internal IEnumerable<string> ExtractDroppedPaths", importPaths, StringComparison.Ordinal);
    }
}
