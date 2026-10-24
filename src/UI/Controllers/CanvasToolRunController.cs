#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AppAgasCs.Features.Tools.Execution;
using AppAgasCs.Features.Tools.Model;
using AppAgasCs.Features.Tools.Storage;

namespace AGAS.UI.Controllers;

internal sealed record CanvasToolSelection(string Id, string Name, bool IsChain, int Revision);

internal sealed record CanvasToolRunContext(
    Guid ProjectSessionId,
    string ProjectRoot,
    string ProjectId,
    string SceneId,
    string LayerId,
    string ImageId,
    string ImagePath,
    bool CanMutate,
    string ReadOnlyReasonCode);

internal sealed record CanvasToolRunResult(bool Success, string Code, string Message, string RelativePath)
{
    internal static CanvasToolRunResult Ok(string relativePath) => new(true, "", "", relativePath);
    internal static CanvasToolRunResult Fail(string code, string message) => new(false, code, message, "");
}

internal static class CanvasToolRunErrorCodes
{
    internal const string ContextUnavailable = "CANVAS_TOOL_CONTEXT_UNAVAILABLE";
    internal const string ContextStale = "CANVAS_TOOL_CONTEXT_STALE";
    internal const string ToolChanged = "CANVAS_TOOL_CHANGED";
    internal const string ApplyFailed = "CANVAS_TOOL_APPLY_FAILED";
}

/// <summary>Runs one tool or ordered chain against the selected image and inserts one undoable result.</summary>
internal sealed class CanvasToolRunController
{
    private readonly Func<CanvasToolRunContext?> _captureContext;
    private readonly Func<CanvasToolRunContext, string, bool> _apply;
    private readonly ToolCatalog _catalog;
    private readonly ImageToolRunner _runner;
    private readonly object _gate = new();
    private CancellationTokenSource? _activeCancellation;

    internal CanvasToolRunController(
        Func<CanvasToolRunContext?> captureContext,
        Func<CanvasToolRunContext, string, bool> apply,
        ToolCatalog catalog,
        ImageToolRunner? runner = null)
    {
        _captureContext = captureContext ?? throw new ArgumentNullException(nameof(captureContext));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _runner = runner ?? new ImageToolRunner();
    }

