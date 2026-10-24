#nullable enable

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AppAgasCs.Features.Tools.Model;

public enum ToolImplementationKind
{
    BuiltIn,
    Gdscript,
}

/// <summary>A single image-to-image operation. Input and output ports are fixed.</summary>
public sealed record ToolDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int Revision { get; init; } = 1;
    public bool IsReadOnly { get; init; }
    public ToolImplementationKind Kind { get; init; }
    public string? OperationId { get; init; }
    public string? Source { get; init; }
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    [JsonIgnore]
    public string InputPort => "image";

    [JsonIgnore]
    public string OutputPort => "image";
}
