#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using AGAS.UI;
using AppAgasCs.Core.Resources;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class DemoAgasProjectSmokeAcceptance : Node
{
    private const string Prefix = "[AGAS][DEMO-PROJECT-SMOKE]";
    private const string DemoRootVariable = "AGAS_DEMO_PROJECT_ROOT";
    private const string AgasHomeVariable = "AGAS_HOME";
    private const string Tile47SceneName = "tile47";
    private const string Tile47StandardReverseToolId = "tile47-standard-reverse";
    private const string Tile47IsometricToolId = "tile47-isometric";
    private string? _snapshotRoot;
    private string? _isolatedHome;
    private string? _previousHome;

    public override async void _Ready()
    {
        int exitCode = 0;
        try
        {
            await VerifyAsync();
            GD.Print($"{Prefix}[PASS]");
        }
        catch (Exception error)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {error}");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(AgasHomeVariable, _previousHome);
            DeleteTemporaryDirectory(_snapshotRoot);
            DeleteTemporaryDirectory(_isolatedHome);
            GetTree().Quit(exitCode);
        }
    }

    private async Task VerifyAsync()
    {
        string sourceRoot = Path.GetFullPath(System.Environment.GetEnvironmentVariable(DemoRootVariable)
            ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), "DEMO-AGAS"));
        string sourceProject = Path.Combine(sourceRoot, ".agas", "project.tres");
        Require(File.Exists(sourceProject), $"Demo project is missing: {sourceProject}");
        byte[] originalProjectHash = SHA256.HashData(File.ReadAllBytes(sourceProject));

        _snapshotRoot = Path.Combine(Path.GetTempPath(), "agas-demo-snapshot-" + Guid.NewGuid().ToString("N"));
        _isolatedHome = Path.Combine(Path.GetTempPath(), "agas-demo-home-" + Guid.NewGuid().ToString("N"));
        CopyProjectWithoutCaches(sourceRoot, _snapshotRoot);
        _previousHome = System.Environment.GetEnvironmentVariable(AgasHomeVariable);
        System.Environment.SetEnvironmentVariable(AgasHomeVariable, _isolatedHome);

        GetTree().Root.Size = new Vector2I(1600, 900);
        var workspace = new MainWorkspace { CustomMinimumSize = new Vector2(1600, 900) };
        AddChild(workspace);
        await Frames(2);
        workspace.OpenProject(_snapshotRoot);
        await Frames(6);

        Require(workspace.HasOpenProject && workspace.Project?.Name == "DEMO-AGAS",
            "The copied DEMO-AGAS project did not open with its original name.");
        AgasSceneResource scene = workspace.CurrentScene
            ?? throw new InvalidOperationException("The Demo active scene did not load.");
        Require(scene.Name == Tile47SceneName && scene.Readme.Contains("768×256", StringComparison.Ordinal),
            "The Tile47 scene or its instructions did not load.");

        ImageInstanceResource[] existingOutputs = scene.LayersSnapshot
            .SelectMany(layer => layer.ImagesSnapshot)
            .Where(image => image.ImagePath.StartsWith("agas/blueprints/Tile47_", StringComparison.Ordinal))
            .ToArray();
        Require(existingOutputs.Length == 2
                && existingOutputs.Any(image => image.ImagePath.StartsWith(
                    "agas/blueprints/Tile47_标准图反推/", StringComparison.Ordinal))
                && existingOutputs.Any(image => image.ImagePath.StartsWith(
                    "agas/blueprints/Tile47_菱形俯视转换/", StringComparison.Ordinal))
                && existingOutputs.All(image => LoadImage(image.ImagePath)),
            "The two existing Tile47 output image paths or pixel resources did not load.");
        var originalOutputs = existingOutputs
            .Select(image => (image.Id, image.ImagePath, Hash: HashImage(image.ImagePath)))
            .OrderBy(image => image.Id, StringComparer.Ordinal)
            .ToArray();
        string sourceScenePath = Path.Combine(sourceRoot, ".agas", "scenes", scene.Id + ".tres");
        byte[] originalSceneHash = SHA256.HashData(File.ReadAllBytes(sourceScenePath));

        FilesScenesPanel files = FindRequired<FilesScenesPanel>(workspace, "FilesScenesPanel");
        files.ActiveTab = "Scenes";
        ItemList sceneList = FindRequired<ItemList>(files, "ScenesList");
        int tile47Index = Enumerable.Range(0, sceneList.ItemCount)
            .Single(index => sceneList.GetItemMetadata(index).AsString() == scene.Id);
        sceneList.Select(tile47Index);
        sceneList.EmitSignal(ItemList.SignalName.ItemSelected, (long)tile47Index);
        await Frames(2);

        PropertiesPanel properties = FindRequired<PropertiesPanel>(workspace, "PropertiesPanel");
        Control readmePanel = FindRequired<Control>(properties, "SceneReadmePanel");
        TextEdit readme = FindRequired<TextEdit>(properties, "SceneReadmeEdit");
        Control previewPanel = FindRequired<Control>(properties, "ImagePreviewPanel");
        Require(readmePanel.IsVisibleInTree() && readme.Text == scene.Readme
                && !previewPanel.IsVisibleInTree()
                && files.FindChild("SceneReadmeEdit", true, false) == null,
            "Selecting Tile47 must show only its scene instructions in the right dock.");

        TabContainer bottom = FindRequired<TabContainer>(workspace, "BottomDockTabs");
        Require(bottom.GetTabCount() == 2 && bottom.GetTabTitle(1) == "工具",
            "The Demo bottom dock does not expose the Tools tab.");
        bottom.CurrentTab = 1;
        await Frames(2);
        ToolboxPanel toolbox = FindRequired<ToolboxPanel>(workspace, "ToolboxPanel");
        ItemList tools = FindRequired<ItemList>(toolbox, "ToolboxItems");
        CodeEdit code = FindRequired<CodeEdit>(toolbox, "ToolboxCodeEditor");
        Button save = FindRequired<Button>(toolbox, "ToolboxSave");
        foreach (string id in new[]
        {
            Tile47StandardReverseToolId,
            Tile47IsometricToolId,
        })
        {
            Require(Enumerable.Range(0, tools.ItemCount)
                    .Any(index => tools.GetItemMetadata(index).AsString() == id),
                $"Tile47 tool is missing from the toolbox: {id}");
            Require(toolbox.SelectItem(id) && code.Visible && code.Editable
                    && !save.Disabled && !string.IsNullOrWhiteSpace(code.Text),
                $"Tile47 tool does not open an editable code pane: {id}");
        }

        Require(typeof(MainWorkspace).GetMethod("SaveProject", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(workspace, null) is true, "The copied Demo project did not save.");
        string savedScene = File.ReadAllText(Path.Combine(_snapshotRoot!, ".agas", "scenes", scene.Id + ".tres"));
        string savedProject = File.ReadAllText(Path.Combine(_snapshotRoot!, ".agas", "project.tres"));
        string savedLayout = File.ReadAllText(Path.Combine(_snapshotRoot!, ".agas", "workspace-layout.tres"));
        Require(!savedScene.Contains("BlueprintBindings =", StringComparison.Ordinal)
                && !savedProject.Contains("Blueprints =", StringComparison.Ordinal)
                && !savedLayout.Contains("OpenDocumentIds =", StringComparison.Ordinal)
                && !savedLayout.Contains("ActiveDocumentKind =", StringComparison.Ordinal)
                && !savedLayout.Contains("ActiveDocumentId =", StringComparison.Ordinal),
            "Saving the Demo copy retained obsolete Blueprint or document-layout state.");
        workspace.QueueFree();
        await Frames(2);

        var reopened = new MainWorkspace { CustomMinimumSize = new Vector2(1600, 900) };
        AddChild(reopened);
        await Frames(2);
        reopened.OpenProject(_snapshotRoot);
        await Frames(6);
        Require(reopened.HasOpenProject && reopened.CurrentScene?.Id == scene.Id,
            "Saved Demo copy did not reopen its active scene.");
        AgasSceneResource reopenedScene = reopened.CurrentScene
            ?? throw new InvalidOperationException("Saved Demo scene was not loaded.");
        var reopenedOutputs = reopenedScene.LayersSnapshot
            .SelectMany(layer => layer.ImagesSnapshot)
            .Where(image => image.ImagePath.StartsWith("agas/blueprints/Tile47_", StringComparison.Ordinal))
            .Select(image => (image.Id, image.ImagePath, Hash: HashImage(image.ImagePath)))
            .OrderBy(image => image.Id, StringComparer.Ordinal)
            .ToArray();
        Require(originalOutputs.SequenceEqual(reopenedOutputs),
            "Demo Tile47 image IDs, paths, or pixels changed after save and reopen.");
        Require(originalProjectHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(sourceProject)))
                && originalSceneHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(sourceScenePath))),
            "The original Demo project or active scene changed during the smoke test.");
        reopened.QueueFree();
        await Frames(2);
    }

    private static void CopyProjectWithoutCaches(string sourceRoot, string targetRoot)
    {
        Directory.CreateDirectory(targetRoot);
        foreach (string sourceFile in Directory.EnumerateFiles(sourceRoot))
        {
            if (Path.GetFileName(sourceFile) == ".DS_Store") continue;
            File.Copy(sourceFile, Path.Combine(targetRoot, Path.GetFileName(sourceFile)));
        }
        foreach (string sourceDirectory in Directory.EnumerateDirectories(sourceRoot))
        {
            string name = Path.GetFileName(sourceDirectory);
            if (name is ".godot" or ".git" or ".claude") continue;
            CopyDirectory(sourceDirectory, Path.Combine(targetRoot, name));
        }
    }

    private bool LoadImage(string relativePath)
    {
        string absolutePath = Path.Combine(_snapshotRoot!, relativePath);
        if (!File.Exists(absolutePath)) return false;
        Image? image = Image.LoadFromFile(absolutePath);
        return image != null && !image.IsEmpty();
    }

    private string HashImage(string relativePath)
    {
        string absolutePath = Path.Combine(_snapshotRoot!, relativePath);
        using Image image = Image.LoadFromFile(absolutePath);
        Require(!image.IsEmpty(), "Image pixels could not be loaded: " + relativePath);
        return Convert.ToHexString(SHA256.HashData(image.GetData()));
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            if (Path.GetFileName(file) == ".DS_Store") continue;
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private async Task Frames(int count)
    {
        for (int index = 0; index < count; index++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static void DeleteTemporaryDirectory(string? path)
    {
        if (path != null && Directory.Exists(path))
        {
            try { Directory.Delete(path, recursive: true); } catch { }
        }
    }

    private static T FindRequired<T>(Node root, string name) where T : Node =>
        root.FindChild(name, true, false) as T
        ?? throw new InvalidOperationException("Missing node: " + name);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
