#nullable enable

using System;

namespace Agas.Features.Canvas;

internal sealed class DelegateCanvasCommand : ICanvasCommand
{
    private readonly Action _doAction;
    private readonly Action _undoAction;

    public DelegateCanvasCommand(string label, Action doAction, Action undoAction)
    {
        Label = label;
        _doAction = doAction;
        _undoAction = undoAction;
    }

    public string Label { get; }

    public void Do() => _doAction();

    public void Undo() => _undoAction();
}
