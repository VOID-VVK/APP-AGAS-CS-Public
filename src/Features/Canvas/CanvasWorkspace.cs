#nullable enable

using Godot;
using Agas.Features.Commands;
using AppAgasCs.Application;
using AppAgasCs.Application.Contracts;
using AppAgasCs.Core.Resources;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Agas.Features.Canvas;

/// <summary>
/// MVP canvas workspace for AGAS.
///
/// Uses the typed Core project model and application-owned mutation/selection ports.
/// </summary>
[GlobalClass]
public partial class CanvasWorkspace : Control, ICanvasSelectionWorkspaceBridge, ICanvasLayerMergeBridge, ICanvasInputHost, ICanvasDocumentHost
{
    private const int FallbackGridSizePx = 48;
    private const float DefaultMinZoom = 0.10f;
    private const float DefaultMaxZoom = 8.0f;
    private const float FitVisibleOpacityEpsilon = 0.001f;
    private static readonly StringName CanvasChangedSignal = new("CanvasChanged");
    private static readonly StringName SelectionChangedSignal = new("SelectionChanged");
    private static readonly StringName UndoRedoAvailabilityChangedSignal = new("UndoRedoAvailabilityChanged");
    private static readonly StringName CanvasStateRefreshRequestedSignal = new("CanvasStateRefreshRequested");
    private static readonly StringName RegionStateChangedSignal = new("RegionStateChanged");
    private static readonly StringName ViewChangedSignal = new("ViewChanged");

    private readonly CanvasTextureCache _textureCache = new();
    private readonly CanvasPatchCaptureService _patchCapture = new();
    private readonly CanvasWorkspaceRenderer _renderer = new();
    private readonly CanvasViewportController _viewport = new();
    private readonly CanvasSelectionHelper _selection = new();
    private readonly CanvasRegionSelectionState _regions = new();
    private readonly CanvasInputController _input;
    private readonly CanvasImageImportPathService _imageImportPaths = new();
    private readonly CanvasEditApplication _edits;
    private readonly CanvasDocumentCommands _document;

    private AgasProjectResource? _projectResource;
    private AgasSceneResource? _sceneResource;
    private string _projectRoot = string.Empty;
    private ImageInstanceResource? _movePreviewImage;
    private Vector2 _movePreviewPosition;

    private ImageInstanceResource? _clipboardImagePrototype;
    private readonly System.Collections.Generic.Dictionary<int, CanvasAiGenerationOverlay> _aiGenerationOverlays = new();

    public CanvasWorkspace()
    {
        _input = new CanvasInputController(this);
        _edits = new CanvasEditApplication(
            CanMutate,
            (clearRedo, pushCanvasSignal) => NotifyChanged(clearRedo, pushCanvasSignal),
            RefreshCanvasState,
            ReconcileSelectionAfterLayerMutation);
        _document = new CanvasDocumentCommands(this);
    }

    // --- ICanvasDocumentHost -------------------------------------------------
    string ICanvasDocumentHost.ProjectRoot => _projectRoot;
    AgasProjectResource? ICanvasDocumentHost.Project => _projectResource;
    AgasSceneResource? ICanvasDocumentHost.Scene => _sceneResource;
    ImageInstanceResource? ICanvasDocumentHost.SelectedImage => SelectedImageState;
    ImageInstanceResource? ICanvasDocumentHost.ClipboardPrototype
    {
        get => _clipboardImagePrototype;
        set => _clipboardImagePrototype = value;
    }

    bool ICanvasDocumentHost.CanMutate(string action) => CanMutate(action);
    bool ICanvasDocumentHost.CanEditCurrentLayer() => CanEditCurrentLayer();
    AgasLayerResource? ICanvasDocumentHost.GetCurrentLayer() => GetCurrentLayer();
    IEnumerable<AgasLayerResource> ICanvasDocumentHost.GetOrderedLayers() => GetOrderedLayers();
    IEnumerable<ImageInstanceResource> ICanvasDocumentHost.GetOrderedImages(AgasLayerResource layer) =>
        GetOrderedImages(layer);
    IEnumerable<PromptFragmentResource> ICanvasDocumentHost.GetPromptFragmentsOrdered(AgasLayerResource layer) =>
        GetPromptFragmentsOrdered(layer);
    AgasLayerResource? ICanvasDocumentHost.FindLayerContainingImage(ImageInstanceResource? image) =>
        FindLayerContainingImage(image);
    AgasLayerResource? ICanvasDocumentHost.FindLayerContainingPromptFragment(PromptFragmentResource? fragment) =>
        FindLayerContainingPromptFragment(fragment);
    bool ICanvasDocumentHost.CanEditPromptFragmentInLayer(AgasLayerResource? layer, PromptFragmentResource? fragment) =>
        CanEditPromptFragmentInLayer(layer, fragment);
    bool ICanvasDocumentHost.CanEditLayerContent(AgasLayerResource? layer) => CanEditLayerContent(layer);
    Vector2 ICanvasDocumentHost.GetImagePosition(ImageInstanceResource image) => GetImagePosition(image);
    Vector2 ICanvasDocumentHost.SnapWorldPosition(Vector2 world) => SnapWorldPosition(world);
    int ICanvasDocumentHost.GetGridSizePx() => GetGridSizePx();
    ProjectMutationResult ICanvasDocumentHost.MutateImage(ImageMutationCommand command, bool undoable) =>
        MutateImage(command, undoable);
    ProjectMutationResult ICanvasDocumentHost.MutateGrid(GridMutationCommand command, bool undoable) =>
        MutateGrid(command, undoable);
    ProjectMutationResult ICanvasDocumentHost.MutateLayer(LayerMutationCommand command, bool undoable) =>
        MutateLayer(command, undoable);
    ProjectMutationResult ICanvasDocumentHost.MutatePrompt(PromptMutationCommand command, bool undoable) =>
        MutatePrompt(command, undoable);
    ProjectImagePathResult ICanvasDocumentHost.ResolveImportPath(string path) =>
        _imageImportPaths.ResolveImportPath(_projectRoot, path);
    void ICanvasDocumentHost.ClearTextureCache() => _textureCache.Clear();
    void ICanvasDocumentHost.NotifyChanged(bool clearRedo, bool pushCanvasSignal) =>
        NotifyChanged(clearRedo, pushCanvasSignal);
    void ICanvasDocumentHost.ClearSelection() => ClearSelection();
    void ICanvasDocumentHost.SelectImage(ImageInstanceResource? image) => SelectImage(image);
    void ICanvasDocumentHost.ReconcileSelectionAfterLayerMutation() => ReconcileSelectionAfterLayerMutation();

