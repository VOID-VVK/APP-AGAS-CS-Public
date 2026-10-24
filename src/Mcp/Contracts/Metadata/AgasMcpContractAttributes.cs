using System;
using System.Collections.Generic;
using System.Linq;

namespace AppAgasCs.Mcp.Contracts.Metadata;

internal static class McpContractMetadataSnapshot
{
    internal static IReadOnlyList<string> Freeze(string[] values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Any(value => value is null))
        {
            throw new ArgumentException("MCP contract metadata cannot contain null values.", parameterName);
        }
        return Array.AsReadOnly(values.ToArray());
    }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class McpClosedObjectAttribute : Attribute
{
    public bool IncludeSchemaKeyword { get; set; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class McpUseDefinitionAttribute : Attribute
{
    public McpUseDefinitionAttribute(string name) => Name = name;

    public string Name { get; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class McpConstAttribute : Attribute
{
    public McpConstAttribute(object value) => Value = value;

    public object Value { get; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class McpUniqueItemsAttribute : Attribute;

[AttributeUsage(AttributeTargets.Property)]
public sealed class McpItemsAllowedValuesAttribute : Attribute
{
    public McpItemsAllowedValuesAttribute(params string[] values) =>
        Values = McpContractMetadataSnapshot.Freeze(values, nameof(values));

    public IReadOnlyList<string> Values { get; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class McpWhenEqualsRequiredAttribute : Attribute
{
    public McpWhenEqualsRequiredAttribute(
        string discriminatorProperty,
        object expectedValue,
        params string[] requiredProperties)
    {
        DiscriminatorProperty = discriminatorProperty;
        ExpectedValue = expectedValue;
        RequiredProperties = McpContractMetadataSnapshot.Freeze(requiredProperties, nameof(requiredProperties));
    }

    public string DiscriminatorProperty { get; }
    public object ExpectedValue { get; }
    public IReadOnlyList<string> RequiredProperties { get; }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
public sealed class McpWhenEqualsAnyRequiredAttribute : Attribute
{
    public McpWhenEqualsAnyRequiredAttribute(
        string discriminatorProperty,
        object expectedValue,
        params string[] candidateProperties)
    {
        DiscriminatorProperty = discriminatorProperty;
        ExpectedValue = expectedValue;
        CandidateProperties = McpContractMetadataSnapshot.Freeze(candidateProperties, nameof(candidateProperties));
    }

    public string DiscriminatorProperty { get; }
    public object ExpectedValue { get; }
    public IReadOnlyList<string> CandidateProperties { get; }
}
