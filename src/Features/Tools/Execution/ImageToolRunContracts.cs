#nullable enable

using System;

namespace AppAgasCs.Features.Tools.Execution;

public static class ImageToolRunErrorCodes
{
    public const string PathInvalid = "TOOL_PATH_INVALID";
    public const string InputInvalid = "TOOL_INPUT_INVALID";
    public const string ToolInvalid = "TOOL_INVALID";
    public const string ScriptFailed = "TOOL_SCRIPT_FAILED";
    public const string ImageLimitExceeded = "TOOL_IMAGE_LIMIT_EXCEEDED";
    public const string MemoryBudgetExceeded = "TOOL_MEMORY_BUDGET_EXCEEDED";
    public const string OutputExists = "TOOL_OUTPUT_EXISTS";
    public const string OutputFailed = "TOOL_OUTPUT_FAILED";
    public const string Cancelled = "TOOL_CANCELLED";
}

public sealed record ImageToolRunResult(
    bool Success,
    string Code,
    string Message,
    string RelativePath,
    int Width,
    int Height)
{
    public static ImageToolRunResult Ok(string path, int width, int height) =>
        new(true, "", "", path, width, height);

    public static ImageToolRunResult Fail(string code, string message) =>
        new(false, code, message, "", 0, 0);
}

public sealed class ImageToolExecutionLimits
{
    public const int AbsoluteMaxDimension = 2048;
    public const long AbsoluteMaxPixels = 4_194_304;
    public const long AbsoluteMaxInputBytes = 32L * 1024 * 1024;
    public const long AbsoluteMaxLiveBytes = 256L * 1024 * 1024;
    public const long AbsoluteMaxPngBytes = 64L * 1024 * 1024;

    public ImageToolExecutionLimits(
        int maxDimension = AbsoluteMaxDimension,
        long maxPixels = AbsoluteMaxPixels,
        long maxInputBytes = AbsoluteMaxInputBytes,
        long maxLiveBytes = AbsoluteMaxLiveBytes)
    {
        MaxDimension = Math.Clamp(maxDimension, 1, AbsoluteMaxDimension);
        MaxPixels = Math.Clamp(maxPixels, 1, AbsoluteMaxPixels);
        MaxInputBytes = Math.Clamp(maxInputBytes, 1, AbsoluteMaxInputBytes);
        MaxLiveBytes = Math.Clamp(maxLiveBytes, 1, AbsoluteMaxLiveBytes);
    }

    public int MaxDimension { get; }
    public long MaxPixels { get; }
    public long MaxInputBytes { get; }
    public long MaxLiveBytes { get; }

    public long ValidateImage(int width, int height)
    {
        if (width < 1 || height < 1 || width > MaxDimension || height > MaxDimension)
            throw new ImageToolRunException(ImageToolRunErrorCodes.ImageLimitExceeded, "Image dimensions exceed the tool limit.");
        long pixels = checked((long)width * height);
        if (pixels > MaxPixels)
            throw new ImageToolRunException(ImageToolRunErrorCodes.ImageLimitExceeded, "Image pixels exceed the tool limit.");
        return pixels;
    }

    public void ValidateWorkingSet(long pixels)
    {
        // Godot Image may temporarily hold more than RGBA8; reserve 32 bytes/pixel for
        // the input, output, conversion and encoder together.
        if (checked(pixels * 32L) > MaxLiveBytes)
            throw new ImageToolRunException(ImageToolRunErrorCodes.MemoryBudgetExceeded, "Tool memory budget exceeded.");
    }
}

public sealed class ImageToolRunException : Exception
{
    public ImageToolRunException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}
