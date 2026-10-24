#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AppAgasCs.Features.Tools.Model;
using AppAgasCs.Features.Tools.Storage;
using Godot;
using Godot.Collections;
using GodotDictionary = Godot.Collections.Dictionary;

namespace AppAgasCs.Features.Tools.Execution;

/// <summary>
/// Executes one image-to-image tool, or a strictly ordered chain, in the Godot process.
/// Custom GDScript is local trusted code: it has no sandbox and no hard timeout. A cancellation
/// request is observed before and after each script invocation, not while a script is running.
/// </summary>
public sealed class ImageToolRunner
{
    private readonly ImageToolExecutionLimits _limits;
    private readonly ImageToolSecureFileAccess _files;

    public ImageToolRunner(ImageToolExecutionLimits? limits = null)
        : this(limits, new ImageToolSecureFileAccess()) { }

    internal ImageToolRunner(ImageToolExecutionLimits? limits, ImageToolSecureFileAccess files)
    {
        _limits = limits ?? new ImageToolExecutionLimits();
        _files = files;
    }

    public ImageToolRunResult RunTool(
        string projectRoot,
        string inputRelativePath,
        ToolDefinition tool,
        string outputRelativePath,
        CancellationToken cancellationToken = default) =>
        Run(projectRoot, inputRelativePath, [tool], outputRelativePath, cancellationToken);