    private ImageInstanceResource? SelectedImageState => _selection.SelectedImage;

    [Signal]
    public delegate void CanvasChangedEventHandler();

    [Signal]
    public delegate void SelectionChangedEventHandler();

    [Signal]
    public delegate void UndoRedoAvailabilityChangedEventHandler(bool canUndo, bool canRedo);

    [Signal]
    public delegate void CanvasStateRefreshRequestedEventHandler();

    [Signal]
    public delegate void RegionStateChangedEventHandler();

    [Signal]
    public delegate void ViewChangedEventHandler(float zoom);

    public Action? ContextMenuRequested { get; set; }

    /// <summary>
    /// Optional project-level gate invoked before every persistent canvas mutation.
    /// Returning false rejects the operation before resource state is changed.
    /// </summary>
    public Func<string, bool>? MutationGuard { get; set; }

    /// <summary>
    /// Shared project session for resource mutations (full multi-scene cache/load semantics).
    /// MainWorkspace binds this in production. When null, <see cref="CanvasEditApplication"/>
    /// keeps a private <see cref="ProjectSession"/> in sync with local project/scene pointers.
    /// </summary>
    public IProjectMutationSession? MutationSession
    {
        get => _edits.MutationSession;
        set => _edits.MutationSession = value;
    }

    [Export]
    public Color GridMajorColor { get; set; } = new(0.30f, 0.36f, 0.46f, 0.52f);

    [Export]
    public Color GridMinorColor { get; set; } = new(0.30f, 0.36f, 0.46f, 0.22f);

    [Export]
    public Color BackgroundColor { get; set; } = new(0.075f, 0.085f, 0.105f, 1.0f);

    [Export]
    public Color SelectionColor { get; set; } = new(0.22f, 0.60f, 1.0f, 1.0f);

    [Export]
    public Color SelectionRegionColor { get; set; } = Colors.Yellow;

    [Export]
    public Color ReplacementRegionColor { get; set; } = Colors.Red;

    [Export]
    public float MinZoom { get; set; } = DefaultMinZoom;

    [Export]
    public float MaxZoom { get; set; } = DefaultMaxZoom;

    /// <summary>
    /// Controls whether viewport navigation is written back to the scene resource.
    /// Read-only projects keep this disabled while still allowing local zoom and pan.
    /// </summary>
    [Export]
    public bool ViewStatePersistenceEnabled { get; set; } = true;

    [Export]
    public bool LeftButtonDragsEmptyCanvas { get; set; } = true;

    public CanvasRegionTool ActiveRegionTool => _regions.ActiveTool;

    public CanvasGridRegion? SelectionRegion => _regions.SelectionRegion;

    public CanvasGridRegion? ReplacementRegion => _regions.ReplacementRegion;

    public Rect2? SelectionRegionWorldRect => SelectionRegion?.ToWorldRect(GetGridSizePx());

    public Rect2? ReplacementRegionWorldRect => ReplacementRegion?.ToWorldRect(GetGridSizePx());

    public CanvasGridRegion? EffectiveGenerationRegion => _regions.EffectiveGenerationRegion;

    public IReadOnlyList<CanvasAiGenerationOverlay> AiGenerationOverlays => Array.AsReadOnly(
        _aiGenerationOverlays.Values
            .OrderBy(overlay => overlay.RunId)
            .ToArray());

    public IReadOnlyList<Rect2> AiGenerationOverlayScreenRects => Array.AsReadOnly(
        AiGenerationOverlays
            .Select(overlay => ToScreenRect(overlay.WorldRect))
            .ToArray());

    public CanvasAiGenerationOverlay? AiGenerationOverlay => AiGenerationOverlays.FirstOrDefault();

    public Rect2? AiGenerationOverlayScreenRect => AiGenerationOverlay is CanvasAiGenerationOverlay overlay
        ? ToScreenRect(overlay.WorldRect)
        : null;

    public Vector2 LastContextMenuPosition { get; private set; }

    /// <summary>
    /// Captures immutable Q/W provider input from the current scene. Q is the
    /// required reference region; W is optional and defaults to Q.
    /// </summary>
    public CanvasPatchCapture CaptureCurrentPatch(string? targetImageId = null) =>
        CaptureCurrentPatch(targetImageId, CanvasPatchLayerScope.AllVisibleLayers, null);

    public CanvasPatchCapture CaptureCurrentPatch(
        string? targetImageId,
        CanvasPatchLayerScope layerScope,
        string? currentLayerId)
    {
        CanvasGridRegion effectiveReplacement = EffectiveGenerationRegion
            ?? throw new InvalidOperationException("A Q reference region is required.");
        return CaptureCurrentPatch(targetImageId, layerScope, currentLayerId, effectiveReplacement);
    }

    public CanvasPatchCapture CaptureCurrentPatch(
        string? targetImageId,
        CanvasPatchLayerScope layerScope,
        string? currentLayerId,
        CanvasGridRegion effectiveReplacement)
    {
        if (!SelectionRegion.HasValue)
        {
            throw new InvalidOperationException("A Q reference region is required.");
        }

        return CapturePatch(
            targetImageId,
            layerScope,
            currentLayerId,
            SelectionRegion.Value,
            effectiveReplacement,
            GetGridSizePx(),
            ReplacementRegion.HasValue
                ? CanvasPatchGenerationMode.RegionReplacement
                : CanvasPatchGenerationMode.FullRegionGeneration);
    }

    public CanvasPatchCapture CapturePatch(
        string? targetImageId,
        CanvasPatchLayerScope layerScope,
        string? currentLayerId,
        CanvasGridRegion referenceRegion,
        CanvasGridRegion replacementRegion,
        int gridSizePx,
        CanvasPatchGenerationMode generationMode)
    {
        if (_sceneResource == null)
        {
            throw new InvalidOperationException("A scene must be open before capturing a canvas patch.");
        }
        return _patchCapture.Capture(
            _sceneResource,
            _projectRoot,
            SelectedImageState?.Id,
            targetImageId,
            layerScope,
            currentLayerId,
            referenceRegion,
            replacementRegion,
            gridSizePx,
            generationMode);
    }

