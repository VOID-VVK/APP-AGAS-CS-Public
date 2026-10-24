using System;
using System.Collections.Generic;
using System.Linq;

namespace AppAgasCs.Mcp.Contracts.Output;

public sealed record AgasMcpFrozenOutputField(
    string Name,
    string Type,
    bool Required = true,
    string? Constraint = null);

public sealed record AgasMcpFrozenOutputShape
{
    public AgasMcpFrozenOutputShape(
        string name,
        IEnumerable<AgasMcpFrozenOutputField> fields,
        string? constraint = null,
        IEnumerable<string>? unionVariants = null)
    {
        AgasMcpFrozenOutputField[] frozenFields = fields.ToArray();
        string[] frozenUnionVariants = (unionVariants ?? Array.Empty<string>()).ToArray();
        if (frozenFields.Any(field => field.Name is "oneOf" or "variant"))
            throw new ArgumentException("Union metadata cannot be represented as an ordinary wire field.", nameof(fields));
        if (frozenUnionVariants.Length > 0 && frozenFields.Length > 0)
            throw new ArgumentException("A frozen union cannot also declare ordinary wire fields.", nameof(fields));
        if (frozenUnionVariants.Any(string.IsNullOrWhiteSpace)
            || frozenUnionVariants.Distinct(StringComparer.Ordinal).Count() != frozenUnionVariants.Length)
            throw new ArgumentException("Union variants must be non-empty and unique.", nameof(unionVariants));

        Name = name;
        Fields = Array.AsReadOnly(frozenFields);
        UnionVariants = Array.AsReadOnly(frozenUnionVariants);
        Constraint = constraint;
        Closed = true;
    }

    public string Name { get; init; }
    public IReadOnlyList<AgasMcpFrozenOutputField> Fields { get; }
    public IReadOnlyList<string> UnionVariants { get; }
    public string? Constraint { get; init; }
    public bool Closed { get; init; }
}

public sealed record AgasMcpFrozenOutputTool
{
    public AgasMcpFrozenOutputTool(
        string toolName,
        IEnumerable<AgasMcpFrozenOutputShape> successVariants,
        IEnumerable<AgasMcpFrozenOutputShape>? failureDataVariants = null,
        bool allowsFailureWithoutData = true,
        string? constraint = null)
    {
        ToolName = toolName;
        SuccessVariants = Array.AsReadOnly(successVariants.ToArray());
        FailureDataVariants = Array.AsReadOnly(
            (failureDataVariants ?? Array.Empty<AgasMcpFrozenOutputShape>()).ToArray());
        AllowsFailureWithoutData = allowsFailureWithoutData;
        Constraint = constraint;
    }

    public string ToolName { get; }
    public IReadOnlyList<AgasMcpFrozenOutputShape> SuccessVariants { get; }
    public IReadOnlyList<AgasMcpFrozenOutputShape> FailureDataVariants { get; }
    public bool AllowsFailureWithoutData { get; }
    public string? Constraint { get; }
}

public sealed record AgasMcpFrozenOutputManifestVersion
{
    public AgasMcpFrozenOutputManifestVersion(
        string version,
        IEnumerable<AgasMcpFrozenOutputShape> primitives,
        IEnumerable<AgasMcpFrozenOutputShape> sharedShapes,
        IEnumerable<AgasMcpFrozenOutputTool> tools)
    {
        Version = version;
        Primitives = Array.AsReadOnly(primitives.ToArray());
        SharedShapes = Array.AsReadOnly(sharedShapes.ToArray());
        Tools = Array.AsReadOnly(tools.ToArray());
    }

    public string Version { get; }
    public IReadOnlyList<AgasMcpFrozenOutputShape> Primitives { get; }
    public IReadOnlyList<AgasMcpFrozenOutputShape> SharedShapes { get; }
    public IReadOnlyList<AgasMcpFrozenOutputTool> Tools { get; }
}
