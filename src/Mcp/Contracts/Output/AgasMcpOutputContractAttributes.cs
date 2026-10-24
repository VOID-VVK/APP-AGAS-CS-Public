using System;
using System.Collections.Generic;
using System.Linq;

namespace AppAgasCs.Mcp.Contracts.Output;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class AgasMcpOutputObjectAttribute : Attribute;

/// <summary>
/// Declares a closed wire union. The annotated base type has no wire fields of its own;
/// every runtime value must be exactly one of the registered, closed output-object variants.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface)]
public sealed class AgasMcpOutputUnionAttribute : Attribute
{
    public AgasMcpOutputUnionAttribute(params Type[] variants)
    {
        ArgumentNullException.ThrowIfNull(variants);
        if (variants.Length < 2
            || variants.Any(type => type == null)
            || variants.Distinct().Count() != variants.Length)
        {
            throw new ArgumentException("An output union requires at least two unique variants.", nameof(variants));
        }

        Variants = Array.AsReadOnly((Type[])variants.Clone());
    }

    public IReadOnlyList<Type> Variants { get; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class AgasMcpOutputOptionalAttribute : Attribute;

[AttributeUsage(AttributeTargets.Property)]
public sealed class AgasMcpOutputStringAttribute : Attribute
{
    public int MinimumLength { get; init; } = -1;
    public int MaximumLength { get; init; } = -1;
    public string? Pattern { get; init; }
    public string? Format { get; init; }
}

/// <summary>
/// Marks a wire-level AI provider identifier. Provider identifiers intentionally allow dots,
/// unlike the more restrictive general-purpose safe identifier contract.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AgasMcpOutputProviderIdAttribute : Attribute;

[AttributeUsage(AttributeTargets.Property)]
public sealed class AgasMcpOutputNumberAttribute : Attribute
{
    public double Minimum { get; init; } = double.NaN;
    public double Maximum { get; init; } = double.NaN;
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class AgasMcpOutputAllowedValuesAttribute : Attribute
{
    public AgasMcpOutputAllowedValuesAttribute(params string[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0
            || values.Any(string.IsNullOrEmpty)
            || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw new ArgumentException("Output string enum values must be non-empty and unique.", nameof(values));
        }
        Values = Array.AsReadOnly((string[])values.Clone());
    }

    public IReadOnlyList<string> Values { get; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class AgasMcpOutputIntegerEnumAttribute : Attribute
{
    public AgasMcpOutputIntegerEnumAttribute(params int[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0 || values.Distinct().Count() != values.Length)
        {
            throw new ArgumentException("Output integer enum values must be non-empty and unique.", nameof(values));
        }
        Values = Array.AsReadOnly((int[])values.Clone());
    }

    public IReadOnlyList<int> Values { get; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class AgasMcpOutputConstAttribute : Attribute
{
    public AgasMcpOutputConstAttribute(object value) =>
        Value = value ?? throw new ArgumentNullException(nameof(value));

    public object Value { get; }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class AgasMcpOutputWhenBooleanAttribute : Attribute
{
    public AgasMcpOutputWhenBooleanAttribute(
        string conditionProperty,
        bool conditionValue,
        string[] requiredProperties,
        string[] forbiddenProperties)
    {
        if (string.IsNullOrWhiteSpace(conditionProperty))
            throw new ArgumentException("A boolean condition property is required.", nameof(conditionProperty));
        ConditionProperty = conditionProperty;
        ConditionValue = conditionValue;
        RequiredProperties = Array.AsReadOnly((requiredProperties ?? Array.Empty<string>()).ToArray());
        ForbiddenProperties = Array.AsReadOnly((forbiddenProperties ?? Array.Empty<string>()).ToArray());
    }

    public string ConditionProperty { get; }
    public bool ConditionValue { get; }
    public IReadOnlyList<string> RequiredProperties { get; }
    public IReadOnlyList<string> ForbiddenProperties { get; }
}
