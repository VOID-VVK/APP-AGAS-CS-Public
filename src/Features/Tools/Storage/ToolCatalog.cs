#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppAgasCs.Features.Tools.Execution;
using AppAgasCs.Features.Tools.Model;

namespace AppAgasCs.Features.Tools.Storage;

/// <summary>Global tool definitions; no project or former graph/module storage is read.</summary>
public sealed class ToolCatalog
{
    public const string FileName = "catalog-v1.json";
    private const int SchemaVersion = 1;
    private const int MaxDocumentBytes = 8 * 1024 * 1024;
    private const int MaxSourceLength = 1024 * 1024;
    private const int MaxChains = 1000;
    private const int MaxTools = 1000;
    private const int MaxChainSteps = 32;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();

    public ToolCatalog(string? agasHome = null)
    {
        string home = agasHome ?? ResolveHome();
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        RootDirectory = Path.GetFullPath(Path.Combine(home, "tools"));
        DocumentPath = Path.Combine(RootDirectory, FileName);
    }

    public string RootDirectory { get; }
    public string DocumentPath { get; }

    public IReadOnlyList<ToolDefinition> ListTools()
    {
        lock (_gate) return Array.AsReadOnly(Read().Tools.Select(Copy).ToArray());
    }

    public ToolDefinition LoadTool(string id)
    {
        ValidateId(id);
        lock (_gate) return Copy(FindTool(Read(), id));
    }

    public ToolDefinition CreateTool(string id, string name, string source)
    {
        ValidateId(id);
        ValidateName(name);
        ValidateSource(source);
        lock (_gate)
        {
            CatalogDocument document = Read();
            EnsureAvailable(document, id);
            if (document.Tools.Count >= MaxTools)
                throw new InvalidOperationException("Tool catalog is full.");
            var tool = new ToolDefinition
            {
                Id = id,
                Name = name.Trim(),
                Revision = 1,
                Kind = ToolImplementationKind.Gdscript,
                Source = source,
            };
            document.Tools.Add(tool);
            Write(document);
            return Copy(tool);
        }
    }

    public ToolDefinition UpdateTool(string id, int expectedRevision, string name, string source)
    {
        ValidateId(id);
        ValidateName(name);
        ValidateSource(source);
        lock (_gate)
        {
            CatalogDocument document = Read();
            int index = IndexOfTool(document, id);
            ToolDefinition previous = document.Tools[index];
            RequireEditable(previous);
            RequireRevision(previous.Revision, expectedRevision);
            ToolDefinition updated = previous with
            {
                Name = name.Trim(),
                Source = source,
                Revision = checked(previous.Revision + 1),
            };
            document.Tools[index] = updated;
            Write(document);
            return Copy(updated);
        }
    }

    public ToolDefinition RenameTool(string id, int expectedRevision, string name)
    {
        ValidateId(id);
        ValidateName(name);
        lock (_gate)
        {
            CatalogDocument document = Read();
            int index = IndexOfTool(document, id);
            ToolDefinition previous = document.Tools[index];
            RequireEditable(previous);
            RequireRevision(previous.Revision, expectedRevision);
            ToolDefinition updated = previous with
            {
                Name = name.Trim(),
                Revision = checked(previous.Revision + 1),
            };
            document.Tools[index] = updated;
            Write(document);
            return Copy(updated);
        }
    }

    public void DeleteTool(string id, int expectedRevision)
    {
        ValidateId(id);
        lock (_gate)
        {
            CatalogDocument document = Read();
            int index = IndexOfTool(document, id);
            ToolDefinition previous = document.Tools[index];
            RequireEditable(previous);
            RequireRevision(previous.Revision, expectedRevision);
            if (document.Chains.Any(chain => chain.ToolIds.Contains(id, StringComparer.Ordinal)))
                throw new InvalidOperationException("Remove this tool from its chains before deleting it.");
            document.Tools.RemoveAt(index);
            Write(document);
        }
    }

    public IReadOnlyList<ToolChainDefinition> ListChains()
    {
        lock (_gate) return Array.AsReadOnly(Read().Chains.Select(Copy).ToArray());
    }

    public ToolChainDefinition LoadChain(string id)
    {
        ValidateId(id);
        lock (_gate) return Copy(FindChain(Read(), id));
    }

    public ToolChainDefinition CreateChain(string id, string name, IReadOnlyList<string> toolIds)
    {
        ValidateId(id);
        ValidateName(name);
        lock (_gate)
        {
            CatalogDocument document = Read();
            EnsureAvailable(document, id);
            if (document.Chains.Count >= MaxChains)
                throw new InvalidOperationException("Tool chain catalog is full.");
            ValidateSteps(document, toolIds);
            var chain = new ToolChainDefinition
            {
                Id = id,
                Name = name.Trim(),
                Revision = 1,
                ToolIds = toolIds.ToArray(),
            };
            document.Chains.Add(chain);
            Write(document);
            return Copy(chain);
        }
    }

