using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AppAgasCs.Mcp.Contracts.Output;

namespace AppAgasCs.Mcp.Contracts.Runtime;

public sealed class AgasMcpOutputContractException : InvalidOperationException
{
    public AgasMcpOutputContractException(string message) : base(message)
    {
    }
}

public sealed class AgasMcpOutputContract
{
    internal AgasMcpOutputContract(
        IReadOnlyList<Type> successDataTypes,
        IReadOnlyList<Type> failureDataTypes,
        bool allowsFailureWithoutData,
        JsonElement outputSchema)
    {
        SuccessDataTypes = successDataTypes;
        FailureDataTypes = failureDataTypes;
        AllowsFailureWithoutData = allowsFailureWithoutData;
        OutputSchema = outputSchema;
    }

    public IReadOnlyList<Type> SuccessDataTypes { get; }
    public IReadOnlyList<Type> FailureDataTypes { get; }
    public bool AllowsFailureWithoutData { get; }
    public JsonElement OutputSchema { get; }
}

public static class AgasMcpOutputSchemaBuilder
{
    private const long JavaScriptSafeIntegerMaximum = 9_007_199_254_740_991;
    private const string ProviderIdPattern = "^[a-z0-9][a-z0-9._-]{0,79}$";
    private static readonly NullabilityInfoContext Nullability = new();

