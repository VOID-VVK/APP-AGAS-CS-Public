#nullable enable

using AppAgasCs.Core.Resources;
using Godot;

namespace Agas.Features.Canvas;

/// <summary>Narrow host surface required by the canvas input state machine.</summary>
internal interface ICanvasInputHost
{
    CanvasRegionTool ActiveRegionTool { get; }
    bool LeftButtonDragsEmptyCanvas { get; }
    Vector2 ScreenToWorld(Vector2 screenPosition);
    Vector2 SnapWorldPosition(Vector2 worldPosition);
    ImageInstanceResource? HitTestCurrentEditableLayer(Vector2 worldPosition);
    Vector2 GetImagePosition(ImageInstanceResource image);
    bool BeginImageMovePreview(ImageInstanceResource image);
    void UpdateImageMovePreview(ImageInstanceResource image, Vector2 position);
    bool CommitImageMovePreview(ImageInstanceResource image, Vector2 position);
    void CancelImageMovePreview();
    bool CanMutate(string action);
    bool CanEditCurrentLayer();
    void SelectImage(ImageInstanceResource? image);
    void ClearSelection();
    void SetActiveRegionTool(CanvasRegionTool tool);
    void UpdateActiveRegion(Vector2 startWorld, Vector2 endWorld);
    void ZoomAt(Vector2 screenPosition, float factor);
    void PanByScreenDelta(Vector2 relative);
    void RequestContextMenu(Vector2 screenPosition);
    void QueueInputRedraw();
    void GrabInputFocus();
    void AcceptInputEvent();
    bool DeleteSelected();
    bool CopySelectedToClipboard();
    bool PasteClipboard();
    bool Undo();
    bool Redo();
}

/// <summary>Owns mouse, keyboard, region-drag, pan, and image-move interaction state.</summary>
internal sealed class CanvasInputController
{
    private readonly ICanvasInputHost _host;
    private bool _isPanning;
    private bool _isMovingSelection;
    private bool _movedSelectionDuringDrag;
    private Vector2 _moveStartMouseWorld;
    private Vector2 _moveStartImagePosition;
    private Vector2 _movePreviewPosition;
    private ImageInstanceResource? _movingImage;
    private bool _isDefiningRegion;
    private Vector2 _regionDragStartWorld;

    public CanvasInputController(ICanvasInputHost host)
    {
        _host = host;
    }

    public bool IsDefiningRegion => _isDefiningRegion;
    public bool HasActivePointerInteraction => _isPanning || _isMovingSelection || _isDefiningRegion;

