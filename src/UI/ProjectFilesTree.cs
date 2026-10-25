using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Godot;

namespace AGAS.UI;

public partial class ProjectFilesTree : Tree
{
    private const int ShowInFileManagerMenuItem = 1;
    private const double RefreshDebounceSeconds = 0.25;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp"
    };

    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".agas", ".godot", ".git", "bin", "obj"
    };

    private string _projectRoot = string.Empty;
    private PopupMenu? _contextMenu;
    private ProjectWatcherState? _projectWatcher;
    private HashSet<string> _knownDirectories = new(PathComparer);
    private readonly HashSet<string> _expandedDirectoryPaths = new(PathComparer);
    private int _focusRefreshRequested;
    private SelectionBookmark? _selectionBookmark;
    private bool _isRefreshing;
    private bool _skipSelectionCapture;
    private string _fileNameFilter = string.Empty;
    private DirectoryTreeNode? _fileIndex;
    private bool _hasFileIndex;

    private static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public event Action<string, string>? ImageSelected;

    internal Action<string, bool> ShowInFileManager { get; set; } =
        static (path, openFolder) => Godot.OS.ShellShowInFileManager(path, openFolder);

    internal int AutomaticRefreshCount { get; private set; }

    internal int WatcherGeneration { get; private set; }

    internal int FileSystemScanCount { get; private set; }

    public ProjectFilesTree()
    {
        Columns = 1;
        HideRoot = true;
        AllowReselect = true;
        ItemSelected += OnItemSelected;
    }

    public override void _Ready()
    {
        _contextMenu = new PopupMenu { Name = "ProjectFilesContextMenu" };
        _contextMenu.AddItem("在文件管理器中显示", ShowInFileManagerMenuItem);
        _contextMenu.IdPressed += OnContextMenuItemPressed;
        AddChild(_contextMenu);
    }

    public override void _Process(double delta)
    {
        bool forceRefresh = Interlocked.Exchange(ref _focusRefreshRequested, 0) != 0;
        ProjectWatcherState? watcher = Volatile.Read(ref _projectWatcher);
        bool watcherOverflowed = watcher != null && Interlocked.Exchange(ref watcher.Overflowed, 0) != 0;
        if (watcherOverflowed)
        {
            RebuildProjectWatcher();
            watcher = Volatile.Read(ref _projectWatcher);
        }

        if (watcherOverflowed || forceRefresh)
        {
            long forcedRefreshVersion = watcher == null ? 0 : Volatile.Read(ref watcher.ChangeVersion);
            RefreshProjectTree();
            if (watcher != null)
            {
                Volatile.Write(ref watcher.ProcessedChangeVersion, forcedRefreshVersion);
            }

            AutomaticRefreshCount++;
            return;
        }

        if (watcher == null)
        {
            return;
        }

        long observedVersion = Volatile.Read(ref watcher.ChangeVersion);
        if (observedVersion == Volatile.Read(ref watcher.ProcessedChangeVersion))
        {
            return;
        }

        long lastEventTimestamp = Volatile.Read(ref watcher.LastEventTimestamp);
        if (Stopwatch.GetElapsedTime(lastEventTimestamp).TotalSeconds < RefreshDebounceSeconds)
        {
            return;
        }

        RefreshProjectTree();
        Volatile.Write(ref watcher.ProcessedChangeVersion, observedVersion);
        AutomaticRefreshCount++;
    }

    public override void _Notification(int what)
    {
        if (what != NotificationApplicationFocusIn || string.IsNullOrWhiteSpace(_projectRoot))
        {
            return;
        }

        if (Volatile.Read(ref _projectWatcher) == null && Directory.Exists(_projectRoot))
        {
            RebuildProjectWatcher();
        }

        Interlocked.Exchange(ref _focusRefreshRequested, 1);
    }

    public override void _ExitTree()
    {
        DisposeProjectWatcher();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mouseButton ||
            mouseButton.ButtonIndex != MouseButton.Right ||
            !mouseButton.Pressed)
        {
            return;
        }

        TreeItem? item = GetItemAtPosition(mouseButton.Position);
        if (!TryResolveFileManagerTarget(item, out _, out _))
        {
            return;
        }

        item!.Select(0);
        _selectionBookmark = CreateSelectionBookmark(item);
        Vector2 popupPosition = GetScreenPosition() + mouseButton.Position;
        _contextMenu?.Popup(new Rect2I((Vector2I)popupPosition, Vector2I.Zero));
        AcceptEvent();
    }

    public void ShowUnavailableProject()
    {
        DisposeProjectWatcher();
        _projectRoot = string.Empty;
        _expandedDirectoryPaths.Clear();
        _selectionBookmark = null;
        _fileIndex = null;
        _hasFileIndex = false;
        Volatile.Write(ref _knownDirectories, new HashSet<string>(PathComparer));
        Interlocked.Exchange(ref _focusRefreshRequested, 0);
        Clear();

        TreeItem root = CreateItem();
        TreeItem message = CreateItem(root);
        message.SetText(0, "项目目录不可用。");
        message.SetSelectable(0, false);
    }

    public void LoadProject(string projectRoot)
    {
        string normalizedRoot = Path.GetFullPath(projectRoot);
        ProjectWatcherState? currentWatcher = Volatile.Read(ref _projectWatcher);
        if (currentWatcher != null &&
            Volatile.Read(ref currentWatcher.Disposed) == 0 &&
            Volatile.Read(ref currentWatcher.Overflowed) == 0 &&
            PathComparer.Equals(currentWatcher.ProjectRoot, normalizedRoot))
        {
            long observedVersion = Volatile.Read(ref currentWatcher.ChangeVersion);
            Interlocked.Exchange(ref _focusRefreshRequested, 0);
            _projectRoot = normalizedRoot;
            RefreshProjectTree();
            Volatile.Write(ref currentWatcher.ProcessedChangeVersion, observedVersion);
            return;
        }

        DisposeProjectWatcher();
        _projectRoot = normalizedRoot;
        _expandedDirectoryPaths.Clear();
        _selectionBookmark = null;
        _fileIndex = null;
        _hasFileIndex = false;
        _skipSelectionCapture = true;
        Interlocked.Exchange(ref _focusRefreshRequested, 0);
        RefreshProjectTree();
        CreateProjectWatcher();
    }

    public void SetFileNameFilter(string? filter)
    {
        string normalized = filter?.Trim() ?? string.Empty;
        if (string.Equals(_fileNameFilter, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _fileNameFilter = normalized;
        RefreshProjectTree(rebuildIndex: false);
    }

    private void RefreshProjectTree(bool rebuildIndex = true)
    {
        if (string.IsNullOrWhiteSpace(_projectRoot))
        {
            return;
        }

        if (!_skipSelectionCapture && GetRoot() is TreeItem previousRoot)
        {
            CaptureDirectoryExpansion(previousRoot);
        }

        SelectionBookmark? selectedItem = _skipSelectionCapture
            ? null
            : _selectionBookmark ?? CaptureSelection();
        _skipSelectionCapture = false;
        _isRefreshing = true;
        try
        {
            Clear();

            TreeItem root = CreateItem();
            root.SetText(0, Path.GetFileName(_projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            root.SetAutoTranslateMode(0, AutoTranslateModeEnum.Disabled);
            root.SetSelectable(0, false);

            if (rebuildIndex || !_hasFileIndex)
            {
                var knownDirectories = new HashSet<string>(PathComparer);
                _fileIndex = CollectVisibleDirectory(_projectRoot, knownDirectories);
                _hasFileIndex = true;
                FileSystemScanCount++;
                Volatile.Write(ref _knownDirectories, knownDirectories);
                ProjectWatcherState? watcher = Volatile.Read(ref _projectWatcher);
                if (watcher != null && PathComparer.Equals(watcher.ProjectRoot, _projectRoot))
                {
                    Volatile.Write(ref watcher.KnownDirectories, knownDirectories);
                }
            }

            if (!string.IsNullOrEmpty(_fileNameFilter))
            {
                RenderSearchResults(root, _fileIndex, _fileNameFilter);
            }
            else if (_fileIndex != null)
            {
                RenderDirectory(root, _fileIndex);
            }

            bool selectionRestored = RestoreSelection(root, selectedItem);
            _selectionBookmark = selectionRestored || !string.IsNullOrEmpty(_fileNameFilter)
                ? selectedItem
                : null;
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        TreeItem? item = GetItemAtPosition(atPosition) ?? GetSelected();
        string relativePath = GetImageRelativePath(item);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return default;
        }

        string absolutePath = GetAbsolutePath(relativePath);
        SetDragPreview(CreateDragPreview(relativePath, absolutePath));
        return absolutePath;
    }

    private DirectoryTreeNode? CollectVisibleDirectory(string directory, HashSet<string> knownDirectories)
    {
        knownDirectories.Add(Path.GetFullPath(directory));
        var childDirectories = new List<DirectoryTreeNode>();
        var imageFiles = new List<FileTreeNode>();

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return null;
        }

        foreach (string entry in entries)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(entry);
            }
            catch
            {
                continue;
            }

            string name = Path.GetFileName(entry);
            if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
            {
                continue;
            }

            if ((attributes & FileAttributes.Directory) == FileAttributes.Directory)
            {
                if (ShouldSkipDirectory(name))
                {
                    continue;
                }

                DirectoryTreeNode? child = CollectVisibleDirectory(entry, knownDirectories);
                if (child != null)
                {
                    childDirectories.Add(child);
                }

                continue;
            }

            if (!ImageExtensions.Contains(Path.GetExtension(entry)))
            {
                continue;
            }

            imageFiles.Add(new FileTreeNode(name, NormalizeRelativePath(entry)));
        }

        SortGeneratedResultFiles(imageFiles);

        if (childDirectories.Count == 0 && imageFiles.Count == 0)
        {
            return null;
        }

        return new DirectoryTreeNode(
            Path.GetFileName(directory),
            NormalizeRelativePath(directory),
            childDirectories,
            imageFiles);
    }

    private static void SortGeneratedResultFiles(List<FileTreeNode> imageFiles)
    {
        SortNumberedFiles(imageFiles, "Result ");
        SortNumberedFiles(imageFiles, "Tools ");
    }

    private static void SortNumberedFiles(List<FileTreeNode> imageFiles, string prefix)
    {
        var resultFiles = imageFiles
            .Select((file, index) => (File: file, Index: index, Digits: GetNormalizedResultNumber(file.Name, prefix)))
            .Where(item => item.Digits != null)
            .ToArray();
        if (resultFiles.Length < 2)
        {
            return;
        }

        FileTreeNode[] orderedResults = resultFiles
            .OrderBy(item => item.Digits!.Length)
            .ThenBy(item => item.Digits, StringComparer.Ordinal)
            .ThenBy(item => item.File.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.File)
            .ToArray();
        for (int index = 0; index < resultFiles.Length; index++)
        {
            imageFiles[resultFiles[index].Index] = orderedResults[index];
        }
    }

    private static string? GetNormalizedResultNumber(string fileName, string prefix)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (!stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        ReadOnlySpan<char> digits = stem.AsSpan(prefix.Length);
        if (digits.IsEmpty || digits.IndexOfAnyExceptInRange('0', '9') >= 0)
        {
            return null;
        }

        int firstSignificantDigit = 0;
        while (firstSignificantDigit < digits.Length - 1 && digits[firstSignificantDigit] == '0')
        {
            firstSignificantDigit++;
        }

        return digits[firstSignificantDigit..].ToString();
    }

    private void RenderDirectory(TreeItem parent, DirectoryTreeNode directory)
    {
        foreach (DirectoryTreeNode childDirectory in directory.Directories)
        {
            RenderDirectoryItem(parent, childDirectory);
        }

        foreach (FileTreeNode file in directory.Files)
        {
            TreeItem fileItem = CreateItem(parent);
            fileItem.SetText(0, file.Name);
            fileItem.SetAutoTranslateMode(0, AutoTranslateModeEnum.Disabled);
            fileItem.SetTooltipText(0, file.RelativePath);
            fileItem.SetMetadata(0, file.RelativePath);
        }
    }

    private void CaptureDirectoryExpansion(TreeItem parent)
    {
        for (TreeItem? child = parent.GetFirstChild(); child != null; child = child.GetNext())
        {
            if (child.GetFirstChild() == null)
            {
                continue;
            }

            string relativePath = child.GetTooltipText(0);
            if (child.Collapsed)
            {
                _expandedDirectoryPaths.Remove(relativePath);
            }
            else
            {
                _expandedDirectoryPaths.Add(relativePath);
            }

            CaptureDirectoryExpansion(child);
        }
    }

    private void RenderSearchResults(TreeItem root, DirectoryTreeNode? index, string filter)
    {
        FileTreeNode[] matches = index == null
            ? Array.Empty<FileTreeNode>()
            : EnumerateFiles(index)
                .Where(file => file.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => SearchMatchRank(file.Name, filter))
                .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        if (matches.Length == 0)
        {
            TreeItem message = CreateItem(root);
            message.SetText(0, "没有匹配的图片文件。");
            message.SetSelectable(0, false);
            return;
        }

        foreach (FileTreeNode file in matches)
        {
            TreeItem fileItem = CreateItem(root);
            fileItem.SetText(0, file.Name);
            fileItem.SetAutoTranslateMode(0, AutoTranslateModeEnum.Disabled);
            fileItem.SetTooltipText(0, file.RelativePath);
            fileItem.SetMetadata(0, file.RelativePath);
        }
    }

    private static IEnumerable<FileTreeNode> EnumerateFiles(DirectoryTreeNode directory)
    {
        foreach (FileTreeNode file in directory.Files)
        {
            yield return file;
        }

        foreach (DirectoryTreeNode child in directory.Directories)
        {
            foreach (FileTreeNode file in EnumerateFiles(child))
            {
                yield return file;
            }
        }
    }

    private static int SearchMatchRank(string fileName, string filter)
    {
        if (string.Equals(fileName, filter, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileNameWithoutExtension(fileName), filter, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return fileName.StartsWith(filter, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private void RenderDirectoryItem(TreeItem parent, DirectoryTreeNode directory)
    {
        var compactNames = new List<string> { directory.Name };
        DirectoryTreeNode deepestDirectory = directory;
        while (deepestDirectory.Files.Count == 0 && deepestDirectory.Directories.Count == 1)
        {
            deepestDirectory = deepestDirectory.Directories[0];
            compactNames.Add(deepestDirectory.Name);
        }

        TreeItem directoryItem = CreateItem(parent);
        directoryItem.SetText(0, string.Join(".", compactNames));
        directoryItem.SetAutoTranslateMode(0, AutoTranslateModeEnum.Disabled);
        directoryItem.SetTooltipText(0, deepestDirectory.RelativePath);
        directoryItem.SetSelectable(0, true);
        directoryItem.SetMetadata(0, string.Empty);
        directoryItem.Collapsed = !_expandedDirectoryPaths.Contains(deepestDirectory.RelativePath);
        RenderDirectory(directoryItem, deepestDirectory);
    }

    private static bool ShouldSkipDirectory(string name)
    {
        return string.IsNullOrEmpty(name)
            || name.StartsWith(".", StringComparison.Ordinal)
            || SkippedDirectoryNames.Contains(name);
    }

    private sealed record DirectoryTreeNode(string Name, string RelativePath, IReadOnlyList<DirectoryTreeNode> Directories, IReadOnlyList<FileTreeNode> Files);

    private sealed record FileTreeNode(string Name, string RelativePath);

    private sealed record SelectionBookmark(string RelativePath, bool IsImage);

    private void OnItemSelected()
    {
        if (_isRefreshing)
        {
            return;
        }

        TreeItem? selected = GetSelected();
        _selectionBookmark = CreateSelectionBookmark(selected);
        if (_selectionBookmark?.IsImage != true)
        {
            return;
        }

        string relativePath = _selectionBookmark.RelativePath;
        ImageSelected?.Invoke(relativePath, GetAbsolutePath(relativePath));
    }

    private SelectionBookmark? CreateSelectionBookmark(TreeItem? item)
    {
        string imagePath = GetImageRelativePath(item);
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            return new SelectionBookmark(imagePath, IsImage: true);
        }

        string directoryPath = item?.GetTooltipText(0) ?? string.Empty;
        return string.IsNullOrWhiteSpace(directoryPath)
            ? null
            : new SelectionBookmark(directoryPath, IsImage: false);
    }

    private void OnContextMenuItemPressed(long id)
    {
        if (id != ShowInFileManagerMenuItem ||
            !TryResolveFileManagerTarget(GetSelected(), out string absolutePath, out bool openFolder))
        {
            return;
        }

        ShowInFileManager(absolutePath, openFolder);
    }

    private bool TryResolveFileManagerTarget(TreeItem? item, out string absolutePath, out bool openFolder)
    {
        absolutePath = string.Empty;
        openFolder = false;
        if (item == null)
        {
            return false;
        }

        string imageRelativePath = GetImageRelativePath(item);
        string relativePath = string.IsNullOrWhiteSpace(imageRelativePath)
            ? item.GetTooltipText(0)
            : imageRelativePath;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        absolutePath = GetAbsolutePath(relativePath);
        if (Directory.Exists(absolutePath))
        {
            openFolder = true;
            return true;
        }

        return !string.IsNullOrWhiteSpace(imageRelativePath) && File.Exists(absolutePath);
    }

    private string GetImageRelativePath(TreeItem? item)
    {
        if (item == null)
        {
            return string.Empty;
        }

        string relativePath = item.GetMetadata(0).AsString();
        return ImageExtensions.Contains(Path.GetExtension(relativePath)) ? relativePath : string.Empty;
    }

    private string NormalizeRelativePath(string path)
    {
        return Path.GetRelativePath(_projectRoot, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private string GetAbsolutePath(string relativePath)
    {
        return Path.Combine(_projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private SelectionBookmark? CaptureSelection() => CreateSelectionBookmark(GetSelected());

    private bool RestoreSelection(TreeItem root, SelectionBookmark? bookmark)
    {
        if (bookmark == null)
        {
            DeselectAll();
            return false;
        }

        TreeItem? item = FindItem(root, bookmark);
        if (item == null)
        {
            DeselectAll();
            return false;
        }

        item.Select(0);
        return true;
    }

    private static TreeItem? FindItem(TreeItem parent, SelectionBookmark bookmark)
    {
        for (TreeItem? child = parent.GetFirstChild(); child != null; child = child.GetNext())
        {
            string candidate = bookmark.IsImage
                ? child.GetMetadata(0).AsString()
                : child.GetTooltipText(0);
            if (string.Equals(candidate, bookmark.RelativePath, StringComparison.Ordinal))
            {
                return child;
            }

            TreeItem? descendant = FindItem(child, bookmark);
            if (descendant != null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void CreateProjectWatcher()
    {
        if (!Directory.Exists(_projectRoot))
        {
            return;
        }

        FileSystemWatcher? watcher = null;
        ProjectWatcherState? state = null;
        try
        {
            watcher = new FileSystemWatcher(_projectRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
            };
            var createdState = new ProjectWatcherState(
                watcher,
                _projectRoot,
                Volatile.Read(ref _knownDirectories));
            state = createdState;
            watcher.Created += (_, args) => OnProjectEntryChanged(createdState, args);
            watcher.Deleted += (_, args) => OnProjectEntryChanged(createdState, args);
            watcher.Renamed += (_, args) => OnProjectEntryRenamed(createdState, args);
            watcher.Error += (_, _) => OnProjectWatcherError(createdState);

            Volatile.Write(ref _projectWatcher, state);
            watcher.EnableRaisingEvents = true;
            WatcherGeneration++;
        }
        catch
        {
            if (state != null)
            {
                Interlocked.CompareExchange(ref _projectWatcher, null, state);
                Interlocked.Exchange(ref state.Disposed, 1);
            }

            watcher?.Dispose();
        }
    }

    private void RebuildProjectWatcher()
    {
        DisposeProjectWatcher();
        CreateProjectWatcher();
    }

    private void DisposeProjectWatcher()
    {
        ProjectWatcherState? state = Interlocked.Exchange(ref _projectWatcher, null);
        if (state != null)
        {
            Interlocked.Exchange(ref state.Disposed, 1);
            DisposeWatcher(state.Watcher);
        }
    }

    private static void DisposeWatcher(FileSystemWatcher watcher)
    {
        watcher.Dispose();
    }

    private void OnProjectEntryChanged(ProjectWatcherState state, FileSystemEventArgs args)
    {
        if (!ShouldRefreshForPath(state, args.FullPath))
        {
            return;
        }

        MarkWatcherChange(state);
    }

    private void OnProjectEntryRenamed(ProjectWatcherState state, RenamedEventArgs args)
    {
        if (!ShouldRefreshForPath(state, args.OldFullPath) && !ShouldRefreshForPath(state, args.FullPath))
        {
            return;
        }

        MarkWatcherChange(state);
    }

    private static void OnProjectWatcherError(ProjectWatcherState state)
    {
        Interlocked.Exchange(ref state.Overflowed, 1);
    }

    private static void MarkWatcherChange(ProjectWatcherState state)
    {
        Volatile.Write(ref state.LastEventTimestamp, Stopwatch.GetTimestamp());
        Interlocked.Increment(ref state.ChangeVersion);
    }

    private static bool ShouldRefreshForPath(ProjectWatcherState state, string path)
    {
        string fullPath;
        string relativePath;
        try
        {
            fullPath = Path.GetFullPath(path);
            relativePath = Path.GetRelativePath(state.ProjectRoot, fullPath);
        }
        catch
        {
            return false;
        }

        if (relativePath == ".." ||
            relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }

        string[] segments = relativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Take(Math.Max(segments.Length - 1, 0)).Any(ShouldSkipDirectory))
        {
            return false;
        }

        if (ImageExtensions.Contains(Path.GetExtension(fullPath)))
        {
            return true;
        }

        if (Directory.Exists(fullPath))
        {
            return segments.Length == 0 || !ShouldSkipDirectory(segments[^1]);
        }

        return Volatile.Read(ref state.KnownDirectories).Contains(fullPath);
    }

    private sealed class ProjectWatcherState(
        FileSystemWatcher watcher,
        string projectRoot,
        HashSet<string> knownDirectories)
    {
        public FileSystemWatcher Watcher { get; } = watcher;

        public string ProjectRoot { get; } = projectRoot;

        public HashSet<string> KnownDirectories = knownDirectories;

        public long ChangeVersion;

        public long ProcessedChangeVersion;

        public long LastEventTimestamp;

        public int Overflowed;

        public int Disposed;
    }

    private static Control CreateDragPreview(string relativePath, string absolutePath)
    {
        var box = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(120, 120)
        };

        Image? image = Image.LoadFromFile(absolutePath);
        if (image != null && !image.IsEmpty())
        {
            var textureRect = new TextureRect
            {
                Texture = ImageTexture.CreateFromImage(image),
                ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(96, 96)
            };
            box.AddChild(textureRect);
        }

        box.AddChild(new Label
        {
            AutoTranslateMode = AutoTranslateModeEnum.Disabled,
            Text = Path.GetFileName(relativePath),
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        });
        return box;
    }
}