    public bool TryCaptureCurrentPatch(out CanvasPatchCapture? capture, out string error, string? targetImageId = null)
    {
        try
        {
            capture = CaptureCurrentPatch(targetImageId);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            capture = null;
            error = exception.Message;
            return false;
        }
    }

    public string ComputeCurrentPatchFingerprint(string? targetImageId = null) =>
        CaptureCurrentPatch(targetImageId).ContentFingerprint;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.All;
        ClipContents = true;
        LoadViewStateFromScene();
        EmitSignal(ViewChangedSignal, _viewport.Zoom);
        QueueRedraw();
    }

    public override void _Draw()
    {
        _renderer.Draw(this);
    }

    public override void _GuiInput(InputEvent @event)
    {
        _input.Handle(@event);
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        if (!CanMutate("拖放图片") || !CanEditCurrentLayer())
        {
            return false;
        }

        return _imageImportPaths.CanImportDroppedData(_projectRoot, data);
    }

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (!CanEditCurrentLayer())
        {
            return;
        }

        Vector2 world = ScreenToWorld(atPosition);
        int added = 0;
        foreach (string path in _imageImportPaths.ExtractDroppedPaths(data))
        {
            ProjectImagePathResult import = _imageImportPaths.ResolveImportPath(_projectRoot, path);
            if (!import.Success)
            {
                continue;
            }

            Vector2 position = world + new Vector2(added * GetGridSizePx(), added * GetGridSizePx());
            if (_document.TryAddResolvedImage(import, position, selectNewImage: true, undoable: true))
            {
                added++;
            }
        }
    }

    /// <summary>Connects this canvas to the current project and scene resources.</summary>
    internal void SetWorkspace(AgasProjectResource? projectResource, AgasSceneResource? sceneResource, string projectRoot)
    {
        _input.CancelPointerInteraction();
        ClearAiGenerationOverlays();
        _projectResource = projectResource;
        _sceneResource = sceneResource;
        _projectRoot = _imageImportPaths.NormalizeProjectRoot(projectRoot);
        _edits.BindLocalWorkspace(_projectRoot, _projectResource, _sceneResource);
        ClearSelection();
        LoadViewStateFromScene();
        EmitSignal(ViewChangedSignal, _viewport.Zoom);
        _textureCache.Clear();
        ClearRegions();
        QueueRedraw();
    }

    internal void SetProject(AgasProjectResource? projectResource, string projectRoot)
    {
        ClearAiGenerationOverlays();
        _projectResource = projectResource;
        _projectRoot = _imageImportPaths.NormalizeProjectRoot(projectRoot);
        _edits.BindLocalWorkspace(_projectRoot, _projectResource, _sceneResource);
        QueueRedraw();
    }

    internal void SetScene(AgasSceneResource? sceneResource)
    {
        _input.CancelPointerInteraction();
        ClearAiGenerationOverlays();
        _sceneResource = sceneResource;
        _edits.BindLocalWorkspace(_projectRoot, _projectResource, _sceneResource);
        ClearSelection();
        ClearRegions();
        LoadViewStateFromScene();
        EmitSignal(ViewChangedSignal, _viewport.Zoom);
        QueueRedraw();
    }

    internal AgasProjectResource? GetProjectResource() => _projectResource;

    internal AgasSceneResource? GetSceneResource() => _sceneResource;

    internal ImageInstanceResource? GetSelectedImage() => SelectedImageState;

    internal float Zoom => _viewport.Zoom;

    ImageInstanceResource? ICanvasInputHost.HitTestCurrentEditableLayer(Vector2 worldPosition) =>
        HitTestCurrentEditableLayer(worldPosition);
    Vector2 ICanvasInputHost.GetImagePosition(ImageInstanceResource image) => GetImagePosition(image);
    bool ICanvasInputHost.BeginImageMovePreview(ImageInstanceResource image) => BeginImageMovePreview(image);
    void ICanvasInputHost.UpdateImageMovePreview(ImageInstanceResource image, Vector2 position) =>
        UpdateImageMovePreview(image, position);
    bool ICanvasInputHost.CommitImageMovePreview(ImageInstanceResource image, Vector2 position) =>
        CommitImageMovePreview(image, position);
    void ICanvasInputHost.CancelImageMovePreview() => CancelImageMovePreview();
    bool ICanvasInputHost.CanMutate(string action) => CanMutate(action);
    void ICanvasInputHost.SelectImage(ImageInstanceResource? image) => SelectImage(image);
    void ICanvasInputHost.ClearSelection() => ClearSelection();
    void ICanvasInputHost.UpdateActiveRegion(Vector2 startWorld, Vector2 endWorld) => UpdateActiveRegion(startWorld, endWorld);
    void ICanvasInputHost.ZoomAt(Vector2 screenPosition, float factor) => ZoomAt(screenPosition, factor);
    void ICanvasInputHost.PanByScreenDelta(Vector2 relative) => PanByScreenDelta(relative);
    void ICanvasInputHost.RequestContextMenu(Vector2 screenPosition) => RequestContextMenu(screenPosition);
    void ICanvasInputHost.QueueInputRedraw() => QueueRedraw();
    void ICanvasInputHost.GrabInputFocus() => GrabFocus();
    void ICanvasInputHost.AcceptInputEvent() => AcceptEvent();

    string ICanvasSelectionWorkspaceBridge.SceneId => _sceneResource?.Id ?? string.Empty;

    string? ICanvasSelectionWorkspaceBridge.SelectedImageId =>
        string.IsNullOrWhiteSpace(SelectedImageState?.Id)
            ? null
            : SelectedImageState.Id;

    string? ICanvasSelectionWorkspaceBridge.SelectedLayerId
    {
        get
        {
            string id = FindLayerContainingImage(SelectedImageState)?.Id ?? string.Empty;
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
    }

    int ICanvasSelectionWorkspaceBridge.GridSizePx => GetGridSizePx();

    bool ICanvasSelectionWorkspaceBridge.CanEditCurrentLayer => CanEditCurrentLayer();

    CanvasSelectionTool ICanvasSelectionWorkspaceBridge.ActiveRegionTool => (CanvasSelectionTool)ActiveRegionTool;
    CanvasSelectionRegion? ICanvasSelectionWorkspaceBridge.SelectionRegion => ToSelectionContract(SelectionRegion);
    CanvasSelectionRegion? ICanvasSelectionWorkspaceBridge.ReplacementRegion => ToSelectionContract(ReplacementRegion);
    CanvasSelectionRegion? ICanvasSelectionWorkspaceBridge.EffectiveGenerationRegion => ToSelectionContract(EffectiveGenerationRegion);

    bool ICanvasSelectionWorkspaceBridge.TrySetRegions(CanvasSelectionRegion selection, CanvasSelectionRegion? replacement) =>
        TrySetRegions(FromSelectionContract(selection), replacement.HasValue ? FromSelectionContract(replacement.Value) : null);

    void ICanvasSelectionWorkspaceBridge.ClearRegions() => ClearRegions();
    bool ICanvasSelectionWorkspaceBridge.TrySelectImage(string imageId) => TrySelectImage(imageId);
    void ICanvasSelectionWorkspaceBridge.ClearImageSelection() => ClearImageSelection();

    /// <summary>Sets the complete Q/W state without synthesizing pointer input.</summary>
    public bool TrySetRegions(CanvasGridRegion selection, CanvasGridRegion? replacement = null)
    {
        // Validate before any side effects so a failed call does not cancel an in-progress drag
        // and so pointer cancel runs before region assignment (matches pre-extract order).
        if (!CanvasRegionSelectionState.IsValid(selection)
            || (replacement.HasValue && !CanvasRegionSelectionState.IsValid(replacement.Value)))
        {
            return false;
        }

        // W is only meaningful as a transparent hole inside Q. Reject out-of-bounds W
        // so MCP/UI callers cannot leave the canvas in a state that silently blocks
        // the next concurrent generation.
        if (replacement.HasValue && !selection.Contains(replacement.Value))
        {
            return false;
        }

        CancelPointerInteraction();
        if (!_regions.TrySet(selection, replacement))
        {
            return false;
        }

        EmitSignal(RegionStateChangedSignal);
        QueueRedraw();
        return true;
    }

    /// <summary>Selects an image from the current visible unlocked layer by id.</summary>
    public bool TrySelectImage(string imageId)
    {
        if (string.IsNullOrWhiteSpace(imageId) || !CanEditCurrentLayer())
        {
            return false;
        }

        AgasLayerResource? layer = GetCurrentLayer();
        ImageInstanceResource? image = layer == null
            ? null
            : GetOrderedImages(layer).FirstOrDefault(item =>
                string.Equals(item.Id, imageId, StringComparison.Ordinal));
        if (image == null)
        {
            return false;
        }

        SelectImage(image);
        return true;
    }

    public void ClearImageSelection() => ClearSelection();

    public void SetActiveRegionTool(CanvasRegionTool tool)
    {
        if (!_regions.SetTool(tool))
        {
            if (tool == CanvasRegionTool.Edit)
            {
                CancelPointerInteraction();
                QueueRedraw();
            }
            return;
        }

        CancelPointerInteraction();
        EmitSignal(RegionStateChangedSignal);
        QueueRedraw();
    }

    private void CancelPointerInteraction()
    {
        _input.CancelPointerInteraction();
    }

    public void ClearRegions()
    {
        // Include active pointer interaction so canceling a live Q/W drag still
        // refreshes UI even when stored regions were already empty.
        bool interactionActive = _input.HasActivePointerInteraction;
        CancelPointerInteraction();
        bool changed = _regions.Clear() || interactionActive;
        if (changed)
        {
            EmitSignal(RegionStateChangedSignal);
            QueueRedraw();
        }
    }

    public void SetAiGenerationOverlays(IEnumerable<CanvasAiGenerationOverlay> overlays)
    {
        ArgumentNullException.ThrowIfNull(overlays);
        _aiGenerationOverlays.Clear();
        foreach (CanvasAiGenerationOverlay overlay in overlays)
        {
            _aiGenerationOverlays[overlay.RunId] = overlay;
        }
        QueueRedraw();
    }

    public void ClearAiGenerationOverlays()
    {
        if (_aiGenerationOverlays.Count == 0)
        {
            return;
        }
        _aiGenerationOverlays.Clear();
        QueueRedraw();
    }

    public void SetAiGenerationOverlay(CanvasAiGenerationOverlay? overlay)
    {
        SetAiGenerationOverlays(overlay == null ? System.Array.Empty<CanvasAiGenerationOverlay>() : new[] { overlay });
    }

    internal ImageInstanceResource? SelectedImage => SelectedImageState;

    internal AgasLayerResource? GetCurrentLayer()
    {
        if (_sceneResource == null)
        {
            return null;
        }

        string activeLayerId = _sceneResource.ActiveLayerId;
        List<AgasLayerResource> layers = GetOrderedLayers().ToList();
        if (!string.IsNullOrWhiteSpace(activeLayerId))
        {
            AgasLayerResource? matched = layers.FirstOrDefault(layer => layer.Id == activeLayerId);
            if (matched != null)
            {
                return matched;
            }
        }

        return layers.FirstOrDefault();
    }

    public bool CanEditCurrentLayer()
    {
        AgasLayerResource? layer = GetCurrentLayer();
        return layer != null && layer.Visible && !layer.Locked;
    }

    public int GetGridSizePx()
    {
        int size = _projectResource?.Grid?.SizePx ?? FallbackGridSizePx;
        return size > 0 ? size : FallbackGridSizePx;
    }

    public bool SetGridSizePx(int sizePx, bool undoable = true) =>
        _document.SetGridSizePx(sizePx, undoable);

    public bool IsSnapEnabled()
    {
        return _projectResource?.Grid?.SnapEnabled ?? true;
    }

    public bool SetSnapEnabled(bool enabled, bool undoable = true) =>
        _document.SetSnapEnabled(enabled, undoable);

    public bool IsGridVisible()
    {
        return _projectResource?.Grid?.Visible ?? true;
    }

    public bool SetGridVisible(bool visible, bool undoable = true) =>
        _document.SetGridVisible(visible, undoable);

    public Vector2 GetCameraWorld() => _viewport.CameraWorld;

    public float GetZoom() => _viewport.Zoom;

    public void SetView(Vector2 cameraWorld, float zoom)
    {
        if (!_viewport.SetView(cameraWorld, zoom, MinZoom, MaxZoom))
        {
            return;
        }

        NotifyViewChanged();
    }

    public void ZoomIn() => ZoomAt(Size * 0.5f, CanvasViewportController.ZoomStepFactor);

    public void ZoomOut() => ZoomAt(Size * 0.5f, 1.0f / CanvasViewportController.ZoomStepFactor);

    public void ResetZoomTo100Percent()
    {
        _viewport.ResetZoom(MinZoom, MaxZoom);
        NotifyViewChanged();
    }

    /// <summary>
    /// Fits every image on a visible layer into the viewport. Automatic fitting does
    /// not upscale above 100%; explicit zoom controls remain available for inspection.
    /// </summary>
    public bool FitVisibleContent(float marginPx = CanvasViewportController.DefaultFitMarginPx)
    {
        IEnumerable<Vector2[]> cornerSets = GetOrderedLayers()
            .Where(layer => layer.Visible)
            .SelectMany(GetOrderedImages)
            .Where(image => float.IsFinite((float)image.Opacity)
                && image.Opacity > FitVisibleOpacityEpsilon)
            .Select(GetImageWorldCorners);
        bool fitted = _viewport.FitVisibleContent(cornerSets, Size, marginPx, MinZoom, MaxZoom);
        NotifyViewChanged();
        return fitted;
    }

    public Vector2 ScreenToWorld(Vector2 screenPosition) => _viewport.ScreenToWorld(screenPosition, Size);

    public Vector2 WorldToScreen(Vector2 worldPosition) => _viewport.WorldToScreen(worldPosition, Size);

    public Vector2 SnapWorldPosition(Vector2 worldPosition)
    {
        if (!IsSnapEnabled())
        {
            return worldPosition;
        }

        int grid = GetGridSizePx();
        return new Vector2(
            Mathf.Round(worldPosition.X / grid) * grid,
            Mathf.Round(worldPosition.Y / grid) * grid);
    }

    public bool TryAddImageFromProjectRelativePath(string imagePath, Vector2 worldPosition, bool selectNewImage = true) =>
        _document.TryAddImageFromPath(imagePath, worldPosition, selectNewImage, undoable: true);

    public bool TryAddToolOutputBelowImage(
        string sourceImageId,
        string imagePath)
    {
        AgasLayerResource? layer = GetCurrentLayer();
        ImageInstanceResource? source = layer == null
            ? null
            : GetOrderedImages(layer).FirstOrDefault(image =>
                string.Equals(image.Id, sourceImageId, StringComparison.Ordinal));
        if (source == null)
        {
            return false;
        }

        Vector2[] corners = GetImageWorldCorners(source);
        float left = corners.Min(point => point.X);
        float bottom = corners.Max(point => point.Y);
        Vector2 outputPosition = new(left, bottom + Math.Max(1, GetGridSizePx()));
        return _document.TryAddImageFromPath(
            imagePath,
            outputPosition,
            selectNewImage: true,
            undoable: true);
    }

    string ICanvasLayerMergeBridge.SceneId => _sceneResource?.Id ?? string.Empty;

    bool ICanvasLayerMergeBridge.TryMergeVisibleLayersIntoCurrent(string targetLayerId, out CanvasVisibleLayerMergeResult result) =>
        TryMergeVisibleLayersIntoCurrent(targetLayerId, out result);

    public bool TryMergeVisibleLayersIntoCurrent(string? targetLayerId, out CanvasVisibleLayerMergeResult result)
    {
        result = CanvasVisibleLayerMergeResult.Fail("没有打开的场景。");
        if (_sceneResource == null || string.IsNullOrWhiteSpace(_projectRoot))
        {
            return false;
        }

        AgasLayerResource? current = GetCurrentLayer();
        if (current == null)
        {
            result = CanvasVisibleLayerMergeResult.Fail("当前没有可写入的图层。");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(targetLayerId)
            && !string.Equals(current.Id, targetLayerId, StringComparison.Ordinal))
        {
            result = CanvasVisibleLayerMergeResult.Fail("只能把可见图层合并到当前活动图层。");
            return false;
        }

        if (!CanEditCurrentLayer())
        {
            result = CanvasVisibleLayerMergeResult.Fail("当前图层隐藏或锁定，无法写入合并结果。");
            return false;
        }

        AgasLayerResource[] sources = GetOrderedLayers()
            .Where(layer => layer.Visible && !string.Equals(layer.Id, current.Id, StringComparison.Ordinal))
            .ToArray();
        if (sources.Length == 0 || sources.All(layer => layer.Images.Count == 0))
        {
            result = CanvasVisibleLayerMergeResult.Fail("没有其他可见图层可合并。");
            return false;
        }

        if (!TryResolveMergeRegion(sources, out CanvasGridRegion region, out string regionError))
        {
            result = CanvasVisibleLayerMergeResult.Fail(regionError);
            return false;
        }

        CanvasPatchCapture capture;
        try
        {
            capture = CapturePatch(
                targetImageId: null,
                CanvasPatchLayerScope.VisibleLayersExceptCurrent,
                current.Id,
                region,
                region,
                GetGridSizePx(),
                CanvasPatchGenerationMode.FullRegionGeneration);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Failed to merge visible layers: {exception.GetType().Name}");
            result = CanvasVisibleLayerMergeResult.Fail("其他可见图层无法合成。");
            return false;
        }

        if (!CanvasPatchAssetStore.TrySavePng(
                _projectRoot,
                capture.SourceCanvasPng,
                _sceneResource.Id,
                _sceneResource.Name,
                current.Id,
                current.Name,
                out string relativePath,
                out string saveError))
        {
            result = CanvasVisibleLayerMergeResult.Fail(saveError);
            return false;
        }

        Vector2 position = new(capture.ReferenceWorldPixels.Position.X, capture.ReferenceWorldPixels.Position.Y);
        if (!TryAddImageFromProjectRelativePath(relativePath, position, selectNewImage: true))
        {
            CanvasPatchAssetStore.DeleteGeneratedCandidate(_projectRoot, relativePath);
            result = CanvasVisibleLayerMergeResult.Fail("合并结果无法写入当前图层。");
            return false;
        }

        ImageInstanceResource? added = SelectedImageState;
        result = CanvasVisibleLayerMergeResult.Ok(
            relativePath,
            added?.Id ?? string.Empty,
            added?.X ?? position.X,
            added?.Y ?? position.Y,
            sources.Select(layer => layer.Id).ToArray());
        return true;
    }

    private bool TryResolveMergeRegion(
        IReadOnlyList<AgasLayerResource> sources,
        out CanvasGridRegion region,
        out string error)
    {
        region = default;
        error = string.Empty;
        if (SelectionRegion is CanvasGridRegion selection)
        {
            region = selection;
            return true;
        }

        int grid = GetGridSizePx();
        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;
        bool any = false;
        var resolver = new ProjectImagePathResolver();
        foreach (AgasLayerResource layer in sources)
        {
            foreach (ImageInstanceResource image in layer.Images)
            {
                if (!TryGetImageWorldRect(resolver, image, out Rect2 rect))
                {
                    continue;
                }

                any = true;
                minX = Math.Min(minX, rect.Position.X);
                minY = Math.Min(minY, rect.Position.Y);
                maxX = Math.Max(maxX, rect.Position.X + rect.Size.X);
                maxY = Math.Max(maxY, rect.Position.Y + rect.Size.Y);
            }
        }

        if (!any || minX >= maxX || minY >= maxY)
        {
            error = "其他可见图层没有可合并的图片。";
            return false;
        }

        int minColumn = Mathf.FloorToInt(minX / grid);
        int minRow = Mathf.FloorToInt(minY / grid);
        int maxColumn = Mathf.FloorToInt((maxX - 0.001f) / grid);
        int maxRow = Mathf.FloorToInt((maxY - 0.001f) / grid);
        region = new CanvasGridRegion(
            minColumn,
            minRow,
            Math.Max(1, maxColumn - minColumn + 1),
            Math.Max(1, maxRow - minRow + 1));
        return true;
    }

    private bool TryGetImageWorldRect(
        ProjectImagePathResolver resolver,
        ImageInstanceResource imageInstance,
        out Rect2 rect)
    {
        rect = default;
        ProjectImagePathResult path = resolver.Resolve(_projectRoot, imageInstance.ImagePath, mustExist: true);
        if (!path.Success)
        {
            return false;
        }

        using Image decoded = Image.LoadFromFile(path.AbsolutePath);
        if (decoded == null || decoded.IsEmpty())
        {
            return false;
        }

        float width = decoded.GetWidth() * (float)imageInstance.ScaleX;
        float height = decoded.GetHeight() * (float)imageInstance.ScaleY;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        rect = new Rect2((float)imageInstance.X, (float)imageInstance.Y, width, height);
        return true;
    }

    public bool TryAddImageFromAbsolutePath(string absolutePath, Vector2 worldPosition, bool selectNewImage = true) =>
        _document.TryAddImageFromPath(absolutePath, worldPosition, selectNewImage, undoable: true);

    public bool DeleteSelected() => _document.DeleteSelected();

    public bool CopySelectedToClipboard() => _document.CopySelectedToClipboard();

    public bool PasteClipboard() => _document.PasteClipboard();

    public bool MoveSelectedTo(Vector2 worldPosition, bool undoable = true) =>
        _document.MoveSelectedTo(worldPosition, undoable);

    public bool ReplaceSelectedImagePath(string imagePath, bool undoable = true) =>
        ReplaceImagePath(SelectedImageState?.Id ?? string.Empty, imagePath, undoable);

    public bool ReplaceImagePath(string imageId, string imagePath, bool undoable = true) =>
        _document.ReplaceImagePath(imageId, imagePath, undoable);

    public bool UpdateSelectedImageProperties(
        double? x = null,
        double? y = null,
        double? scaleX = null,
        double? scaleY = null,
        double? rotationDegrees = null,
        double? opacity = null,
        bool undoable = true) =>
        _document.UpdateSelectedImageProperties(x, y, scaleX, scaleY, rotationDegrees, opacity, undoable);

    internal bool SetLayerVisible(AgasLayerResource layer, bool visible, bool undoable = true) =>
        _document.SetLayerVisible(layer, visible, undoable);

    internal bool SetLayerLocked(AgasLayerResource layer, bool locked, bool undoable = true) =>
        _document.SetLayerLocked(layer, locked, undoable);

    internal bool SetLayerShared(AgasLayerResource layer, bool shared, bool undoable = true) =>
        _document.SetLayerShared(layer, shared, undoable);

    internal PromptFragmentResource? AddPromptFragment(
        AgasLayerResource layer,
        string text = "",
        bool enabled = true,
        bool undoable = true) =>
        _document.AddPromptFragment(layer, text, enabled, undoable);

    internal bool DeletePromptFragment(AgasLayerResource layer, PromptFragmentResource fragment, bool undoable = true) =>
        _document.DeletePromptFragment(layer, fragment, undoable);

    internal bool UpdatePromptFragmentText(PromptFragmentResource fragment, string text, bool undoable = true) =>
        _document.UpdatePromptFragment(fragment, text, null, undoable);

    internal bool SetPromptFragmentEnabled(PromptFragmentResource fragment, bool enabled, bool undoable = true) =>
        _document.SetPromptFragmentEnabled(fragment, enabled, undoable);

    internal bool SetPromptFragmentRole(PromptFragmentResource fragment, int role, bool undoable = true) =>
        _document.SetPromptFragmentRole(fragment, role, undoable);

    internal bool UpdatePromptFragment(
        PromptFragmentResource fragment,
        string? text = null,
        bool? enabled = null,
        bool undoable = true) =>
        _document.UpdatePromptFragment(fragment, text, enabled, undoable);

    internal bool MovePromptFragment(
        AgasLayerResource layer,
        PromptFragmentResource fragment,
        int direction,
        bool undoable = true) =>
        _document.MovePromptFragment(layer, fragment, direction, undoable);

    internal bool ReorderPromptFragment(
        AgasLayerResource layer,
        PromptFragmentResource fragment,
        int targetIndex,
        bool undoable = true) =>
        _document.ReorderPromptFragment(layer, fragment, targetIndex, undoable);

    internal bool SetCurrentLayer(AgasLayerResource layer) =>
        _document.SetCurrentLayer(layer);

    public bool CanUndo() => _edits.CanUndo;

    public bool CanRedo() => _edits.CanRedo;

    public bool Undo() => _edits.Undo();

    public bool Redo() => _edits.Redo();

    /// <summary>
    /// Safe API for UI-driven persisted edits. View, selection, and current-layer changes
    /// remain non-undoable UI state; persisted grid settings share the normal undo boundary.
    /// </summary>
    public bool ExecuteCanvasEdit(string label, CanvasEditScope scope, Action doAction, Action undoAction)
    {
        return TryExecuteCanvasEdit(label, scope, doAction, undoAction);
    }

    /// <summary>
    /// Backward-compatible content edit entry point. New UI code should pass an explicit
    /// CanvasEditScope so non-undoable UI state cannot be queued accidentally.
    /// </summary>
    public void ExecuteCanvasEdit(string label, Action doAction, Action undoAction)
    {
        TryExecuteCanvasEdit(label, CanvasEditScope.CanvasContent, doAction, undoAction);
    }

    public bool TryExecuteCanvasEdit(string label, CanvasEditScope scope, Action doAction, Action undoAction) =>
        _edits.TryExecuteCanvasEdit(label, scope, doAction, undoAction);

    public void RefreshCanvasState()
    {
        bool selectionCleared = false;
        AgasLayerResource? selectedLayer = FindLayerContainingImage(SelectedImageState);
        if (SelectedImageState != null
            && (selectedLayer == null
                || !ReferenceEquals(selectedLayer, GetCurrentLayer())
                || !selectedLayer.Visible
                || selectedLayer.Locked))
        {
            ClearSelection();
            selectionCleared = true;
        }

        QueueRedraw();
        if (!selectionCleared)
        {
            ReemitSelectionChanged();
        }
        EmitSignal(UndoRedoAvailabilityChangedSignal, _edits.CanUndo, _edits.CanRedo);
        EmitSignal(CanvasStateRefreshRequestedSignal);
    }

    /// <summary>
    /// Reloads resource-backed view state after an external mutation while
    /// preserving valid Q/W regions and image selection in the same scene.
    /// </summary>
    public void RefreshCanvasStateFromResources()
    {
        LoadViewStateFromScene();
        EmitSignal(ViewChangedSignal, _viewport.Zoom);
        RefreshCanvasState();
    }

    public void ClearUndoRedoHistory() => _edits.ClearHistory();

    private void UpdateActiveRegion(Vector2 startWorld, Vector2 endWorld)
    {
        _regions.UpdateFromDrag(startWorld, endWorld, GetGridSizePx());
        EmitSignal(RegionStateChangedSignal);
        QueueRedraw();
    }

    private void ZoomAt(Vector2 screenPosition, float factor)
    {
        if (!_viewport.ZoomAt(screenPosition, factor, Size, MinZoom, MaxZoom)) return;
        NotifyViewChanged();
    }

    private void PanByScreenDelta(Vector2 relative)
    {
        _viewport.PanByScreenDelta(relative);
        NotifyViewChanged();
    }

    private void RequestContextMenu(Vector2 screenPosition)
    {
        LastContextMenuPosition = screenPosition;
        ContextMenuRequested?.Invoke();
    }

    public void RequestContextMenuAt(Vector2 screenPosition)
    {
        Vector2 insideCanvas = new(
            Mathf.Clamp(screenPosition.X, 0, Mathf.Max(0, Size.X - 1)),
            Mathf.Clamp(screenPosition.Y, 0, Mathf.Max(0, Size.Y - 1)));
        RequestContextMenu(insideCanvas);
    }

    private void NotifyViewChanged()
    {
        PersistViewStateToScene();
        QueueRedraw();
        EmitSignal(ViewChangedSignal, _viewport.Zoom);
    }

    internal Rect2 ToScreenRect(Rect2 worldRect) => _viewport.ToScreenRect(worldRect, Size);

    private ImageInstanceResource? HitTestCurrentEditableLayer(Vector2 worldPosition)
    {
        return _selection.HitTestCurrentEditableLayer(GetCurrentLayer(), worldPosition, GetImagePixelSize);
    }

    internal bool ExecuteCommand(ICanvasCommand command) => _edits.ExecuteCommand(command);

    private bool CanMutate(string action)
    {
        if (MutationGuard == null)
        {
            return true;
        }

        try
        {
            return MutationGuard(action);
        }
        catch (Exception ex)
        {
            GD.PushError($"Canvas mutation guard failed closed for '{action}': {ex}");
            return false;
        }
    }

    private void NotifyChanged(bool clearRedo = false, bool pushCanvasSignal = true)
    {
        // clearRedo is owned by CanvasEditApplication when recording history; kept for API symmetry.
        _ = clearRedo;
        PersistViewStateToScene();
        QueueRedraw();
        if (pushCanvasSignal)
        {
            EmitSignal(CanvasChangedSignal);
        }
        EmitSignal(UndoRedoAvailabilityChangedSignal, _edits.CanUndo, _edits.CanRedo);
        EmitSignal(CanvasStateRefreshRequestedSignal);
    }

    internal void SelectImage(ImageInstanceResource? imageInstance)
    {
        _selection.Select(imageInstance);
        ReemitSelectionChanged();
        EmitSignal(CanvasStateRefreshRequestedSignal);
        QueueRedraw();
    }

    internal void ClearSelection()
    {
        if (!_selection.Clear())
        {
            return;
        }

        ReemitSelectionChanged();
        EmitSignal(CanvasStateRefreshRequestedSignal);
        QueueRedraw();
    }

    private void ReemitSelectionChanged()
    {
        EmitSignal(SelectionChangedSignal);
    }

    internal bool IsImageRenderable(ImageInstanceResource image)
    {
        AgasLayerResource? layer = FindLayerContainingImage(image);
        return layer != null && layer.Visible;
    }

    internal Texture2D? GetImageTexture(ImageInstanceResource imageInstance)
    {
        string relativePath = imageInstance.ImagePath;
        if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(_projectRoot))
        {
            return null;
        }

        ProjectImagePathResult validation = _imageImportPaths.ResolveStoredProjectPath(_projectRoot, relativePath);
        return validation.Success ? _textureCache.GetOrLoad(validation.AbsolutePath) : null;
    }

    private Vector2? GetImagePixelSize(ImageInstanceResource imageInstance)
    {
        Texture2D? texture = GetImageTexture(imageInstance);
        return texture?.GetSize();
    }

    internal Vector2[] GetImageWorldCorners(ImageInstanceResource imageInstance)
    {
        return CanvasSelectionHelper.GetWorldCorners(
            imageInstance,
            GetImagePixelSize(imageInstance),
            GetImagePosition(imageInstance));
    }

    internal IEnumerable<AgasLayerResource> GetOrderedLayers()
    {
        return _sceneResource == null
            ? Enumerable.Empty<AgasLayerResource>()
            : _sceneResource.Layers.OrderBy(layer => layer.Order);
    }

    internal static IEnumerable<ImageInstanceResource> GetOrderedImages(AgasLayerResource layer)
    {
        return layer.Images.OrderBy(image => image.Order);
    }

    internal AgasLayerResource? FindLayerContainingImage(ImageInstanceResource? imageInstance)
    {
        return CanvasSelectionHelper.FindContainingLayer(GetOrderedLayers(), imageInstance);
    }

    internal IEnumerable<PromptFragmentResource> GetPromptFragmentsOrdered(AgasLayerResource layer)
    {
        return layer.PromptFragments.OrderBy(fragment => fragment.Order);
    }

    internal AgasLayerResource? FindLayerContainingPromptFragment(PromptFragmentResource? fragment)
    {
        if (fragment == null)
        {
            return null;
        }

        return GetOrderedLayers().FirstOrDefault(layer => GetPromptFragmentsOrdered(layer).Any(item => ReferenceEquals(item, fragment)));
    }

    internal bool CanEditLayerContent(AgasLayerResource? layer)
    {
        return layer is { Visible: true, Locked: false };
    }

    internal bool CanEditPromptFragmentInLayer(AgasLayerResource? layer, PromptFragmentResource? fragment)
    {
        return fragment != null
            && CanEditLayerContent(layer)
            && FindLayerContainingPromptFragment(fragment) == layer;
    }

    internal Vector2 GetImagePosition(ImageInstanceResource imageInstance)
    {
        if (ReferenceEquals(_movePreviewImage, imageInstance))
        {
            return _movePreviewPosition;
        }
        return new Vector2((float)imageInstance.X, (float)imageInstance.Y);
    }

    private bool BeginImageMovePreview(ImageInstanceResource imageInstance)
    {
        if (!ReferenceEquals(imageInstance, SelectedImageState)
            || !ReferenceEquals(FindLayerContainingImage(imageInstance), GetCurrentLayer())
            || !CanEditCurrentLayer())
        {
            return false;
        }

        _movePreviewImage = imageInstance;
        _movePreviewPosition = new Vector2((float)imageInstance.X, (float)imageInstance.Y);
        return true;
    }

    private void UpdateImageMovePreview(ImageInstanceResource imageInstance, Vector2 position)
    {
        if (!ReferenceEquals(_movePreviewImage, imageInstance))
        {
            return;
        }

        _movePreviewPosition = position;
        QueueRedraw();
    }

    private bool CommitImageMovePreview(ImageInstanceResource imageInstance, Vector2 position)
    {
        if (!ReferenceEquals(_movePreviewImage, imageInstance))
        {
            return false;
        }

        CancelImageMovePreview();
        return MoveSelectedTo(position, undoable: true);
    }

    private void CancelImageMovePreview()
    {
        if (_movePreviewImage == null)
        {
            return;
        }

        _movePreviewImage = null;
        QueueRedraw();
    }

    internal Vector2 GetImageScale(ImageInstanceResource imageInstance)
    {
        return new Vector2((float)imageInstance.ScaleX, (float)imageInstance.ScaleY);
    }

    private void LoadViewStateFromScene()
    {
        _viewport.Load(_sceneResource, MinZoom, MaxZoom);
    }

    private void PersistViewStateToScene()
    {
        if (!ViewStatePersistenceEnabled)
        {
            return;
        }

        if (_sceneResource == null || _projectResource == null)
        {
            return;
        }
        _edits.MutateView(
            new ViewMutationCommand(
                _sceneResource.Id, _viewport.CameraWorld.X, _viewport.CameraWorld.Y, _viewport.Zoom),
            _projectRoot,
            _projectResource,
            _sceneResource);
    }

    private static string CreateId(string prefix)
    {
        return $"{prefix}-{Guid.NewGuid():N}";
    }

    private ProjectMutationResult MutateImage(ImageMutationCommand command, bool undoable) =>
        _edits.MutateImage(command, undoable, _projectRoot, _projectResource, _sceneResource);

    private ProjectMutationResult MutateGrid(GridMutationCommand command, bool undoable) =>
        _edits.MutateGrid(command, undoable, _projectRoot, _projectResource, _sceneResource);

    private ProjectMutationResult MutateLayer(LayerMutationCommand command, bool undoable) =>
        _edits.MutateLayer(command, undoable, _projectRoot, _projectResource, _sceneResource);

    private ProjectMutationResult MutatePrompt(PromptMutationCommand command, bool undoable) =>
        _edits.MutatePrompt(command, undoable, _projectRoot, _projectResource, _sceneResource);

    private static CanvasSelectionRegion? ToSelectionContract(CanvasGridRegion? region) => region.HasValue
        ? new CanvasSelectionRegion(region.Value.Column, region.Value.Row, region.Value.Width, region.Value.Height)
        : null;

    private static CanvasGridRegion FromSelectionContract(CanvasSelectionRegion region) =>
        new(region.Column, region.Row, region.Width, region.Height);

    private void ReconcileSelectionAfterLayerMutation()
    {
        AgasLayerResource? selectedLayer = FindLayerContainingImage(SelectedImageState);
        if (SelectedImageState != null
            && (selectedLayer == null || !selectedLayer.Visible || selectedLayer.Locked))
        {
            ClearSelection();
        }
    }
}
