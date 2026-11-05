#nullable enable

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using AGAS.UI;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Features.Tools.Storage;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class ScenePackageWorkspaceAcceptance : Node
{
    private const string Prefix = "[AGAS][SCENE-PACKAGE-WORKSPACE]";
    private string? _root;
    private string? _exportPath;
    private string? _previousAgasHome;

    public override async void _Ready()
    {
        int exitCode = 0;
        _previousAgasHome = System.Environment.GetEnvironmentVariable("AGAS_HOME");
        try
        {
            await VerifyWorkspaceTransfer();
            GD.Print($"{Prefix}[PASS]");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {exception}");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("AGAS_HOME", _previousAgasHome);
            if (_exportPath != null && File.Exists(_exportPath)) File.Delete(_exportPath);
            if (_root != null && Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            GetTree().Quit(exitCode);
        }
    }

    private async Task VerifyWorkspaceTransfer()
    {
        _root = Path.Combine(Path.GetTempPath(), "agas-scene-ui-" + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);
        System.Environment.SetEnvironmentVariable("AGAS_HOME", Path.Combine(_root, "source-home"));
        Require(new AgasProjectInitializer().EnsureInitialized(project).Success, "Project initialization failed.");
        var tools = new ToolCatalog();
        const string source = "extends RefCounted\nfunc process(image: Image) -> Dictionary:\n\treturn {\"image\": image.duplicate()}\n";
        tools.CreateTool("scene-ui-tool", "Scene UI tool", source);
        tools.CreateChain("scene-ui-chain", "Scene UI chain", new[] { "scene-ui-tool" });

        var workspace = new MainWorkspace { CustomMinimumSize = new Vector2(1200, 760) };
        AddChild(workspace);
        workspace.OpenProject(project);
        await Frames();
        var scene = workspace.CurrentScene ?? throw new InvalidOperationException("No active scene.");
        scene.Name = "Transfer fixture " + Guid.NewGuid().ToString("N");
        string downloads = OS.GetSystemDir(OS.SystemDir.Downloads);
        _exportPath = Path.Combine(downloads, ScenePackageCodec.PackageFolderName(scene.Id, scene.Name) + ".zip");
        Require(!File.Exists(_exportPath), "Export fixture unexpectedly already exists.");
        PopupMenu menu = FindRequired<PopupMenu>(workspace, "ScenesContextMenu");
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 3L);
        await Frames();
        Require(File.Exists(_exportPath), "Scene context menu export did not write to system Downloads.");
        Require(!Directory.Exists(AgasPaths.SceneExportsDirectory(project)), "UI export created the old project export directory.");
        ScenePackageDocument original = ReadArchive(_exportPath);
        Require(original.Toolbox?.Tools.Any(tool => tool.Id == "scene-ui-tool" && tool.Source == source) == true,
            "The exported scene archive omitted custom tool code.");
        FindRequired<AcceptDialog>(workspace, "ScenePackageExportResultDialog").Hide();
        tools.UpdateTool("scene-ui-tool", 1, "Scene UI tool", source + "# latest\n");
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 3L);
        await Frames();
        Require(ReadArchive(_exportPath).Toolbox!.Tools.Single(tool => tool.Id == "scene-ui-tool").Source?.EndsWith("# latest\n") == true,
            "A repeated export did not overwrite the same download with updated content.");
        FindRequired<AcceptDialog>(workspace, "ScenePackageExportResultDialog").Hide();

        string exportedSceneName = scene.Name;
        workspace.QueueFree();
        await Frames();
        System.Environment.SetEnvironmentVariable("AGAS_HOME", Path.Combine(_root, "target-home"));
        workspace = new MainWorkspace { CustomMinimumSize = new Vector2(1200, 760) };
        AddChild(workspace);
        workspace.OpenProject(project);
        await Frames();
        Require(FindRequired<ItemList>(workspace, "ToolboxChainItems").ItemCount == 0,
            "Target toolbox must start without the source chain.");
        menu = FindRequired<PopupMenu>(workspace, "ScenesContextMenu");
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 4L);
        await Frames();
        FileDialog dialog = FindRequired<FileDialog>(workspace, "ScenePackageImportDialog");
        Require(Path.GetFullPath(dialog.CurrentDir) == Path.GetFullPath(downloads),
            "Scene import did not open in the system Downloads directory.");
        string previousSceneId = workspace.CurrentScene!.Id;
        dialog.Hide();
        dialog.EmitSignal(FileDialog.SignalName.FileSelected, _exportPath);
        await Frames();
        Require(workspace.CurrentScene?.Id != previousSceneId && workspace.CurrentScene?.Name == exportedSceneName,
            "Scene import file selection did not activate a new restored scene.");
        Require(File.Exists(AgasPaths.SceneFile(project, workspace.CurrentScene!.Id)), "Imported scene was not persisted.");
        Require(new ToolCatalog().LoadTool("scene-ui-tool").Source?.EndsWith("# latest\n") == true, "UI import omitted custom tool code.");
        ItemList chains = FindRequired<ItemList>(workspace, "ToolboxChainItems");
        Require(Enumerable.Range(0, chains.ItemCount).Any(i => chains.GetItemText(i).Contains("Scene UI chain", StringComparison.Ordinal)),
            "Import did not refresh the toolbox with the restored chain.");
        workspace.QueueFree();
        await Frames();
    }

    private static ScenePackageDocument ReadArchive(string path)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        using var reader = new StreamReader(archive.GetEntry(AgasPaths.ScenePackageManifestFileName)!.Open());
        return ScenePackageCodec.Deserialize(reader.ReadToEnd());
    }

    private async Task Frames()
    {
        for (int index = 0; index < 3; index++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static T FindRequired<T>(Node root, string name) where T : Node =>
        root.FindChild(name, recursive: true, owned: false) as T
        ?? throw new InvalidOperationException("Required node is missing: " + name);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