    public ImageToolRunResult RunChain(
        string projectRoot,
        string inputRelativePath,
        ToolChainDefinition chain,
        ToolCatalog catalog,
        string outputRelativePath,
        CancellationToken cancellationToken = default)
    {
        if (chain == null || catalog == null || chain.ToolIds is not { Count: > 0 and <= 32 })
            return ImageToolRunResult.Fail(ImageToolRunErrorCodes.ToolInvalid, "A nonempty tool chain of at most 32 steps is required.");
        try
        {
            ToolDefinition[] tools = chain.ToolIds.Select(catalog.LoadTool).ToArray();
            return Run(projectRoot, inputRelativePath, tools, outputRelativePath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            return ImageToolRunResult.Fail(ImageToolRunErrorCodes.ToolInvalid, exception.Message);
        }
    }

    private ImageToolRunResult Run(
        string projectRoot,
        string inputRelativePath,
        IReadOnlyList<ToolDefinition> tools,
        string outputRelativePath,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tools.Count is < 1 or > 32 || tools.Any(tool => tool == null))
                throw new ImageToolRunException(ImageToolRunErrorCodes.ToolInvalid, "At least one valid tool is required.");
            string root = ImageToolProjectPaths.ValidateRoot(projectRoot);
            string inputPath = ImageToolProjectPaths.ExistingImage(root, inputRelativePath);
            string outputPath = ImageToolProjectPaths.NewPng(root, outputRelativePath);
            Image current = LoadImage(root, inputPath, cancellationToken);
            try
            {
                foreach (ToolDefinition tool in tools)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Image next = ApplyTool(current, tool, cancellationToken);
                    current.Dispose();
                    current = next;
                }
                cancellationToken.ThrowIfCancellationRequested();
                SaveOutput(root, outputPath, current, cancellationToken);
                return ImageToolRunResult.Ok(outputRelativePath, current.GetWidth(), current.GetHeight());
            }
            finally { current.Dispose(); }
        }
        catch (OperationCanceledException)
        {
            return ImageToolRunResult.Fail(ImageToolRunErrorCodes.Cancelled, "Tool run was cancelled.");
        }
        catch (ImageToolRunException exception)
        {
            return ImageToolRunResult.Fail(exception.Code, exception.Message);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return ImageToolRunResult.Fail(ImageToolRunErrorCodes.PathInvalid, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            return ImageToolRunResult.Fail(ImageToolRunErrorCodes.OutputFailed, exception.Message);
        }
    }

    private Image LoadImage(string root, string path, CancellationToken cancellationToken)
    {
        byte[] bytes = _files.ReadImage(root, path, _limits.MaxInputBytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ImageToolImageHeader.TryRead(path, bytes, out int width, out int height))
            throw new ImageToolRunException(ImageToolRunErrorCodes.InputInvalid, "Input image header is invalid or unsupported.");
        _limits.ValidateWorkingSet(_limits.ValidateImage(width, height));
        Image decoder = new();
        try
        {
            Error error = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => decoder.LoadPngFromBuffer(bytes),
                ".jpg" or ".jpeg" => decoder.LoadJpgFromBuffer(bytes),
                ".webp" => decoder.LoadWebpFromBuffer(bytes),
                _ => Error.FileUnrecognized,
            };
            if (error != Error.Ok || decoder.IsEmpty())
                throw new ImageToolRunException(ImageToolRunErrorCodes.InputInvalid, "Input image could not be decoded.");
            _limits.ValidateWorkingSet(_limits.ValidateImage(decoder.GetWidth(), decoder.GetHeight()));
            return decoder;
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }

    private Image ApplyTool(Image input, ToolDefinition tool, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tool.Id))
            throw new ImageToolRunException(ImageToolRunErrorCodes.ToolInvalid, "Tool ID is required.");
        Image output = tool.Kind switch
        {
            ToolImplementationKind.BuiltIn => ApplyBuiltIn(input, tool, cancellationToken),
            ToolImplementationKind.Gdscript => ApplyScript(input, tool.Source),
            _ => throw new ImageToolRunException(ImageToolRunErrorCodes.ToolInvalid, "Unknown tool implementation kind."),
        };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output.IsEmpty())
                throw new ImageToolRunException(ImageToolRunErrorCodes.ToolInvalid, "Tool produced an empty image.");
            long outputPixels = _limits.ValidateImage(output.GetWidth(), output.GetHeight());
            long inputPixels = _limits.ValidateImage(input.GetWidth(), input.GetHeight());
            _limits.ValidateWorkingSet(checked(inputPixels + outputPixels));
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private Image ApplyBuiltIn(Image input, ToolDefinition tool, CancellationToken cancellationToken)
    {
        switch (tool.OperationId)
        {
            case "resize-256":
                _limits.ValidateWorkingSet(checked(_limits.ValidateImage(input.GetWidth(), input.GetHeight()) + _limits.ValidateImage(256, 256)));
                Image resized = (Image)input.Duplicate();
                resized.Resize(256, 256, Image.Interpolation.Nearest);
                return resized;
            case "crop-128":
                if (input.GetWidth() < 128 || input.GetHeight() < 128)
                    throw new ImageToolRunException(ImageToolRunErrorCodes.ToolInvalid, "Crop rectangle is outside the input image.");
                return input.GetRegion(new Rect2I(0, 0, 128, 128));
            case "matting-edge-connected":
            case "matting-checkerboard":
            case "matting-light-background":
            case "defringe-white":
                _limits.ValidateWorkingSet(checked(_limits.ValidateImage(input.GetWidth(), input.GetHeight()) * 2));
                return ImageToolPixelAlgorithms.Apply(tool.OperationId, input, cancellationToken);
            default:
                throw new ImageToolRunException(ImageToolRunErrorCodes.ToolInvalid, $"Unknown built-in operation '{tool.OperationId}'.");
        }
    }

    private static Image ApplyScript(Image input, string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || System.Text.Encoding.UTF8.GetByteCount(source) > 1_048_576)
            throw new ImageToolRunException(ImageToolRunErrorCodes.ToolInvalid, "GDScript source is empty or too large.");
        using GDScript script = new() { SourceCode = source };
        if (script.Reload(keepState: false) != Error.Ok || !script.CanInstantiate())
            throw new ImageToolRunException(ImageToolRunErrorCodes.ScriptFailed, "Tool GDScript failed to compile.");
        GodotObject? instance = null;
        try
        {
            instance = script.New().AsGodotObject();
            if (instance is not RefCounted || !instance.HasMethod("process"))
                throw new ImageToolRunException(ImageToolRunErrorCodes.ScriptFailed, "Tool script must extend RefCounted and implement process(image: Image).");
            Variant returned = instance.Call("process", input);
            if (returned.VariantType != Variant.Type.Dictionary)
                throw new ImageToolRunException(ImageToolRunErrorCodes.ScriptFailed, "process(image) must return a Dictionary containing 'image'.");
            using GodotDictionary values = returned.AsGodotDictionary();
            if (values.TryGetValue("error", out Variant error) && error.VariantType == Variant.Type.String)
                throw new ImageToolRunException(ImageToolRunErrorCodes.ScriptFailed, error.AsString());
            if (!values.TryGetValue("image", out Variant value) || value.AsGodotObject() is not Image image || image.IsEmpty())
                throw new ImageToolRunException(ImageToolRunErrorCodes.ScriptFailed, "Tool script did not return a valid Image.");
            return (Image)image.Duplicate();
        }
        catch (ImageToolRunException) { throw; }
        catch (Exception exception)
        {
            throw new ImageToolRunException(ImageToolRunErrorCodes.ScriptFailed, $"Tool GDScript failed: {exception.Message}");
        }
        finally { instance?.Dispose(); }
    }

    private void SaveOutput(string root, string outputPath, Image image, CancellationToken cancellationToken)
    {
        byte[] png = image.SavePngToBuffer();
        if (png.LongLength is 0 or > ImageToolExecutionLimits.AbsoluteMaxPngBytes)
            throw new ImageToolRunException(ImageToolRunErrorCodes.OutputFailed, "Encoded PNG exceeds the output limit.");
        _files.WritePng(root, outputPath, png, cancellationToken);
    }
}
