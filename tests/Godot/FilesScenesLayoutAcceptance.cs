#nullable enable

using System;
using System.IO;
using System.Threading.Tasks;
using AGAS.UI;
using AppAgasCs.Core.Resources;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class FilesScenesLayoutAcceptance : Node
{
    private const string Prefix = "[AGAS][FILES-LAYOUT]";
    private string? _fixtureRoot;

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
            if (_fixtureRoot != null)
            {
                try { Directory.Delete(_fixtureRoot, recursive: true); } catch { }
            }
            GetTree().Quit(exitCode);
        }
    }

    private async Task RunAsync()
    {
        var filesPanel = new FilesScenesPanel { Size = new Vector2(600, 700) };
        var propertiesPanel = new PropertiesPanel { Size = new Vector2(440, 700) };
        AddChild(filesPanel);
        AddChild(propertiesPanel);
        await Frame();
        await Frame();

        ProjectFilesTree filesTree = FindRequired<ProjectFilesTree>(filesPanel, "Files");
        TabContainer tabs = FindRequired<TabContainer>(filesPanel, "FilesScenesTabs");
        Require(filesPanel.FindChild("ImagePreviewPanel", recursive: true, owned: false) == null
                && filesPanel.FindChild("PreviewToggleButton", recursive: true, owned: false) == null
                && filesPanel.FindChild("FilesPreviewSplitter", recursive: true, owned: false) == null,
            "The left panel still contains the old image preview layout.");
        Require(filesTree.SizeFlagsVertical.HasFlag(Control.SizeFlags.ExpandFill)
                && tabs.SizeFlagsVertical.HasFlag(Control.SizeFlags.ExpandFill)
                && filesTree.Size.Y > filesPanel.Size.Y * 0.6f,
            "The left file list did not expand into the released vertical space.");

        Control previewPanel = FindRequired<Control>(propertiesPanel, "ImagePreviewPanel");
        TextureRect previewTexture = FindRequired<TextureRect>(propertiesPanel, "SelectedImagePreview");
        Label previewStatus = FindRequired<Label>(propertiesPanel, "PreviewStatusLabel");
        Require(previewPanel.GetParent()?.Name == "PropertiesPreviewSplitter",
            "The image preview is not in the right properties panel split.");
        Control propertiesContent = FindRequired<Control>(propertiesPanel, "PropertiesContent");
        Require(!propertiesContent.SizeFlagsVertical.HasFlag(Control.SizeFlags.Expand)
                && previewPanel.SizeFlagsVertical.HasFlag(Control.SizeFlags.Expand),
            "The right preview must receive the space below the compact property section.");

        _fixtureRoot = Path.Combine(Path.GetTempPath(), "agas-right-preview-" + Guid.NewGuid().ToString("N"));
        string assetsRoot = Path.Combine(_fixtureRoot, "assets");
        string imagePath = Path.Combine(assetsRoot, "preview.png");
        Directory.CreateDirectory(assetsRoot);
        Image image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        image.Fill(Colors.Red);
        Require(image.SavePng(imagePath) == Error.Ok, "Could not create the preview fixture image.");

        filesPanel.ImagePreviewRequested += propertiesPanel.ShowFilePreview;
        filesTree.LoadProject(_fixtureRoot);
        Select(filesTree, FindTaggedItem(filesTree, "assets/preview.png"));
        Require(previewTexture.Texture != null && previewStatus.Text == "assets/preview.png",
            "Selecting a file did not update the right-side preview.");

        filesPanel.ActiveTab = "Scenes";
        TextEdit readme = FindRequired<TextEdit>(propertiesPanel, "SceneReadmeEdit");
        Label readmeTitle = FindRequired<Label>(propertiesPanel, "SceneReadmeTitle");
        Control scenes = FindRequired<Control>(filesPanel, "Scenes");
        Control readmePanel = FindRequired<Control>(propertiesPanel, "SceneReadmePanel");
        Require(filesPanel.FindChild("SceneReadmeEdit", recursive: true, owned: false) == null
                && readme.GetParent() == readmePanel && readmeTitle.GetParent() == readmePanel,
            "Scene instructions must be in the right properties panel, not the left Scenes tab.");

        var project = new AgasProjectResource { ActiveSceneId = "scene-a" };
        project.AddScene(new SceneIndexResource { Id = "scene-a", Name = "场景 A" });
        filesPanel.OpenProject(_fixtureRoot, project, "scene-a");
        filesPanel.SceneSelected += id => propertiesPanel.ShowSceneReadme(id == "scene-a" ? "场景 A 说明" : string.Empty);
        var scenesList = FindRequired<ItemList>(scenes, "ScenesList");
        scenesList.EmitSignal(ItemList.SignalName.ItemSelected, 0L);
        Require(readmePanel.IsVisibleInTree() && !previewPanel.IsVisibleInTree() && readme.Text == "场景 A 说明",
            "Selecting a scene must show only its instructions on the right.");
        string changedReadme = string.Empty;
        propertiesPanel.SceneReadmeChanged += value => changedReadme = value;
        readme.Text = "更新后的说明";
        readme.EmitSignal(TextEdit.SignalName.TextChanged);
        Require(changedReadme == "更新后的说明", "Editing the right-side scene instructions must emit the updated text.");

        Select(filesTree, FindTaggedItem(filesTree, "assets/preview.png"));
        Require(previewPanel.IsVisibleInTree() && !readmePanel.IsVisibleInTree() && previewTexture.Texture != null,
            "Selecting a file after a scene must restore the right-side image preview.");

        filesPanel.QueueFree();
        propertiesPanel.QueueFree();
        await Frame();
    }

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private static void Select(Tree tree, TreeItem item)
    {
        tree.DeselectAll();
        item.Select(0);
        tree.EmitSignal(Tree.SignalName.ItemSelected);
    }

    private static TreeItem FindTaggedItem(Tree tree, string tag)
    {
        TreeItem? root = tree.GetRoot();
        TreeItem? found = root == null ? null : FindTaggedItem(root, tag);
        return found ?? throw new InvalidOperationException($"Missing tree item: {tag}");
    }

    private static TreeItem? FindTaggedItem(TreeItem parent, string tag)
    {
        for (TreeItem? child = parent.GetFirstChild(); child != null; child = child.GetNext())
        {
            if (child.GetMetadata(0).AsString() == tag)
            {
                return child;
            }

            TreeItem? nested = FindTaggedItem(child, tag);
            if (nested != null)
            {
                return nested;
            }
        }
        return null;
    }

    private static T FindRequired<T>(Node root, string name) where T : Node =>
        root.FindChild(name, recursive: true, owned: false) as T
        ?? throw new InvalidOperationException($"Required node is missing: {name}");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
