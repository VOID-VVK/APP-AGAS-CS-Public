using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AGAS.UI;
using AppAgasCs.Core.Persistence;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.UI;
using AppAgasCs.Features.Ai.Workspace;
using AppAgasCs.Mcp;
using Godot;

public partial class McpSettingsUiAcceptance : Node
{
    private const string Prefix = "[AGAS-H2][MCP-SETTINGS-UI]";
    private SettingsBackup? _settingsBackup;
    private SettingsBackup? _recentProjectsBackup;
    private AGAS.UI.Main? _main;
    private CheckBox? _enabledSwitch;
    private TaskCompletionSource<object?>? _mainQuitIssued;
    private bool _wmCloseShutdownCompleted;
    private readonly List<string> _temporaryProjectRoots = new();

    public override async void _Ready()
    {
        int exitCode = 0;
        try
        {
            await RunAllAsync();
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError($"{Prefix}[FAIL] {exception}");
        }
        finally
        {
            var cleanupErrors = new List<Exception>();

            try
            {
                await StopRuntimeForCleanupAsync();
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(new InvalidOperationException(
                    "Failed to stop the MCP runtime and release port 54119.",
                    exception));
            }

            foreach (string projectRoot in _temporaryProjectRoots)
            {
                try
                {
                    if (Directory.Exists(projectRoot))
                    {
                        Directory.Delete(projectRoot, recursive: true);
                    }
                    if (Directory.Exists(projectRoot))
                    {
                        throw new IOException("Temporary project directory still exists: " + projectRoot);
                    }
                }
                catch (Exception exception)
                {
                    cleanupErrors.Add(new InvalidOperationException(
                        "Failed to delete temporary project directory: " + projectRoot,
                        exception));
                }
            }

            try
            {
                RestoreAndVerifyBackup(_recentProjectsBackup, "recent-projects");
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(new InvalidOperationException(
                    "Failed to restore and verify the original recent-projects file state.",
                    exception));
            }

            // Settings must be restored last, after every operation that could
            // persist the test's temporary Enabled value has stopped.
            try
            {
                RestoreAndVerifyBackup(_settingsBackup, "MCP settings");
            }
            catch (Exception exception)
            {
                cleanupErrors.Add(new InvalidOperationException(
                    "Failed to restore and verify the original MCP settings file state.",
                    exception));
            }

            if (exitCode == 0 && !_wmCloseShutdownCompleted)
            {
                cleanupErrors.Add(new InvalidOperationException(
                    "The real Main WM-close shutdown path did not finish before acceptance cleanup."));
            }

            foreach (Exception cleanupError in cleanupErrors)
            {
                GD.PushError($"{Prefix}[CLEANUP][FAIL] {cleanupError}");
            }
            if (cleanupErrors.Count > 0)
            {
                exitCode = 1;
            }

            if (exitCode == 0)
            {
                GD.Print($"{Prefix}[PASS] UI settings/port acceptance completed WM close, runtime stop, and verified user-file restoration.");
            }

            GetTree().Quit(exitCode);
        }
    }

