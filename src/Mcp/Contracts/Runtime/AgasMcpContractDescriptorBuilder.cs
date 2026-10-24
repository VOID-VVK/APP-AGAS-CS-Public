using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AppAgasCs.Mcp.Contracts.Metadata;

namespace AppAgasCs.Mcp.Contracts.Runtime;

internal static class AgasMcpContractDescriptorBuilder
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex DefinitionNamePattern = new(
        "^[A-Za-z_][A-Za-z0-9_.-]*$",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    public static AgasMcpContractDescriptor Build(Type inputType) =>
        Build(inputType, new HashSet<Type>());

    private static AgasMcpContractDescriptor Build(Type inputType, HashSet<Type> activeTypes)
    {
        McpClosedObjectAttribute closed = inputType.GetCustomAttribute<McpClosedObjectAttribute>()
            ?? throw ContractError(inputType, "must be marked with McpClosedObjectAttribute");
        if (!inputType.IsClass && !inputType.IsValueType)
            throw ContractError(inputType, "must be a concrete DTO type");
        if (inputType.IsAbstract || inputType.IsInterface || inputType.ContainsGenericParameters)
            throw ContractError(inputType, "must be a concrete, closed DTO type");
        RejectSerializationOverrides(inputType, inputType.GetCustomAttributesData(), propertyName: null);
        if (!activeTypes.Add(inputType))
            throw ContractError(inputType, "contains a recursive DTO reference");

        try
        {
            PropertyInfo[] propertyInfos = inputType
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .OrderBy(property => property.MetadataToken)
                .ToArray();
            if (propertyInfos.Any(property => property.GetIndexParameters().Length != 0))
                throw ContractError(inputType, "contains an indexer, which is not supported");

            AgasMcpPropertyDescriptor[] properties = propertyInfos
                .Select(property => BuildProperty(inputType, property, activeTypes))
                .ToArray();
            if (properties.Select(item => item.WireName).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                throw ContractError(inputType, "contains duplicate JSON property names");

            IReadOnlyDictionary<string, AgasMcpPropertyDescriptor> byClrName =
                properties.ToDictionary(item => item.ClrName, StringComparer.Ordinal);
            IReadOnlyList<AgasMcpConditionDescriptor> conditions =
                BuildConditions(inputType, byClrName);

            return new AgasMcpContractDescriptor(
                inputType,
                closed.IncludeSchemaKeyword,
                inputType.GetCustomAttribute<DescriptionAttribute>()?.Description,
                properties,
                conditions);
        }
        finally
        {
            activeTypes.Remove(inputType);
        }
    }

    private static AgasMcpPropertyDescriptor BuildProperty(
        Type inputType,
        PropertyInfo property,
        HashSet<Type> activeTypes)
    {
        if (property.GetMethod == null || !property.GetMethod.IsPublic)
            throw PropertyError(inputType, property.Name, "must have a public getter");
        if (property.SetMethod == null || !property.SetMethod.IsPublic)
            throw PropertyError(inputType, property.Name, "must have a public setter or init accessor");
        RejectSerializationOverrides(inputType, property.GetCustomAttributesData(), property.Name);

        Type effectiveType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        AgasMcpValueType valueType = GetValueType(inputType, property.Name, effectiveType);
        bool isArray = valueType == AgasMcpValueType.Array;
        Type? itemType = isArray ? GetListElementType(effectiveType) : null;
        if (itemType != null && GetListElementType(Nullable.GetUnderlyingType(itemType) ?? itemType) != null)
            throw PropertyError(inputType, property.Name, "cannot contain nested collections");

        ValidateAttributeApplicability(inputType, property, valueType, itemType);
        StringLengthAttribute? stringLength = property.GetCustomAttribute<StringLengthAttribute>();
        MinLengthAttribute? minLength = property.GetCustomAttribute<MinLengthAttribute>();
        MaxLengthAttribute? maxLength = property.GetCustomAttribute<MaxLengthAttribute>();
        RangeAttribute? range = property.GetCustomAttribute<RangeAttribute>();
        DefaultValueAttribute? defaultValue = property.GetCustomAttribute<DefaultValueAttribute>();
        AllowedValuesAttribute? allowedValues = property.GetCustomAttribute<AllowedValuesAttribute>();
        McpConstAttribute? constValue = property.GetCustomAttribute<McpConstAttribute>();
        RegularExpressionAttribute? pattern = property.GetCustomAttribute<RegularExpressionAttribute>();
        McpUseDefinitionAttribute? definition = property.GetCustomAttribute<McpUseDefinitionAttribute>();

        if (definition != null && !DefinitionNamePattern.IsMatch(definition.Name ?? string.Empty))
            throw PropertyError(inputType, property.Name, "has an invalid MCP definition name");

        AgasMcpContractDescriptor? objectDescriptor = valueType == AgasMcpValueType.Object
            ? Build(effectiveType, activeTypes)
            : null;
        AgasMcpPropertyDescriptor? itemDescriptor = itemType == null
            ? null
            : BuildItemDescriptor(inputType, property.Name, itemType, activeTypes);

        double? minimum = null;
        double? maximum = null;
        if (range != null)
        {
            try
            {
                minimum = Convert.ToDouble(range.Minimum, CultureInfo.InvariantCulture);
                maximum = Convert.ToDouble(range.Maximum, CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
            {
                throw PropertyError(inputType, property.Name, "has an invalid RangeAttribute");
            }
            if (!double.IsFinite(minimum.Value) || !double.IsFinite(maximum.Value) || minimum > maximum)
                throw PropertyError(inputType, property.Name, "has an invalid RangeAttribute");
        }

        object? normalizedDefault = defaultValue == null
            ? null
            : NormalizeDefault(inputType, property, effectiveType, itemType, defaultValue.Value);
        IReadOnlyList<object?> normalizedAllowed = allowedValues?.Values
            .Select(value => NormalizeLiteral(inputType, property.Name, effectiveType, value, "AllowedValuesAttribute"))
            .ToArray()
            ?? EnumValues(effectiveType);
        object? normalizedConst = constValue == null
            ? null
            : NormalizeLiteral(inputType, property.Name, effectiveType, constValue.Value, "McpConstAttribute");

        var descriptor = new AgasMcpPropertyDescriptor
        {
            ClrName = property.Name,
            WireName = GetWireName(inputType, property),
            ValueType = valueType,
            Required = property.IsDefined(typeof(RequiredAttribute)),
            Description = property.GetCustomAttribute<DescriptionAttribute>()?.Description,
            DefaultValue = normalizedDefault,
            HasDefaultValue = defaultValue != null,
            AllowedValues = normalizedAllowed,
            ConstValue = normalizedConst,
            HasConstValue = constValue != null,
            Pattern = pattern == null
                ? null
                : BuildPattern(inputType, property.Name, pattern.Pattern),
            MinimumLength = !isArray ? stringLength?.MinimumLength ?? minLength?.Length : null,
            MaximumLength = !isArray
                ? stringLength?.MaximumLength ?? maxLength?.Length
                : null,
            Minimum = minimum,
            Maximum = maximum,
            MinimumItems = isArray ? minLength?.Length : null,
            MaximumItems = isArray ? maxLength?.Length : null,
            UniqueItems = property.IsDefined(typeof(McpUniqueItemsAttribute)),
            ItemDescriptor = itemDescriptor,
            ItemAllowedValues = property.GetCustomAttribute<McpItemsAllowedValuesAttribute>()?.Values
                .Cast<object?>()
                .ToArray() ?? Array.Empty<object?>(),
            ObjectDescriptor = objectDescriptor,
            DefinitionName = definition?.Name,
        };

        ValidatePropertyMetadata(inputType, property.Name, descriptor);
        return descriptor;
    }

    private static AgasMcpPropertyDescriptor BuildItemDescriptor(
        Type inputType,
        string propertyName,
        Type itemType,
        HashSet<Type> activeTypes)
    {
        Type effectiveType = Nullable.GetUnderlyingType(itemType) ?? itemType;
        AgasMcpValueType valueType = GetValueType(inputType, propertyName, effectiveType);
        if (valueType == AgasMcpValueType.Array)
            throw PropertyError(inputType, propertyName, "cannot contain nested collections");
        return new AgasMcpPropertyDescriptor
        {
            ClrName = "$item",
            WireName = "$item",
            ValueType = valueType,
            AllowedValues = EnumValues(effectiveType),
            ObjectDescriptor = valueType == AgasMcpValueType.Object
                ? Build(effectiveType, activeTypes)
                : null,
        };
    }

    private static IReadOnlyList<AgasMcpConditionDescriptor> BuildConditions(
        Type inputType,
        IReadOnlyDictionary<string, AgasMcpPropertyDescriptor> properties)
    {
        var order = new List<string>();
        var grouped = new Dictionary<string, ConditionAccumulator>(StringComparer.Ordinal);

        foreach (McpWhenEqualsRequiredAttribute attribute in
                 inputType.GetCustomAttributes<McpWhenEqualsRequiredAttribute>())
        {
            if (attribute.RequiredProperties.Count == 0)
                throw ContractError(inputType, "contains a condition with no required properties");
            AddCondition(
                inputType,
                properties,
                order,
                grouped,
                attribute.DiscriminatorProperty,
                attribute.ExpectedValue,
                attribute.RequiredProperties,
                Array.Empty<string>());
        }
        foreach (McpWhenEqualsAnyRequiredAttribute attribute in
                 inputType.GetCustomAttributes<McpWhenEqualsAnyRequiredAttribute>())
        {
            if (attribute.CandidateProperties.Count == 0)
                throw ContractError(inputType, "contains a condition with no candidate properties");
            AddCondition(
                inputType,
                properties,
                order,
                grouped,
                attribute.DiscriminatorProperty,
                attribute.ExpectedValue,
                Array.Empty<string>(),
                attribute.CandidateProperties);
        }

        return order.Select(key => grouped[key].ToDescriptor()).ToArray();
    }

    private static void AddCondition(
        Type inputType,
        IReadOnlyDictionary<string, AgasMcpPropertyDescriptor> properties,
        List<string> order,
        Dictionary<string, ConditionAccumulator> grouped,
        string discriminatorName,
        object expectedValue,
        IReadOnlyList<string> requiredNames,
        IReadOnlyList<string> anyNames)
    {
        AgasMcpPropertyDescriptor discriminator = GetProperty(properties, inputType, discriminatorName);
        if (discriminator.ValueType is AgasMcpValueType.Array or AgasMcpValueType.Object)
            throw ContractError(inputType, $"uses non-scalar condition discriminator '{discriminatorName}'");
        object? normalizedExpected = NormalizeConditionLiteral(inputType, discriminator, expectedValue);
        ValidateLiteralAgainstDescriptor(inputType, discriminator.ClrName, discriminator, normalizedExpected, "condition expected value");
        string key = discriminator.ClrName + "\0" + JsonSerializer.Serialize(normalizedExpected);
        if (!grouped.TryGetValue(key, out ConditionAccumulator? accumulator))
        {
            accumulator = new ConditionAccumulator(discriminator, normalizedExpected!);
            grouped.Add(key, accumulator);
            order.Add(key);
        }
        foreach (string name in requiredNames)
            accumulator.AddRequired(GetProperty(properties, inputType, name));
        foreach (string name in anyNames)
            accumulator.AddAny(GetProperty(properties, inputType, name));
    }

    private static void ValidateAttributeApplicability(
        Type inputType,
        PropertyInfo property,
        AgasMcpValueType valueType,
        Type? itemType)
    {
        bool isString = valueType == AgasMcpValueType.String;
        bool isArray = valueType == AgasMcpValueType.Array;
        bool isNumber = valueType is AgasMcpValueType.Integer or AgasMcpValueType.Number;
        if (property.IsDefined(typeof(StringLengthAttribute)) && !isString)
            throw PropertyError(inputType, property.Name, "uses StringLengthAttribute on a non-string property");
        if ((property.IsDefined(typeof(MinLengthAttribute)) || property.IsDefined(typeof(MaxLengthAttribute)))
            && !isString && !isArray)
            throw PropertyError(inputType, property.Name, "uses a length attribute on an unsupported property");
        if (property.IsDefined(typeof(RegularExpressionAttribute)) && !isString)
            throw PropertyError(inputType, property.Name, "uses RegularExpressionAttribute on a non-string property");
        if (property.IsDefined(typeof(RangeAttribute)) && !isNumber)
            throw PropertyError(inputType, property.Name, "uses RangeAttribute on a non-numeric property");
        if (property.IsDefined(typeof(McpUniqueItemsAttribute)) && !isArray)
            throw PropertyError(inputType, property.Name, "uses McpUniqueItemsAttribute on a non-array property");
        if (property.IsDefined(typeof(McpItemsAllowedValuesAttribute)) && !isArray)
            throw PropertyError(inputType, property.Name, "uses McpItemsAllowedValuesAttribute on a non-array property");
        if (property.IsDefined(typeof(McpItemsAllowedValuesAttribute))
            && itemType != typeof(string)
            && !(Nullable.GetUnderlyingType(itemType!) ?? itemType!).IsEnum)
            throw PropertyError(inputType, property.Name, "uses string item values on a non-string array");
        if (property.IsDefined(typeof(McpUseDefinitionAttribute)) && valueType != AgasMcpValueType.Object)
            throw PropertyError(inputType, property.Name, "uses McpUseDefinitionAttribute on a non-DTO property");

        foreach (ValidationAttribute validation in property.GetCustomAttributes<ValidationAttribute>())
        {
            if (validation is not RequiredAttribute
                and not StringLengthAttribute
                and not MinLengthAttribute
                and not MaxLengthAttribute
                and not RangeAttribute
                and not RegularExpressionAttribute
                and not AllowedValuesAttribute)
            {
                throw PropertyError(inputType, property.Name, $"uses unsupported validation attribute '{validation.GetType().Name}'");
            }
        }
    }

    private static void ValidatePropertyMetadata(
        Type inputType,
        string propertyName,
        AgasMcpPropertyDescriptor descriptor)
    {
        if (descriptor.MinimumLength is < 0 || descriptor.MaximumLength is < 0
            || descriptor.MinimumItems is < 0 || descriptor.MaximumItems is < 0
            || descriptor.MinimumLength > descriptor.MaximumLength
            || descriptor.MinimumItems > descriptor.MaximumItems)
            throw PropertyError(inputType, propertyName, "has invalid length bounds");

        if (descriptor.HasDefaultValue)
            ValidateLiteralAgainstDescriptor(inputType, propertyName, descriptor, descriptor.DefaultValue, "default value");
        if (descriptor.HasConstValue)
            ValidateLiteralAgainstDescriptor(inputType, propertyName, descriptor, descriptor.ConstValue, "const value");
        foreach (object? allowed in descriptor.AllowedValues)
            ValidateLiteralAgainstDescriptor(inputType, propertyName, descriptor, allowed, "allowed value");
        if (descriptor.ItemAllowedValues.Count > 0)
        {
            foreach (object? allowed in descriptor.ItemAllowedValues)
                ValidateLiteralAgainstDescriptor(inputType, propertyName, descriptor.ItemDescriptor!, allowed, "allowed item value");
        }
    }

    private static void ValidateLiteralAgainstDescriptor(
        Type inputType,
        string propertyName,
        AgasMcpPropertyDescriptor descriptor,
        object? value,
        string label)
    {
        JsonElement element;
        try
        {
            element = JsonSerializer.SerializeToElement(value);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            throw PropertyError(inputType, propertyName, $"has an invalid {label}");
        }
        if (!descriptor.Validate(element, "$", out _))
            throw PropertyError(inputType, propertyName, $"has an invalid {label}");
    }

    private static object? NormalizeDefault(
        Type inputType,
        PropertyInfo property,
        Type effectiveType,
        Type? itemType,
        object? value)
    {
        if (value == null)
            throw PropertyError(inputType, property.Name, "has a null default value");
        if (effectiveType == typeof(bool) && value is bool boolean) return boolean;
        if (effectiveType == typeof(int) && value is int integer) return integer;
        if (effectiveType == typeof(string) && value is string text) return text;
        if (itemType == typeof(string) && value is string[] strings) return strings.ToArray();
        throw PropertyError(
            inputType,
            property.Name,
            "has an unsupported default; only bool, int, string, and string[] defaults are supported");
    }

    private static object? NormalizeLiteral(
        Type inputType,
        string propertyName,
        Type effectiveType,
        object? value,
        string attributeName)
    {
        if (value == null)
            throw PropertyError(inputType, propertyName, $"uses a null value in {attributeName}");
        if (!effectiveType.IsEnum) return value;
        string name = value is string stringValue
            ? stringValue
            : value.GetType() == effectiveType
                ? Enum.GetName(effectiveType, value) ?? string.Empty
                : string.Empty;
        string normalized = JsonNamingPolicy.CamelCase.ConvertName(name);
        if (!EnumValues(effectiveType).Contains(normalized))
            throw PropertyError(inputType, propertyName, $"has an invalid enum value in {attributeName}");
        return normalized;
    }

    private static object? NormalizeConditionLiteral(
        Type inputType,
        AgasMcpPropertyDescriptor discriminator,
        object value)
    {
        if (value == null)
            throw ContractError(inputType, $"has a null condition value for '{discriminator.ClrName}'");
        return value;
    }

    private static Regex BuildPattern(Type inputType, string propertyName, string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException)
        {
            throw PropertyError(inputType, propertyName, "has an invalid regular expression");
        }
    }

    private static string GetWireName(Type inputType, PropertyInfo property)
    {
        string name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
            ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);
        if (string.IsNullOrWhiteSpace(name))
            throw PropertyError(inputType, property.Name, "has an empty JSON property name");
        return name;
    }

    private static AgasMcpValueType GetValueType(Type inputType, string propertyName, Type type)
    {
        if (type == typeof(string) || type.IsEnum) return AgasMcpValueType.String;
        if (type == typeof(bool)) return AgasMcpValueType.Boolean;
        if (type == typeof(int)) return AgasMcpValueType.Integer;
        if (type == typeof(double)) return AgasMcpValueType.Number;
        if (IsDictionary(type))
            throw PropertyError(inputType, propertyName, "uses a dictionary type, which is not supported");
        if (GetListElementType(type) != null) return AgasMcpValueType.Array;
        if (IsNumeric(type))
            throw PropertyError(inputType, propertyName, $"uses unsupported numeric type '{type.Name}'");
        if (type.IsDefined(typeof(McpClosedObjectAttribute))) return AgasMcpValueType.Object;
        throw PropertyError(inputType, propertyName, $"uses unsupported type '{type.FullName}'");
    }

    private static Type? GetListElementType(Type type)
    {
        if (type.IsArray && type.GetArrayRank() == 1) return type.GetElementType();
        if (!type.IsGenericType) return null;
        Type definition = type.GetGenericTypeDefinition();
        return definition == typeof(List<>) || definition == typeof(IReadOnlyList<>)
            ? type.GetGenericArguments()[0]
            : null;
    }

    private static bool IsDictionary(Type type) =>
        type.IsGenericType
        && (type.GetGenericTypeDefinition() == typeof(Dictionary<,>)
            || type.GetGenericTypeDefinition() == typeof(IDictionary<,>)
            || type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));

    private static bool IsNumeric(Type type) => Type.GetTypeCode(type) is
        TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16
        or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64
        or TypeCode.Single or TypeCode.Decimal;

    private static IReadOnlyList<object?> EnumValues(Type type) => type.IsEnum
        ? Enum.GetNames(type)
            .Select(JsonNamingPolicy.CamelCase.ConvertName)
            .Cast<object?>()
            .ToArray()
        : Array.Empty<object?>();

    private static AgasMcpPropertyDescriptor GetProperty(
        IReadOnlyDictionary<string, AgasMcpPropertyDescriptor> properties,
        Type inputType,
        string clrName) =>
        !string.IsNullOrWhiteSpace(clrName)
        && properties.TryGetValue(clrName, out AgasMcpPropertyDescriptor? property)
            ? property
            : throw ContractError(inputType, $"contains a condition referencing unknown property '{clrName}'");

    private static void RejectSerializationOverrides(
        Type inputType,
        IEnumerable<CustomAttributeData> attributes,
        string? propertyName)
    {
        foreach (CustomAttributeData attribute in attributes)
        {
            Type type = attribute.AttributeType;
            if (type == typeof(JsonConverterAttribute)
                || type == typeof(JsonIgnoreAttribute)
                || type == typeof(JsonExtensionDataAttribute)
                || type == typeof(JsonIncludeAttribute)
                || type == typeof(JsonNumberHandlingAttribute)
                || type == typeof(JsonObjectCreationHandlingAttribute))
            {
                if (propertyName == null)
                    throw ContractError(inputType, $"uses unsupported JSON attribute '{type.Name}'");
                throw PropertyError(inputType, propertyName, $"uses unsupported JSON attribute '{type.Name}'");
            }
        }
    }

    private static InvalidOperationException ContractError(Type inputType, string reason) =>
        new($"MCP contract '{inputType.FullName}' {reason}.");

    private static InvalidOperationException PropertyError(Type inputType, string propertyName, string reason) =>
        new($"MCP contract '{inputType.FullName}' property '{propertyName}' {reason}.");

    private sealed class ConditionAccumulator
    {
        private readonly List<AgasMcpPropertyDescriptor> _required = new();
        private readonly List<AgasMcpPropertyDescriptor> _any = new();

        public ConditionAccumulator(AgasMcpPropertyDescriptor discriminator, object expectedValue)
        {
            Discriminator = discriminator;
            ExpectedValue = expectedValue;
        }

        public AgasMcpPropertyDescriptor Discriminator { get; }
        public object ExpectedValue { get; }

        public void AddRequired(AgasMcpPropertyDescriptor property)
        {
            if (!_required.Contains(property)) _required.Add(property);
        }

        public void AddAny(AgasMcpPropertyDescriptor property)
        {
            if (!_any.Contains(property)) _any.Add(property);
        }

        public AgasMcpConditionDescriptor ToDescriptor() => new()
        {
            Discriminator = Discriminator,
            ExpectedValue = ExpectedValue,
            RequiredProperties = _required,
            AnyRequiredProperties = _any,
        };
    }
}
