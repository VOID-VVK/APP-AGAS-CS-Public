using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppAgasCs.Features.Ai.Settings;

namespace AppAgasCs.Features.Ai.Generation;

public sealed record AiAdapterPluginLoadError(string FileName, string Message);

/// <summary>
/// String-keyed protocol adapter registry. Provider JSON files reference an
/// adapter kind; a third party only needs a plugin DLL when it introduces a
/// genuinely new wire protocol. Broken plugin DLLs are isolated and reported.
/// </summary>
public sealed class AiImageGenerationAdapterRegistry
{
    private static readonly Regex SafeAdapterKind = new(
        "^[a-z0-9][a-z0-9._-]{0,79}$",
        RegexOptions.CultureInvariant);
    private readonly IReadOnlyDictionary<string, IAiImageGenerationAdapter> _adapters;

    public AiImageGenerationAdapterRegistry(
        IEnumerable<IAiImageGenerationAdapter> adapters,
        IEnumerable<AiAdapterPluginLoadError>? errors = null)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var map = new Dictionary<string, IAiImageGenerationAdapter>(StringComparer.Ordinal);
        foreach (IAiImageGenerationAdapter adapter in adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            string kind = adapter.AdapterKind?.Trim() ?? string.Empty;
            if (!SafeAdapterKind.IsMatch(kind))
            {
                throw new ArgumentException(
                    "AdapterKind must be 1-80 lowercase letters, digits, dots, underscores or hyphens.",
                    nameof(adapters));
            }
            if (!map.TryAdd(kind, adapter))
            {
                throw new ArgumentException($"Duplicate adapter registration for '{kind}'.", nameof(adapters));
            }
        }

