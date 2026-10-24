#nullable enable

namespace Agas.Features.Canvas;

internal interface ICanvasCommand
{
    string Label { get; }
    void Do();
    void Undo();
}
