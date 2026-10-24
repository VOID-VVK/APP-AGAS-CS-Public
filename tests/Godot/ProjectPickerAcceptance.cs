using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AGAS.UI;
using AGAS.UI.Dialogs;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Settings;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class ProjectPickerAcceptance : Node
{
    private const string Prefix = "[AGAS][PROJECT-PICKER]";
    private string? _temporaryRoot;

    public override async void _Ready()
    {
        int exitCode = 0;
        string? previousHome = System.Environment.GetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable);
        string previousLocale = TranslationServer.GetLocale();
        try
        {
            await VerifyPopulatedPickerAsync();
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {exception}");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, previousHome);
            TranslationServer.SetLocale(previousLocale);
            if (_temporaryRoot != null)
            {
                try
                {
                    Directory.Delete(_temporaryRoot, recursive: true);
                }
                catch (Exception exception)
                {
                    exitCode = 1;
                    GD.PushError($"{Prefix}[CLEANUP-FAIL] {exception}");
                }
            }
            GD.Print(exitCode == 0 ? $"{Prefix}[PASS]" : $"{Prefix}[NOT-PASS]");
            GetTree().Quit(exitCode);
        }
    }

    private async Task VerifyPopulatedPickerAsync()
    {
        GetWindow().Size = new Vector2I(1280, 760);
        await Frames(2);
        _temporaryRoot = Path.Combine(Path.GetTempPath(), "agas-project-picker-" + Guid.NewGuid().ToString("N"));
        string home = Path.Combine(_temporaryRoot, "agas-home");
        System.Environment.SetEnvironmentVariable(AgasAiSettingsPaths.HomeEnvironmentVariable, home);
        new AgasUiAppearanceSettingsStore(home).Save(AgasUiAppearanceSettings.Default with { UiLocale = "zh_CN" });
        TranslationServer.SetLocale("zh_CN");
        string alphaPath = Path.Combine(_temporaryRoot, "alpha", new string('a', 180));
        string betaPath = Path.Combine(_temporaryRoot, "beta-workspace");
        Directory.CreateDirectory(alphaPath);
        Directory.CreateDirectory(betaPath);

        var store = new RecentProjectsStore(Path.Combine(_temporaryRoot, "recent-projects.tres"));
        store.RecordOpened(alphaPath, "Alpha Forge");
        store.RecordOpened(betaPath, "Beta Lab");

        var host = new Control
        {
            Name = "ProjectPickerAcceptanceHost",
            CustomMinimumSize = new Vector2(1280, 760),
            Size = new Vector2(1280, 760)
        };
        AddChild(host);
        var picker = new ProjectPicker(store);
        host.AddChild(picker);
        await Frames(4);

        PanelContainer sidebar = RequireNode<PanelContainer>(picker, "ProjectPickerSidebar");
        MarginContainer mainMargin = RequireNode<MarginContainer>(picker, "ProjectPickerMainMargin");
        HBoxContainer header = RequireNode<HBoxContainer>(picker, "ProjectPickerHeader");
        Require(Math.Abs(sidebar.Size.X - 280) <= 1, $"Sidebar must retain the 280px Rider-like rail, actual {sidebar.Size.X}.");
        Require(mainMargin.GetThemeConstant("margin_left") == 42 && mainMargin.GetThemeConstant("margin_right") == 42,
            "Main project area must retain balanced 42px content margins.");
        Require(picker.Size == host.Size, "Project picker must fill the complete host window.");
        Button projectsNavigation = RequireNode<Button>(picker, "ProjectsNavigationButton");
        Require(projectsNavigation.ButtonPressed, "Projects navigation must start selected.");
        await ClickAsync(projectsNavigation);
        Require(projectsNavigation.ButtonPressed, "The only navigation destination must remain selected when pressed.");
        Label welcome = RequireNode<Label>(picker, "ProjectPickerWelcomeTitle");
        Require(FindByName<Button>(picker, "CloneRepositoryButton") == null, "Picker must not expose a non-functional clone button.");

        bool settingsRequested = false;
        picker.SettingsRequested += () => settingsRequested = true;
        await ClickAsync(RequireNode<Button>(picker, "ProjectPickerSettingsButton"));
        Require(settingsRequested, "Sidebar Settings entry must invoke the existing settings flow.");

        Button newProject = RequireNode<Button>(picker, "NewProjectButton");
        Require(FindByName<Button>(picker, "OpenProjectButton") == null,
            "Picker must expose a single smart New Project entry instead of separate Open/New buttons.");
        LineEdit search = RequireNode<LineEdit>(picker, "ProjectSearchEdit");
        Require(newProject.Position.X > welcome.Position.X,
            "Header action must be ordered to the right of the welcome title.");
        Require(search.Position.Y >= header.Position.Y + header.Size.Y,
            "Search must be laid out below the header without overlap.");
        Require(newProject.FocusMode == Control.FocusModeEnum.All
            && search.FocusMode == Control.FocusModeEnum.All && projectsNavigation.FocusMode == Control.FocusModeEnum.All,
            "All primary picker actions must expose keyboard focus.");

        ScrollContainer list = RequireNode<ScrollContainer>(picker, "RecentProjectsTree");
        VBoxContainer rows = RequireNode<VBoxContainer>(picker, "RecentProjectsList");
        Require(list.Visible, "Recent projects list should be visible when entries exist.");
        Require(CountRows(rows) == 2, "Both recent projects must be rendered.");
        PanelContainer alpha = FindRow(rows, "Alpha Forge", alphaPath);
        Label alphaName = RequireNode<Label>(alpha, "ProjectNameLabel");
        Label alphaPathLabel = RequireNode<Label>(alpha, "ProjectPathLabel");
        Require(alphaName.Text == "Alpha Forge", "Project name must be rendered as the primary row title.");
        Require(alphaPathLabel.Text.Length > 0, "Project path must be rendered as the secondary row subtitle.");
        Require(alpha.TooltipText.Contains(alphaPath, StringComparison.Ordinal), "Long paths must expose the complete value through a tooltip.");
        Require(FindByName<Control>(alpha, "ProjectBadge") != null, "Each project row must include a Rider-style badge.");
        Require(list.Size.X <= RequireNode<Control>(picker, "RecentProjectsHost").Size.X + 1, "Long paths must not force the list outside its clipped host.");

        await EnterTextAsync(search, "beta");
        Require(CountRows(rows) == 1 && FindRow(rows, "Beta Lab", betaPath) != null, "Search must filter by project name.");

        await EnterTextAsync(search, "alpha/");
        Require(CountRows(rows) == 1 && FindRow(rows, "Alpha Forge", alphaPath) != null, "Search must filter by project path.");

        await EnterTextAsync(search, "no-such-project");
        Label empty = RequireNode<Label>(picker, "RecentProjectsEmptyState");
        Require(empty.Visible && empty.Text.Contains("没有匹配", StringComparison.Ordinal), "No-match search must show a dedicated empty state.");

        await EnterTextAsync(search, string.Empty);

        string? openedPath = null;
        picker.ProjectOpened += path => openedPath = path;
        PanelContainer beta = FindRow(rows, "Beta Lab", betaPath);
        beta.GrabFocus();
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Enter, PhysicalKeycode = Key.Enter, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Enter, PhysicalKeycode = Key.Enter, Pressed = false });
        await Frames(2);
        Require(openedPath == betaPath, "Activating a recent project must open its normalized path.");

        PopupMenu contextMenu = RequireNode<PopupMenu>(picker, "RecentProjectContextMenu");
        await RightClickAsync(beta, beta.Size * 0.5f);
        Require(contextMenu.Visible, "A real row right-click event must open its context menu.");
        openedPath = null;
        contextMenu.EmitSignal(PopupMenu.SignalName.IdPressed, 1L);
        Require(openedPath == betaPath, "Context menu Open must open the right-clicked project.");
        contextMenu.Hide();

        await RightClickAsync(beta, beta.Size * 0.5f);
        contextMenu.EmitSignal(PopupMenu.SignalName.IdPressed, 2L);
        await Frames(2);
        Require(CountRows(rows) == 1, "Context menu removal must refresh the recent project list.");
        Require(Directory.Exists(betaPath), "Removing a recent project must not delete its files.");

        PanelContainer remaining = FindRow(rows, "Alpha Forge", alphaPath);
        remaining.GrabFocus();
        contextMenu.EmitSignal(PopupMenu.SignalName.IdPressed, 2L);
        await Frames(2);
        Require(!list.Visible && empty.Visible && empty.Text.Contains("暂无最近项目", StringComparison.Ordinal), "Removing the last entry must show the no-project empty state.");
        FileDialog directoryDialog = RequireNode<FileDialog>(picker, "ProjectDirectoryDialog");
        await ClickAsync(newProject);
        await Frames(1);
        Require(directoryDialog.Visible
            && directoryDialog.Title.Contains("选择项目文件夹", StringComparison.Ordinal)
            && directoryDialog.FileMode == FileDialog.FileModeEnum.OpenDir,
            "New project action must open the unified folder chooser.");

        string ordinaryDirectory = Path.Combine(_temporaryRoot, "ordinary-new-project");
        Directory.CreateDirectory(ordinaryDirectory);
        string? createdPath = null;
        picker.ProjectCreated += path => createdPath = path;
        directoryDialog.EmitSignal(FileDialog.SignalName.DirSelected, ordinaryDirectory);
        Require(createdPath == ordinaryDirectory,
            "Folder picker must emit ProjectCreated for the selected directory (smart open-or-create on Main).");
        directoryDialog.Hide();

        // Same button also works for an already-initialized AGAS project folder.
        createdPath = null;
        await ClickAsync(newProject);
        await Frames(1);
        Require(directoryDialog.Visible, "Folder chooser must reopen for an existing project path.");
        directoryDialog.EmitSignal(FileDialog.SignalName.DirSelected, alphaPath);
        Require(createdPath == alphaPath,
            "Selecting an existing AGAS project folder via New Project must still request open-or-create.");
        directoryDialog.Hide();

        VerifyMainCreateOpenSemantics(ordinaryDirectory);

        picker.ShowProjectOpenFailure("无法打开验收项目。");
        Label status = RequireNode<Label>(picker, "ProjectStatusLabel");
        Require(status.Visible && status.Text == "无法打开验收项目。" && !newProject.Disabled,
            "A failed create/open operation must restore actionable controls and display the error on the picker.");

        host.CustomMinimumSize = Vector2.Zero;
        host.Size = new Vector2(1152, 648);
        await Frames(3);
        Require(picker.Size == host.Size && Math.Abs(sidebar.Size.X - 280) <= 1,
            "The full-window split must remain stable at the supported compact 1152x648 size.");
        Require(newProject.GetGlobalRect().End.X <= picker.GetGlobalRect().End.X
            && RequireNode<Control>(picker, "RecentProjectsHost").GetGlobalRect().End.Y <= picker.GetGlobalRect().End.Y,
            "Compact-window layout must keep primary actions and the project list inside the viewport.");

        host.QueueFree();
        await Frames(2);
        await VerifyMainRoutesAsync();
    }

    private async Task VerifyMainRoutesAsync()
    {
        string integrationRoot = Path.Combine(_temporaryRoot!, "main-integration");
        string ordinaryDirectory = Path.Combine(integrationRoot, "ordinary");
        string invalidProjectDirectory = Path.Combine(integrationRoot, "invalid-project");
        Directory.CreateDirectory(ordinaryDirectory);
        Directory.CreateDirectory(invalidProjectDirectory);

        var initializer = new AgasProjectInitializer();
        Require(initializer.EnsureInitialized(invalidProjectDirectory).Success, "Invalid-project fixture initialization failed.");
        var repository = new AgasResourceRepository();
        AgasLoadResult<AgasProjectResource> invalidLoad = repository.Load<AgasProjectResource>(AgasPaths.ProjectFile(invalidProjectDirectory));
        Require(invalidLoad.Success && invalidLoad.Resource != null, "Invalid-project fixture could not be loaded.");
        invalidLoad.Resource!.ActiveSceneId = "unsafe scene id";
        Require(repository.Save(invalidLoad.Resource, AgasPaths.ProjectFile(invalidProjectDirectory)).Success,
            "Invalid-project fixture could not be saved.");

        var host = new Control
        {
            Name = "MainProjectPickerIntegrationHost",
            Size = new Vector2(1280, 760)
        };
        AddChild(host);
        var main = new AGAS.UI.Main(
            new RecentProjectsStore(Path.Combine(integrationRoot, "main-recent-projects.tres")),
            disableMcpRuntimeForTesting: true);
        host.AddChild(main);
        await Frames(5);

        ProjectPicker picker = FindFirst<ProjectPicker>(main)
            ?? throw new InvalidOperationException("Main did not create the real ProjectPicker.");
        await ClickAsync(RequireNode<Button>(picker, "ProjectPickerSettingsButton"));
        SettingsDialog settingsDialog = FindFirst<SettingsDialog>(main)
            ?? throw new InvalidOperationException("Main did not route SettingsRequested to SettingsDialog.");
        Require(settingsDialog.Visible, "Main must open the real SettingsDialog from the picker sidebar.");
        settingsDialog.Hide();

        await SelectDirectoryThroughPickerAsync(picker, invalidProjectDirectory);
        await Frames(3);
        ProjectPicker pickerAfterInvalid = FindFirst<ProjectPicker>(main)
            ?? throw new InvalidOperationException("Invalid existing project must keep the picker visible.");
        Label invalidStatus = RequireNode<Label>(pickerAfterInvalid, "ProjectStatusLabel");
        Require(invalidStatus.Visible && invalidStatus.Text.Contains("活动场景标识无效", StringComparison.Ordinal)
            && FindFirst<MainWorkspace>(main) == null,
            "Existing-project preflight must report an invalid active scene before destroying the picker.");

        await SelectDirectoryThroughPickerAsync(pickerAfterInvalid, ordinaryDirectory);
        await Frames(8);
        Require(File.Exists(AgasPaths.ProjectFile(ordinaryDirectory))
            && FindFirst<ProjectPicker>(main) == null
            && FindFirst<MainWorkspace>(main) != null,
            "Unified New Project route must initialize an ordinary directory and open the workspace.");

        host.QueueFree();
        await Frames(4);
    }

    private async Task SelectDirectoryThroughPickerAsync(ProjectPicker picker, string directory)
    {
        Button action = RequireNode<Button>(picker, "NewProjectButton");
        await ClickAsync(action);
        FileDialog dialog = RequireNode<FileDialog>(picker, "ProjectDirectoryDialog");
        Require(dialog.Visible, "Project action must show the real FileDialog before directory selection.");
        dialog.EmitSignal(FileDialog.SignalName.DirSelected, directory);
        dialog.Hide();
    }

    private static void VerifyMainCreateOpenSemantics(string ordinaryDirectory)
    {
        var main = new AGAS.UI.Main();

        // Smart path used by the single New Project button.
        string? createResult = main.TryGetOpenableProjectName(ordinaryDirectory, createIfMissing: true, out _);
        Require(!string.IsNullOrWhiteSpace(createResult) && File.Exists(AgasPaths.ProjectFile(ordinaryDirectory)),
            "New Project must initialize a plain folder and return an openable project.");

        // Re-selecting the same folder must open without re-initialization failure.
        string? reopenResult = main.TryGetOpenableProjectName(ordinaryDirectory, createIfMissing: true, out string reopenError);
        Require(!string.IsNullOrWhiteSpace(reopenResult),
            $"Re-opening an existing AGAS project via New Project must succeed: {reopenError}");

        var repository = new AgasResourceRepository();
        AgasLoadResult<AgasProjectResource> tooOldLoad = repository.Load<AgasProjectResource>(AgasPaths.ProjectFile(ordinaryDirectory));
        Require(tooOldLoad.Success && tooOldLoad.Resource != null, "Too-old read-only fixture could not be loaded.");
        tooOldLoad.Resource!.ResourceVersion = 0;
        Require(repository.Save(tooOldLoad.Resource, AgasPaths.ProjectFile(ordinaryDirectory)).Success,
            "Too-old read-only fixture could not be saved.");
        string? tooOldResult = main.TryGetOpenableProjectName(ordinaryDirectory, createIfMissing: true, out _);
        Require(!string.IsNullOrWhiteSpace(tooOldResult),
            "Existing too-old projects must preserve the workspace read-only open path.");
        main.Free();
    }

    private async Task ClickAsync(Control control)
    {
        Vector2 center = control.GetGlobalRect().GetCenter();
        Input.ParseInputEvent(new InputEventMouseMotion { Position = center, GlobalPosition = center });
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = true,
            Position = center,
            GlobalPosition = center
        });
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left,
            Pressed = false,
            Position = center,
            GlobalPosition = center
        });
        await Frames(2);
    }

    private async Task EnterTextAsync(LineEdit edit, string value)
    {
        edit.GrabFocus();
        edit.SelectAll();
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Backspace, PhysicalKeycode = Key.Backspace, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Backspace, PhysicalKeycode = Key.Backspace, Pressed = false });
        foreach (char character in value)
        {
            Input.ParseInputEvent(new InputEventKey { Unicode = character, Pressed = true });
            Input.ParseInputEvent(new InputEventKey { Unicode = character, Pressed = false });
        }
        await Frames(2);
    }

    private async Task RightClickAsync(Control control, Vector2 localPosition)
    {
        Vector2 globalPosition = control.GetGlobalRect().Position + localPosition;
        Input.ParseInputEvent(new InputEventMouseMotion { Position = globalPosition, GlobalPosition = globalPosition });
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Right,
            Pressed = true,
            Position = globalPosition,
            GlobalPosition = globalPosition
        });
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Right,
            Pressed = false,
            Position = globalPosition,
            GlobalPosition = globalPosition
        });
        await Frames(2);
    }

    private static int CountRows(VBoxContainer rows)
    {
        int count = 0;
        foreach (Node child in rows.GetChildren())
        {
            if (child is PanelContainer)
            {
                count++;
            }
        }
        return count;
    }

    private static PanelContainer FindRow(VBoxContainer rows, string projectName, string projectPath)
    {
        foreach (Node child in rows.GetChildren())
        {
            if (child is not PanelContainer panel)
            {
                continue;
            }

            Label? nameLabel = FindByName<Label>(panel, "ProjectNameLabel");
            if (nameLabel?.Text != projectName)
            {
                continue;
            }

            if (panel.TooltipText.Contains(projectPath, StringComparison.Ordinal))
            {
                return panel;
            }
        }
        throw new InvalidOperationException($"Project row was not found: {projectName} ({projectPath})");
    }

    private static T RequireNode<T>(Node root, string name) where T : Node
    {
        return FindByName<T>(root, name)
            ?? throw new InvalidOperationException($"Required node was not found: {name}");
    }

    private static T? FindByName<T>(Node root, string name) where T : Node
    {
        if (root is T match && root.Name == name)
        {
            return match;
        }
        foreach (Node child in root.GetChildren())
        {
            T? found = FindByName<T>(child, name);
            if (found != null)
            {
                return found;
            }
        }
        return null;
    }

    private static T? FindFirst<T>(Node root) where T : Node
    {
        if (root is T match)
        {
            return match;
        }
        foreach (Node child in root.GetChildren())
        {
            T? found = FindFirst<T>(child);
            if (found != null)
            {
                return found;
            }
        }
        return null;
    }

    private async Task Frames(int count)
    {
        for (int index = 0; index < count; index++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
