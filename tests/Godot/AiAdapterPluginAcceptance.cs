using System;
using System.IO;
using System.Linq;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Settings;
using Godot;

namespace AppAgasCs.Tests.Godot;

public partial class AiAdapterPluginAcceptance : Node
{
    private const string Prefix = "[AGAS][AI-ADAPTER-PLUGIN]";

    public override void _Ready()
    {
        string home = Path.Combine(Path.GetTempPath(), "agas-adapter-plugin-" + Guid.NewGuid().ToString("N"));
        try
        {
            string plugins = AgasAiSettingsPaths.AdaptersDirectory(home);
            Directory.CreateDirectory(plugins);
            string fixtures = Path.Combine(AppContext.BaseDirectory, "ai-adapter-fixtures");
            Require(Directory.Exists(fixtures), "External Adapter SDK fixtures were not staged.");
            File.Copy(Path.Combine(fixtures, "Vendor.AgasAdapter.dll"), Path.Combine(plugins, "10-vendor.dll"));
            File.Copy(Path.Combine(fixtures, "Vendor.AgasAdapter.deps.json"), Path.Combine(plugins, "10-vendor.deps.json"));
            File.Copy(Path.Combine(fixtures, "Vendor.Adapter.Dependency.dll"), Path.Combine(plugins, "Vendor.Adapter.Dependency.dll"));
            File.WriteAllText(Path.Combine(plugins, "Vendor.Native.Sidecar.dll"), "native fixture bytes");
            File.WriteAllText(Path.Combine(plugins, "11-native-sidecar.deps.json"), """
            {
              "runtimeTarget": { "name": ".NETCoreApp,Version=v8.0" },
              "targets": {
                ".NETCoreApp,Version=v8.0": {
                  "Vendor.Native.Sidecar/1.0.0": {
                    "native": { "runtimes/win-x64/native/Vendor.Native.Sidecar.dll": {} }
                  }
                }
              },
              "libraries": {}
            }
            """);
            File.WriteAllText(Path.Combine(plugins, "80-broken.dll"), "not a managed assembly");
            File.Copy(Path.Combine(fixtures, "Vendor.AgasAdapter.Conflict.dll"), Path.Combine(plugins, "90-conflict.dll"));

            AiImageGenerationAdapterRegistry registry = AiImageGenerationAdapterRegistry.CreateDefault(plugins);
            IAiImageGenerationAdapter basic = registry.Resolve("vendor.fixture-basic-v1");
            IAiImageGenerationAdapter dependent = registry.Resolve("vendor.fixture-dependent-v1");

            Require(typeof(IAiImageGenerationAdapter).Assembly.GetName().Name == "APP-AGAS-CS.AiAdapter.Contracts",
                "Godot host must consume the independent Adapter Contracts assembly.");
            Require(!ReferenceEquals(typeof(IAiImageGenerationAdapter).Assembly, GetType().Assembly),
                "Adapter contracts must not be compiled into the Godot host assembly.");
            Require(Path.GetFileName(basic.GetType().Assembly.Location) == "10-vendor.dll",
                "Godot must load a real external Adapter DLL.");
            Require(Path.GetFileName(dependent.GetType().Assembly.Location) == "10-vendor.dll",
                "Godot must load an external Adapter that uses a managed sidecar dependency.");
            Require(basic.GetType().GetInterfaces().Any(type =>
                    ReferenceEquals(type.Assembly, typeof(IAiImageGenerationAdapter).Assembly)),
                "Plugin and Godot host must share the exact contract assembly identity across ALCs.");
            Require(registry.Errors.Count == 2, "Only the intentionally broken and conflicting entry DLLs may fail.");
            Require(registry.Errors.Any(error => error.FileName == "80-broken.dll"),
                "Broken DLL must be isolated.");
            Require(registry.Errors.Any(error => error.FileName == "90-conflict.dll"),
                "Later adapterKind conflict must be isolated.");
            Require(registry.Errors.All(error => error.FileName != "Vendor.Adapter.Dependency.dll"),
                "A referenced managed dependency must not be misreported as an Adapter entry.");
            Require(registry.Errors.All(error => error.FileName != "Vendor.Native.Sidecar.dll"),
                "A native dependency declared by deps.json must not be misreported as an Adapter entry.");

            GD.Print($"{Prefix}[PASS]");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GD.PushError($"{Prefix}[FAIL] {exception}");
            GetTree().Quit(1);
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
