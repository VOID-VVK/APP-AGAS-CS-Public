#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using AppAgasCs.Application;
using AppAgasCs.Core.Resources;
using Godot;

namespace Agas.Features.Canvas;

/// <summary>
/// Document-level canvas commands: grid, images, layers, and prompt fragments.
/// <see cref="CanvasWorkspace"/> remains the Godot Control host and selection/viewport owner.
/// </summary>
internal sealed class CanvasDocumentCommands
{
    private readonly ICanvasDocumentHost _host;

    public CanvasDocumentCommands(ICanvasDocumentHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public bool SetGridSizePx(int sizePx, bool undoable = true)
    {
        if (!_host.CanMutate("修改网格大小") || _host.Project?.Grid == null)
        {
            return false;
        }

        ProjectMutationResult result = _host.MutateGrid(
            new GridMutationCommand(GridMutationKind.SetSize, SizePx: sizePx),
            undoable);
        if (result.Success && !undoable)
        {
            _host.NotifyChanged();
        }

        return result.Success;
    }

    public bool SetSnapEnabled(bool enabled, bool undoable = true)
    {
        if (!_host.CanMutate("修改吸附设置") || _host.Project?.Grid == null)
        {
            return false;
        }

        ProjectMutationResult result = _host.MutateGrid(
            new GridMutationCommand(GridMutationKind.SetSnapEnabled, Flag: enabled),
            undoable);
        if (result.Success && !undoable)
        {
            _host.NotifyChanged();
        }

        return result.Success;
    }

    public bool SetGridVisible(bool visible, bool undoable = true)
    {
        if (!_host.CanMutate("修改网格显示") || _host.Project?.Grid == null)
        {
            return false;
        }

        ProjectMutationResult result = _host.MutateGrid(
            new GridMutationCommand(GridMutationKind.SetVisible, Flag: visible),
            undoable);
        if (result.Success && !undoable)
        {
            _host.NotifyChanged();
        }

        return result.Success;
    }

    public bool TryAddImageFromPath(
        string path,
        Vector2 worldPosition,
        bool selectNewImage,
        bool undoable)
    {
        ProjectImagePathResult import = _host.ResolveImportPath(path);
        return import.Success && TryAddResolvedImage(
            import,
            worldPosition,
            selectNewImage,
            undoable);
    }

    public bool TryAddResolvedImage(
        ProjectImagePathResult import,
        Vector2 worldPosition,
        bool selectNewImage,
        bool undoable)
    {
        if (!_host.CanMutate("添加图片") || !_host.CanEditCurrentLayer())
        {
            return false;
        }

        AgasLayerResource? layer = _host.GetCurrentLayer();
        AgasSceneResource? scene = _host.Scene;
        if (layer == null || scene == null)
        {
            return false;
        }

        Vector2 snapped = _host.SnapWorldPosition(worldPosition);
        ProjectMutationResult result = _host.MutateImage(new ImageMutationCommand(
            ImageMutationKind.Add,
            scene.Id,
            layer.Id,
            ImagePath: import.RelativePath,
            X: snapped.X,
            Y: snapped.Y,
            ScaleX: 1.0,
            ScaleY: 1.0,
            RotationDegrees: 0.0,
            Opacity: 1.0), undoable);
        if (!result.Success)
        {
            return false;
        }

        if (selectNewImage)
        {
            SelectMutationImage(layer, result);
        }

        if (!undoable)
        {
            _host.NotifyChanged();
        }

        return true;
    }

    public bool DeleteSelected()
    {
        if (!_host.CanMutate("删除图片"))
        {
            return false;
        }

        ImageInstanceResource? selected = _host.SelectedImage;
        if (selected == null || !_host.CanEditCurrentLayer())
        {
            return false;
        }

        AgasLayerResource? layer = _host.FindLayerContainingImage(selected);
        if (layer == null || layer != _host.GetCurrentLayer() || _host.Scene == null)
        {
            return false;
        }

        ProjectMutationResult result = _host.MutateImage(new ImageMutationCommand(
            ImageMutationKind.Delete, _host.Scene.Id, layer.Id, selected.Id), undoable: true);
        if (!result.Success)
        {
            return false;
        }

        _host.ClearSelection();
        return true;
    }

    public bool CopySelectedToClipboard()
    {
        ImageInstanceResource? selected = _host.SelectedImage;
        if (selected == null || !_host.CanEditCurrentLayer())
        {
            return false;
        }

        ImageInstanceResource? copy = selected.Duplicate(true) as ImageInstanceResource;
        _host.ClipboardPrototype = copy;
        return copy != null;
    }

    public bool PasteClipboard()
    {
        if (!_host.CanMutate("粘贴图片"))
        {
            return false;
        }

        ImageInstanceResource? prototype = _host.ClipboardPrototype;
        if (prototype == null || !_host.CanEditCurrentLayer())
        {
            return false;
        }

        AgasLayerResource? layer = _host.GetCurrentLayer();
        ImageInstanceResource? pasted = prototype.Duplicate(true) as ImageInstanceResource;
        if (layer == null || pasted == null || _host.Scene == null)
        {
            return false;
        }

        int offset = Math.Max(1, _host.GetGridSizePx());
        ProjectMutationResult result = _host.MutateImage(new ImageMutationCommand(
            ImageMutationKind.Add,
            _host.Scene.Id,
            layer.Id,
            ImagePath: pasted.ImagePath,
            X: pasted.X + offset,
            Y: pasted.Y + offset,
            ScaleX: pasted.ScaleX,
            ScaleY: pasted.ScaleY,
            RotationDegrees: pasted.RotationDegrees,
            Opacity: pasted.Opacity), undoable: true);
        return SelectMutationImage(layer, result);
    }

    public bool MoveSelectedTo(Vector2 worldPosition, bool undoable = true)
    {
        if (!_host.CanMutate("移动图片"))
        {
            return false;
        }

        ImageInstanceResource? selected = _host.SelectedImage;
        AgasLayerResource? layer = _host.GetCurrentLayer();
        if (selected == null || layer == null || !_host.CanEditCurrentLayer() || _host.Scene == null)
        {
            return false;
        }

        Vector2 oldPosition = _host.GetImagePosition(selected);
        Vector2 newPosition = _host.SnapWorldPosition(worldPosition);
        if (oldPosition.IsEqualApprox(newPosition))
        {
            return true;
        }

        ProjectMutationResult result = _host.MutateImage(new ImageMutationCommand(
            ImageMutationKind.Update,
            _host.Scene.Id,
            layer.Id,
            selected.Id,
            X: newPosition.X,
            Y: newPosition.Y), undoable);
        if (!result.Success)
        {
            return false;
        }

        if (undoable)
        {
            return SelectMutationImage(layer, result);
        }

        _host.NotifyChanged();
        return true;
    }

    public bool ReplaceImagePath(
        string imageId,
        string imagePath,
        bool undoable = true)
    {
        if (!_host.CanMutate("替换图片"))
        {
            return false;
        }

        if (!_host.CanEditCurrentLayer()
            || string.IsNullOrWhiteSpace(imageId)
            || string.IsNullOrWhiteSpace(imagePath)
            || string.IsNullOrWhiteSpace(_host.ProjectRoot)
            || _host.Scene == null)
        {
            return false;
        }

        AgasLayerResource? layer = _host.GetCurrentLayer();
        ImageInstanceResource? target = layer == null
            ? null
            : _host.GetOrderedImages(layer).FirstOrDefault(image =>
                string.Equals(image.Id, imageId, StringComparison.Ordinal));
        if (target == null || layer == null)
        {
            return false;
        }

        ProjectImagePathResult import = _host.ResolveImportPath(imagePath);
        if (!import.Success)
        {
            return false;
        }

        string beforePath = target.ImagePath;
        string afterPath = import.RelativePath;
        if (string.Equals(beforePath, afterPath, StringComparison.Ordinal))
        {
            return true;
        }

        ProjectMutationResult result = _host.MutateImage(new ImageMutationCommand(
            ImageMutationKind.Update,
            _host.Scene.Id,
            layer.Id,
            target.Id,
            ImagePath: afterPath), undoable);
        if (!result.Success)
        {
            return false;
        }

        _host.ClearTextureCache();
        if (undoable)
        {
            return SelectMutationImage(layer, result);
        }

        _host.NotifyChanged();
        return true;
    }

    public bool UpdateSelectedImageProperties(
        double? x = null,
        double? y = null,
        double? scaleX = null,
        double? scaleY = null,
        double? rotationDegrees = null,
        double? opacity = null,
        bool undoable = true)
    {
        if (!_host.CanMutate("修改图片属性"))
        {
            return false;
        }

        ImageInstanceResource? selected = _host.SelectedImage;
        AgasLayerResource? layer = _host.GetCurrentLayer();
        if (selected == null || layer == null || !_host.CanEditCurrentLayer() || _host.Scene == null)
        {
            return false;
        }

        if ((x ?? selected.X) == selected.X
            && (y ?? selected.Y) == selected.Y
            && (scaleX ?? selected.ScaleX) == selected.ScaleX
            && (scaleY ?? selected.ScaleY) == selected.ScaleY
            && (rotationDegrees ?? selected.RotationDegrees) == selected.RotationDegrees
            && (opacity ?? selected.Opacity) == selected.Opacity)
        {
            return true;
        }

        ProjectMutationResult result = _host.MutateImage(new ImageMutationCommand(
            ImageMutationKind.Update,
            _host.Scene.Id,
            layer.Id,
            selected.Id,
            X: x,
            Y: y,
            ScaleX: scaleX,
            ScaleY: scaleY,
            RotationDegrees: rotationDegrees,
            Opacity: opacity), undoable);
        if (!result.Success)
        {
            return false;
        }

        if (undoable)
        {
            return SelectMutationImage(layer, result);
        }

        _host.NotifyChanged();
        return true;
    }

    public bool SetLayerVisible(AgasLayerResource layer, bool visible, bool undoable = true)
    {
        if (!_host.CanMutate("修改图层可见性")
            || layer == null
            || _host.Scene == null
            || _host.Project == null)
        {
            return false;
        }

        if (layer.Visible == visible)
        {
            return true;
        }

        ProjectMutationResult result = _host.MutateLayer(new LayerMutationCommand(
            LayerMutationKind.SetVisible, _host.Scene.Id, layer.Id, Flag: visible), undoable);
        if (!result.Success)
        {
            return false;
        }

        _host.ReconcileSelectionAfterLayerMutation();
        if (!undoable)
        {
            _host.NotifyChanged();
        }

        return true;
    }

    public bool SetLayerLocked(AgasLayerResource layer, bool locked, bool undoable = true)
    {
        if (!_host.CanMutate("修改图层锁定状态")
            || layer == null
            || _host.Scene == null
            || _host.Project == null)
        {
            return false;
        }

        if (layer.Locked == locked)
        {
            return true;
        }

        ProjectMutationResult result = _host.MutateLayer(new LayerMutationCommand(
            LayerMutationKind.SetLocked, _host.Scene.Id, layer.Id, Flag: locked), undoable);
        if (!result.Success)
        {
            return false;
        }

        _host.ReconcileSelectionAfterLayerMutation();
        if (!undoable)
        {
            _host.NotifyChanged();
        }

        return true;
    }

    public bool SetLayerShared(AgasLayerResource layer, bool shared, bool undoable = true)
    {
        if (!_host.CanMutate("修改图层分享状态")
            || layer == null
            || _host.Scene == null
            || _host.Project == null)
        {
            return false;
        }

        if (layer.Shared == shared)
        {
            return true;
        }

        ProjectMutationResult result = _host.MutateLayer(new LayerMutationCommand(
            LayerMutationKind.SetShared, _host.Scene.Id, layer.Id, Flag: shared), undoable);
        if (!result.Success)
        {
            return false;
        }

        _host.ReconcileSelectionAfterLayerMutation();
        if (!undoable)
        {
            _host.NotifyChanged();
        }

        return true;
    }

    public PromptFragmentResource? AddPromptFragment(
        AgasLayerResource layer,
        string text = "",
        bool enabled = true,
        bool undoable = true)
    {
        if (!_host.CanMutate("新增提示词") || !_host.CanEditLayerContent(layer) || _host.Scene == null)
        {
            return null;
        }

        ProjectMutationResult result = _host.MutatePrompt(new PromptMutationCommand(
            PromptMutationKind.Add,
            _host.Scene.Id,
            layer.Id,
            Text: text ?? string.Empty,
            Enabled: enabled), undoable);
        if (!result.Success)
        {
            return null;
        }

        if (!undoable)
        {
            _host.NotifyChanged();
        }

        return layer.PromptFragments.FirstOrDefault(fragment => fragment.Id == result.ItemId);
    }

    public bool DeletePromptFragment(AgasLayerResource layer, PromptFragmentResource fragment, bool undoable = true)
    {
        if (!_host.CanMutate("删除提示词")
            || !_host.CanEditPromptFragmentInLayer(layer, fragment)
            || _host.Scene == null)
        {
            return false;
        }

        ProjectMutationResult result = _host.MutatePrompt(new PromptMutationCommand(
            PromptMutationKind.Delete, _host.Scene.Id, layer.Id, fragment.Id), undoable);
        if (result.Success && !undoable)
        {
            _host.NotifyChanged();
        }

        return result.Success;
    }

    public bool SetPromptFragmentEnabled(PromptFragmentResource fragment, bool enabled, bool undoable = true)
    {
        if (!_host.CanMutate("修改提示词")
            || !TryFindEditablePromptLayerForFragment(fragment, out AgasLayerResource? layer)
            || layer == null
            || _host.Scene == null)
        {
            return false;
        }

        ProjectMutationResult result = _host.MutatePrompt(new PromptMutationCommand(
            PromptMutationKind.SetEnabled, _host.Scene.Id, layer.Id, fragment.Id, Enabled: enabled), undoable);
        if (result.Success && !undoable)
        {
            _host.NotifyChanged();
        }

        return result.Success;
    }

    public bool SetPromptFragmentRole(PromptFragmentResource fragment, int role, bool undoable = true)
    {
        if (!_host.CanMutate("修改提示词分类") || role is < 0 or > 2)
        {
            return false;
        }

        if (!TryFindEditablePromptLayerForFragment(fragment, out AgasLayerResource? layer)
            || layer == null
            || _host.Scene == null)
        {
            return false;
        }

        if ((int)fragment.Role == role)
        {
            return true;
        }

        ProjectMutationResult result = _host.MutatePrompt(new PromptMutationCommand(
            PromptMutationKind.Update, _host.Scene.Id, layer.Id, fragment.Id, Role: role), undoable);
        if (result.Success && !undoable)
        {
            _host.NotifyChanged();
        }

        return result.Success;
    }

    public bool UpdatePromptFragment(
        PromptFragmentResource fragment,
        string? text = null,
        bool? enabled = null,
        bool undoable = true)
    {
        if (!_host.CanMutate("修改提示词")
            || !TryFindEditablePromptLayerForFragment(fragment, out AgasLayerResource? layer)
            || layer == null
            || _host.Scene == null)
        {
            return false;
        }

        if ((text == null || text == fragment.Text) && (!enabled.HasValue || enabled.Value == fragment.Enabled))
        {
            return true;
        }

        ProjectMutationResult result = _host.MutatePrompt(new PromptMutationCommand(
            PromptMutationKind.Update,
            _host.Scene.Id,
            layer.Id,
            fragment.Id,
            Text: text,
            UpdatedEnabled: enabled), undoable);
        if (result.Success && !undoable)
        {
            _host.NotifyChanged();
        }

        return result.Success;
    }

    public bool MovePromptFragment(
        AgasLayerResource layer,
        PromptFragmentResource fragment,
        int direction,
        bool undoable = true)
    {
        if (!_host.CanMutate("移动提示词") || !_host.CanEditPromptFragmentInLayer(layer, fragment))
        {
            return false;
        }

        if (direction == 0)
        {
            return true;
        }

        List<PromptFragmentResource> ordered = _host.GetPromptFragmentsOrdered(layer).ToList();
        int index = ordered.IndexOf(fragment);
        int targetIndex = index + Math.Sign(direction);
        return ReorderPromptFragment(layer, fragment, targetIndex, undoable);
    }

    public bool ReorderPromptFragment(
        AgasLayerResource layer,
        PromptFragmentResource fragment,
        int targetIndex,
        bool undoable = true)
    {
        if (!_host.CanMutate("移动提示词")
            || !_host.CanEditPromptFragmentInLayer(layer, fragment)
            || _host.Scene == null)
        {
            return false;
        }

        List<PromptFragmentResource> ordered = _host.GetPromptFragmentsOrdered(layer).ToList();
        int currentIndex = ordered.IndexOf(fragment);
        if (currentIndex < 0)
        {
            return false;
        }

        int clampedTarget = Math.Clamp(targetIndex, 0, ordered.Count - 1);
        if (currentIndex == clampedTarget)
        {
            return true;
        }

        ProjectMutationResult result = _host.MutatePrompt(new PromptMutationCommand(
            PromptMutationKind.Move,
            _host.Scene.Id,
            layer.Id,
            fragment.Id,
            TargetIndex: clampedTarget), undoable);
        if (result.Success && !undoable)
        {
            _host.NotifyChanged();
        }

        return result.Success;
    }

    public bool SetCurrentLayer(AgasLayerResource layer)
    {
        if (!_host.CanMutate("切换当前图层")
            || _host.Scene == null
            || _host.Project == null
            || layer == null)
        {
            return false;
        }

        if (!_host.Scene.LayersSnapshot.Any(candidate => ReferenceEquals(candidate, layer)))
        {
            return false;
        }

        string id = layer.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        if (string.Equals(_host.Scene.ActiveLayerId, id, StringComparison.Ordinal))
        {
            return false;
        }

        ProjectMutationResult result = _host.MutateLayer(new LayerMutationCommand(
            LayerMutationKind.SetCurrent, _host.Scene.Id, id), undoable: false);
        if (!result.Success)
        {
            return false;
        }

        _host.ClearSelection();
        _host.NotifyChanged(clearRedo: false, pushCanvasSignal: false);
        return true;
    }

    private bool TryFindEditablePromptLayerForFragment(
        PromptFragmentResource? fragment,
        out AgasLayerResource? layer)
    {
        layer = _host.FindLayerContainingPromptFragment(fragment);
        return fragment != null && _host.CanEditLayerContent(layer);
    }

    private bool SelectMutationImage(AgasLayerResource layer, ProjectMutationResult result)
    {
        if (!result.Success || string.IsNullOrWhiteSpace(result.ItemId))
        {
            return false;
        }

        ImageInstanceResource? image = layer.Images.FirstOrDefault(item => item.Id == result.ItemId);
        if (image != null)
        {
            _host.SelectImage(image);
        }

        return image != null;
    }
}
