using AppAgasCs.Application.Contracts;
using AppAgasCs.Mcp.Contracts;

namespace AppAgasCs.Mcp;

public static class AgasMcpSelectionValidation
{
    public static bool TryCreateRegion(AgasMcpGridRegionDto input, out CanvasSelectionRegion region)
    {
        region = default;
        if (input.Width < 1 || input.Height < 1)
        {
            return false;
        }

        long endColumn = (long)input.Column + input.Width;
        long endRow = (long)input.Row + input.Height;
        if (endColumn > int.MaxValue || endColumn < int.MinValue
            || endRow > int.MaxValue || endRow < int.MinValue)
        {
            return false;
        }

        region = new CanvasSelectionRegion(input.Column, input.Row, input.Width, input.Height);
        return true;
    }

    public static bool IsLayerEditable(bool visible, bool locked) => visible && !locked;

    public static bool IsActiveScene(string? activeSceneId, string? currentSceneId, string requestedSceneId) =>
        string.Equals(activeSceneId, requestedSceneId, System.StringComparison.Ordinal)
        && string.Equals(currentSceneId, requestedSceneId, System.StringComparison.Ordinal);
}
