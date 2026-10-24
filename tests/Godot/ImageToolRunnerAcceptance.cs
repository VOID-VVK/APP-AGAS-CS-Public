#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Threading;
using AppAgasCs.Features.Tools.Execution;
using AppAgasCs.Features.Tools.Storage;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class ImageToolRunnerAcceptance : Node
{
    private const string Prefix = "[AGAS][IMAGE-TOOL-RUNNER]";

    public override void _Ready()
    {
        int exitCode = 0;
        string root = Path.Combine(Path.GetTempPath(), "agas-tool-runner-" + Guid.NewGuid().ToString("N"));
        try
        {
            string project = Path.Combine(root, "project");
            string assets = Path.Combine(project, "assets");
            Directory.CreateDirectory(assets);
            byte[] data = new byte[1024 * 512 * 4];
            for (int y = 0; y < 512; y++)
            for (int x = 0; x < 1024; x++)
            {
                int pixel = (y * 1024 + x) * 4;
                data[pixel] = (byte)(x / 64 * 13 + x % 64);
                data[pixel + 1] = (byte)(y / 64 * 27 + y % 64);
                data[pixel + 2] = (byte)(x ^ y);
                data[pixel + 3] = 255;
            }
            using (Image input = Image.CreateFromData(1024, 512, false, Image.Format.Rgba8, data))
                Require(input.SavePng(Path.Combine(assets, "input.png")) == Error.Ok, "Could not create 1024x512 fixture.");

            var catalog = new ToolCatalog(Path.Combine(root, "home"));
            var runner = new ImageToolRunner();
            var reverse = catalog.LoadTool("tile47-standard-reverse");
            var isometric = catalog.LoadTool("tile47-isometric");
            Require(!reverse.IsReadOnly && !isometric.IsReadOnly, "Tile47 presets must remain editable tools.");

            ImageToolRunResult first = runner.RunTool(project, "assets/input.png", reverse, "assets/manual-reverse.png");
            Require(first.Success, first.Code + ": " + first.Message);
            Require(first.Width == 768 && first.Height == 256, "Reverse result must be 768x256.");
            using (Image reverseImage = Image.LoadFromFile(Path.Combine(assets, "manual-reverse.png")))
            {
                Require(reverseImage.GetPixel(0, 0).IsEqualApprox(ColorFrom(data, 0, 128)), "Reverse cell mapping changed.");
                Require(reverseImage.GetPixel(10 * 64, 64).A == 0, "Special transparent slot changed.");
            }

            ImageToolRunResult second = runner.RunTool(project, "assets/manual-reverse.png", isometric, "assets/manual-final.png");
            Require(second.Success, second.Code + ": " + second.Message);
            Require(second.Width == 1536 && second.Height == 512, "Isometric result must be 1536x512.");

            var chain = catalog.CreateChain("tile47-two-steps", "Tile47 两步", [reverse.Id, isometric.Id]);
            ImageToolRunResult chained = runner.RunChain(project, "assets/input.png", chain, catalog, "assets/chained-final.png");
            Require(chained.Success, chained.Code + ": " + chained.Message);
            using (Image manual = Image.LoadFromFile(Path.Combine(assets, "manual-final.png")))
            using (Image combined = Image.LoadFromFile(Path.Combine(assets, "chained-final.png")))
                Require(manual.GetData().SequenceEqual(combined.GetData()), "Manual two-step pixels differ from chain pixels.");

            foreach (string id in new[]
            {
                "resize-256", "crop-128", "matting-edge-connected", "matting-checkerboard",
                "matting-light-single", "defringe-white",
            })
            {
                ImageToolRunResult builtIn = runner.RunTool(project, "assets/input.png", catalog.LoadTool(id), $"assets/{id}.png");
                Require(builtIn.Success, $"Built-in {id} failed: {builtIn.Code}: {builtIn.Message}");
                Require(File.Exists(Path.Combine(assets, id + ".png")), $"Built-in {id} did not save its output.");
            }
            var custom = catalog.CreateTool("custom-pass", "Pass", """
extends RefCounted
func process(image: Image) -> Dictionary:
	return {"image": image}
""");
            ImageToolRunResult customResult = runner.RunTool(project, "assets/input.png", custom, "assets/custom-pass.png");
            Require(customResult.Success && customResult.Width == 1024 && customResult.Height == 512,
                $"Custom GDScript tool failed: {customResult.Code}: {customResult.Message}");

            var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            ImageToolRunResult cancelledResult = runner.RunTool(project, "assets/input.png", reverse, "assets/cancelled.png", cancelled.Token);
            Require(!cancelledResult.Success && cancelledResult.Code == ImageToolRunErrorCodes.Cancelled, "Cancelled run was accepted.");
            Require(!File.Exists(Path.Combine(assets, "cancelled.png")), "Cancelled run wrote output.");

            ImageToolRunResult path = runner.RunTool(project, "../outside.png", reverse, "assets/invalid.png");
            Require(!path.Success && path.Code == ImageToolRunErrorCodes.PathInvalid, "Traversal input was accepted.");
            if (!OperatingSystem.IsWindows())
            {
                string link = Path.Combine(assets, "linked.png");
                File.CreateSymbolicLink(link, Path.Combine(assets, "input.png"));
                ImageToolRunResult linked = runner.RunTool(project, "assets/linked.png", reverse, "assets/linked-result.png");
                Require(!linked.Success && linked.Code == ImageToolRunErrorCodes.PathInvalid, "Linked input was accepted.");
            }
            ImageToolRunResult budget = new ImageToolRunner(new ImageToolExecutionLimits(maxPixels: 100))
                .RunTool(project, "assets/input.png", reverse, "assets/budget.png");
            Require(!budget.Success && budget.Code == ImageToolRunErrorCodes.ImageLimitExceeded, "Oversize input was accepted.");
            ImageToolRunResult conflict = runner.RunTool(project, "assets/input.png", reverse, "assets/manual-reverse.png");
            Require(!conflict.Success && conflict.Code == ImageToolRunErrorCodes.OutputExists, "Existing PNG was overwritten.");
            Require(!Directory.GetFiles(assets, ".agas-tool-*.tmp").Any(), "Temporary output was left behind.");

            GD.Print($"{Prefix}[PASS] 1024x512 -> 768x256 -> 1536x512; manual and chain pixels match; guards passed");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {exception}");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
            GetTree().Quit(exitCode);
        }
    }

    private static Color ColorFrom(byte[] rgba, int x, int y)
    {
        int offset = (y * 1024 + x) * 4;
        return Color.Color8(rgba[offset], rgba[offset + 1], rgba[offset + 2], rgba[offset + 3]);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
