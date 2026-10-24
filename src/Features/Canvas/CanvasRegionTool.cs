#nullable enable

namespace Agas.Features.Canvas;

public enum CanvasRegionTool
{
    /// <summary>Normal canvas editing: select and drag image instances.</summary>
    Edit = 0,

    /// <summary>Compatibility alias for older callers. Zero now means Edit.</summary>
    None = Edit,

    Selection = 1,
    Replacement = 2
}