    private async Task RunAllAsync()
    {
        GD.Print($"{Prefix}[PHASE 0] backup and isolate user://agas-mcp-settings.tres");
        BackupAndDeleteSettings();
        BackupAndDeleteRecentProjects();

        Assert.False(await CanConnectTcpAsync(), "54119 should not listen before Main.tscn is instantiated.");
        Assert.True(CanBindFixedPort(), "54119 should be bindable before Main.tscn is instantiated.");

        GD.Print($"{Prefix}[PHASE 1] instantiate real res://src/UI/Main.tscn and wait Ready");
        PackedScene? mainScene = GD.Load<PackedScene>("res://src/UI/Main.tscn");
        Assert.True(mainScene != null, "Main.tscn should load.");
        _main = mainScene!.Instantiate<AGAS.UI.Main>();
        _mainQuitIssued = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _main.ApplicationQuitOverrideForTesting = () => _mainQuitIssued.TrySetResult(null);
        AddChild(_main);
        await WaitFramesAsync(4);

        PopupMenu settingsMenu = await WaitForNodeAsync<PopupMenu>(_main, "设置", "App Settings menu should exist after Main Ready.");
        Assert.False(await CanConnectTcpAsync(), "54119 should not listen with default disabled settings.");
        Assert.True(CanBindFixedPort(), "54119 should remain bindable with default disabled settings.");

        GD.Print($"{Prefix}[PHASE 2] open dialog through AppMenuBar Settings > MCP...");
        settingsMenu.EmitSignal(PopupMenu.SignalName.IdPressed, 1);
        await WaitFramesAsync(2);
        McpSettingsDialog dialog = await WaitForDialogAsync();
        _enabledSwitch = await WaitForNodeAsync<CheckBox>(dialog, "McpEnabledSwitch", "McpEnabledSwitch should exist in McpSettingsDialog.");

        Assert.False(_enabledSwitch.ButtonPressed, "Default MCP switch should be false.");
        Assert.Equal(McpSettingsStatus.Disabled, dialog.Status, "Default dialog status should be Disabled.");
        Assert.False(LoadPersistedEnabled(), "Persisted default MCP setting should be false after isolated LoadOrCreate.");
        Assert.False(await CanConnectTcpAsync(), "54119 should still not listen after opening settings dialog.");

        GD.Print($"{Prefix}[PHASE 3] enable through McpEnabledSwitch and verify persistence/runtime/port");
        await ToggleSwitchViaNodeAsync(_enabledSwitch, true);
        await WaitUntilAsync(
            () => LoadPersistedEnabled(),
            "settings file should persist Enabled=true after enabling via UI switch.",
            TimeSpan.FromSeconds(10));
        GD.Print($"{Prefix}[PHASE 3] persisted Enabled=true");
        await WaitUntilAsync(
            () => dialog.Status == McpSettingsStatus.Running,
            "dialog status should become Running after enabling via UI switch.",
            TimeSpan.FromSeconds(20));
        GD.Print($"{Prefix}[PHASE 3] status Running");
        await WaitUntilAsync(
            CanConnectTcpAsync,
            "54119 should accept TCP after enabling MCP through UI.",
            TimeSpan.FromSeconds(10));
        GD.Print($"{Prefix}[PHASE 3] TCP reachable");
        Assert.True(await CanConnectHttpAsync(), "54119 should answer HTTP after enabling MCP through UI.");
        GD.Print($"{Prefix}[PHASE 3] HTTP reachable");

        AgasMcpAiJobScheduler aiScheduler = _main.McpAiJobSchedulerForTesting
            ?? throw new InvalidOperationException("Main MCP AI job scheduler is not initialized.");
        AgasMcpAiJobStore aiStore = _main.McpAiJobStoreForTesting
            ?? throw new InvalidOperationException("Main MCP AI job store is not initialized.");
        AgasAiGenerationCoordinator aiCoordinator = GetPrivateField<AgasAiGenerationCoordinator>(_main, "_aiGenerationCoordinator");
        await VerifyProjectSwitchAndCloseCancelJobsAsync(aiScheduler, aiStore, aiCoordinator);
        AgasMcpAiProjectScope lifecycleScope = AgasMcpAiProjectScope.Create(
            ProjectSettings.GlobalizePath("user://"),
            "settings-lifecycle-project");
        var runnerEntered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        AgasMcpAiJobSubmitResult runningJob = aiScheduler.Submit(new AgasMcpAiJobSubmission(
            lifecycleScope,
            "TextToImage",
            async token =>
            {
                runnerEntered.TrySetResult(null);
                await Task.Delay(TimeSpan.FromMinutes(1), token);
                return new AgasMcpAiJobRunResult(new AgasMcpAiJobResultMetadata(
                    "assets/generated/never-written.png",
                    null,
                    "must be canceled"));
            }));
        Assert.True(runningJob.Accepted && runningJob.Job != null, "Real Main AI scheduler must accept the running lifecycle fixture.");
        await runnerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, aiScheduler.ActiveCount, "Lifecycle fixture must be running before MCP stop.");
        Assert.Equal(1, aiCoordinator.ActiveCount, "Running lifecycle fixture must hold one shared permit before stop.");