    public void Handle(InputEvent @event)
    {
        if (@event is InputEventMagnifyGesture magnifyGesture)
        {
            _host.ZoomAt(magnifyGesture.Position, magnifyGesture.Factor);
            _host.AcceptInputEvent();
            return;
        }

        if (@event is InputEventMouseButton mouseButton)
        {
            HandleMouseButton(mouseButton);
            return;
        }

        if (@event is InputEventMouseMotion mouseMotion)
        {
            HandleMouseMotion(mouseMotion);
            return;
        }

        if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo)
        {
            HandleKeyInput(keyEvent);
        }
    }

    public void CancelPointerInteraction()
    {
        if (_isMovingSelection)
        {
            _host.CancelImageMovePreview();
        }

        _isPanning = false;
        _isMovingSelection = false;
        _movedSelectionDuringDrag = false;
        _movingImage = null;
        _isDefiningRegion = false;
    }

    private void HandleMouseButton(InputEventMouseButton mouseButton)
    {
        if (mouseButton.ButtonIndex == MouseButton.WheelUp && mouseButton.Pressed)
        {
            _host.ZoomAt(mouseButton.Position, CanvasViewportController.ZoomStepFactor);
            _host.AcceptInputEvent();
            return;
        }

        if (mouseButton.ButtonIndex == MouseButton.WheelDown && mouseButton.Pressed)
        {
            _host.ZoomAt(mouseButton.Position, 1.0f / CanvasViewportController.ZoomStepFactor);
            _host.AcceptInputEvent();
            return;
        }

        if (mouseButton.ButtonIndex == MouseButton.Middle)
        {
            _isPanning = mouseButton.Pressed;
            _host.AcceptInputEvent();
            return;
        }

        if (mouseButton.ButtonIndex == MouseButton.Right && mouseButton.Pressed)
        {
            _host.GrabInputFocus();
            _host.RequestContextMenu(mouseButton.Position);
            _host.AcceptInputEvent();
            return;
        }

        if (mouseButton.ButtonIndex != MouseButton.Left) return;

        if (_host.ActiveRegionTool != CanvasRegionTool.Edit)
        {
            HandleRegionMouseButton(mouseButton);
            return;
        }

        if (mouseButton.Pressed)
        {
            BeginEditPointer(mouseButton.Position);
            return;
        }

        EndEditPointer();
    }

    private void HandleRegionMouseButton(InputEventMouseButton mouseButton)
    {
        _host.GrabInputFocus();
        Vector2 world = _host.ScreenToWorld(mouseButton.Position);
        if (mouseButton.Pressed)
        {
            _regionDragStartWorld = world;
            _isDefiningRegion = true;
        }

        if (_isDefiningRegion) _host.UpdateActiveRegion(_regionDragStartWorld, world);
        if (!mouseButton.Pressed) _isDefiningRegion = false;
        _host.AcceptInputEvent();
    }

    private void BeginEditPointer(Vector2 screenPosition)
    {
        _host.GrabInputFocus();
        Vector2 world = _host.ScreenToWorld(screenPosition);
        ImageInstanceResource? hit = _host.HitTestCurrentEditableLayer(world);
        if (hit != null)
        {
            _host.SelectImage(hit);
            _isMovingSelection = _host.CanMutate("移动图片") && _host.BeginImageMovePreview(hit);
            _movedSelectionDuringDrag = false;
            _movingImage = _isMovingSelection ? hit : null;
            _moveStartMouseWorld = world;
            _moveStartImagePosition = _host.GetImagePosition(hit);
            _movePreviewPosition = _moveStartImagePosition;
        }
        else
        {
            _host.ClearSelection();
            _isPanning = _host.LeftButtonDragsEmptyCanvas;
        }

        _host.AcceptInputEvent();
    }

    private void EndEditPointer()
    {
        if (_isMovingSelection && _movingImage != null)
        {
            if (!_host.CanMutate("移动图片"))
            {
                RollBackMove();
                _host.QueueInputRedraw();
                _host.AcceptInputEvent();
                return;
            }

            if (_movedSelectionDuringDrag)
            {
                if (!_moveStartImagePosition.IsEqualApprox(_movePreviewPosition))
                {
                    _host.CommitImageMovePreview(_movingImage, _movePreviewPosition);
                }
                else
                {
                    _host.CancelImageMovePreview();
                }
            }
            else
            {
                _host.CancelImageMovePreview();
            }

            ClearMoveState();
            _host.AcceptInputEvent();
            return;
        }

        _isPanning = false;
        _host.AcceptInputEvent();
    }

    private void HandleMouseMotion(InputEventMouseMotion mouseMotion)
    {
        if (_isDefiningRegion && _host.ActiveRegionTool != CanvasRegionTool.Edit)
        {
            _host.UpdateActiveRegion(_regionDragStartWorld, _host.ScreenToWorld(mouseMotion.Position));
            _host.AcceptInputEvent();
            return;
        }

        if (_isPanning)
        {
            _host.PanByScreenDelta(mouseMotion.Relative);
            _host.AcceptInputEvent();
            return;
        }

        if (_host.ActiveRegionTool != CanvasRegionTool.Edit
            || !_isMovingSelection
            || _movingImage == null
            || !_host.CanEditCurrentLayer())
        {
            return;
        }

        if (!_host.CanMutate("移动图片"))
        {
            RollBackMove();
            _host.QueueInputRedraw();
            return;
        }

        Vector2 currentMouseWorld = _host.ScreenToWorld(mouseMotion.Position);
        Vector2 target = _moveStartImagePosition + currentMouseWorld - _moveStartMouseWorld;
        _movePreviewPosition = _host.SnapWorldPosition(target);
        _host.UpdateImageMovePreview(_movingImage, _movePreviewPosition);
        _movedSelectionDuringDrag = true;
        _host.AcceptInputEvent();
    }

    private void HandleKeyInput(InputEventKey keyEvent)
    {
        bool command = keyEvent.CommandOrControlAutoremap;
        switch (keyEvent.Keycode)
        {
            case Key.Q when !command:
                _host.SetActiveRegionTool(CanvasRegionTool.Selection);
                _host.AcceptInputEvent();
                break;
            case Key.W when !command:
                _host.SetActiveRegionTool(CanvasRegionTool.Replacement);
                _host.AcceptInputEvent();
                break;
            case Key.E when !command:
            case Key.Escape:
                _host.SetActiveRegionTool(CanvasRegionTool.Edit);
                _host.AcceptInputEvent();
                break;
            case Key.Delete:
            case Key.Backspace:
                _host.DeleteSelected();
                _host.AcceptInputEvent();
                break;
            case Key.C when command:
                _host.CopySelectedToClipboard();
                _host.AcceptInputEvent();
                break;
            case Key.V when command:
                _host.PasteClipboard();
                _host.AcceptInputEvent();
                break;
            case Key.Z when command && keyEvent.ShiftPressed:
                _host.Redo();
                _host.AcceptInputEvent();
                break;
            case Key.Z when command:
                _host.Undo();
                _host.AcceptInputEvent();
                break;
            case Key.Y when command:
                _host.Redo();
                _host.AcceptInputEvent();
                break;
        }
    }

    private void RollBackMove()
    {
        _host.CancelImageMovePreview();
        ClearMoveState();
    }

    private void ClearMoveState()
    {
        _isMovingSelection = false;
        _movedSelectionDuringDrag = false;
        _movingImage = null;
    }
}
