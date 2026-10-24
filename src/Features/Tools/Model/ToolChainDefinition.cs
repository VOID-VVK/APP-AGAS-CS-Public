#nullable enable

using System.Collections.Generic;

namespace AppAgasCs.Features.Tools.Model;

/// <summary>An ordered sequence of single-image tools, not a graph or a nested chain.</summary>
public sealed record ToolChainDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int Revision { get; init; } = 1;
    public IReadOnlyList<string> ToolIds { get; init; } = [];
}
