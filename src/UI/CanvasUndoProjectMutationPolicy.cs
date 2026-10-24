using System;
using AppAgasCs.Application;
using Agas.Features.Canvas;
using Agas.Features.Commands;

namespace AGAS.UI;

/// <summary>Adapts application mutations to the existing Canvas undo/redo stack.</summary>
internal sealed class CanvasUndoProjectMutationPolicy : IProjectMutationExecutionPolicy
{
    private readonly CanvasWorkspace _canvas;
    private readonly CanvasEditScope _scope;

    public CanvasUndoProjectMutationPolicy(CanvasWorkspace canvas, CanvasEditScope scope)
    {
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        _scope = scope;
    }

    public void Execute(IProjectMutationOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!_canvas.ExecuteCanvasEdit(
                operation.Description,
                _scope,
                operation.Apply,
                operation.Revert))
        {
            throw new InvalidOperationException($"Canvas rejected project mutation '{operation.Description}'.");
        }
    }
}
