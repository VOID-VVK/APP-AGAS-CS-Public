#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AGAS.UI;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class FilesScenesSearchAcceptance : Node
{
    private const string Prefix = "[AGAS][FILES-SEARCH]";
    private string? _root;

    public override async void _Ready()
    {
        int exitCode = 0;
        try
        {
            await RunAsync();
            GD.Print($"{Prefix}[PASS]");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {exception}");
        }
        finally
        {
            if (_root != null) { try { Directory.Delete(_root, recursive: true); } catch { } }
            GetTree().Quit(exitCode);
        }
    }

    private async Task RunAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "agas-files-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "assets", "nested"));
        File.WriteAllText(Path.Combine(_root, "assets", "apple.png"), "fixture");
        File.WriteAllText(Path.Combine(_root, "assets", "nested", "Banana.WebP"), "fixture");

        var panel = new FilesScenesPanel { Size = new Vector2(520, 700) };
        AddChild(panel);
        await Frame();

        LineEdit search = FindRequired<LineEdit>(panel, "FileSearchEdit");
        ProjectFilesTree tree = FindRequired<ProjectFilesTree>(panel, "Files");
        TabContainer tabs = FindRequired<TabContainer>(panel, "FilesScenesTabs");
        Require(search.GetParent()?.Name == "FilesScenesTop" && search.GetIndex() < tabs.GetIndex(),
            "Search input must stay visible above all left-panel tabs.");
        Require(search.ClearButtonEnabled && search.PlaceholderText.Contains("搜索文件名", StringComparison.Ordinal),
            "Search input must expose its file-name purpose and a clear action.");

        tree.LoadProject(_root);
        Require(ImagePaths(tree).SequenceEqual(new[] { "assets/nested/Banana.WebP", "assets/apple.png" }),
            "Fixture files were not loaded into the file tree.");

        tabs.CurrentTab = 1;
        search.Text = "banana";
        search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text);
        Require(tabs.CurrentTab == 0, "Typing a file search must reveal the Files tab automatically.");
        Require(ImagePaths(tree).SequenceEqual(new[] { "assets/nested/Banana.WebP" }),
            "Typing a partial file name must filter case-insensitively through the wired search input.");

        search.Text = string.Empty;
        search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text);
        Require(ImagePaths(tree).Count == 2, "Clearing search must restore every image file.");

        panel.QueueFree();
        await Frame();
    }

    private static IReadOnlyList<string> ImagePaths(ProjectFilesTree tree)
    {
        var paths = new List<string>();
        TreeItem? root = tree.GetRoot();
        if (root != null)
        {
            Collect(root, paths);
        }
        return paths;
    }

    private static void Collect(TreeItem item, ICollection<string> paths)
    {
        string metadata = item.GetMetadata(0).AsString();
        if (!string.IsNullOrEmpty(metadata))
        {
            paths.Add(metadata);
        }
        for (TreeItem? child = item.GetFirstChild(); child != null; child = child.GetNext())
        {
            Collect(child, paths);
        }
    }

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private static T FindRequired<T>(Node root, string name) where T : Node =>
        root.FindChild(name, recursive: true, owned: false) as T
        ?? throw new InvalidOperationException($"Required node is missing: {name}");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