    internal IReadOnlyList<CanvasToolSelection> ListAvailable()
    {
        if (!IsUsable(_captureContext())) return Array.Empty<CanvasToolSelection>();
        return _catalog.ListTools().Select(tool => new CanvasToolSelection(tool.Id, tool.Name, false, tool.Revision))
            .Concat(_catalog.ListChains().Select(chain =>
                new CanvasToolSelection(chain.Id, chain.Name, true, chain.Revision)))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    // The runner invokes Godot Image and GDScript APIs and must remain on the main thread.
    internal CanvasToolRunResult Run(CanvasToolSelection selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        CanvasToolRunContext? frozen = _captureContext();
        if (!IsUsable(frozen))
            return CanvasToolRunResult.Fail(CanvasToolRunErrorCodes.ContextUnavailable,
                frozen == null || string.IsNullOrWhiteSpace(frozen.ReadOnlyReasonCode)
                    ? "请先选中可编辑图层中的图片。"
                    : $"项目为只读状态（{frozen.ReadOnlyReasonCode}）。");

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _activeCancellation?.Cancel();
            _activeCancellation = linkedCancellation;
        }

        string output = $"agas/tools/{SanitizeName(selection.Id)}-{Guid.NewGuid():N}.png";
        try
        {
            EnsureOutputDirectory(frozen!.ProjectRoot);
            ImageToolRunResult result;
            if (selection.IsChain)
            {
                ToolChainDefinition chain = _catalog.LoadChain(selection.Id);
                if (chain.Revision != selection.Revision) return Changed();
                ToolDefinition[] steps = chain.ToolIds.Select(_catalog.LoadTool).ToArray();
                var revisions = steps.ToDictionary(step => step.Id, step => step.Revision, StringComparer.Ordinal);
                result = _runner.RunChain(frozen.ProjectRoot, frozen.ImagePath, chain, _catalog, output, linkedCancellation.Token);
                if (!result.Success) return CanvasToolRunResult.Fail(result.Code, result.Message);
                if (_catalog.LoadChain(selection.Id).Revision != selection.Revision
                    || revisions.Any(step => _catalog.LoadTool(step.Key).Revision != step.Value))
                {
                    DeleteOutput(frozen.ProjectRoot, output);
                    return Changed();
                }
            }
            else
            {
                ToolDefinition tool = _catalog.LoadTool(selection.Id);
                if (tool.Revision != selection.Revision) return Changed();
                result = _runner.RunTool(frozen.ProjectRoot, frozen.ImagePath, tool, output, linkedCancellation.Token);
                if (!result.Success) return CanvasToolRunResult.Fail(result.Code, result.Message);
                if (_catalog.LoadTool(selection.Id).Revision != selection.Revision)
                {
                    DeleteOutput(frozen.ProjectRoot, output);
                    return Changed();
                }
            }

            if (linkedCancellation.IsCancellationRequested || !SameTarget(frozen, _captureContext()))
            {
                DeleteOutput(frozen.ProjectRoot, output);
                return CanvasToolRunResult.Fail(CanvasToolRunErrorCodes.ContextStale,
                    "执行期间项目、场景、图层或所选图片已变化，结果未应用。");
            }
            if (!_apply(frozen, output))
            {
                DeleteOutput(frozen.ProjectRoot, output);
                return CanvasToolRunResult.Fail(CanvasToolRunErrorCodes.ApplyFailed,
                    "画布目标已变化或不可编辑，工具结果未应用。");
            }
            return CanvasToolRunResult.Ok(output);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                   or InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            DeleteOutput(frozen!.ProjectRoot, output);
            return CanvasToolRunResult.Fail(ImageToolRunErrorCodes.ToolInvalid, exception.Message);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeCancellation, linkedCancellation)) _activeCancellation = null;
            }
        }
    }

    internal void Invalidate()
    {
        lock (_gate) _activeCancellation?.Cancel();
    }

    private static CanvasToolRunResult Changed() => CanvasToolRunResult.Fail(
        CanvasToolRunErrorCodes.ToolChanged, "所选工具在执行期间发生变化，请刷新后重试。");

    private static bool IsUsable(CanvasToolRunContext? context) =>
        context != null && context.CanMutate && !string.IsNullOrWhiteSpace(context.ImagePath);

    private static bool SameTarget(CanvasToolRunContext expected, CanvasToolRunContext? current) =>
        current != null && current.CanMutate
        && current.ProjectSessionId == expected.ProjectSessionId
        && string.Equals(current.ProjectRoot, expected.ProjectRoot, StringComparison.Ordinal)
        && string.Equals(current.ProjectId, expected.ProjectId, StringComparison.Ordinal)
        && string.Equals(current.SceneId, expected.SceneId, StringComparison.Ordinal)
        && string.Equals(current.LayerId, expected.LayerId, StringComparison.Ordinal)
        && string.Equals(current.ImageId, expected.ImageId, StringComparison.Ordinal)
        && string.Equals(current.ImagePath, expected.ImagePath, StringComparison.Ordinal);

    private static string SanitizeName(string id) =>
        new(id.Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_').Take(48).ToArray());

    private static void EnsureOutputDirectory(string projectRoot)
    {
        string root = ImageToolProjectPaths.ValidateRoot(projectRoot);
        string agas = Path.Combine(root, "agas");
        string tools = Path.Combine(agas, "tools");
        EnsureOrdinaryDirectory(agas);
        EnsureOrdinaryDirectory(tools);
    }

    private static void EnsureOrdinaryDirectory(string path)
    {
        if (File.Exists(path) || IsLink(path))
            throw new InvalidDataException("Tool output directory is not an ordinary directory.");
        Directory.CreateDirectory(path);
        if (IsLink(path))
            throw new InvalidDataException("Tool output directory cannot be a symbolic link.");
    }

    private static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void DeleteOutput(string projectRoot, string relativePath)
    {
        try
        {
            string path = Path.Combine(projectRoot, relativePath);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
