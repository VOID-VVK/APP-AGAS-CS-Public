#nullable enable

using System;
using System.IO;
using System.Linq;
using AppAgasCs.Features.Tools.Execution;
using AppAgasCs.Features.Tools.Model;
using AppAgasCs.Features.Tools.Storage;
using Godot;
using Environment = System.Environment;

namespace AppAgasCs.Tests.Godot;

public partial class ImageToolChainDemoAcceptance : Node
{
    private const string Prefix = "[AGAS][IMAGE-TOOL-CHAIN-DEMO]";

    public override void _Ready()
    {
        int exitCode = 0;
        string home = Path.Combine(Path.GetTempPath(), "agas-tool-chain-demo-" + Guid.NewGuid().ToString("N"));
        string? previousHome = Environment.GetEnvironmentVariable("AGAS_HOME");
        try
        {
            string source = Environment.GetEnvironmentVariable("AGAS_DEMO_TILE47_IMAGE") ?? string.Empty;
            if (!File.Exists(source)) throw new FileNotFoundException("Set AGAS_DEMO_TILE47_IMAGE to the Demo 1024x512 PNG.", source);
            using Image input = Image.LoadFromFile(source);
            Require(input.GetWidth() == 1024 && input.GetHeight() == 512, "Demo input must be 1024x512.");

            Environment.SetEnvironmentVariable("AGAS_HOME", home);
            string projectRoot = Path.Combine(home, "project");
            Directory.CreateDirectory(Path.Combine(projectRoot, "assets"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "agas", "tools"));
            File.Copy(source, Path.Combine(projectRoot, "assets", "demo-input.png"));

            var catalog = new ToolCatalog(home);
            ToolDefinition reverse = catalog.LoadTool("tile47-standard-reverse");
            ToolDefinition isometric = catalog.LoadTool("tile47-isometric");
            Require(!reverse.IsReadOnly && !isometric.IsReadOnly
                    && reverse.Kind == ToolImplementationKind.Gdscript
                    && isometric.Kind == ToolImplementationKind.Gdscript
                    && !string.IsNullOrWhiteSpace(reverse.Source)
                    && !string.IsNullOrWhiteSpace(isometric.Source),
                "The two Tile47 presets must be separately editable code tools.");
            const string passThrough = "extends RefCounted\nfunc process(image: Image) -> Dictionary:\n    return {\"image\": image.duplicate()}\n";
            catalog.CreateTool("demo-pass", "Demo 直通工具", passThrough);
            ToolDefinition updated = catalog.UpdateTool("demo-pass", 1, "Demo 直通工具", passThrough);
            Require(updated.Revision == 2, "Custom code tool was not persisted as editable.");
            ToolChainDefinition chain = catalog.CreateChain("demo-tile47-chain", "Demo Tile47 两步处理",
                [reverse.Id, isometric.Id]);
            Require(chain.ToolIds.SequenceEqual(new[] { reverse.Id, isometric.Id }),
                "Tool chain was not saved as two ordered steps.");

            var runner = new ImageToolRunner();
            ImageToolRunResult manualReverse = runner.RunTool(projectRoot, "assets/demo-input.png", reverse,
                "agas/tools/manual-reverse.png");
            Require(manualReverse.Success, $"Manual reverse tool failed: {manualReverse.Code} {manualReverse.Message}");
            Require(manualReverse.Width == 768 && manualReverse.Height == 256,
                "Manual reverse tool should produce a 768x256 atlas.");

            ImageToolRunResult manualIsometric = runner.RunTool(projectRoot, manualReverse.RelativePath, isometric,
                "agas/tools/manual-isometric.png");
            Require(manualIsometric.Success, $"Manual isometric tool failed: {manualIsometric.Code} {manualIsometric.Message}");
            Require(manualIsometric.Width == 1536 && manualIsometric.Height == 512,
                "Manual isometric tool should produce a 1536x512 atlas.");

            ImageToolRunResult chained = runner.RunChain(projectRoot, "assets/demo-input.png", chain, catalog,
                "agas/tools/chained.png");
            Require(chained.Success, $"Tool chain execution failed: {chained.Code} {chained.Message}");
            Require(chained.Width == 1536 && chained.Height == 512,
                "Tool chain output should be a 1536x512 isometric Tile47 atlas.");
            using Image manualImage = Image.LoadFromFile(Path.Combine(projectRoot, manualIsometric.RelativePath));
            using Image chainImage = Image.LoadFromFile(Path.Combine(projectRoot, chained.RelativePath));
            Require(manualImage.GetData().SequenceEqual(chainImage.GetData()),
                "One-click chain output differs from two manual tool invocations.");

            GD.Print($"{Prefix}[PASS] manual 1024x512 -> 768x256 -> 1536x512; chain pixels match");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {exception}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGAS_HOME", previousHome);
            try { Directory.Delete(home, recursive: true); } catch { }
            GetTree().Quit(exitCode);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
