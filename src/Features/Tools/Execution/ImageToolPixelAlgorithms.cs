#nullable enable

using System.Threading;
using Godot;

namespace AppAgasCs.Features.Tools.Execution;

internal static class ImageToolPixelAlgorithms
{
    public static Image Apply(string operationId, Image source, CancellationToken cancellationToken)
    {
        using Image rgba = (Image)source.Duplicate();
        if (rgba.GetFormat() != Image.Format.Rgba8) rgba.Convert(Image.Format.Rgba8);
        int width = rgba.GetWidth();
        int height = rgba.GetHeight();
        byte[] pixels = rgba.GetData();
        byte[] result = ToolMattingAlgorithmRegistry.TryGet(operationId, out IToolMattingAlgorithm matting)
            ? matting.Process(pixels, width, height, cancellationToken)
            : ToolPostProcessAlgorithmRegistry.GetRequired(operationId).Process(pixels, width, height, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Image.CreateFromData(width, height, false, Image.Format.Rgba8, result);
    }
}
