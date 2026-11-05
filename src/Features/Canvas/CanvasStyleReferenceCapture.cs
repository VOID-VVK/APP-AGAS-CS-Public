#nullable enable

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AppAgasCs.Application.Contracts;
using AppAgasCs.Core.Resources;

namespace Agas.Features.Canvas;

internal sealed class CanvasStyleReferenceException(string message) : InvalidOperationException(message);

/// <summary>Resolves an explicitly configured style attachment independently of Q and layer transforms.</summary>
internal static class CanvasStyleReferenceCapture
{
    internal static ImageInstanceResource? Resolve(
        AgasSceneResource scene,
        CanvasPatchGenerationMode mode,
        CanvasPatchLayerScope scope,
        string? currentLayerId)
    {
        AgasLayerResource[] layers = scene.LayersSnapshot.Where(layer => layer.AiStyleReference).ToArray();
        if (layers.Length == 0) return null;
        if (layers.Length != 1)
            throw new CanvasStyleReferenceException("双图生成需要且只能有一个画风参考层。");
        AgasLayerResource style = layers[0];
        if (!style.Visible || style.ImagesSnapshot.Count != 1)
            throw new CanvasStyleReferenceException("请先在画风参考层放入一张参考图片，并保持该层可见。当前不会发送生成请求。");
        if (mode != CanvasPatchGenerationMode.FullRegionGeneration)
            throw new CanvasStyleReferenceException("双图生成请只用 Q 框选模板范围，清除 W 局部编辑框。");
        if (scope == CanvasPatchLayerScope.CurrentLayer || style.Id == currentLayerId)
            throw new CanvasStyleReferenceException("请选择生图结果层，并使用所有图层发送或其他可见图层发送。");
        return style.ImagesSnapshot[0];
    }

    internal static CanvasPatchCapture Attach(CanvasPatchCapture capture, byte[] stylePng)
    {
        // Changes outside Q must invalidate a pending result just like changes inside Q.
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            capture.ContentFingerprint + ":style:" + Convert.ToHexString(SHA256.HashData(stylePng)))));
        return new CanvasPatchCapture(
            capture.ReferenceRegion, capture.ReplacementRegion, capture.GridSizePx,
            capture.ReferenceWorldPixels, capture.ReplacementWorldPixels, capture.EditRegionPixels,
            capture.SourceCanvasPng, capture.MaskPng, capture.ReplacementPatchPng,
            fingerprint, capture.TargetItem, capture.GenerationMode, stylePng);
    }
}
