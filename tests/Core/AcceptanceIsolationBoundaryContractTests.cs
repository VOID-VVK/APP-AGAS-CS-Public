using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AppAgasCs.Tests.Core;

public sealed class AcceptanceIsolationBoundaryContractTests
{
    [Fact]
    public void RunnerContractIsolatesAllRootsAndGuardsKnownRealPathsAndProcessTrees()
    {
        string source = File.ReadAllText(Path.Combine(ProjectRoot.Path, "tests/Godot/run-acceptance.sh"));
        foreach (string isolatedRoot in new[]
        {
            "export HOME=",
            "export AGAS_HOME=",
            "export XDG_CONFIG_HOME=",
            "export XDG_DATA_HOME=",
            "export XDG_CACHE_HOME=",
            "export GODOT_USER_HOME=",
            "export AGAS_TEST_MANAGED_PROJECT_ROOT=",
        })
        {
            Assert.Contains(isolatedRoot, source, StringComparison.Ordinal);
        }

        Assert.Contains("real_known_paths=(", source, StringComparison.Ordinal);
        Assert.Contains("$real_home/.agas", source, StringComparison.Ordinal);
        Assert.Contains("$real_home/Documents/AGAS Projects", source, StringComparison.Ordinal);
        Assert.Contains("$real_godot_app_data", source, StringComparison.Ordinal);
        Assert.Contains("write_fingerprint \"$fingerprint_before\"", source, StringComparison.Ordinal);
        Assert.Contains("compare_fingerprints \"$fingerprint_before\" \"$fingerprint_after\"", source, StringComparison.Ordinal);

        Assert.Contains("os.setsid()", source, StringComparison.Ordinal);
        Assert.Contains("kill -TERM -- \"-$child_pid\"", source, StringComparison.Ordinal);
        Assert.Contains("kill -KILL -- \"-$child_pid\"", source, StringComparison.Ordinal);
        Assert.Contains("AGAS_ACCEPTANCE_COMMAND_TIMEOUT_SECONDS", source, StringComparison.Ordinal);
        Assert.Contains("timeout_marker", source, StringComparison.Ordinal);

        int isolationExports = source.IndexOf("export HOME=", StringComparison.Ordinal);
        int projectCopy = source.IndexOf("rsync -a", StringComparison.Ordinal);
        int isolatedBuild = source.IndexOf("run_isolated_command \\\n    \"$dotnet_physical_path\" build", StringComparison.Ordinal);
        int isolatedGodot = source.IndexOf("run_isolated_command \\\n    \"$godot_bin\" --headless", StringComparison.Ordinal);
        Assert.True(isolationExports >= 0 && isolationExports < projectCopy);
        Assert.True(projectCopy < isolatedBuild);
        Assert.True(isolatedBuild < isolatedGodot);
    }

    [Fact]
    public async Task SelfTestUsesFakesAndCoversTimeoutFingerprintAndParallelIsolation()
    {
        string script = Path.Combine(ProjectRoot.Path, "tests/Godot/AcceptanceIsolationSelfTest.sh");
        string selfTestSource = File.ReadAllText(script);
        Assert.Contains("fake_bin=\"$self_test_root/bin\"", selfTestSource, StringComparison.Ordinal);
        Assert.Contains("cat >\"$fake_bin/dotnet\" <<'FAKE'", selfTestSource, StringComparison.Ordinal);
        Assert.Contains("cat >\"$fake_bin/godot-mono\" <<'FAKE'", selfTestSource, StringComparison.Ordinal);
        Assert.Contains("GODOT_MONO_BIN=\"$fake_bin/godot-mono\"", selfTestSource, StringComparison.Ordinal);
        Assert.Contains("setup_case timeout", selfTestSource, StringComparison.Ordinal);
        Assert.Contains("setup_case fingerprint", selfTestSource, StringComparison.Ordinal);
        Assert.Contains("setup_case symlink-fingerprint", selfTestSource, StringComparison.Ordinal);
        Assert.Contains("setup_case parallel", selfTestSource, StringComparison.Ordinal);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("/bin/bash")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add(script);
        Assert.True(process.Start());
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        string output = await stdout;
        string errors = await stderr;
        Assert.True(process.ExitCode == 0, $"Acceptance isolation self-test failed ({process.ExitCode}).\nSTDOUT:\n{output}\nSTDERR:\n{errors}");
        Assert.Contains("Acceptance isolation self-test: PASS", output, StringComparison.Ordinal);
    }
}
