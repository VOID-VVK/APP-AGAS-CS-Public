using Agas.Features.Canvas;
using AppAgasCs.Features.Ai.UI;
using Godot;

namespace AGAS.UI.Controllers;

/// <summary>
/// Node graph produced by <see cref="MainWorkspaceShellBuilder"/> for MainWorkspace field wiring
/// and dock-chrome binding.
/// </summary>
internal sealed class MainWorkspaceShellUi
{
    internal Label? TitleLabel { get; init; }
    internal Label? StatusLabel { get; init; }
    internal Button? UndoButton { get; init; }
    internal Button? RedoButton { get; init; }
    internal Button? SelectionToolButton { get; init; }
    internal Button? ReplacementToolButton { get; init; }
    internal Button? EditToolButton { get; init; }
    internal Label? CanvasSelectionStatusLabel { get; init; }
    internal Label? CanvasContextStatusLabel { get; init; }
    internal Button? CanvasZoomButton { get; init; }
    internal Label? AiTaskCountLabel { get; init; }
    internal OptionButton? AiProfileSelector { get; init; }
    internal OptionButton? AiQualitySelector { get; init; }
    internal SpinBox? GridSize { get; init; }
    internal CheckBox? SnapEnabled { get; init; }
    internal HSplitContainer? MainHorizontalSplit { get; init; }
    internal HSplitContainer? CenterRightSplit { get; init; }
    internal VSplitContainer? WorkspaceBottomSplit { get; init; }
    internal AGAS.UI.FilesScenesPanel? FilesScenesPanel { get; init; }
    internal AGAS.UI.LayerTabs? LayerTabs { get; init; }
    internal AGAS.UI.PropertiesPanel? PropertiesPanel { get; init; }
    internal AGAS.UI.PromptPanel? PromptPanel { get; init; }
    internal AGAS.UI.ToolboxPanel? ToolboxPanel { get; init; }
    internal AiGenerationPanel? AiGenerationPanel { get; init; }
    internal CanvasWorkspace? Canvas { get; init; }
    internal Control? LeftPanel { get; init; }
    internal Control? RightPanel { get; init; }
    internal Control? BottomPanel { get; init; }
    internal PanelContainer? LeftDock { get; init; }
    internal PanelContainer? RightDock { get; init; }
    internal PanelContainer? BottomDock { get; init; }
    internal Label? LeftDockTitle { get; init; }
    internal Label? RightDockTitle { get; init; }
    internal Label? BottomDockTitle { get; init; }
    internal Button? LeftDockToggle { get; init; }
    internal Button? RightDockToggle { get; init; }
    internal Button? BottomDockToggle { get; init; }
    internal TabContainer? BottomTabs { get; init; }
}