    public ToolChainDefinition UpdateChain(string id, int expectedRevision, string name, IReadOnlyList<string> toolIds)
    {
        ValidateId(id);
        ValidateName(name);
        lock (_gate)
        {
            CatalogDocument document = Read();
            int index = IndexOfChain(document, id);
            ToolChainDefinition previous = document.Chains[index];
            RequireRevision(previous.Revision, expectedRevision);
            ValidateSteps(document, toolIds);
            ToolChainDefinition updated = previous with
            {
                Name = name.Trim(),
                ToolIds = toolIds.ToArray(),
                Revision = checked(previous.Revision + 1),
            };
            document.Chains[index] = updated;
            Write(document);
            return Copy(updated);
        }
    }

    public ToolChainDefinition RenameChain(string id, int expectedRevision, string name)
    {
        ValidateId(id);
        ValidateName(name);
        lock (_gate)
        {
            CatalogDocument document = Read();
            int index = IndexOfChain(document, id);
            ToolChainDefinition previous = document.Chains[index];
            RequireRevision(previous.Revision, expectedRevision);
            ToolChainDefinition updated = previous with
            {
                Name = name.Trim(),
                Revision = checked(previous.Revision + 1),
            };
            document.Chains[index] = updated;
            Write(document);
            return Copy(updated);
        }
    }

    public void DeleteChain(string id, int expectedRevision)
    {
        ValidateId(id);
        lock (_gate)
        {
            CatalogDocument document = Read();
            int index = IndexOfChain(document, id);
            RequireRevision(document.Chains[index].Revision, expectedRevision);
            document.Chains.RemoveAt(index);
            Write(document);
        }
    }

    private CatalogDocument Read()
    {
        EnsureDirectory();
        CatalogDocument document;
        if (!File.Exists(DocumentPath))
        {
            document = new CatalogDocument();
        }
        else
        {
            RejectLink(DocumentPath);
            var info = new FileInfo(DocumentPath);
            if (info.Length > MaxDocumentBytes)
                throw new InvalidDataException("Tool catalog exceeds the size limit.");
            using FileStream stream = File.OpenRead(DocumentPath);
            document = JsonSerializer.Deserialize<CatalogDocument>(stream, JsonOptions)
                ?? throw new InvalidDataException("Tool catalog is empty.");
            ValidateDocument(document);
        }

        if (document.Tools.Count == 0 && document.Chains.Count == 0)
        {
            document.Tools.AddRange(Presets());
            Write(document);
        }
        return document;
    }

    private void Write(CatalogDocument document)
    {
        ValidateDocument(document);
        EnsureDirectory();
        string stage = Path.Combine(RootDirectory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            if (new FileInfo(stage).Length > MaxDocumentBytes)
                throw new InvalidDataException("Tool catalog exceeds the size limit.");
            if (File.Exists(DocumentPath)) RejectLink(DocumentPath);
            File.Move(stage, DocumentPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(stage)) File.Delete(stage);
        }
    }

    private void EnsureDirectory()
    {
        Directory.CreateDirectory(RootDirectory);
        RejectLink(RootDirectory);
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Tool catalog path cannot be a symbolic link.");
    }

    private static string ResolveHome()
    {
        string? configured = Environment.GetEnvironmentVariable("AGAS_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
            throw new InvalidOperationException("Cannot resolve AGAS_HOME.");
        return Path.Combine(profile, ".agas");
    }

    private static ToolDefinition Copy(ToolDefinition tool) => tool with
    {
        Parameters = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(tool.Parameters, StringComparer.Ordinal)),
    };

    private static ToolChainDefinition Copy(ToolChainDefinition chain) => chain with
    {
        ToolIds = Array.AsReadOnly(chain.ToolIds.ToArray()),
    };

    private static ToolDefinition FindTool(CatalogDocument document, string id) =>
        document.Tools.FirstOrDefault(tool => tool.Id == id)
            ?? throw new KeyNotFoundException($"Tool '{id}' does not exist.");

    private static ToolChainDefinition FindChain(CatalogDocument document, string id) =>
        document.Chains.FirstOrDefault(chain => chain.Id == id)
            ?? throw new KeyNotFoundException($"Tool chain '{id}' does not exist.");

    private static int IndexOfTool(CatalogDocument document, string id)
    {
        int index = document.Tools.FindIndex(tool => tool.Id == id);
        if (index < 0) throw new KeyNotFoundException($"Tool '{id}' does not exist.");
        return index;
    }

    private static int IndexOfChain(CatalogDocument document, string id)
    {
        int index = document.Chains.FindIndex(chain => chain.Id == id);
        if (index < 0) throw new KeyNotFoundException($"Tool chain '{id}' does not exist.");
        return index;
    }

    private static void EnsureAvailable(CatalogDocument document, string id)
    {
        if (document.Tools.Any(tool => tool.Id == id) || document.Chains.Any(chain => chain.Id == id))
            throw new InvalidOperationException($"Tool or chain ID '{id}' already exists.");
    }

