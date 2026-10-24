namespace AppAgasCs.Application.Contracts;

public enum CanvasSelectionTool
{
    Edit,
    Selection,
    Replacement,
}

public readonly record struct CanvasSelectionRegion(int Column, int Row, int Width, int Height);

/// <summary>
/// Application-owned port for reading and changing the live canvas selection.
/// MCP is one adapter of this port; the canvas must not depend on MCP types.
/// </summary>
public interface ICanvasSelectionWorkspaceBridge
{
    string SceneId { get; }
    CanvasSelectionTool ActiveRegionTool { get; }
    CanvasSelectionRegion? SelectionRegion { get; }
    CanvasSelectionRegion? ReplacementRegion { get; }
    CanvasSelectionRegion? EffectiveGenerationRegion { get; }
    string? SelectedImageId { get; }
    string? SelectedLayerId { get; }
    int GridSizePx { get; }
    bool CanEditCurrentLayer { get; }

    bool TrySetRegions(CanvasSelectionRegion selection, CanvasSelectionRegion? replacement);
    void ClearRegions();
    bool TrySelectImage(string imageId);
    void ClearImageSelection();
}