    public static AgasMcpOutputContract Build(
        IEnumerable<Type> successDataTypes,
        IEnumerable<Type>? failureDataTypes = null,
        bool allowsFailureWithoutData = true)
    {
        Type[] success = FreezeTypes(successDataTypes, nameof(successDataTypes), requireNonEmpty: true);
        Type[] failure = FreezeTypes(failureDataTypes ?? Array.Empty<Type>(), nameof(failureDataTypes), requireNonEmpty: false);
        foreach (Type type in success.Concat(failure))
        {
            EnsureOutputContractType(type);
        }

        var variants = new JsonArray
        {
            Envelope(ok: true, BuildDataSchema(success), includeData: true),
        };
        if (allowsFailureWithoutData)
        {
            variants.Add(Envelope(ok: false, dataSchema: null, includeData: false));
        }
        if (failure.Length > 0)
        {
            variants.Add(Envelope(ok: false, BuildDataSchema(failure), includeData: true));
        }

        if (variants.Count == 1)
        {
            throw new AgasMcpOutputContractException("An output contract must permit at least one failure envelope.");
        }

        JsonElement schema = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["type"] = "object",
            ["oneOf"] = variants,
        });
        return new AgasMcpOutputContract(
            Array.AsReadOnly(success),
            Array.AsReadOnly(failure),
            allowsFailureWithoutData,
            schema);
    }

    internal static bool ValidateData(Type expectedType, object data, out string reason)
    {
        AgasMcpOutputUnionAttribute? union = expectedType.GetCustomAttribute<AgasMcpOutputUnionAttribute>();
        if (union == null && data.GetType() != expectedType)
        {
            reason = "data-type";
            return false;
        }

        if (union != null)
        {
            return ValidateUnion(expectedType, union, data, new HashSet<object>(ReferenceEqualityComparer.Instance), out reason);
        }

        return ValidateObject(expectedType, data, new HashSet<object>(ReferenceEqualityComparer.Instance), out reason);
    }

    private static JsonObject Envelope(bool ok, JsonNode? dataSchema, bool includeData)
    {
        var properties = new JsonObject
        {
            ["ok"] = new JsonObject { ["const"] = ok },
            ["saved"] = new JsonObject { ["type"] = "boolean" },
            ["dirty"] = new JsonObject { ["type"] = "boolean" },
            ["warnings"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
            },
        };
        var required = new JsonArray("ok", "saved", "dirty");
        if (includeData)
        {
            properties["data"] = dataSchema;
            required.Add("data");
        }
        if (!ok)
        {
            properties["error"] = ErrorSchema();
            required.Add("warnings");
            required.Add("error");
        }
        else
        {
            required.Add("warnings");
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["required"] = required,
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject ErrorSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("code", "message", "suggestion"),
        ["properties"] = new JsonObject
        {
            ["code"] = new JsonObject { ["type"] = "string" },
            ["message"] = new JsonObject { ["type"] = "string" },
            ["suggestion"] = new JsonObject { ["type"] = "string" },
        },
        ["additionalProperties"] = false,
    };

    private static JsonNode BuildDataSchema(IReadOnlyList<Type> types)
    {
        if (types.Count == 1)
        {
            return BuildContractTypeSchema(types[0], new HashSet<Type>());
        }

        var variants = new JsonArray();
        foreach (Type type in types)
        {
            variants.Add(BuildContractTypeSchema(type, new HashSet<Type>()));
        }
        return new JsonObject { ["oneOf"] = variants };
    }

    private static JsonObject BuildContractTypeSchema(Type type, HashSet<Type> activeTypes)
    {
        AgasMcpOutputUnionAttribute? union = type.GetCustomAttribute<AgasMcpOutputUnionAttribute>();
        if (union == null)
        {
            return BuildObjectSchema(type, activeTypes);
        }

        EnsureOutputUnion(type, union);
        if (!activeTypes.Add(type))
            throw ContractError(type, "contains a recursive output union graph");
        try
        {
            return new JsonObject
            {
                ["oneOf"] = new JsonArray(union.Variants
                    .Select(variant => (JsonNode?)BuildObjectSchema(variant, activeTypes))
                    .ToArray()),
            };
        }
        finally
        {
            activeTypes.Remove(type);
        }
    }

    private static JsonObject BuildObjectSchema(Type type, HashSet<Type> activeTypes)
    {
        EnsureOutputObject(type);
        if (!activeTypes.Add(type))
        {
            throw ContractError(type, "contains a recursive output object graph");
        }

        try
        {
            var properties = new JsonObject();
            var required = new JsonArray();
            foreach (PropertyInfo property in OutputProperties(type))
            {
                string wireName = WireName(property);
                if (properties.ContainsKey(wireName))
                {
                    throw ContractError(type, $"contains duplicate wire property '{wireName}'");
                }
                bool optional = IsOptional(property);
                if (property.GetCustomAttribute<AgasMcpOutputOptionalAttribute>() != null
                    && property.PropertyType.IsValueType
                    && Nullable.GetUnderlyingType(property.PropertyType) == null)
                {
                    throw PropertyError(type, property, "declares optional omission on a non-nullable value type");
                }
                properties[wireName] = BuildValueSchema(type, property, activeTypes);
                if (!optional)
                {
                    required.Add(wireName);
                }
            }

            var schema = new JsonObject
            {
                ["type"] = "object",
                ["required"] = required,
                ["properties"] = properties,
                ["additionalProperties"] = false,
            };
            ApplyObjectConditions(type, schema);
            return schema;
        }
        finally
        {
            activeTypes.Remove(type);
        }
    }

    private static JsonNode BuildValueSchema(Type owner, PropertyInfo property, HashSet<Type> activeTypes)
    {
        Type effectiveType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        RejectTypeLevelSerializationOverrides(owner, property, effectiveType);
        Type? itemType = GetCollectionElementType(effectiveType);
        JsonObject schema;
        if (itemType != null)
        {
            if (GetCollectionElementType(itemType) != null)
            {
                throw PropertyError(owner, property, "cannot contain nested collections");
            }
            schema = new JsonObject
            {
                ["type"] = "array",
                ["items"] = BuildTypeSchema(owner, property, itemType, activeTypes),
            };
        }
        else
        {
            schema = BuildTypeSchema(owner, property, effectiveType, activeTypes);
        }

        ApplyPropertyConstraints(owner, property, schema, effectiveType, itemType);
        return schema;
    }

    private static JsonObject BuildTypeSchema(
        Type owner,
        PropertyInfo property,
        Type type,
        HashSet<Type> activeTypes)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        RejectTypeLevelSerializationOverrides(owner, property, type);
        if (type == typeof(string)) return new JsonObject { ["type"] = "string" };
        if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (type == typeof(int) || type.IsEnum) return new JsonObject { ["type"] = "integer" };
        if (type == typeof(long)) return new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = JavaScriptSafeIntegerMaximum };
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) return new JsonObject { ["type"] = "number" };
        if (type == typeof(DateTimeOffset)) return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
        if (type.GetCustomAttribute<AgasMcpOutputUnionAttribute>() != null)
        {
            return BuildContractTypeSchema(type, activeTypes);
        }
        if (type.GetCustomAttribute<AgasMcpOutputObjectAttribute>() != null)
        {
            return BuildObjectSchema(type, activeTypes);
        }
        throw PropertyError(owner, property, $"uses unsupported output type '{type.FullName}'");
    }

    private static void ApplyPropertyConstraints(
        Type owner,
        PropertyInfo property,
        JsonObject schema,
        Type effectiveType,
        Type? itemType)
    {
        AgasMcpOutputStringAttribute? text = property.GetCustomAttribute<AgasMcpOutputStringAttribute>();
        AgasMcpOutputProviderIdAttribute? providerId = property.GetCustomAttribute<AgasMcpOutputProviderIdAttribute>();
        AgasMcpOutputNumberAttribute? number = property.GetCustomAttribute<AgasMcpOutputNumberAttribute>();
        AgasMcpOutputAllowedValuesAttribute? allowed = property.GetCustomAttribute<AgasMcpOutputAllowedValuesAttribute>();
        AgasMcpOutputIntegerEnumAttribute? integerEnum = property.GetCustomAttribute<AgasMcpOutputIntegerEnumAttribute>();
        AgasMcpOutputConstAttribute? constValue = property.GetCustomAttribute<AgasMcpOutputConstAttribute>();

        if (text != null)
        {
            if (effectiveType != typeof(string)) throw PropertyError(owner, property, "uses string constraints on a non-string property");
            if (text.MinimumLength >= 0) schema["minLength"] = text.MinimumLength;
            if (text.MaximumLength >= 0) schema["maxLength"] = text.MaximumLength;
            if (text.MinimumLength > text.MaximumLength && text.MaximumLength >= 0) throw PropertyError(owner, property, "has invalid string bounds");
            if (text.Pattern != null)
            {
                try
                {
                    _ = new Regex(text.Pattern, RegexOptions.CultureInvariant);
                }
                catch (ArgumentException exception)
                {
                    throw PropertyError(owner, property, $"has an invalid regex pattern: {exception.GetType().Name}");
                }
                schema["pattern"] = text.Pattern;
            }
            if (text.Format != null) schema["format"] = text.Format;
        }
        if (providerId != null)
        {
            if (effectiveType != typeof(string) || itemType != null)
                throw PropertyError(owner, property, "uses providerId constraints on a non-string property");
            if (text != null)
                throw PropertyError(owner, property, "combines providerId with generic string constraints");
            schema["minLength"] = 1;
            schema["maxLength"] = 80;
            schema["pattern"] = ProviderIdPattern;
        }
        if (number != null)
        {
            if (effectiveType != typeof(int) && effectiveType != typeof(long)
                && effectiveType != typeof(double) && effectiveType != typeof(float) && effectiveType != typeof(decimal))
                throw PropertyError(owner, property, "uses numeric constraints on a non-number property");
            if (!double.IsNaN(number.Minimum)) schema["minimum"] = number.Minimum;
            if (!double.IsNaN(number.Maximum)) schema["maximum"] = number.Maximum;
            if (!double.IsNaN(number.Minimum) && !double.IsNaN(number.Maximum) && number.Minimum > number.Maximum)
                throw PropertyError(owner, property, "has invalid numeric bounds");
        }
        if (allowed != null)
        {
            Type valueType = itemType ?? effectiveType;
            if (valueType != typeof(string)) throw PropertyError(owner, property, "uses string enum values on a non-string property or item");
            JsonNode?[] values = allowed.Values
                .Select(value => (JsonNode?)JsonValue.Create(value))
                .ToArray();
            schema[itemType == null ? "enum" : "items"] = itemType == null
                ? new JsonArray(values)
                : new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(values) };
        }
        if (integerEnum != null)
        {
            if (itemType != null || (effectiveType != typeof(int) && !effectiveType.IsEnum))
                throw PropertyError(owner, property, "uses integer enum values on an unsupported property");
            schema["enum"] = new JsonArray(integerEnum.Values.Select(value => JsonValue.Create(value)).ToArray());
        }
        if (constValue != null)
        {
            if (itemType != null)
                throw PropertyError(owner, property, "uses const on a collection property");
            try
            {
                _ = ConvertConst(constValue.Value, effectiveType);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidCastException or OverflowException)
            {
                throw PropertyError(owner, property, "has a const value incompatible with its property type");
            }
            schema["const"] = JsonSerializer.SerializeToNode(constValue.Value);
        }
    }

    private static bool ValidateObject(Type type, object value, HashSet<object> active, out string reason)
    {
        if (!active.Add(value))
        {
            reason = "recursive-value";
            return false;
        }
        try
        {
            foreach (PropertyInfo property in OutputProperties(type))
            {
                object? propertyValue = property.GetValue(value);
                if (propertyValue == null)
                {
                    if (!IsOptional(property))
                    {
                        reason = "required-null";
                        return false;
                    }
                    continue;
                }
                if (!ValidateValue(property, propertyValue, active, out reason)) return false;
            }
            if (!ValidateObjectConditions(type, value, out reason)) return false;
            reason = string.Empty;
            return true;
        }
        finally
        {
            active.Remove(value);
        }
    }

    private static bool ValidateValue(PropertyInfo property, object value, HashSet<object> active, out string reason)
    {
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        Type? itemType = GetCollectionElementType(type);
        if (itemType != null)
        {
            foreach (object? item in (IEnumerable)value)
            {
                if (item == null)
                {
                    reason = "null-array-item";
                    return false;
                }
                if (!ValidateScalar(property, itemType, item, active, applyPropertyConstraints: false, out reason)) return false;
            }
            AgasMcpOutputAllowedValuesAttribute? allowedItems = property.GetCustomAttribute<AgasMcpOutputAllowedValuesAttribute>();
            if (allowedItems != null && ((IEnumerable)value).Cast<object>().Any(item => !allowedItems.Values.Contains((string)item, StringComparer.Ordinal)))
            {
                reason = "array-enum";
                return false;
            }
            reason = string.Empty;
            return true;
        }
        return ValidateScalar(property, type, value, active, applyPropertyConstraints: true, out reason);
    }

    private static bool ValidateScalar(
        PropertyInfo property,
        Type type,
        object value,
        HashSet<object> active,
        bool applyPropertyConstraints,
        out string reason)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(long) && ((long)value < 0 || (long)value > JavaScriptSafeIntegerMaximum))
        {
            reason = "int64-range";
            return false;
        }
        if (type == typeof(double) && !double.IsFinite((double)value)
            || type == typeof(float) && !float.IsFinite((float)value))
        {
            reason = "non-finite";
            return false;
        }
        if (type == typeof(DateTimeOffset) && ((DateTimeOffset)value).Offset != TimeSpan.Zero)
        {
            reason = "date-not-utc";
            return false;
        }
        AgasMcpOutputUnionAttribute? union = type.GetCustomAttribute<AgasMcpOutputUnionAttribute>();
        if (union != null)
        {
            return ValidateUnion(type, union, value, active, out reason);
        }
        if (type.GetCustomAttribute<AgasMcpOutputObjectAttribute>() != null)
        {
            if (value.GetType() != type)
            {
                reason = "nested-data-type";
                return false;
            }
            return ValidateObject(type, value, active, out reason);
        }
        if (!applyPropertyConstraints)
        {
            reason = string.Empty;
            return true;
        }

        AgasMcpOutputStringAttribute? text = property.GetCustomAttribute<AgasMcpOutputStringAttribute>();
        if (text != null)
        {
            string stringValue = (string)value;
            if (text.MinimumLength >= 0 && stringValue.Length < text.MinimumLength
                || text.MaximumLength >= 0 && stringValue.Length > text.MaximumLength
                || text.Pattern != null && !Regex.IsMatch(stringValue, text.Pattern, RegexOptions.CultureInvariant))
            {
                reason = "string-constraint";
                return false;
            }
        }
        if (property.GetCustomAttribute<AgasMcpOutputProviderIdAttribute>() != null
            && !Regex.IsMatch((string)value, ProviderIdPattern, RegexOptions.CultureInvariant))
        {
            reason = "provider-id";
            return false;
        }
        AgasMcpOutputNumberAttribute? number = property.GetCustomAttribute<AgasMcpOutputNumberAttribute>();
        if (number != null)
        {
            double numeric = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            if (!double.IsNaN(number.Minimum) && numeric < number.Minimum
                || !double.IsNaN(number.Maximum) && numeric > number.Maximum)
            {
                reason = "number-constraint";
                return false;
            }
        }
        AgasMcpOutputAllowedValuesAttribute? allowed = property.GetCustomAttribute<AgasMcpOutputAllowedValuesAttribute>();
        if (allowed != null && !allowed.Values.Contains((string)value, StringComparer.Ordinal))
        {
            reason = "string-enum";
            return false;
        }
        AgasMcpOutputIntegerEnumAttribute? integerEnum = property.GetCustomAttribute<AgasMcpOutputIntegerEnumAttribute>();
        if (integerEnum != null && !integerEnum.Values.Contains(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)))
        {
            reason = "integer-enum";
            return false;
        }
        AgasMcpOutputConstAttribute? constValue = property.GetCustomAttribute<AgasMcpOutputConstAttribute>();
        if (constValue != null && !Equals(value, ConvertConst(constValue.Value, type)))
        {
            reason = "const";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static object ConvertConst(object value, Type type) =>
        type.IsEnum ? Enum.ToObject(type, value) : Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture);

    private static PropertyInfo[] OutputProperties(Type type)
    {
        PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(property => property.MetadataToken)
            .ToArray();
        foreach (PropertyInfo property in properties)
        {
            if (property.GetIndexParameters().Length != 0 || property.GetMethod is not { IsPublic: true })
                throw PropertyError(type, property, "must be a public non-indexed getter");
            if (property.GetCustomAttribute<JsonExtensionDataAttribute>() != null
                || property.GetCustomAttribute<JsonIgnoreAttribute>() != null
                || property.GetCustomAttribute<JsonPropertyNameAttribute>() != null
                || property.GetCustomAttribute<JsonIncludeAttribute>() != null
                || property.GetCustomAttribute<JsonConverterAttribute>() != null
                || property.GetCustomAttribute<JsonNumberHandlingAttribute>() != null)
                throw PropertyError(type, property, "uses a serialization override that can drift from the output contract");
        }
        return properties;
    }

    private static bool IsOptional(PropertyInfo property)
    {
        if (property.GetCustomAttribute<AgasMcpOutputOptionalAttribute>() != null) return true;
        if (Nullable.GetUnderlyingType(property.PropertyType) != null) return true;
        return !property.PropertyType.IsValueType
            && Nullability.Create(property).ReadState == NullabilityState.Nullable;
    }

    private static Type? GetCollectionElementType(Type type)
    {
        if (type == typeof(string)) return null;
        if (type.IsArray) return type.GetElementType();
        Type? collection = type.GetInterfaces().Append(type)
            .FirstOrDefault(candidate => candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IReadOnlyList<>));
        return collection?.GetGenericArguments()[0];
    }

    private static string WireName(PropertyInfo property) =>
        JsonNamingPolicy.CamelCase.ConvertName(property.Name);

    private static void RejectTypeLevelSerializationOverrides(Type owner, PropertyInfo property, Type outputType)
    {
        if (outputType.GetCustomAttribute<JsonConverterAttribute>() != null)
            throw PropertyError(owner, property, $"uses output type '{outputType.FullName}' with a type-level JsonConverterAttribute that can bypass the output contract");
        if (outputType.GetCustomAttribute<JsonNumberHandlingAttribute>() != null)
            throw PropertyError(owner, property, $"uses output type '{outputType.FullName}' with a type-level JsonNumberHandlingAttribute that can bypass the output contract");
    }

    private static void EnsureOutputObject(Type type)
    {
        if (type.GetCustomAttribute<AgasMcpOutputObjectAttribute>() == null)
            throw ContractError(type, "must declare AgasMcpOutputObjectAttribute");
        if (type.GetCustomAttribute<JsonConverterAttribute>() != null)
            throw ContractError(type, "uses a type-level JsonConverterAttribute that can bypass the output contract");
        if (type.GetCustomAttribute<JsonNumberHandlingAttribute>() != null)
            throw ContractError(type, "uses a type-level JsonNumberHandlingAttribute that can bypass the output contract");
    }

    private static void ApplyObjectConditions(Type type, JsonObject schema)
    {
        AgasMcpOutputWhenBooleanAttribute[] conditions = type
            .GetCustomAttributes<AgasMcpOutputWhenBooleanAttribute>().ToArray();
        if (conditions.Length == 0) return;
        var allOf = new JsonArray();
        PropertyInfo[] properties = OutputProperties(type);
        foreach (AgasMcpOutputWhenBooleanAttribute condition in conditions)
        {
            PropertyInfo conditionProperty = properties.SingleOrDefault(property => property.Name == condition.ConditionProperty)
                ?? throw ContractError(type, $"declares an unknown conditional property '{condition.ConditionProperty}'");
            if (conditionProperty.PropertyType != typeof(bool))
                throw PropertyError(type, conditionProperty, "must be boolean for an output condition");
            foreach (string propertyName in condition.RequiredProperties.Concat(condition.ForbiddenProperties))
                if (!properties.Any(property => property.Name == propertyName))
                    throw ContractError(type, $"declares an unknown conditional member '{propertyName}'");

            var then = new JsonObject();
            if (condition.RequiredProperties.Count > 0)
                then["required"] = new JsonArray(condition.RequiredProperties
                    .Select(name => (JsonNode?)JsonValue.Create(WireName(properties.Single(property => property.Name == name))))
                    .ToArray());
            if (condition.ForbiddenProperties.Count > 0)
                then["not"] = new JsonObject
                {
                    ["anyOf"] = new JsonArray(condition.ForbiddenProperties.Select(name => (JsonNode?)new JsonObject
                    {
                        ["required"] = new JsonArray(WireName(properties.Single(property => property.Name == name))),
                    }).ToArray()),
                };
            allOf.Add(new JsonObject
            {
                ["if"] = new JsonObject
                {
                    ["properties"] = new JsonObject
                    {
                        [WireName(conditionProperty)] = new JsonObject { ["const"] = condition.ConditionValue },
                    },
                    ["required"] = new JsonArray(WireName(conditionProperty)),
                },
                ["then"] = then,
            });
        }
        schema["allOf"] = allOf;
    }

    private static bool ValidateObjectConditions(Type type, object value, out string reason)
    {
        PropertyInfo[] properties = OutputProperties(type);
        foreach (AgasMcpOutputWhenBooleanAttribute condition in type.GetCustomAttributes<AgasMcpOutputWhenBooleanAttribute>())
        {
            PropertyInfo conditionProperty = properties.Single(property => property.Name == condition.ConditionProperty);
            if ((bool)conditionProperty.GetValue(value)! != condition.ConditionValue) continue;
            if (condition.RequiredProperties.Any(name => properties.Single(property => property.Name == name).GetValue(value) == null))
            {
                reason = "conditional-required";
                return false;
            }
            if (condition.ForbiddenProperties.Any(name => properties.Single(property => property.Name == name).GetValue(value) != null))
            {
                reason = "conditional-forbidden";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }

    private static void EnsureOutputContractType(Type type)
    {
        AgasMcpOutputUnionAttribute? union = type.GetCustomAttribute<AgasMcpOutputUnionAttribute>();
        if (union != null)
        {
            EnsureOutputUnion(type, union);
            return;
        }
        EnsureOutputObject(type);
    }

    private static void EnsureOutputUnion(Type type, AgasMcpOutputUnionAttribute union)
    {
        if (type.GetCustomAttribute<AgasMcpOutputObjectAttribute>() != null)
            throw ContractError(type, "cannot declare both output object and output union attributes");
        if (!type.IsAbstract && !type.IsInterface)
            throw ContractError(type, "must be an abstract class or interface when declaring an output union");
        foreach (Type variant in union.Variants)
        {
            if (!type.IsAssignableFrom(variant))
                throw ContractError(type, $"contains non-assignable union variant '{variant.FullName}'");
            EnsureOutputObject(variant);
        }
        if (type.GetCustomAttribute<JsonConverterAttribute>() != null
            || type.GetCustomAttribute<JsonNumberHandlingAttribute>() != null)
            throw ContractError(type, "uses a type-level serialization override that can bypass the output contract");
    }

    private static bool ValidateUnion(
        Type unionType,
        AgasMcpOutputUnionAttribute union,
        object value,
        HashSet<object> active,
        out string reason)
    {
        Type runtimeType = value.GetType();
        if (!union.Variants.Contains(runtimeType) || !unionType.IsAssignableFrom(runtimeType))
        {
            reason = "union-variant";
            return false;
        }
        return ValidateObject(runtimeType, value, active, out reason);
    }

    private static Type[] FreezeTypes(IEnumerable<Type> types, string parameterName, bool requireNonEmpty)
    {
        ArgumentNullException.ThrowIfNull(types, parameterName);
        Type[] frozen = types.ToArray();
        if (requireNonEmpty && frozen.Length == 0)
            throw new ArgumentException("At least one success data type is required.", parameterName);
        if (frozen.Any(type => type == null))
            throw new ArgumentException("Output data types cannot contain null.", parameterName);
        if (frozen.Distinct().Count() != frozen.Length)
            throw new ArgumentException("Output data types cannot contain duplicates.", parameterName);
        return frozen;
    }

    private static AgasMcpOutputContractException ContractError(Type type, string message) =>
        new($"Output contract '{type.FullName}' {message}.");

    private static AgasMcpOutputContractException PropertyError(Type owner, PropertyInfo property, string message) =>
        ContractError(owner, $"property '{property.Name}' {message}");
}