        GD.Print($"{Prefix}[PHASE 4] disable through same McpEnabledSwitch and verify persistence/runtime/port release");
        await ToggleSwitchViaNodeAsync(_enabledSwitch, false);
        await WaitUntilAsync(
            () => !LoadPersistedEnabled(),
            "settings file should persist Enabled=false after disabling via UI switch.",
            TimeSpan.FromSeconds(10));
        GD.Print($"{Prefix}[PHASE 4] persisted Enabled=false");
        await WaitUntilAsync(
            () => dialog.Status is McpSettingsStatus.Disabled or McpSettingsStatus.Stopped,
            "dialog status should become Disabled or Stopped after disabling via UI switch.",
            TimeSpan.FromSeconds(20));
        GD.Print($"{Prefix}[PHASE 4] status Disabled/Stopped");
        await WaitUntilAsync(
            CanBindFixedPort,
            "54119 should be bindable after disabling MCP through UI.",
            TimeSpan.FromSeconds(10));
        Assert.False(await CanConnectTcpAsync(), "54119 should not accept TCP after disabling MCP through UI.");
        Assert.True(aiStore.TryGet(lifecycleScope, runningJob.Job!.JobId, out AgasMcpAiJobSnapshot? canceledJob),
            "Stopped lifecycle fixture must remain queryable in its project scope.");
        Assert.Equal(AgasMcpAiJobState.Canceled, canceledJob!.State,
            "Disabling MCP through real Main/Lifecycle must cancel a running AI job.");
        Assert.Equal(0, aiScheduler.ActiveCount, "Real Main/Lifecycle stop must return with no active AI jobs.");
        Assert.Equal(0, aiScheduler.QueuedCount, "Real Main/Lifecycle stop must return with no queued AI jobs.");
        Assert.Equal(0, aiCoordinator.ActiveCount, "Real Main/Lifecycle stop must release all shared AI permits.");

        GD.Print($"{Prefix}[PHASE 5] re-enable through switch, then trigger WM close request");
        await ToggleSwitchViaNodeAsync(_enabledSwitch, true);
        await WaitUntilAsync(
            () => LoadPersistedEnabled(),
            "settings file should persist Enabled=true after second enable via UI switch.",
            TimeSpan.FromSeconds(10));
        GD.Print($"{Prefix}[PHASE 5] persisted Enabled=true");
        await WaitUntilAsync(
            () => dialog.Status == McpSettingsStatus.Running,
            "dialog status should become Running after second enable.",
            TimeSpan.FromSeconds(20));
        GD.Print($"{Prefix}[PHASE 5] status Running");
        Assert.True(await CanConnectTcpAsync(), "54119 should accept TCP before close-path shutdown.");

