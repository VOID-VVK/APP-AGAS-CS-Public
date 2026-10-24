#nullable enable

using System.Collections.Generic;
using AppAgasCs.Application;
using AppAgasCs.Core.Resources;
using Godot;

namespace Agas.Features.Canvas;

/// <summary>
/// Narrow view/session surface used by <see cref="CanvasDocumentCommands"/>.
/// Keeps document mutation logic free of Godot Control layout/input details (Demeter / SRP).
/// </summary>
internal interface ICanvasDocumentHost
{
    string ProjectRoot { get; }
    AgasProjectResource? Project { get; }
    AgasSceneResource? Scene { get; }
    ImageInstanceResource? SelectedImage { get; }
    ImageInstanceResource? ClipboardPrototype { get; set; }

    bool CanMutate(string action);
    bool CanEditCurrentLayer();
    AgasLayerResource? GetCurrentLayer();
    IEnumerable<AgasLayerResource> GetOrderedLayers();
    IEnumerable<ImageInstanceResource> GetOrderedImages(AgasLayerResource layer);
    IEnumerable<PromptFragmentResource> GetPromptFragmentsOrdered(AgasLayerResource layer);
    AgasLayerResource? FindLayerContainingImage(ImageInstanceResource? image);
    AgasLayerResource? FindLayerContainingPromptFragment(PromptFragmentResource? fragment);
    bool CanEditPromptFragmentInLayer(AgasLayerResource? layer, PromptFragmentResource? fragment);
    bool CanEditLayerContent(AgasLayerResource? layer);

    Vector2 GetImagePosition(ImageInstanceResource image);
    Vector2 SnapWorldPosition(Vector2 world);
    int GetGridSizePx();

    ProjectMutationResult MutateImage(ImageMutationCommand command, bool undoable);
    ProjectMutationResult MutateGrid(GridMutationCommand command, bool undoable);
    ProjectMutationResult MutateLayer(LayerMutationCommand command, bool undoable);
    ProjectMutationResult MutatePrompt(PromptMutationCommand command, bool undoable);

    ProjectImagePathResult ResolveImportPath(string path);
    void ClearTextureCache();
    void NotifyChanged(bool clearRedo = false, bool pushCanvasSignal = true);
    void ClearSelection();
    void SelectImage(ImageInstanceResource? image);
    void ReconcileSelectionAfterLayerMutation();
}
