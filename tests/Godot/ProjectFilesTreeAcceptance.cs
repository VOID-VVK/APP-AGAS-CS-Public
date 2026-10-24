using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AGAS.UI;
using Godot;

public partial class ProjectFilesTreeAcceptance : Node
{
    private const string Prefix = "[AGAS-H2][PROJECT-FILES-TREE]";
    private readonly List<string> _tempRoots = new();

    public override async void _Ready()
    {
        int exitCode = 0;
        try
        {
            await RunAll();
            GD.Print($"{Prefix}[PASS] Godot C# headless ProjectFilesTree acceptance completed.");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {exception}");
        }
        finally
        {
            CleanupTempRoots();
            GetTree().Quit(exitCode);
        }
    }

    private async Task RunAll()
    {
        string root = NewTempRoot("project-files-tree");
        string externalRoot = NewTempRoot("project-files-tree-external");
        bool symlinkFixturesCreated = CreateFixture(root, externalRoot);

        ProjectFilesTree tree = new();
        AddChild(tree);
        tree.LoadProject(root);

        TreeItem rootItem = tree.GetRoot() ?? throw new InvalidOperationException("ProjectFilesTree should create a root item.");
        List<ItemSnapshot> items = SnapshotTree(rootItem);
        Assert.True(tree.HideRoot && !rootItem.Collapsed, "The virtual root must not hide the directory entry points.");
        foreach (ItemSnapshot directory in items.Where(item => item.Item.GetFirstChild() != null))
        {
            Assert.True(directory.Item.Collapsed, $"Directory '{directory.Tooltip}' must initially be collapsed, including nested directories.");
        }
        string[] visiblePaths = items.Select(item => item.Path).ToArray();

        Assert.SequenceEqual(
            new[]
            {
                "boundary",
                "boundary/only",
                "boundary/only/child.png",
                "boundary/root.png",
                "branch",
                "branch/left.one",
                "branch/left.one/left.png",
                "branch/right.two",
                "branch/right.two/right.png",
                "case",
                "case/lower.WebP",
                "case/photo.JpG",
                "case/UPPER.PNG",
                "deep.level1.level2",
                "deep.level1.level2/photo.png",
                "dot.name.inner.folder",
                "dot.name.inner.folder/dotted.png",
                "mixed",
                "mixed/child",
                "mixed/child/UPPER.JPEG",
                "mixed/root.JPG",
                "results",
                "results/alpha.png",
                "results/Result 1.png",
                "results/Result 2.png",
                "results/Result 10.png",
                "results/zeta.png",
                "space dir.inner dir",
                "space dir.inner dir/image file.png",
            },
            visiblePaths,
            "Tree should render only supported images and their visible ancestor directories.");

        Assert.DoesNotContain(visiblePaths, "empty-dir", "Empty directories should be hidden.");
        Assert.DoesNotContain(visiblePaths, "docs", "Directories containing only non-images should be hidden.");
        Assert.DoesNotContain(visiblePaths, "unsupported", "Directories containing only unsupported gif/svg should be hidden.");
        Assert.DoesNotContain(visiblePaths, ".cache", "Dot directory .cache should be hidden and not scanned.");
        Assert.DoesNotContain(visiblePaths, ".hidden-images", "Dot directory .hidden-images should be hidden and not scanned.");
        Assert.DoesNotContain(visiblePaths, "visible-parent", "Directory whose only supported image is under nested .private should be hidden.");
        Assert.DoesNotContain(visiblePaths, ".agas", "Project metadata dot directory .agas should be hidden and not scanned.");
        Assert.DoesNotContain(visiblePaths, ".godot", "Godot dot directory .godot should be hidden and not scanned.");
        Assert.DoesNotContain(visiblePaths, ".git", "Git dot directory .git should be hidden and not scanned.");
        Assert.DoesNotContain(visiblePaths, "bin", "bin should be hidden and not scanned.");
        Assert.DoesNotContain(visiblePaths, "obj", "obj should be hidden and not scanned.");
        if (symlinkFixturesCreated)
        {
            Assert.DoesNotContain(visiblePaths, "loop", "Directory symlink loop -> fixture root should be skipped and not scanned.");
            Assert.DoesNotContain(visiblePaths, "external-link", "Directory symlink to external image directory should be skipped and not scanned.");
        }
        else
        {
            GD.PushWarning($"{Prefix}[SKIP] Symlink fixtures were not created on this platform/user context.");
        }

        Assert.False(visiblePaths.Any(path => path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)), "gif files should not be visible.");
        Assert.False(visiblePaths.Any(path => path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)), "svg files should not be visible.");

        AssertDirectory(items.Single(item => item.Path == "case"), "case");
        AssertDirectory(items.Single(item => item.Path == "deep.level1.level2"), "deep/level1/level2");
        AssertDirectory(items.Single(item => item.Path == "dot.name.inner.folder"), "dot.name/inner.folder");
        AssertDirectory(items.Single(item => item.Path == "branch"), "branch");
        AssertDirectory(items.Single(item => item.Path == "branch/left.one"), "branch/left/one");
        AssertDirectory(items.Single(item => item.Path == "boundary"), "boundary");
        AssertDirectory(items.Single(item => item.Path == "boundary/only"), "boundary/only");
        AssertImage(items.Single(item => item.Path == "case/lower.WebP"), "case/lower.WebP");
        AssertImage(items.Single(item => item.Path == "case/photo.JpG"), "case/photo.JpG");
        AssertImage(items.Single(item => item.Path == "case/UPPER.PNG"), "case/UPPER.PNG");
        AssertImage(items.Single(item => item.Path == "deep.level1.level2/photo.png"), "deep/level1/level2/photo.png");
        AssertImage(items.Single(item => item.Path == "dot.name.inner.folder/dotted.png"), "dot.name/inner.folder/dotted.png");
        AssertImage(items.Single(item => item.Path == "mixed/child/UPPER.JPEG"), "mixed/child/UPPER.JPEG");
        AssertImage(items.Single(item => item.Path == "mixed/root.JPG"), "mixed/root.JPG");
        AssertImage(items.Single(item => item.Path == "space dir.inner dir/image file.png"), "space dir/inner dir/image file.png");

        VerifySelectionAndMetadataSemantics(tree, items.Single(item => item.Path == "deep.level1.level2/photo.png"), root);
        VerifyDirectorySelectionDoesNotEmitImage(tree, items.Single(item => item.Path == "deep.level1.level2"));
        VerifyFileManagerContextActions(tree, items, root);
        VerifyFileNameFiltering(tree);
        await VerifyDirectoryExpansionState(tree, root);
        await VerifyExternalFileChangesRefreshAutomatically(tree, root);
        await VerifyRightClickSelectionSurvivesRefresh(tree, root);
        await VerifyProjectSwitchIsolatesWatcherSessions(tree, root, externalRoot);

        tree.QueueFree();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        WriteText(externalRoot, "after-dispose.png", "post-disposal event must not reach the freed tree");
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);
    }

    private static void VerifyFileNameFiltering(ProjectFilesTree tree)
    {
        int scansBeforeFiltering = tree.FileSystemScanCount;
        tree.SetFileNameFilter("photo");
        List<ItemSnapshot> photoResults = SnapshotTree(tree.GetRoot()!);
        string[] photoMatches = photoResults
            .Select(item => item.Metadata)
            .Where(path => !string.IsNullOrEmpty(path))
            .ToArray();
        Assert.SequenceEqual(
            new[] { "case/photo.JpG", "deep/level1/level2/photo.png" },
            photoMatches,
            "File-name search must retain matching images from different directories.");
        Assert.False(
            photoResults.Any(item => string.IsNullOrEmpty(item.Metadata)),
            "Active search must render only matching files and hide all directory rows.");
        Assert.Equal(
            scansBeforeFiltering,
            tree.FileSystemScanCount,
            "Filtering an existing file index must not rescan the project directory.");

        tree.SetFileNameFilter("upper.png");
        Assert.SequenceEqual(
            new[] { "case/UPPER.PNG" },
            SnapshotTree(tree.GetRoot()!)
                .Select(item => item.Metadata)
                .Where(path => !string.IsNullOrEmpty(path))
                .ToArray(),
            "File-name search must be case-insensitive.");
        Assert.Equal(
            scansBeforeFiltering,
            tree.FileSystemScanCount,
            "Changing a search query must keep using the in-memory file index.");

        tree.SetFileNameFilter("missing-name");
        TreeItem? noResults = tree.GetRoot()?.GetFirstChild();
        Assert.True(noResults != null
                && noResults.GetText(0) == "没有匹配的图片文件。"
                && !noResults.IsSelectable(0),
            "A search with no matches must show a non-selectable empty state.");

        tree.SetFileNameFilter(string.Empty);
        Assert.True(
            SnapshotTree(tree.GetRoot()!).Any(item => item.Metadata == "space dir/inner dir/image file.png"),
            "Clearing file-name search must restore the full file tree.");
        Assert.Equal(
            scansBeforeFiltering,
            tree.FileSystemScanCount,
            "Clearing a search must restore the cached tree without disk I/O.");
    }

    private async Task VerifyDirectoryExpansionState(ProjectFilesTree tree, string root)
    {
        tree.Size = new Vector2(640, 1200);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        TreeItem boundary = SnapshotTree(tree.GetRoot()!).Single(item => item.Tooltip == "boundary").Item;
        TreeItem nested = SnapshotTree(tree.GetRoot()!).Single(item => item.Tooltip == "boundary/only").Item;
        boundary.Select(0);
        tree.GrabFocus();
        GetViewport().PushInput(new InputEventKey { Keycode = Key.Right, Pressed = true });
        GetViewport().PushInput(new InputEventKey { Keycode = Key.Right, Pressed = false });
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Assert.False(boundary.Collapsed, "Native user input should expand the selected directory.");
        Assert.True(nested.Collapsed, "Expanding a parent must not automatically expand nested directories.");

        TreeItem image = SnapshotTree(tree.GetRoot()!).Single(item => item.Metadata == "boundary/root.png").Item;
        string? openedPath = null;
        void OnImageSelected(string relativePath, string absolutePath) => openedPath = relativePath;
        tree.ImageSelected += OnImageSelected;
        try
        {
            Rect2 imageRect = tree.GetItemAreaRect(image, 0);
            Vector2 position = tree.GlobalPosition + imageRect.Position + imageRect.Size / 2;
            Assert.True(tree.GetItemAtPosition(position - tree.GlobalPosition) == image,
                "The native file-click coordinates must target the visible image row.");
            GetViewport().PushInput(new InputEventMouseMotion { Position = position, GlobalPosition = position });
            GetViewport().PushInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                Position = position,
                GlobalPosition = position
            });
            GetViewport().PushInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = false,
                Position = position,
                GlobalPosition = position
            });
            GetViewport().PushInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                DoubleClick = true,
                Position = position,
                GlobalPosition = position
            });
            GetViewport().PushInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left,
                Pressed = false,
                Position = position,
                GlobalPosition = position
            });
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Assert.Equal("boundary/root.png", openedPath, "Native file double-click must retain the image-open event.");
        }
        finally
        {
            tree.ImageSelected -= OnImageSelected;
        }

        nested.Collapsed = false;
        tree.LoadProject(root);
        AssertDirectoryExpansionState(tree);
        tree.SetFileNameFilter("photo");
        tree.LoadProject(root);
        tree.SetFileNameFilter(string.Empty);
        AssertDirectoryExpansionState(tree);

        string previousLocale = TranslationServer.GetLocale();
        try
        {
            TranslationServer.SetLocale(previousLocale.StartsWith("en", StringComparison.Ordinal) ? "zh_CN" : "en");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            AssertDirectoryExpansionState(tree);
        }
        finally
        {
            TranslationServer.SetLocale(previousLocale);
        }

        WriteText(root, "new-folder/child/image.png", "new directories should remain collapsed");
        await WaitUntil(
            () => SnapshotMetadata(tree).Contains("new-folder/child/image.png", StringComparer.Ordinal),
            "Watcher refresh should discover the new nested directory.");
        AssertDirectoryExpansionState(tree);
        Assert.True(SnapshotTree(tree.GetRoot()!).Single(item => item.Tooltip == "new-folder/child").Item.Collapsed,
            "A newly discovered directory must default to collapsed without resetting expanded siblings.");

        TreeItem expandedParent = SnapshotTree(tree.GetRoot()!).Single(item => item.Tooltip == "boundary").Item;
        expandedParent.Collapsed = true;
        tree.LoadProject(root);
        Assert.True(SnapshotTree(tree.GetRoot()!).Single(item => item.Tooltip == "boundary").Item.Collapsed,
            "A directory manually collapsed again must stay collapsed after refresh.");
        Assert.False(SnapshotTree(tree.GetRoot()!).Single(item => item.Tooltip == "boundary/only").Item.Collapsed,
            "A hidden descendant must retain its manually expanded state.");
    }

    private static void AssertDirectoryExpansionState(ProjectFilesTree tree)
    {
        foreach (ItemSnapshot directory in SnapshotTree(tree.GetRoot()!).Where(item => item.Item.GetFirstChild() != null))
        {
            bool wasExpanded = directory.Tooltip == "boundary" || directory.Tooltip == "boundary/only";
            Assert.Equal(!wasExpanded, directory.Item.Collapsed,
                $"Refresh/search/locale change must preserve the expansion state of '{directory.Tooltip}'.");
        }
    }

    private async Task VerifyExternalFileChangesRefreshAutomatically(ProjectFilesTree tree, string root)
    {
        const string selectedPath = "space dir/inner dir/image file.png";
        TreeItem selectedImage = SnapshotTree(tree.GetRoot()!)
            .Single(item => item.Metadata == selectedPath)
            .Item;
        selectedImage.Select(0);
        tree.EmitSignal(Tree.SignalName.ItemSelected);

        var refreshSelectionEvents = new List<string>();
        string previewSelectionPath = selectedPath;
        tree.ImageSelected += (relativePath, _) =>
        {
            refreshSelectionEvents.Add(relativePath);
            previewSelectionPath = relativePath;
        };
        Assert.Equal(
            selectedPath,
            tree.GetSelected()?.GetMetadata(0).AsString(),
            "The fixture image should still be selected before automatic refresh testing.");

        WriteText(root, "external-drop/created.png", "externally created image");
        await WaitUntil(
            () => SnapshotMetadata(tree).Contains("external-drop/created.png", StringComparer.Ordinal),
            "An externally created image should appear without explicitly calling LoadProject.");
        Assert.Equal(
            selectedPath,
            tree.GetSelected()?.GetMetadata(0).AsString(),
            "Automatic refresh should preserve an existing image selection.");
        Assert.Equal(
            0,
            refreshSelectionEvents.Count,
            "Automatic refresh must not emit ImageSelected. Events=" + string.Join(",", refreshSelectionEvents));
        Assert.Equal(selectedPath, previewSelectionPath, "Create refresh should preserve the preview selection path.");

        WriteText(root, "external-drop/notes.txt", "unsupported external file");
        Assert.False(
            SnapshotTree(tree.GetRoot()!).Any(item => item.Path.EndsWith("notes.txt", StringComparison.Ordinal)),
            "An externally created non-image should remain hidden.");

        string createdPath = Path.Combine(root, "external-drop", "created.png");
        string renamedPath = Path.Combine(root, "external-drop", "renamed.webp");
        File.Move(createdPath, renamedPath);
        await WaitUntil(
            () =>
            {
                string[] metadata = SnapshotMetadata(tree);
                return !metadata.Contains("external-drop/created.png", StringComparer.Ordinal) &&
                       metadata.Contains("external-drop/renamed.webp", StringComparer.Ordinal);
            },
            "An externally renamed image should replace the old file in the tree.");
        AssertSelectionRemainedStable(tree, selectedPath, previewSelectionPath, refreshSelectionEvents, "rename refresh");

        File.Delete(renamedPath);
        await WaitUntil(
            () => !SnapshotMetadata(tree).Contains("external-drop/renamed.webp", StringComparer.Ordinal),
            "An externally deleted image should disappear without explicitly calling LoadProject.");
        Assert.False(
            SnapshotTree(tree.GetRoot()!).Any(item => item.Path == "external-drop"),
            "A directory containing only unsupported files should be hidden after its last image is deleted.");
        await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);
        AssertSelectionRemainedStable(tree, selectedPath, previewSelectionPath, refreshSelectionEvents, "delete refresh");

        WriteText(root, "direct-delete.png", "direct create then delete probe");
        await WaitUntil(
            () => SnapshotMetadata(tree).Contains("direct-delete.png", StringComparer.Ordinal),
            "The direct create/delete probe should appear automatically.");
        File.Delete(Path.Combine(root, "direct-delete.png"));
        await WaitUntil(
            () => !SnapshotMetadata(tree).Contains("direct-delete.png", StringComparer.Ordinal),
            "The direct create/delete probe should disappear automatically.");
        await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);
        AssertSelectionRemainedStable(
            tree,
            selectedPath,
            previewSelectionPath,
            refreshSelectionEvents,
            "direct create-delete refresh");
    }

    private static void AssertSelectionRemainedStable(
        ProjectFilesTree tree,
        string expectedPath,
        string previewSelectionPath,
        IReadOnlyList<string> selectionEvents,
        string phase)
    {
        Assert.Equal(
            expectedPath,
            tree.GetSelected()?.GetMetadata(0).AsString(),
            $"The selected image should survive {phase}.");
        Assert.Equal(
            0,
            selectionEvents.Count,
            $"{phase} must not emit ImageSelected. Events=" + string.Join(",", selectionEvents));
        Assert.Equal(expectedPath, previewSelectionPath, $"The preview selection path should survive {phase}.");
    }

    private async Task VerifyRightClickSelectionSurvivesRefresh(ProjectFilesTree tree, string root)
    {
        const string rightClickedPath = "case/photo.JpG";
        tree.Size = new Vector2(640, 1200);
        SnapshotTree(tree.GetRoot()!).Single(item => item.Tooltip == "case").Item.Collapsed = false;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        TreeItem rightClickedItem = SnapshotTree(tree.GetRoot()!)
            .Single(item => item.Metadata == rightClickedPath)
            .Item;
        Rect2 itemRect = tree.GetItemAreaRect(rightClickedItem, 0);
        Assert.True(itemRect.Size.Y > 0, "The right-click target should have a visible item rectangle.");

        var refreshSelectionEvents = new List<string>();
        string previewSelectionPath = "space dir/inner dir/image file.png";
        tree.ImageSelected += (relativePath, _) =>
        {
            refreshSelectionEvents.Add(relativePath);
            previewSelectionPath = relativePath;
        };
        tree._GuiInput(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Right,
            Pressed = true,
            Position = itemRect.Position + new Vector2(8, itemRect.Size.Y / 2)
        });

        Assert.Equal(
            rightClickedPath,
            tree.GetSelected()?.GetMetadata(0).AsString(),
            "Right-click should select the context-menu target.");
        string previewPathAfterRightClick = previewSelectionPath;
        refreshSelectionEvents.Clear();

        WriteText(root, "right-click-refresh-probe.png", "right-click selection refresh probe");
        await WaitUntil(
            () => SnapshotMetadata(tree).Contains("right-click-refresh-probe.png", StringComparer.Ordinal),
            "The right-click refresh probe should appear automatically.");
        File.Delete(Path.Combine(root, "right-click-refresh-probe.png"));
        await WaitUntil(
            () => !SnapshotMetadata(tree).Contains("right-click-refresh-probe.png", StringComparer.Ordinal),
            "The right-click refresh probe should disappear automatically.");
        await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);

        Assert.Equal(
            rightClickedPath,
            tree.GetSelected()?.GetMetadata(0).AsString(),
            "External create/delete refresh should preserve the right-clicked item.");
        Assert.Equal(
            0,
            refreshSelectionEvents.Count,
            "Refresh after right-click selection must not emit ImageSelected. Events=" +
            string.Join(",", refreshSelectionEvents));
        Assert.Equal(
            previewPathAfterRightClick,
            previewSelectionPath,
            "Refresh after right-click selection should preserve the existing preview selection path.");
    }

    private async Task VerifyProjectSwitchIsolatesWatcherSessions(
        ProjectFilesTree tree,
        string oldRoot,
        string newRoot)
    {
        int oldGeneration = tree.WatcherGeneration;
        WriteText(oldRoot, "queued-before-switch.png", "event may already be queued on the old watcher");
        tree.LoadProject(newRoot);

        Assert.Equal(
            oldGeneration + 1,
            tree.WatcherGeneration,
            "Switching project roots should create exactly one new watcher generation.");
        Assert.SequenceEqual(
            new[] { "outside.png" },
            SnapshotMetadata(tree),
            "Switching projects should immediately render only the new project files.");

        int refreshCountAfterSwitch = tree.AutomaticRefreshCount;
        TreeItem newProjectTreeRoot = tree.GetRoot()
            ?? throw new InvalidOperationException("The new project should have a tree root.");
        WriteText(oldRoot, "after-switch.png", "old watcher events must stay isolated");
        await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);

        Assert.Equal(
            refreshCountAfterSwitch,
            tree.AutomaticRefreshCount,
            "Queued or later events from the old watcher must not refresh the new project.");
        Assert.True(
            ReferenceEquals(newProjectTreeRoot, tree.GetRoot()),
            "Old-project events must not replace the new project's rendered tree.");

        int newGeneration = tree.WatcherGeneration;
        tree.LoadProject(newRoot);
        Assert.Equal(
            newGeneration,
            tree.WatcherGeneration,
            "Reloading the same normalized root should reuse its healthy watcher.");

        WriteText(newRoot, "after-switch.png", "new watcher should remain active");
        await WaitUntil(
            () => SnapshotMetadata(tree).Contains("after-switch.png", StringComparer.Ordinal),
            "The reused watcher should continue observing the current project.");

        tree.LoadProject(oldRoot);
        Assert.True(SnapshotTree(tree.GetRoot()!)
                .Where(item => item.Item.GetFirstChild() != null)
                .All(item => item.Item.Collapsed),
            "Switching back to a project must start with collapsed directories rather than leaking another session's state.");
    }

    private async Task WaitUntil(Func<bool> condition, string failureMessage)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await ToSignal(GetTree().CreateTimer(0.025), SceneTreeTimer.SignalName.Timeout);
        }

        Assert.True(condition(), failureMessage);
    }

    private static string[] SnapshotMetadata(ProjectFilesTree tree)
    {
        TreeItem root = tree.GetRoot() ?? throw new InvalidOperationException("ProjectFilesTree should retain a root item.");
        return SnapshotTree(root)
            .Select(item => item.Metadata)
            .Where(metadata => !string.IsNullOrWhiteSpace(metadata))
            .ToArray();
    }

    private static bool CreateFixture(string root, string externalRoot)
    {
        Directory.CreateDirectory(Path.Combine(root, "empty-dir"));
        WriteText(root, "docs/readme.txt", "not an image");
        WriteText(root, "unsupported/logo.gif", "gif is unsupported");
        WriteText(root, "unsupported/icon.svg", "svg is unsupported");

        WriteText(root, "deep/level1/level2/photo.png", "png marker");
        WriteText(root, "branch/left/one/left.png", "left branch marker");
        WriteText(root, "branch/right/two/right.png", "right branch marker");
        WriteText(root, "boundary/root.png", "boundary root marker");
        WriteText(root, "boundary/only/child.png", "boundary child marker");
        WriteText(root, "dot.name/inner.folder/dotted.png", "dotted directory marker");
        WriteText(root, "space dir/inner dir/image file.png", "space path marker");
        WriteText(root, "mixed/root.JPG", "jpg marker");
        WriteText(root, "mixed/notes.txt", "ignored");
        WriteText(root, "mixed/child/UPPER.JPEG", "jpeg marker");
        WriteText(root, "case/UPPER.PNG", "upper png marker");
        WriteText(root, "case/lower.WebP", "webp marker");
        WriteText(root, "case/photo.JpG", "mixed jpg marker");
        WriteText(root, "results/alpha.png", "ordinary file before generated results");
        WriteText(root, "results/Result 1.png", "first generated result");
        WriteText(root, "results/Result 10.png", "tenth generated result");
        WriteText(root, "results/Result 2.png", "second generated result");
        WriteText(root, "results/zeta.png", "ordinary file after generated results");

        WriteText(root, ".cache/cached.png", "must not scan");
        WriteText(root, ".hidden-images/nested/hidden.jpg", "must not scan");
        WriteText(root, "visible-parent/.private/secret.webp", "must not scan nested dot directory");
        WriteText(root, ".agas/project.png", "must not scan");
        WriteText(root, ".godot/imported.png", "must not scan");
        WriteText(root, ".git/object.jpg", "must not scan");
        WriteText(root, "bin/generated.png", "must not scan");
        WriteText(root, "obj/generated.webp", "must not scan");
        WriteText(externalRoot, "outside.png", "external symlink target must not scan");

        return TryCreateDirectorySymlink(Path.Combine(root, "loop"), root)
            & TryCreateDirectorySymlink(Path.Combine(root, "external-link"), externalRoot);
    }

    private static bool TryCreateDirectorySymlink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            FileAttributes attributes = File.GetAttributes(linkPath);
            return (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            GD.PushWarning($"{Prefix}[SKIP] Failed to create symlink fixture {linkPath} -> {targetPath}: {exception.GetBaseException().Message}");
            return false;
        }
    }

    private static void WriteText(string root, string relativePath, string content)
    {
        string absolutePath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath) ?? root);
        File.WriteAllText(absolutePath, content);
    }

    private static List<ItemSnapshot> SnapshotTree(TreeItem rootItem)
    {
        var items = new List<ItemSnapshot>();
        for (TreeItem? child = rootItem.GetFirstChild(); child != null; child = child.GetNext())
        {
            AddSnapshot(items, child, string.Empty);
        }

        return items;
    }

    private static void AddSnapshot(List<ItemSnapshot> items, TreeItem item, string parentPath)
    {
        string text = item.GetText(0);
        string path = string.IsNullOrEmpty(parentPath) ? text : parentPath + "/" + text;
        items.Add(new ItemSnapshot(path, item.GetTooltipText(0), item.GetMetadata(0).AsString(), item));

        for (TreeItem? child = item.GetFirstChild(); child != null; child = child.GetNext())
        {
            AddSnapshot(items, child, path);
        }
    }

    private static void AssertDirectory(ItemSnapshot item, string expectedRelativePath)
    {
        Assert.Equal(string.Empty, item.Metadata, "Directory metadata should remain empty for " + item.Path);
        Assert.Equal(expectedRelativePath, item.Tooltip, "Directory tooltip should retain the deepest normalized relative path for " + item.Path);
    }

    private static void AssertImage(ItemSnapshot item, string expectedRelativePath)
    {
        Assert.Equal(expectedRelativePath, item.Metadata, "Image metadata should remain the normalized relative path for " + item.Path);
        Assert.Equal(expectedRelativePath, item.Tooltip, "Image tooltip should remain the normalized relative path for " + item.Path);
    }

    private static void VerifySelectionAndMetadataSemantics(ProjectFilesTree tree, ItemSnapshot image, string root)
    {
        string? selectedRelativePath = null;
        string? selectedAbsolutePath = null;
        tree.ImageSelected += (relativePath, absolutePath) =>
        {
            selectedRelativePath = relativePath;
            selectedAbsolutePath = absolutePath;
        };

        image.Item.Select(0);
        tree.EmitSignal(Tree.SignalName.ItemSelected);

        string expectedAbsolutePath = Path.Combine(root, image.Metadata.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(image.Metadata, selectedRelativePath, "Selecting an image should emit ImageSelected with relative path.");
        Assert.Equal(expectedAbsolutePath, selectedAbsolutePath, "Selecting an image should emit ImageSelected with absolute path.");

    }

    private static void VerifyDirectorySelectionDoesNotEmitImage(ProjectFilesTree tree, ItemSnapshot directory)
    {
        bool emitted = false;
        tree.ImageSelected += (_, _) => emitted = true;
        directory.Item.Select(0);
        tree.EmitSignal(Tree.SignalName.ItemSelected);
        Assert.False(emitted, "Selecting a directory should not emit ImageSelected.");
    }

    private static void VerifyFileManagerContextActions(ProjectFilesTree tree, IReadOnlyList<ItemSnapshot> items, string root)
    {
        PopupMenu menu = tree.GetNodeOrNull<PopupMenu>("ProjectFilesContextMenu")
            ?? throw new InvalidOperationException("Project files context menu should exist after the tree enters the scene.");
        Assert.Equal(1, menu.ItemCount, "Project files context menu should remain focused on one action.");
        Assert.Equal("在文件管理器中显示", menu.GetItemText(0), "Context menu wording should remain platform-neutral.");

        var launches = new List<(string Path, bool OpenFolder)>();
        tree.ShowInFileManager = (path, openFolder) => launches.Add((path, openFolder));

        ItemSnapshot directory = items.Single(item => item.Path == "deep.level1.level2");
        directory.Item.Select(0);
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 1L);
        Assert.Equal(1, launches.Count, "Invoking the directory context action should launch the file manager once.");
        Assert.Equal(
            Path.Combine(root, "deep", "level1", "level2"),
            launches[0].Path,
            "A compact directory should open its deepest real directory rather than reconstructing from dotted text.");
        Assert.True(launches[0].OpenFolder, "Directory context action should open the directory.");

        ItemSnapshot image = items.Single(item => item.Path == "space dir.inner dir/image file.png");
        image.Item.Select(0);
        menu.EmitSignal(PopupMenu.SignalName.IdPressed, 1L);
        Assert.Equal(2, launches.Count, "Invoking the image context action should launch the file manager once more.");
        Assert.Equal(
            Path.Combine(root, "space dir", "inner dir", "image file.png"),
            launches[1].Path,
            "Image context action should preserve the exact real path including spaces.");
        Assert.False(launches[1].OpenFolder, "Image context action should reveal/select the file rather than opening it as a folder.");
    }

    private string NewTempRoot(string name)
    {
        string root = Path.Combine(Path.GetTempPath(), "agas-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        return root;
    }

    private void CleanupTempRoots()
    {
        foreach (string root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception exception)
            {
                GD.PushWarning($"{Prefix}[CLEANUP] Failed to delete {root}: {exception.GetBaseException().Message}");
            }
        }
    }

    private sealed record ItemSnapshot(string Path, string Tooltip, string Metadata, TreeItem Item);

    private static class Assert
    {
        public static void True(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        public static void False(bool condition, string message) => True(!condition, message);

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(message + " Expected=" + expected + " Actual=" + actual);
            }
        }

        public static void SequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string message)
        {
            if (!expected.SequenceEqual(actual))
            {
                throw new InvalidOperationException(message + " Expected=[" + string.Join(", ", expected) + "] Actual=[" + string.Join(", ", actual) + "]");
            }
        }

        public static void DoesNotContain(IEnumerable<string> values, string unexpected, string message)
        {
            if (values.Contains(unexpected, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(message + " Unexpected=" + unexpected);
            }
        }
    }
}
