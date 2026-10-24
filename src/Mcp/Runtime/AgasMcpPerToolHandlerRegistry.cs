#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Mcp.Contracts;

namespace AppAgasCs.Mcp.Runtime;

public delegate ValueTask<AgasMcpToolResponse> AgasMcpPerToolHandler(
    IReadOnlyDictionary<string, JsonElement>? arguments,
    AgasMcpToolExecutionContext context,
    CancellationToken cancellationToken);

public sealed class AgasMcpToolExecutionContext
{
    private readonly Func<Func<CancellationToken, AgasMcpToolResponse>, CancellationToken, Task<AgasMcpToolResponse>> _invokeMainThread;
    private readonly Func<Func<CancellationToken, object>, CancellationToken, Task<object>> _captureMainThread;
    private Func<object?>? _failureDataFactory;

    public AgasMcpToolExecutionContext(
        string toolName,
        AgasMcpCommitExecution commitExecution,
        Func<Func<CancellationToken, AgasMcpToolResponse>, CancellationToken, Task<AgasMcpToolResponse>>? invokeMainThread = null,
        Func<Func<CancellationToken, object>, CancellationToken, Task<object>>? captureMainThread = null)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            throw new ArgumentException("A tool name is required.", nameof(toolName));

        ToolName = toolName;
        CommitExecution = commitExecution ?? throw new ArgumentNullException(nameof(commitExecution));
        _invokeMainThread = invokeMainThread ?? ((callback, token) => Task.FromResult(callback(token)));
        _captureMainThread = captureMainThread ?? ((callback, token) => Task.FromResult(callback(token)));
    }

    public string ToolName { get; }
    public AgasMcpCommitExecution CommitExecution { get; }

    public ValueTask<AgasMcpToolResponse> InvokeMainThreadAsync(
        Func<CancellationToken, AgasMcpToolResponse> callback,
        CancellationToken cancellationToken) =>
        new(_invokeMainThread(callback, cancellationToken));

    public async ValueTask<T> CaptureMainThreadAsync<T>(
        Func<CancellationToken, T> callback,
        CancellationToken cancellationToken)
    {
        object captured = await _captureMainThread(token => callback(token)!, cancellationToken).ConfigureAwait(false);
        return (T)captured;
    }

    public void SetFailureDataFactory(Func<object?> failureDataFactory) =>
        _failureDataFactory = failureDataFactory ?? throw new ArgumentNullException(nameof(failureDataFactory));

    public object? CreateFailureData() => _failureDataFactory?.Invoke();
}

/// <summary>
/// Per-tool composition table. Descriptor names are supplied explicitly so Wave 3 can bind
/// the central descriptor registry without this foundation depending on or modifying it.
/// </summary>
public sealed class AgasMcpPerToolHandlerRegistry
{
    private readonly object _gate = new();
    private readonly IReadOnlyList<string> _descriptorNames;
    private readonly HashSet<string> _knownNames;
    private readonly Dictionary<string, AgasMcpPerToolHandler> _handlers = new(StringComparer.Ordinal);
    private bool _frozen;

    public AgasMcpPerToolHandlerRegistry(IEnumerable<string> descriptorNames)
    {
        ArgumentNullException.ThrowIfNull(descriptorNames);

        string[] names = descriptorNames.ToArray();
        if (names.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Descriptor names cannot be null, empty, or whitespace.", nameof(descriptorNames));

        _knownNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (!_knownNames.Add(name))
                throw new ArgumentException($"Duplicate MCP descriptor '{name}'.", nameof(descriptorNames));
        }

        _descriptorNames = Array.AsReadOnly(names);
    }

    public IReadOnlyList<string> DescriptorNames => _descriptorNames;

    public IReadOnlyDictionary<string, AgasMcpPerToolHandler> FreezeAndValidateComplete()
    {
        lock (_gate)
        {
            string[] missing = _descriptorNames.Where(name => !_handlers.ContainsKey(name)).ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    $"MCP handlers are missing for: {string.Join(", ", missing)}.");
            }

            _frozen = true;
            return new ReadOnlyDictionary<string, AgasMcpPerToolHandler>(
                new Dictionary<string, AgasMcpPerToolHandler>(_handlers, StringComparer.Ordinal));
        }
    }

    public void Register(string toolName, AgasMcpPerToolHandler handler)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(handler);

        lock (_gate)
        {
            if (_frozen)
                throw new InvalidOperationException("The MCP per-tool handler registry is frozen.");
            if (!_knownNames.Contains(toolName))
                throw new KeyNotFoundException($"Unknown AGAS MCP tool '{toolName}'.");
            if (!_handlers.TryAdd(toolName, handler))
                throw new InvalidOperationException($"MCP handler '{toolName}' is already registered.");
        }
    }

    public AgasMcpPerToolHandler GetRequired(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);

        lock (_gate)
        {
            if (!_knownNames.Contains(toolName))
                throw new KeyNotFoundException($"Unknown AGAS MCP tool '{toolName}'.");
            if (!_handlers.TryGetValue(toolName, out AgasMcpPerToolHandler? handler))
                throw new InvalidOperationException($"AGAS MCP tool '{toolName}' has no registered handler.");
            return handler;
        }
    }
}