    private static void ValidateSteps(CatalogDocument document, IReadOnlyList<string> toolIds)
    {
        ArgumentNullException.ThrowIfNull(toolIds);
        if (toolIds.Count is < 2 or > MaxChainSteps)
            throw new ArgumentOutOfRangeException(nameof(toolIds), "A chain needs 2 to 32 tools.");
        foreach (string id in toolIds)
        {
            ValidateId(id);
            if (!document.Tools.Any(tool => tool.Id == id))
                throw new InvalidDataException($"Tool '{id}' does not exist; chains cannot contain chains.");
        }
    }

    private static void RequireEditable(ToolDefinition tool)
    {
        if (tool.IsReadOnly || tool.Kind != ToolImplementationKind.Gdscript)
            throw new InvalidOperationException("Built-in tools are read-only.");
    }

    private static void RequireRevision(int actual, int expected)
    {
        if (actual != expected)
            throw new InvalidOperationException($"Tool revision changed from {expected} to {actual}.");
    }

    private static void ValidateId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > 64 || id[0] is < 'a' or > 'z'
            || id.Skip(1).Any(ch => !(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
            throw new ArgumentException("Tool ID must be 1-64 lowercase ASCII letters, digits, '-' or '_', beginning with a letter.", nameof(id));
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Trim().Length > 120)
            throw new ArgumentOutOfRangeException(nameof(name), "Tool name exceeds 120 characters.");
    }

    private static void ValidateSource(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (source.Length > MaxSourceLength)
            throw new ArgumentOutOfRangeException(nameof(source), "Tool source exceeds 1 MiB.");
    }

    private static void ValidateDocument(CatalogDocument document)
    {
        if (document.SchemaVersion != SchemaVersion || document.Tools == null || document.Chains == null
            || document.Tools.Count > MaxTools || document.Chains.Count > MaxChains)
            throw new InvalidDataException("Invalid tool catalog document.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (ToolDefinition tool in document.Tools)
        {
            if (tool == null) throw new InvalidDataException("Tool catalog contains a null tool.");
            try
            {
                ValidateId(tool.Id);
                ValidateName(tool.Name);
                if (tool.Revision < 1 || !Enum.IsDefined(tool.Kind) || tool.Parameters == null)
                    throw new InvalidDataException("Tool definition is invalid.");
                if (tool.Kind == ToolImplementationKind.Gdscript)
                {
                    ValidateSource(tool.Source!);
                    if (tool.OperationId != null) throw new InvalidDataException("Script tool has an operation ID.");
                }
                else if (string.IsNullOrWhiteSpace(tool.OperationId) || tool.Source != null || !tool.IsReadOnly)
                    throw new InvalidDataException("Built-in tool definition is invalid.");
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("Tool definition is invalid.", exception);
            }
            if (!ids.Add(tool.Id)) throw new InvalidDataException("Duplicate tool ID.");
        }
        foreach (ToolChainDefinition chain in document.Chains)
        {
            if (chain == null) throw new InvalidDataException("Tool catalog contains a null chain.");
            try
            {
                ValidateId(chain.Id);
                ValidateName(chain.Name);
                if (chain.Revision < 1) throw new InvalidDataException("Invalid chain revision.");
                ValidateSteps(document, chain.ToolIds);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("Tool chain is invalid.", exception);
            }
            if (!ids.Add(chain.Id)) throw new InvalidDataException("Duplicate tool or chain ID.");
        }
    }

    private static IReadOnlyList<ToolDefinition> Presets() =>
    [
        BuiltIn("resize-256", "缩放至 256×256", "resize-256", new Dictionary<string, string> { ["width"] = "256", ["height"] = "256" }),
        BuiltIn("crop-128", "裁剪左上 128×128", "crop-128", new Dictionary<string, string> { ["x"] = "0", ["y"] = "0", ["width"] = "128", ["height"] = "128" }),
        BuiltIn("matting-edge-connected", "抠图（边缘连通）", "matting-edge-connected"),
        BuiltIn("matting-checkerboard", "抠图（棋盘格）", "matting-checkerboard"),
        BuiltIn("matting-light-single", "抠图", "matting-light-background"),
        BuiltIn("defringe-white", "去边缘杂色", "defringe-white"),
        Script("tile47-isometric", "Tile47 菱形俯视转换", ToolPresetSources.Tile47Isometric),
        Script("tile47-standard-reverse", "Tile47 标准图反推", ToolPresetSources.Tile47StandardReverse),
    ];

    private static ToolDefinition BuiltIn(string id, string name, string operationId,
        IReadOnlyDictionary<string, string>? parameters = null) => new()
    {
        Id = id,
        Name = name,
        Revision = 1,
        IsReadOnly = true,
        Kind = ToolImplementationKind.BuiltIn,
        OperationId = operationId,
        Parameters = parameters ?? new Dictionary<string, string>(),
    };

    private static ToolDefinition Script(string id, string name, string source) => new()
    {
        Id = id,
        Name = name,
        Revision = 1,
        Kind = ToolImplementationKind.Gdscript,
        Source = source,
    };

    private sealed class CatalogDocument
    {
        public int SchemaVersion { get; set; } = ToolCatalog.SchemaVersion;
        public List<ToolDefinition> Tools { get; set; } = [];
        public List<ToolChainDefinition> Chains { get; set; } = [];
    }
}
