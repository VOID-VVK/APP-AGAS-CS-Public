#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace AppAgasCs.Application;

/// <summary>
/// Aggregates dirty signals from independent contributors (local UI edits and MCP)
/// so presentation code does not hard-code every source.
/// Supports Law of Demeter: callers ask "is anything dirty?" without knowing each origin.
/// </summary>
public sealed class WorkspaceDirtyAggregator
{
    private bool _localDirty;
    private readonly List<(string Name, Func<bool> IsDirty)> _contributors = new();

    public bool IsLocalDirty => _localDirty;

    public void MarkLocalDirty() => _localDirty = true;

    public void ClearLocalDirty() => _localDirty = false;

    /// <summary>
    /// Registers or replaces a named external dirty contributor (e.g. "mcp").
    /// Contributors are evaluated lazily; they must be cheap and side-effect free.
    /// </summary>
    public void RegisterContributor(string name, Func<bool> isDirty)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(isDirty);
        _contributors.RemoveAll(item => string.Equals(item.Name, name, StringComparison.Ordinal));
        _contributors.Add((name, isDirty));
    }

    public void UnregisterContributor(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _contributors.RemoveAll(item => string.Equals(item.Name, name, StringComparison.Ordinal));
    }

    public bool IsDirty =>
        _localDirty || _contributors.Any(item => item.IsDirty());

    /// <summary>Clears local dirty only; external contributors keep their own state.</summary>
    public void ResetLocal() => _localDirty = false;
}