        _main.Notification((int)Node.NotificationWMCloseRequest);
        await _mainQuitIssued!.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(_main.ApplicationQuitIssuedForTesting,
            "Main must issue application quit only after its WM-close shutdown path completes.");
        Assert.True(_main.McpDisposedForTesting,
            "Main must finish MCP disposal before issuing application quit.");
        await WaitUntilAsync(
            CanBindFixedPort,
            "54119 should be released by Main's real WM-close shutdown path.",
            TimeSpan.FromSeconds(5));
        Assert.False(await CanConnectTcpAsync(),
            "54119 should not accept TCP after Main's real WM-close shutdown path completes.");
        _wmCloseShutdownCompleted = true;
        GD.Print($"{Prefix}[PHASE 5] real WM-close shutdown completed and quit was issued");
    }

    private async Task VerifyProjectSwitchAndCloseCancelJobsAsync(
        AgasMcpAiJobScheduler scheduler,
        AgasMcpAiJobStore store,
        AgasAiGenerationCoordinator coordinator)
    {
        GD.Print($"{Prefix}[PHASE 3B] verify project switch/close cancel real running and queued MCP AI jobs");
        string firstRoot = CreateTemporaryProject("switch-source");
        string secondRoot = CreateTemporaryProject("switch-target");
        InvokeMain("OpenWorkspace", new[] { typeof(string), typeof(bool) }, firstRoot, false);
        await WaitFramesAsync(3);
        MainWorkspace firstWorkspace = GetPrivateField<MainWorkspace>(_main!, "_workspace");
        Assert.True(firstWorkspace.TryGetCurrentProjectIdentity(out string firstProjectRoot, out string firstProjectId),
            "First lifecycle project identity must be available.");
        AgasMcpAiProjectScope firstScope = AgasMcpAiProjectScope.Create(firstProjectRoot, firstProjectId);
        AiGenerationPanel firstAiPanel = FindFirstNode<AiGenerationPanel>(firstWorkspace)
            ?? throw new InvalidOperationException("First workspace AI panel is unavailable.");
        AgasMcpAiJobSubmitResult automaticTerminal = scheduler.Submit(new AgasMcpAiJobSubmission(
            firstScope,
            "RegionEdit",
            async token =>
            {
                await Task.Delay(40, token);
                return new AgasMcpAiJobRunResult(new AgasMcpAiJobResultMetadata(
                    "assets/generated/main-wiring-fixture.png",
                    null,
                    "completed without polling"));
            }));
        Assert.True(automaticTerminal.Accepted && automaticTerminal.Job != null,
            "Production Main scheduler must accept the no-poll UI fixture.");
        await WaitUntilAsync(
            () => firstAiPanel.LatestExternalJobStatus is { } status
                && status.JobId == automaticTerminal.Job!.JobId
                && status.State == AiGenerationExternalJobState.Succeeded,
            "Production Main wiring must refresh the AI panel to Succeeded without ai.status polling.",
            TimeSpan.FromSeconds(10));
        GD.Print($"{Prefix}[PHASE 3B] production Main no-poll terminal UI refresh PASS");

        List<AgasMcpAiJobSnapshot> switchJobs = await SubmitRunningAndQueuedJobsAsync(
            scheduler,
            coordinator,
            firstScope,
            "switch");

        InvokeMain("OpenWorkspace", new[] { typeof(string), typeof(bool) }, secondRoot, false);
        await WaitForCanceledAndQuiescedAsync(scheduler, store, switchJobs, "project switch");
        await WaitFramesAsync(5);

        MainWorkspace secondWorkspace = GetPrivateField<MainWorkspace>(_main!, "_workspace");
        Assert.True(secondWorkspace.TryGetCurrentProjectIdentity(out string secondProjectRoot, out string secondProjectId),
            "Second lifecycle project identity must be available.");
        AiGenerationPanel secondAiPanel = FindFirstNode<AiGenerationPanel>(secondWorkspace)
            ?? throw new InvalidOperationException("Second workspace AI panel is unavailable.");
        Assert.True(secondAiPanel.LatestExternalJobStatus == null,
            "The new workspace AI panel must not receive any queued/running/canceled state from the previous project scope.");
        GD.Print($"{Prefix}[PHASE 3B] cross-project dispatcher scope isolation PASS");
        AgasMcpAiProjectScope secondScope = AgasMcpAiProjectScope.Create(secondProjectRoot, secondProjectId);
        List<AgasMcpAiJobSnapshot> closeJobs = await SubmitRunningAndQueuedJobsAsync(
            scheduler,
            coordinator,
            secondScope,
            "close");

        InvokeMain("ShowProjectPicker", Type.EmptyTypes);
        await WaitForCanceledAndQuiescedAsync(scheduler, store, closeJobs, "project close");
        Assert.Equal(0, coordinator.ActiveCount,
            "Project close must release every shared MCP generation permit.");
        GD.Print($"{Prefix}[PHASE 3B] project switch/close AI cancellation PASS");
    }

    private async Task<List<AgasMcpAiJobSnapshot>> SubmitRunningAndQueuedJobsAsync(
        AgasMcpAiJobScheduler scheduler,
        AgasAiGenerationCoordinator coordinator,
        AgasMcpAiProjectScope scope,
        string label)
    {
        var jobs = new List<AgasMcpAiJobSnapshot>();
        var entered = new List<TaskCompletionSource<object?>>();
        for (int index = 0; index < coordinator.MaximumConcurrentGenerations; index++)
        {
            var started = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            entered.Add(started);
            AgasMcpAiJobSubmitResult submitted = scheduler.Submit(new AgasMcpAiJobSubmission(
                scope,
                "RegionEdit",
                async token =>
                {
                    started.TrySetResult(null);
                    await Task.Delay(TimeSpan.FromMinutes(1), token);
                    return new AgasMcpAiJobRunResult(new AgasMcpAiJobResultMetadata(
                        $"assets/generated/{label}-never-written.png",
                        null,
                        "must be canceled"));
                }));
            Assert.True(submitted.Accepted && submitted.Job != null,
                $"{label} lifecycle running fixture must be accepted.");
            jobs.Add(submitted.Job!);
        }

        await Task.WhenAll(entered.ConvertAll(item => item.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        AgasMcpAiJobSubmitResult queued = scheduler.Submit(new AgasMcpAiJobSubmission(
            scope,
            "RegionEdit",
            token => throw new InvalidOperationException($"{label} queued fixture must never start.")));
        Assert.True(queued.Accepted && queued.Job != null,
            $"{label} lifecycle queued fixture must be accepted.");
        jobs.Add(queued.Job!);
        Assert.Equal(coordinator.MaximumConcurrentGenerations, scheduler.ActiveCount,
            $"{label} fixture must have real running jobs before the lifecycle action.");
        Assert.Equal(1, scheduler.QueuedCount,
            $"{label} fixture must have one queued job before the lifecycle action.");
        return jobs;
    }

    private async Task WaitForCanceledAndQuiescedAsync(
        AgasMcpAiJobScheduler scheduler,
        AgasMcpAiJobStore store,
        IReadOnlyList<AgasMcpAiJobSnapshot> jobs,
        string label)
    {
        await WaitUntilAsync(
            () => scheduler.ActiveCount == 0
                && scheduler.QueuedCount == 0
                && AllCanceled(store, jobs),
            $"{label} must cancel running and queued MCP AI jobs and release their permits.",
            TimeSpan.FromSeconds(10));
    }

    private static bool AllCanceled(
        AgasMcpAiJobStore store,
        IReadOnlyList<AgasMcpAiJobSnapshot> jobs)
    {
        foreach (AgasMcpAiJobSnapshot job in jobs)
        {
            if (!store.TryGet(job.ProjectScope, job.JobId, out AgasMcpAiJobSnapshot? current)
                || current?.State != AgasMcpAiJobState.Canceled)
            {
                return false;
            }
        }
        return true;
    }

    private string CreateTemporaryProject(string label)
    {
        string root = Path.Combine(Path.GetTempPath(), $"agas-mcp-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _temporaryProjectRoots.Add(root);
        AgasPersistenceResult initialized = new AgasProjectInitializer().EnsureInitialized(root);
        Assert.True(initialized.Success, initialized.ErrorMessage ?? $"Failed to initialize {label} lifecycle project.");
        return root;
    }

    private void InvokeMain(string methodName, Type[] parameterTypes, params object[] arguments)
    {
        MethodInfo? method = typeof(AGAS.UI.Main).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: parameterTypes,
            modifiers: null);
        if (method == null)
        {
            throw new InvalidOperationException($"Missing Main.{methodName} lifecycle method.");
        }
        method.Invoke(_main, arguments);
    }

    private async Task ToggleSwitchViaNodeAsync(CheckBox enabledSwitch, bool enabled)
    {
        await WaitUntilAsync(
            () => !enabledSwitch.Disabled,
            $"McpEnabledSwitch should be enabled before toggling to {enabled}.",
            TimeSpan.FromSeconds(10));

        if (enabledSwitch.ButtonPressed == enabled)
        {
            return;
        }

        enabledSwitch.ButtonPressed = enabled;
        await WaitFramesAsync(2);
    }

    private async Task StopRuntimeForCleanupAsync()
    {
        if (_main != null && IsInstanceValid(_main))
        {
            await _main.EnsureMcpDisposedForTestingAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }

        await WaitUntilAsync(
            CanBindFixedPort,
            "54119 should be released before restoring user settings.",
            TimeSpan.FromSeconds(15));
        if (!CanBindFixedPort())
        {
            throw new InvalidOperationException("Port 54119 was not released after MCP runtime cleanup.");
        }
    }

    private void BackupAndDeleteSettings()
    {
        string absolutePath = ProjectSettings.GlobalizePath(AgasMcpSettingsStore.SettingsPath);
        _settingsBackup = new SettingsBackup(absolutePath, File.Exists(absolutePath), File.Exists(absolutePath) ? File.ReadAllBytes(absolutePath) : null);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath) ?? ".");

        if (File.Exists(absolutePath))
        {
            File.Delete(absolutePath);
        }

        if (File.Exists(absolutePath))
        {
            throw new InvalidOperationException("Failed to isolate existing settings file: " + absolutePath);
        }
    }

    private void BackupAndDeleteRecentProjects()
    {
        string absolutePath = ProjectSettings.GlobalizePath(RecentProjectsStore.RecentProjectsPath);
        _recentProjectsBackup = new SettingsBackup(
            absolutePath,
            File.Exists(absolutePath),
            File.Exists(absolutePath) ? File.ReadAllBytes(absolutePath) : null);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath) ?? ".");
        if (File.Exists(absolutePath))
        {
            File.Delete(absolutePath);
        }
        if (File.Exists(absolutePath))
        {
            throw new InvalidOperationException("Failed to isolate existing recent-projects file: " + absolutePath);
        }
    }

    private void RestoreAndVerifyBackup(SettingsBackup? backup, string label)
    {
        if (backup == null)
        {
            throw new InvalidOperationException($"No {label} backup was captured.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(backup.AbsolutePath) ?? ".");
        if (backup.Existed)
        {
            byte[] expectedBytes = backup.Bytes
                ?? throw new InvalidOperationException($"The existing {label} backup has no captured bytes.");
            File.WriteAllBytes(backup.AbsolutePath, expectedBytes);
            if (!File.Exists(backup.AbsolutePath))
            {
                throw new IOException($"The restored {label} file does not exist: {backup.AbsolutePath}");
            }

            byte[] actualBytes = File.ReadAllBytes(backup.AbsolutePath);
            if (!actualBytes.AsSpan().SequenceEqual(expectedBytes))
            {
                throw new IOException($"The restored {label} file differs from its byte-for-byte backup: {backup.AbsolutePath}");
            }
        }
        else
        {
            if (File.Exists(backup.AbsolutePath))
            {
                File.Delete(backup.AbsolutePath);
            }
            if (File.Exists(backup.AbsolutePath))
            {
                throw new IOException($"The originally absent {label} file still exists after restoration: {backup.AbsolutePath}");
            }
        }

        GD.Print($"{Prefix}[CLEANUP] restored and verified original {label} file state: {(backup.Existed ? "present" : "absent")}");
    }

    private bool LoadPersistedEnabled()
    {
        string absolutePath = ProjectSettings.GlobalizePath(AgasMcpSettingsStore.SettingsPath);
        Assert.True(File.Exists(absolutePath), "settings file should exist at " + absolutePath);
        Resource? loaded = ResourceLoader.Load(AgasMcpSettingsStore.SettingsPath, string.Empty, ResourceLoader.CacheMode.Ignore);
        Assert.True(loaded is AgasMcpSettingsResource, "settings file should load as AgasMcpSettingsResource.");
        return ((AgasMcpSettingsResource)loaded!).Enabled;
    }

    private async Task<McpSettingsDialog> WaitForDialogAsync()
    {
        return await WaitUntilValueAsync(
            () => FindFirstNode<McpSettingsDialog>(_main),
            "McpSettingsDialog should be created by AppMenuBar Settings > MCP... signal chain.",
            TimeSpan.FromSeconds(10));
    }

    private async Task<T> WaitForNodeAsync<T>(Node root, string name, string message) where T : Node
    {
        return await WaitUntilValueAsync(
            () => root.FindChild(name, true, false) as T,
            message,
            TimeSpan.FromSeconds(10));
    }

    private async Task<T> WaitUntilValueAsync<T>(Func<T?> probe, string message, TimeSpan timeout) where T : class
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            T? value = probe();
            if (value != null)
            {
                return value;
            }

            await WaitFramesAsync(1);
        }

        throw new InvalidOperationException(message);
    }

    private async Task WaitUntilAsync(Func<bool> condition, string message, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (condition())
                {
                    return;
                }
            }
            catch (Exception exception)
            {
                lastError = exception;
            }

            await WaitFramesAsync(1);
        }

        throw new InvalidOperationException(lastError == null ? message : message + " LastError=" + lastError.GetBaseException().Message);
    }

    private async Task WaitUntilAsync(Func<Task<bool>> condition, string message, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (Exception exception)
            {
                lastError = exception;
            }

            await WaitFramesAsync(1);
        }

        throw new InvalidOperationException(lastError == null ? message : message + " LastError=" + lastError.GetBaseException().Message);
    }

    private async Task WaitFramesAsync(int count)
    {
        for (int i = 0; i < count; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static T? FindFirstNode<T>(Node? root) where T : Node
    {
        if (root == null)
        {
            return null;
        }

        if (root is T typed)
        {
            return typed;
        }

        foreach (Node child in root.GetChildren())
        {
            T? match = FindFirstNode<T>(child);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private static T GetPrivateField<T>(object instance, string fieldName) where T : class
    {
        FieldInfo? field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(instance) as T
            ?? throw new InvalidOperationException($"Missing private field {fieldName} ({typeof(T).Name}).");
    }

    private static async Task<bool> CanConnectTcpAsync()
    {
        using TcpClient client = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(700));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, AgasMcpSettingsResource.FixedPort, timeout.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> CanConnectHttpAsync()
    {
        using TcpClient client = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, AgasMcpSettingsResource.FixedPort, timeout.Token);
            await using NetworkStream stream = client.GetStream();
            byte[] request = Encoding.ASCII.GetBytes("GET /mcp HTTP/1.1\r\nHost: 127.0.0.1:54119\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(request, timeout.Token);
            byte[] responsePrefix = new byte[5];
            int read = await stream.ReadAsync(responsePrefix, timeout.Token);
            return read == responsePrefix.Length && Encoding.ASCII.GetString(responsePrefix) == "HTTP/";
        }
        catch
        {
            return false;
        }
    }

    private static bool CanBindFixedPort()
    {
        TcpListener listener = new(IPAddress.Loopback, AgasMcpSettingsResource.FixedPort);
        try
        {
            listener.Start();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed record SettingsBackup(string AbsolutePath, bool Existed, byte[]? Bytes);

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
    }
}
