using Godot;

namespace AGAS.UI;

public partial class MainWorkspace
{
    private VBoxContainer? _canvasDocumentHost;

    private void BuildDocumentTabs(VBoxContainer center)
    {
        _canvasDocumentHost = new VBoxContainer
        {
            Name = "CanvasDocumentHost",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _canvasDocumentHost.AddThemeConstantOverride("separation", 4);
        center.AddChild(_canvasDocumentHost);
    }

    private void AddCanvasDocumentChild(Control child) => _canvasDocumentHost!.AddChild(child);

    private void RefreshToolbox() => _toolboxPanel?.RefreshTools();

    private void UpdateDocumentVisibility()
    {
        if (_canvasDocumentHost != null) _canvasDocumentHost.Visible = true;
        if (_propertiesPanel != null) _propertiesPanel.Visible = !_dockChrome.RightDockCollapsed;
    }

    private void RefreshCanvasUndoRedo() =>
        UpdateUndoRedoButtons(_canvas?.CanUndo() == true, _canvas?.CanRedo() == true);

    private bool UndoActiveDocument()
    {
        if (!CanMutateProject("撤销")) return false;
        return _canvas?.Undo() == true;
    }

    private bool RedoActiveDocument()
    {
        if (!CanMutateProject("重做")) return false;
        return _canvas?.Redo() == true;
    }

    private bool HasTextEditorFocus() => GetViewport()?.GuiGetFocusOwner() is LineEdit or TextEdit;
}
