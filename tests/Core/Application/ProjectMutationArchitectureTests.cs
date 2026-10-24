using Xunit;

namespace AppAgasCs.Tests.Core.Application;

public sealed class ProjectMutationArchitectureTests
{
    [Fact]
    public void MainWorkspace_DelegatesSceneAndLayerBusinessMutationsToApplicationService()
    {
        string source = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/UI/MainWorkspace.cs"));
        string layerPromptController = File.ReadAllText(Path.Combine(
            ProjectRoot.Path,
            "src/UI/Controllers/MainWorkspaceLayerPromptController.cs"));
        string projectSessionController = File.ReadAllText(Path.Combine(
            ProjectRoot.Path,
            "src/UI/Controllers/MainWorkspaceProjectSessionController.cs"));

        Assert.Contains("SceneMutationKind.Add", projectSessionController, StringComparison.Ordinal);
        Assert.Contains("SceneMutationKind.Rename", projectSessionController, StringComparison.Ordinal);
        Assert.Contains("SceneMutationKind.Delete", projectSessionController, StringComparison.Ordinal);
        Assert.Contains("SceneMutationKind.SetActive", projectSessionController, StringComparison.Ordinal);
        Assert.Contains("SceneMutationKind.SetReadme", File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/Application/ProjectMutationService.cs")), StringComparison.Ordinal);
        Assert.Contains("new LayerMutationCommand(", layerPromptController, StringComparison.Ordinal);
        Assert.Contains("CanvasUndoProjectMutationPolicy", layerPromptController, StringComparison.Ordinal);
        Assert.DoesNotContain("private void NormalizeSceneOrders", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private void NormalizeLayerOrders", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWorkspace_DelegatesSaveTransactionsAndLayoutGeometry()
    {
        string source = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/UI/MainWorkspace.cs"));
        string saveController = File.ReadAllText(Path.Combine(
            ProjectRoot.Path,
            "src/UI/Controllers/MainWorkspaceSaveController.cs"));
        string chrome = File.ReadAllText(Path.Combine(
            ProjectRoot.Path,
            "src/UI/Controllers/MainWorkspaceDockChromeController.cs"));

        Assert.Contains("MainWorkspaceSaveController _saveController", source, StringComparison.Ordinal);
        Assert.Contains("_projectSaveCoordinator.Save(new ProjectSaveRequest(", saveController, StringComparison.Ordinal);
        Assert.DoesNotContain("new ProjectSaveRequest(", source, StringComparison.Ordinal);
        Assert.Contains("=> _dockChrome.CaptureLayoutFromUi();", source, StringComparison.Ordinal);
        Assert.Contains("=> _dockChrome.ApplyLayoutToUi();", source, StringComparison.Ordinal);
        Assert.Contains("_layoutController.CapturePanelSizes(", chrome, StringComparison.Ordinal);
        Assert.Contains("_layoutController.ApplyOuterSplitLayout(", chrome, StringComparison.Ordinal);
        Assert.DoesNotContain("private AgasPersistenceResult SaveAllLoadedProjectScenes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private static int ClampPaneSize", source, StringComparison.Ordinal);
        Assert.DoesNotContain("private static int ClampPaneSize", chrome, StringComparison.Ordinal);
        Assert.DoesNotContain("_repository.Save(_project", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_repository.Save(_layout", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWorkspace_DelegatesAiBridgeOrchestrationAndRequestMapping()
    {
        string workspace = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/UI/MainWorkspace.cs"));
        string bridge = File.ReadAllText(Path.Combine(
            ProjectRoot.Path,
            "src/UI/Controllers/MainWorkspaceAiGenerationBridge.cs"));
        string preparation = File.ReadAllText(Path.Combine(
            ProjectRoot.Path,
            "src/Application/AiGenerationRequestPreparationService.cs"));
        string applicationController = File.ReadAllText(Path.Combine(
            ProjectRoot.Path,
            "src/UI/Controllers/MainWorkspaceAiApplicationController.cs"));

        Assert.DoesNotContain(", IAgasAiGenerationWorkspaceBridge", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("IAgasAiGenerationWorkspaceBridge.", workspace, StringComparison.Ordinal);
        Assert.Contains("MainWorkspaceAiGenerationBridge : IAgasAiGenerationWorkspaceBridge", bridge, StringComparison.Ordinal);
        Assert.Contains("_aiApplicationController.Prepare(input)", workspace, StringComparison.Ordinal);
        Assert.Contains("_requestPreparation.Prepare(input, capture)", applicationController, StringComparison.Ordinal);
        Assert.Contains("AiGenerationOperation.RegionEdit", preparation, StringComparison.Ordinal);
        Assert.Contains("AiGenerationOperation.ReferenceEdit", preparation, StringComparison.Ordinal);
    }

    [Fact]
    public void McpFacade_DoesNotOwnResourceCollectionMutationOrToolNameSwitches()
    {
        string facade = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/Mcp/AgasMcpProjectFacade.cs"));
        string runtime = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/Mcp/AgasMcpToolRuntime.cs"));
        string aiFacade = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/Mcp/AgasMcpAiFacade.cs"));
        string catalog = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/Mcp/AgasMcpToolCatalog.cs"));

        Assert.DoesNotContain(".Scenes.Add(", facade, StringComparison.Ordinal);
        Assert.DoesNotContain(".Layers.Add(", facade, StringComparison.Ordinal);
        Assert.DoesNotContain(".Images.Add(", facade, StringComparison.Ordinal);
        Assert.DoesNotContain("toolName switch", facade, StringComparison.Ordinal);
        const string compatibilityMarker = "internal static IReadOnlyDictionary<string, AgasMcpPerToolHandler> CreateLegacyCompatibilityHandlers";
        int compatibilityIndex = runtime.IndexOf(compatibilityMarker, StringComparison.Ordinal);
        Assert.True(compatibilityIndex > 0, "Runtime must keep legacy compatibility handlers isolated below the production path.");
        string productionRuntime = runtime[..compatibilityIndex];
        Assert.Contains("IReadOnlyDictionary<string, AgasMcpPerToolHandler> handlers", productionRuntime, StringComparison.Ordinal);
        Assert.Contains("ValidateHandlerParity(_handlers)", productionRuntime, StringComparison.Ordinal);
        Assert.Contains("AgasMcpPerToolHandler handler = _handlers[descriptor.Name]", productionRuntime, StringComparison.Ordinal);
        Assert.DoesNotContain("string.Equals(toolName", productionRuntime, StringComparison.Ordinal);
        Assert.DoesNotContain("switch (descriptor.Family)", productionRuntime, StringComparison.Ordinal);
        Assert.DoesNotContain("toolName switch", aiFacade, StringComparison.Ordinal);
        Assert.Contains("AgasMcpDescriptorHandlerRegistry", File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/Mcp/AgasMcpDescriptorHandlerRegistry.cs")), StringComparison.Ordinal);
        Assert.Contains("IProjectReadModelQuery", facade, StringComparison.Ordinal);
        Assert.Contains("_mutationService.MutateGrid(context, command)", facade, StringComparison.Ordinal);
        Assert.Contains("readModel.Grid", facade, StringComparison.Ordinal);
        Assert.DoesNotContain("context.CachedScenes", facade, StringComparison.Ordinal);
        Assert.Contains("AgasMcpToolDescriptorRegistry.All", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedProjectSession_IsOwnedByApplicationAndBoundByMainCanvasAndMcp()
    {
        string session = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/Application/ProjectSession.cs"));
        string workspace = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/UI/MainWorkspace.cs"));
        string canvas = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/Features/Canvas/CanvasWorkspace.cs"));
        string mcpContext = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/Mcp/AgasMcpProjectContext.cs"));
        string dirtyAggregator = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/Application/WorkspaceDirtyAggregator.cs"));
        string mcpAdapter = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/UI/Controllers/MainWorkspaceMcpSurfaceAdapter.cs"));

        Assert.Contains("class ProjectSession : IProjectMutationSession", session, StringComparison.Ordinal);
        Assert.Contains("internal Dictionary<string, AgasSceneResource> SceneCache", session, StringComparison.Ordinal);
        Assert.DoesNotContain("public Dictionary<string, AgasSceneResource> SceneCache", session, StringComparison.Ordinal);
        Assert.Contains("private readonly ProjectSession _projectSession", workspace, StringComparison.Ordinal);
        Assert.Contains("_canvas.MutationSession = _projectSession", workspace, StringComparison.Ordinal);
        Assert.Contains("() => _projectSession", workspace, StringComparison.Ordinal);
        Assert.Contains("WorkspaceDirtyAggregator _dirtyAggregator", workspace, StringComparison.Ordinal);
        Assert.Contains("MainWorkspaceMcpSurfaceAdapter", workspace, StringComparison.Ordinal);
        Assert.Contains("public IProjectMutationSession? MutationSession", canvas, StringComparison.Ordinal);
        Assert.Contains("CanvasEditApplication _edits", canvas, StringComparison.Ordinal);
        string canvasEdits = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/Features/Canvas/CanvasEditApplication.cs"));
        Assert.Contains("ResolveMutationSession()", canvasEdits, StringComparison.Ordinal);
        Assert.Contains("class CanvasEditApplication", canvasEdits, StringComparison.Ordinal);
        // Canvas is a view, not a second IProjectMutationSession implementation (LSP / ISP).
        Assert.DoesNotContain("IProjectMutationSession, ICanvasInputHost", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("class CanvasLocalMutationSession", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("class CanvasMutationExecutionPolicy", canvas, StringComparison.Ordinal);
        Assert.Contains("class CanvasMutationExecutionPolicy", canvasEdits, StringComparison.Ordinal);
        Assert.Contains("private readonly ProjectSession _session", mcpContext, StringComparison.Ordinal);
        Assert.Contains("class WorkspaceDirtyAggregator", dirtyAggregator, StringComparison.Ordinal);
        Assert.Contains("IAgasMcpProjectContextSource", mcpAdapter, StringComparison.Ordinal);
        Assert.DoesNotContain("IProjectMutationSession", workspace.Split('\n')
            .FirstOrDefault(line => line.Contains("class MainWorkspace")) ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("IAgasMcpProjectContextSource", workspace.Split('\n')
            .FirstOrDefault(line => line.Contains("class MainWorkspace")) ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void CanvasWorkspace_UsesTypedResourcesAndApplicationOwnedPorts()
    {
        string canvas = File.ReadAllText(Path.Combine(ProjectRoot.Path, "src/Features/Canvas/CanvasWorkspace.cs"));
        string canvasEdits = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/Features/Canvas/CanvasEditApplication.cs"));

        Assert.Contains("AgasProjectResource? _projectResource", canvas, StringComparison.Ordinal);
        Assert.Contains("AgasSceneResource? _sceneResource", canvas, StringComparison.Ordinal);
        Assert.Contains("CanvasSelectionHelper _selection", canvas, StringComparison.Ordinal);
        Assert.Contains("CanvasRegionSelectionState _regions", canvas, StringComparison.Ordinal);
        Assert.Contains("ImageInstanceResource? SelectedImageState", canvas, StringComparison.Ordinal);
        Assert.Contains("CanvasInputController _input", canvas, StringComparison.Ordinal);
        Assert.Contains("_input.Handle(@event)", canvas, StringComparison.Ordinal);
        Assert.Contains("CanvasViewportController _viewport", canvas, StringComparison.Ordinal);
        Assert.Contains("CanvasEditApplication _edits", canvas, StringComparison.Ordinal);
        Assert.Contains("CanvasDocumentCommands _document", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("private void HandleMouseButton", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("private void HandleMouseMotion", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("private void HandleKeyInput", canvas, StringComparison.Ordinal);
        // Q/W region interaction state lives on CanvasRegionSelectionState (pure).
        string regionState = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/Features/Canvas/CanvasRegionSelectionState.cs"));
        Assert.Contains("sealed class CanvasRegionSelectionState", regionState, StringComparison.Ordinal);
        Assert.Contains("bool TrySet(", regionState, StringComparison.Ordinal);
        Assert.Contains("static bool IsValid(", regionState, StringComparison.Ordinal);
        Assert.Contains("UpdateFromDrag(", regionState, StringComparison.Ordinal);
        Assert.Contains("_regions.UpdateFromDrag", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("private static bool IsValidRegion", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("private static CanvasGridRegion ExpandRegion", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("leftover W outside the new Q", canvas, StringComparison.Ordinal);
        // Mutation/undo application lives on CanvasEditApplication (SRP).
        Assert.Contains("ProjectMutationService", canvasEdits, StringComparison.Ordinal);
        Assert.DoesNotContain("new ProjectMutationService", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("Stack<ICanvasCommand> _undoStack", canvas, StringComparison.Ordinal);
        Assert.Contains("Stack<ICanvasCommand> _undoStack", canvasEdits, StringComparison.Ordinal);
        // Document image/layer/prompt commands live on CanvasDocumentCommands.
        string documentCommands = File.ReadAllText(Path.Combine(
            ProjectRoot.Path, "src/Features/Canvas/CanvasDocumentCommands.cs"));
        Assert.Contains("class CanvasDocumentCommands", documentCommands, StringComparison.Ordinal);
        Assert.Contains("ImageMutationKind.Delete", documentCommands, StringComparison.Ordinal);
        Assert.Contains("LayerMutationKind.SetVisible", documentCommands, StringComparison.Ordinal);
        Assert.Contains("PromptMutationKind.Add", documentCommands, StringComparison.Ordinal);
        Assert.DoesNotContain("ImageMutationKind.Delete", canvas, StringComparison.Ordinal);
        Assert.Contains("ImageMutationCommand", documentCommands, StringComparison.Ordinal);
        Assert.Contains("PromptMutationCommand", documentCommands, StringComparison.Ordinal);
        Assert.Contains("ViewMutationCommand", canvas, StringComparison.Ordinal);
        Assert.Contains("AppAgasCs.Application.Contracts", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("using AppAgasCs.Mcp", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("BindingFlags", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("FindResourceType", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("GetObject(", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("SetObject(", canvas, StringComparison.Ordinal);
        Assert.Contains("CanvasPatchCaptureService", canvas, StringComparison.Ordinal);
        Assert.Contains("CanvasWorkspaceRenderer", canvas, StringComparison.Ordinal);
        Assert.Contains("CanvasTextureCache", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("GetString(object? target, string propertyName", canvas, StringComparison.Ordinal);
        Assert.DoesNotContain("SetBool(object? target, string propertyName", canvas, StringComparison.Ordinal);

        string commands = string.Join('\n', Directory.GetFiles(
                Path.Combine(ProjectRoot.Path, "src/Features/Canvas/Commands"), "*.cs")
            .Select(File.ReadAllText));
        Assert.DoesNotContain("string propertyName", commands, StringComparison.Ordinal);
        Assert.DoesNotContain("CanvasWorkspace.GetDouble", commands, StringComparison.Ordinal);
    }
}