        _adapters = map;
        Errors = new ReadOnlyCollection<AiAdapterPluginLoadError>((errors ?? Array.Empty<AiAdapterPluginLoadError>())
            .OrderBy(error => error.FileName, StringComparer.Ordinal)
            .ThenBy(error => error.Message, StringComparer.Ordinal)
            .ToArray());
    }

    public IReadOnlyList<AiAdapterPluginLoadError> Errors { get; }

    public IAiImageGenerationAdapter Resolve(string adapterKind)
    {
        if (_adapters.TryGetValue(adapterKind?.Trim() ?? string.Empty, out IAiImageGenerationAdapter? adapter))
        {
            return adapter;
        }

        throw new AiGenerationException(
            AiGenerationErrorCode.AdapterNotFound,
            $"No image adapter is registered for '{adapterKind}'.");
    }

    public static AiImageGenerationAdapterRegistry CreateDefault(string? pluginDirectory = null)
    {
        string directory = Path.GetFullPath(pluginDirectory
            ?? AgasAiSettingsPaths.AdaptersDirectory(AgasAiSettingsPaths.ResolveHome()));
        var adapters = new List<IAiImageGenerationAdapter>
        {
            new OpenAiCompatibleImageAdapter(),
            new GeminiCompatibleImageAdapter(),
            new NewApiImageAdapter(),
        };
        var errors = new List<AiAdapterPluginLoadError>();
        var assemblyReferences = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var adapterEntryAssemblies = new HashSet<string>(StringComparer.Ordinal);
        var nonAdapterAssemblies = new List<NonAdapterAssembly>();
        if (Directory.Exists(directory))
        {
            HashSet<string> declaredSidecars = ReadDeclaredSidecarFileNames(directory);
            foreach (string path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal))
            {
                if (declaredSidecars.Contains(Path.GetFileName(path)))
                {
                    continue;
                }
                LoadPlugin(
                    path,
                    adapters,
                    errors,
                    assemblyReferences,
                    adapterEntryAssemblies,
                    nonAdapterAssemblies);
            }
        }
        HashSet<string> adapterDependencyClosure = ResolveDependencyClosure(
            adapterEntryAssemblies,
            assemblyReferences);
        foreach (NonAdapterAssembly candidate in nonAdapterAssemblies)
        {
            if (!adapterDependencyClosure.Contains(candidate.AssemblyName))
            {
                errors.Add(new AiAdapterPluginLoadError(
                    candidate.FileName,
                    "DLL 未导出 IAiImageGenerationAdapter 实现。"));
            }
        }
        return new AiImageGenerationAdapterRegistry(adapters, errors);
    }

    private static HashSet<string> ReadDeclaredSidecarFileNames(string directory)
    {
        var sidecars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(directory, "*.deps.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal))
        {
            string fileName = Path.GetFileName(path);
            const string suffix = ".deps.json";
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                entries.Add(fileName[..^suffix.Length] + ".dll");
            }
            try
            {
                if (new FileInfo(path).Length > 4 * 1024 * 1024) continue;
                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
                if (!document.RootElement.TryGetProperty("targets", out JsonElement targets)
                    || targets.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                foreach (JsonProperty target in targets.EnumerateObject())
                {
                    if (target.Value.ValueKind != JsonValueKind.Object) continue;
                    foreach (JsonProperty library in target.Value.EnumerateObject())
                    {
                        if (library.Value.ValueKind != JsonValueKind.Object) continue;
                        foreach (JsonProperty assetGroup in library.Value.EnumerateObject())
                        {
                            if (assetGroup.Name is not ("runtime" or "native" or "runtimeTargets" or "resources")
                                || assetGroup.Value.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }
                            foreach (JsonProperty asset in assetGroup.Value.EnumerateObject())
                            {
                                string assetFileName = Path.GetFileName(asset.Name.Replace('\\', '/'));
                                if (assetFileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                                    && File.Exists(Path.Combine(directory, assetFileName)))
                                {
                                    sidecars.Add(assetFileName);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // The plugin entry remains discoverable. AssemblyDependencyResolver
                // will diagnose an unusable deps file as one isolated entry failure.
            }
        }
        sidecars.ExceptWith(entries);
        return sidecars;
    }

    private static void LoadPlugin(
        string path,
        ICollection<IAiImageGenerationAdapter> adapters,
        ICollection<AiAdapterPluginLoadError> errors,
        IDictionary<string, HashSet<string>> assemblyReferences,
        ISet<string> adapterEntryAssemblies,
        ICollection<NonAdapterAssembly> nonAdapterAssemblies)
    {
        AdapterPluginLoadContext? loadContext = null;
        try
        {
            loadContext = new AdapterPluginLoadContext(path);
            Assembly assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(path));
            string assemblyName = assembly.GetName().Name
                ?? throw new InvalidDataException("DLL 缺少程序集名称。");
            if (!assemblyReferences.TryGetValue(assemblyName, out HashSet<string>? references))
            {
                references = new HashSet<string>(StringComparer.Ordinal);
                assemblyReferences.Add(assemblyName, references);
            }
            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
            {
                if (!string.IsNullOrWhiteSpace(reference.Name)) references.Add(reference.Name);
            }
            Type[] types = assembly.GetExportedTypes()
                .Where(type => typeof(IAiImageGenerationAdapter).IsAssignableFrom(type)
                               && !type.IsAbstract
                               && !type.IsInterface)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();
            if (types.Length == 0)
            {
                nonAdapterAssemblies.Add(new NonAdapterAssembly(Path.GetFileName(path), assemblyName));
                loadContext.Unload();
                return;
            }
            // Even an invalid/conflicting entry owns its dependency sidecars;
            // report the entry failure once without mislabeling its libraries.
            adapterEntryAssemblies.Add(assemblyName);
            var pluginAdapters = new List<IAiImageGenerationAdapter>();
            foreach (Type type in types)
            {
                if (type.GetConstructor(Type.EmptyTypes) == null)
                {
                    throw new InvalidDataException($"Adapter '{type.FullName}' 必须提供 public 无参构造函数。");
                }
                pluginAdapters.Add((IAiImageGenerationAdapter)(Activator.CreateInstance(type)
                    ?? throw new InvalidDataException($"Adapter '{type.FullName}' 无法创建。")));
            }
            HashSet<string> existingKinds = adapters.Select(adapter => adapter.AdapterKind).ToHashSet(StringComparer.Ordinal);
            string[] pluginKinds = pluginAdapters.Select(adapter => adapter.AdapterKind?.Trim() ?? string.Empty).ToArray();
            if (pluginKinds.Any(kind => !SafeAdapterKind.IsMatch(kind))
                || pluginKinds.Distinct(StringComparer.Ordinal).Count() != pluginKinds.Length
                || pluginKinds.Any(existingKinds.Contains))
                throw new InvalidDataException("Adapter 插件包含空或重复的 adapterKind，整个 DLL 已隔离。");
            foreach (IAiImageGenerationAdapter adapter in pluginAdapters) adapters.Add(adapter);
        }
        catch (Exception exception)
        {
            loadContext?.Unload();
            errors.Add(new AiAdapterPluginLoadError(
                Path.GetFileName(path),
                exception is InvalidDataException ? exception.Message : "Adapter 插件无法安全加载。"));
        }
    }

    private static HashSet<string> ResolveDependencyClosure(
        IEnumerable<string> entryAssemblies,
        IReadOnlyDictionary<string, HashSet<string>> assemblyReferences)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(entryAssemblies.OrderBy(name => name, StringComparer.Ordinal));
        while (pending.Count > 0)
        {
            string assemblyName = pending.Dequeue();
            if (!visited.Add(assemblyName)
                || !assemblyReferences.TryGetValue(assemblyName, out HashSet<string>? references))
            {
                continue;
            }
            foreach (string reference in references.OrderBy(name => name, StringComparer.Ordinal))
            {
                pending.Enqueue(reference);
            }
        }
        return visited;
    }

    private sealed record NonAdapterAssembly(string FileName, string AssemblyName);

    private sealed class AdapterPluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _pluginDirectory;

        public AdapterPluginLoadContext(string pluginPath)
            : base($"AGAS AI adapter: {Path.GetFileName(pluginPath)}", isCollectible: true)
        {
            string fullPath = Path.GetFullPath(pluginPath);
            _resolver = new AssemblyDependencyResolver(fullPath);
            _pluginDirectory = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidDataException("Adapter plugin directory is unavailable.");
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // IAiImageGenerationAdapter must keep the host assembly's type identity.
            if (string.Equals(
                    assemblyName.Name,
                    typeof(IAiImageGenerationAdapter).Assembly.GetName().Name,
                    StringComparison.Ordinal))
            {
                // Godot loads game assemblies in its own AssemblyLoadContext.
                // Returning null falls back only to the default context, which
                // cannot see that contract assembly and breaks every real DLL.
                return typeof(IAiImageGenerationAdapter).Assembly;
            }
            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path != null) return LoadFromAssemblyPath(path);

            // A minimal plugin built with a direct host reference may not ship a deps.json.
            // Permit managed dependencies beside the plugin while keeping probing top-level and
            // deterministic; framework/host assemblies still fall back to the default context.
            string? simpleName = assemblyName.Name;
            if (string.IsNullOrWhiteSpace(simpleName)) return null;
            string sibling = Path.GetFullPath(Path.Combine(_pluginDirectory, simpleName + ".dll"));
            if (!string.Equals(Path.GetDirectoryName(sibling), _pluginDirectory, StringComparison.Ordinal)
                || !File.Exists(sibling)) return null;
            return LoadFromAssemblyPath(sibling);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path == null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
