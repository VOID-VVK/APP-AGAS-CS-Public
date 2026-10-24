#nullable enable

using AppAgasCs.Application.Contracts;
using Godot;
using System;

namespace Agas.Features.Canvas;

/// <summary>
/// Pure Q/W region interaction state for the canvas.
/// Godot side effects (signals, redraw, pointer cancel) stay on CanvasWorkspace.
/// </summary>
internal sealed class CanvasRegionSelectionState
{
    public CanvasRegionTool ActiveTool { get; private set; } = CanvasRegionTool.Edit;

    public CanvasGridRegion? SelectionRegion { get; private set; }

    public CanvasGridRegion? ReplacementRegion { get; private set; }

    public CanvasGridRegion? EffectiveGenerationRegion => SelectionRegion.HasValue
        ? ReplacementRegion ?? SelectionRegion
        : null;

    /// <summary>
    /// Validates and assigns complete Q/W state. Sets the active tool to Replacement
    /// when W is present, otherwise Selection. Does not emit signals or redraw.
    /// </summary>
    public bool TrySet(CanvasGridRegion selection, CanvasGridRegion? replacement)
    {
        if (!IsValid(selection) || (replacement.HasValue && !IsValid(replacement.Value)))
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

        SelectionRegion = selection;
        ReplacementRegion = replacement;
        ActiveTool = replacement.HasValue ? CanvasRegionTool.Replacement : CanvasRegionTool.Selection;
        return true;
    }

    /// <summary>Sets the active region tool. Returns true when the tool value changed.</summary>
    public bool SetTool(CanvasRegionTool tool)
    {
        if (ActiveTool == tool)
        {
            return false;
        }

        ActiveTool = tool;
        return true;
    }

    /// <summary>
    /// Clears Q/W and resets the tool to Edit. Returns true when region state or tool changed.
    /// </summary>
    public bool Clear()
    {
        bool changed = SelectionRegion.HasValue
            || ReplacementRegion.HasValue
            || ActiveTool != CanvasRegionTool.Edit;
        SelectionRegion = null;
        ReplacementRegion = null;
        ActiveTool = CanvasRegionTool.Edit;
        return changed;
    }

    /// <summary>
    /// Updates the active region from a world-space drag. When redrawing Q, clears W
    /// if it falls outside the new selection.
    /// </summary>
    public void UpdateFromDrag(Vector2 start, Vector2 end, int gridSizePx)
    {
        CanvasGridRegion region = CanvasGridRegion.FromWorldDrag(start, end, gridSizePx);
        if (ActiveTool == CanvasRegionTool.Selection)
        {
            SelectionRegion = region;
            // Concurrent tile workflow: user redraws Q for the next box while a
            // previous generation is still running. A leftover W outside the new Q
            // used to disable generation and made it look like only one box could run.
            if (ReplacementRegion is CanvasGridRegion replacement && !region.Contains(replacement))
            {
                ReplacementRegion = null;
            }
        }
        else if (ActiveTool == CanvasRegionTool.Replacement)
        {
            ReplacementRegion = region;
        }
    }

    public static bool IsValid(CanvasGridRegion region)
    {
        if (region.Width < 1 || region.Height < 1)
        {
            return false;
        }

        long endColumn = (long)region.Column + region.Width;
        long endRow = (long)region.Row + region.Height;
        return endColumn <= int.MaxValue
            && endColumn >= int.MinValue
            && endRow <= int.MaxValue
            && endRow >= int.MinValue;
    }

    public static CanvasGridRegion Union(CanvasGridRegion first, CanvasGridRegion second)
    {
        int left = Math.Min(first.Column, second.Column);
        int top = Math.Min(first.Row, second.Row);
        int right = Math.Max(first.EndColumnExclusive, second.EndColumnExclusive);
        int bottom = Math.Max(first.EndRowExclusive, second.EndRowExclusive);
        return new CanvasGridRegion(left, top, right - left, bottom - top);
    }
}
