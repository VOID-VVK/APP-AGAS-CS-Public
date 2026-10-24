using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AppAgasCs.Mcp.Contracts.Runtime;

public sealed class AgasMcpContractDescriptor
{
    private readonly IReadOnlyList<AgasMcpPropertyDescriptor> _properties;
    private readonly IReadOnlyDictionary<string, AgasMcpPropertyDescriptor> _propertiesByWireName;
    private readonly IReadOnlyList<AgasMcpConditionDescriptor> _conditions;

    internal AgasMcpContractDescriptor(
        Type inputType,
        bool includeSchemaKeyword,
        string? description,
        IReadOnlyList<AgasMcpPropertyDescriptor> properties,
        IReadOnlyList<AgasMcpConditionDescriptor> conditions)
    {
        InputType = inputType;
        Closed = true;
        IncludeSchemaKeyword = includeSchemaKeyword;
        Description = description;
        _properties = properties;
        _propertiesByWireName = properties.ToDictionary(item => item.WireName, StringComparer.Ordinal);
        _conditions = conditions;
        InputSchema = JsonSerializer.SerializeToElement(BuildRootSchema());
    }

    public Type InputType { get; }
    public bool Closed { get; }
    public bool IncludeSchemaKeyword { get; }
    public string? Description { get; }
    public JsonElement InputSchema { get; }

    public bool Validate(JsonElement value, out AgasMcpContractValidationError? error) =>
        ValidateObject(value, "$", out error);

    internal JsonObject BuildObjectSchema(bool includeDialect) =>
        BuildObjectSchema(includeDialect, new AgasMcpSchemaBuildContext());

    private JsonObject BuildRootSchema()
    {
        var context = new AgasMcpSchemaBuildContext();
        JsonObject schema = BuildObjectSchema(IncludeSchemaKeyword, context);
        JsonObject definitions = context.BuildDefinitions();
        if (definitions.Count > 0)
        {
            schema["$defs"] = definitions;
        }
        return schema;
    }

    internal JsonObject BuildObjectSchema(
        bool includeDialect,
        AgasMcpSchemaBuildContext context)
    {
        var schema = new JsonObject();
        if (includeDialect)
        {
            schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        }
        schema["type"] = "object";
        schema["additionalProperties"] = !Closed;
        if (!string.IsNullOrWhiteSpace(Description))
        {
            schema["description"] = Description;
        }

        var properties = new JsonObject();
        foreach (AgasMcpPropertyDescriptor property in _properties)
        {
            properties[property.WireName] = property.BuildSchema(context);
        }
        schema["properties"] = properties;

        var required = new JsonArray();
        foreach (AgasMcpPropertyDescriptor property in _properties.Where(item => item.Required))
        {
            required.Add(property.WireName);
        }
        schema["required"] = required;

        if (_conditions.Count > 0)
        {
            var allOf = new JsonArray();
            foreach (AgasMcpConditionDescriptor condition in _conditions)
            {
                allOf.Add(condition.BuildSchema());
            }
            schema["allOf"] = allOf;
        }
        return schema;
    }

    internal JsonElement MaterializeDefaults(JsonElement value)
    {
        JsonNode node = JsonNode.Parse(value.GetRawText())
            ?? throw new InvalidOperationException("A validated MCP object could not be materialized.");
        MaterializeObjectDefaults(node.AsObject());
        return JsonSerializer.SerializeToElement(node);
    }

    internal void MaterializeObjectDefaults(JsonObject value)
    {
        foreach (AgasMcpPropertyDescriptor property in _properties)
        {
            if (!value.TryGetPropertyValue(property.WireName, out JsonNode? propertyValue))
            {
                if (property.HasDefaultValue)
                {
                    value[property.WireName] = JsonSerializer.SerializeToNode(property.DefaultValue);
                }
                continue;
            }
            if (propertyValue == null) continue;
            property.MaterializeNestedDefaults(propertyValue);
        }
    }

    internal bool ValidateObject(
        JsonElement value,
        string path,
        out AgasMcpContractValidationError? error)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            error = Error(AgasMcpContractValidationCodes.Type, path);
            return false;
        }

        if (Closed)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!_propertiesByWireName.ContainsKey(property.Name))
                {
                    error = Error(
                        AgasMcpContractValidationCodes.AdditionalProperties,
                        AppendProperty(path, property.Name));
                    return false;
                }
            }
        }

        foreach (AgasMcpPropertyDescriptor property in _properties)
        {
            if (!value.TryGetProperty(property.WireName, out JsonElement propertyValue))
            {
                if (property.Required)
                {
                    error = Error(
                        AgasMcpContractValidationCodes.Required,
                        AppendProperty(path, property.WireName));
                    return false;
                }
                continue;
            }
            if (!property.Validate(propertyValue, AppendProperty(path, property.WireName), out error))
            {
                return false;
            }
        }

        foreach (AgasMcpConditionDescriptor condition in _conditions)
        {
            if (!condition.Validate(value, path, out error))
            {
                return false;
            }
        }

        error = null;
        return true;
    }

    internal static string AppendProperty(string path, string propertyName) =>
        path + "." + propertyName;

    internal static AgasMcpContractValidationError Error(string code, string path) =>
        new(code, path, $"Value does not satisfy contract keyword '{code}'.");
}

internal enum AgasMcpValueType
{
    String,
    Boolean,
    Integer,
    Number,
    Array,
    Object,
}

internal sealed class AgasMcpPropertyDescriptor
{
    public required string ClrName { get; init; }
    public required string WireName { get; init; }
    public required AgasMcpValueType ValueType { get; init; }
    public bool Required { get; init; }
    public string? Description { get; init; }
    public object? DefaultValue { get; init; }
    public bool HasDefaultValue { get; init; }
    public IReadOnlyList<object?> AllowedValues { get; init; } = Array.Empty<object?>();
    public object? ConstValue { get; init; }
    public bool HasConstValue { get; init; }
    public Regex? Pattern { get; init; }
    public int? MinimumLength { get; init; }
    public int? MaximumLength { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public int? MinimumItems { get; init; }
    public int? MaximumItems { get; init; }
    public bool UniqueItems { get; init; }
    public AgasMcpPropertyDescriptor? ItemDescriptor { get; init; }
    public IReadOnlyList<object?> ItemAllowedValues { get; init; } = Array.Empty<object?>();
    public AgasMcpContractDescriptor? ObjectDescriptor { get; init; }
    public string? DefinitionName { get; init; }

    public JsonObject BuildSchema(AgasMcpSchemaBuildContext context)
    {
        if (DefinitionName != null)
        {
            context.Register(DefinitionName, ObjectDescriptor!);
            return new JsonObject { ["$ref"] = $"#/$defs/{DefinitionName}" };
        }
        JsonObject schema = ValueType == AgasMcpValueType.Object
            ? ObjectDescriptor!.BuildObjectSchema(includeDialect: false, context)
            : new JsonObject { ["type"] = TypeName(ValueType) };
        if (!string.IsNullOrWhiteSpace(Description)) schema["description"] = Description;
        if (HasDefaultValue) schema["default"] = ToNode(DefaultValue);
        if (AllowedValues.Count > 0) schema["enum"] = ToArray(AllowedValues);
        if (HasConstValue) schema["const"] = ToNode(ConstValue);
        if (Pattern != null) schema["pattern"] = Pattern.ToString();
        if (MinimumLength.HasValue) schema["minLength"] = MinimumLength.Value;
        if (MaximumLength.HasValue) schema["maxLength"] = MaximumLength.Value;
        if (Minimum.HasValue) schema["minimum"] = Minimum.Value;
        if (Maximum.HasValue) schema["maximum"] = Maximum.Value;
        if (ValueType == AgasMcpValueType.Array)
        {
            JsonObject itemSchema = ItemDescriptor!.BuildSchema(context);
            if (ItemAllowedValues.Count > 0) itemSchema["enum"] = ToArray(ItemAllowedValues);
            schema["items"] = itemSchema;
            if (MinimumItems.HasValue) schema["minItems"] = MinimumItems.Value;
            if (MaximumItems.HasValue) schema["maxItems"] = MaximumItems.Value;
            if (UniqueItems) schema["uniqueItems"] = true;
        }
        return schema;
    }

    public void MaterializeNestedDefaults(JsonNode value)
    {
        if (ValueType == AgasMcpValueType.Object)
        {
            ObjectDescriptor!.MaterializeObjectDefaults(value.AsObject());
            return;
        }
        if (ValueType != AgasMcpValueType.Array || ItemDescriptor == null) return;
        foreach (JsonNode? item in value.AsArray())
        {
            if (item != null) ItemDescriptor.MaterializeNestedDefaults(item);
        }
    }

    public bool Validate(
        JsonElement value,
        string path,
        out AgasMcpContractValidationError? error)
    {
        if (!MatchesType(value))
        {
            error = AgasMcpContractDescriptor.Error(AgasMcpContractValidationCodes.Type, path);
            return false;
        }
        if (AllowedValues.Count > 0 && !AllowedValues.Any(item => JsonEquals(value, item)))
        {
            error = AgasMcpContractDescriptor.Error(AgasMcpContractValidationCodes.Enum, path);
            return false;
        }
        if (HasConstValue && !JsonEquals(value, ConstValue))
        {
            error = AgasMcpContractDescriptor.Error(AgasMcpContractValidationCodes.Const, path);
            return false;
        }

        switch (ValueType)
        {
            case AgasMcpValueType.String:
                string text = value.GetString()!;
                int length = CountRunes(text);
                if (MinimumLength.HasValue && length < MinimumLength.Value)
                {
                    error = AgasMcpContractDescriptor.Error(AgasMcpContractValidationCodes.MinLength, path);
                    return false;
                }
                if (MaximumLength.HasValue && length > MaximumLength.Value)
                {
                    error = AgasMcpContractDescriptor.Error(AgasMcpContractValidationCodes.MaxLength, path);
                    return false;
                }
                try
                {
                    if (Pattern != null && !Pattern.IsMatch(text))
                    {
                        error = AgasMcpContractDescriptor.Error(AgasMcpContractValidationCodes.Pattern, path);
                        return false;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    error = AgasMcpContractDescriptor.Error(AgasMcpContractValidationCodes.Pattern, path);
                    return false;
                }
                break;
            case AgasMcpValueType.Integer:
            case AgasMcpValueType.Number:
                double number = value.GetDouble();
                if (Minimum.HasValue && number < Minimum.Value)
                {
                    error = AgasMcpContractDescriptor.Error(AgasMcpContractValidationCodes.Minimum, path);
                    return false;
                }
                if (Maximum.HasValue && number > Maximum.Value)
                {
                    error = AgasMcpContractDescriptor.Error(AgasMcpContractValidationCodes.Maximum, path);
                    return false;
                }
                break;
            case AgasMcpValueType.Array:
                int itemCount = value.GetArrayLength();
                if (MinimumItems.HasValue && itemCount < MinimumItems.Value)
                {
                    error = AgasMcpContractDescriptor.Error(AgasMcpContractValidationCodes.MinItems, path);
                    return false;
                }
                if (MaximumItems.HasValue && itemCount > MaximumItems.Value)
                {
                    error = AgasMcpContractDescriptor.Error(AgasMcpContractValidationCodes.MaxItems, path);
                    return false;
                }
                JsonElement[] items = value.EnumerateArray().Select(item => item.Clone()).ToArray();
                for (int index = 0; index < items.Length; index++)
                {
                    string itemPath = $"{path}[{index}]";
                    if (!ItemDescriptor!.Validate(items[index], itemPath, out error)) return false;
                    if (ItemAllowedValues.Count > 0
                        && !ItemAllowedValues.Any(allowed => JsonEquals(items[index], allowed)))
                    {
                        error = AgasMcpContractDescriptor.Error(AgasMcpContractValidationCodes.Enum, itemPath);
                        return false;
                    }
                    if (UniqueItems && items.Take(index).Any(previous => JsonEquals(previous, items[index])))
                    {
                        error = AgasMcpContractDescriptor.Error(AgasMcpContractValidationCodes.UniqueItems, itemPath);
                        return false;
                    }
                }
                break;
            case AgasMcpValueType.Object:
                if (!ObjectDescriptor!.ValidateObject(value, path, out error)) return false;
                break;
        }

        error = null;
        return true;
    }

    private bool MatchesType(JsonElement value) => ValueType switch
    {
        AgasMcpValueType.String => value.ValueKind == JsonValueKind.String,
        AgasMcpValueType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        AgasMcpValueType.Integer => value.ValueKind == JsonValueKind.Number
            && value.TryGetDecimal(out decimal integer)
            && decimal.Truncate(integer) == integer,
        AgasMcpValueType.Number => value.ValueKind == JsonValueKind.Number,
        AgasMcpValueType.Array => value.ValueKind == JsonValueKind.Array,
        AgasMcpValueType.Object => value.ValueKind == JsonValueKind.Object,
        _ => false,
    };

    private static int CountRunes(string value)
    {
        int count = 0;
        foreach (System.Text.Rune _ in value.EnumerateRunes()) count++;
        return count;
    }

    internal static bool JsonEquals(JsonElement actual, object? expected) =>
        JsonEquals(actual, JsonSerializer.SerializeToElement(expected));

    internal static bool JsonEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number)
        {
            return left.TryGetDecimal(out decimal leftNumber)
                && right.TryGetDecimal(out decimal rightNumber)
                && leftNumber == rightNumber;
        }
        if (left.ValueKind != right.ValueKind) return false;
        return left.ValueKind switch
        {
            JsonValueKind.Object => ObjectEquals(left, right),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength()
                && left.EnumerateArray().Zip(right.EnumerateArray(), JsonEquals).All(item => item),
            JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False => left.GetBoolean() == right.GetBoolean(),
            JsonValueKind.Null => true,
            _ => left.GetRawText() == right.GetRawText(),
        };
    }

    private static bool ObjectEquals(JsonElement left, JsonElement right)
    {
        JsonProperty[] leftProperties = left.EnumerateObject().ToArray();
        JsonProperty[] rightProperties = right.EnumerateObject().ToArray();
        return leftProperties.Length == rightProperties.Length
            && leftProperties.All(property => right.TryGetProperty(property.Name, out JsonElement value)
                && JsonEquals(property.Value, value));
    }

    private static JsonNode? ToNode(object? value) => JsonSerializer.SerializeToNode(value);

    private static JsonArray ToArray(IEnumerable<object?> values)
    {
        var array = new JsonArray();
        foreach (object? value in values) array.Add(ToNode(value));
        return array;
    }

    private static string TypeName(AgasMcpValueType valueType) => valueType switch
    {
        AgasMcpValueType.String => "string",
        AgasMcpValueType.Boolean => "boolean",
        AgasMcpValueType.Integer => "integer",
        AgasMcpValueType.Number => "number",
        AgasMcpValueType.Array => "array",
        AgasMcpValueType.Object => "object",
        _ => throw new ArgumentOutOfRangeException(nameof(valueType)),
    };
}

internal sealed class AgasMcpSchemaBuildContext
{
    private readonly Dictionary<string, AgasMcpContractDescriptor> _definitions =
        new(StringComparer.Ordinal);
    private readonly List<string> _definitionOrder = new();

    public void Register(string name, AgasMcpContractDescriptor descriptor)
    {
        if (_definitions.TryGetValue(name, out AgasMcpContractDescriptor? existing))
        {
            if (existing.InputType != descriptor.InputType)
            {
                throw new InvalidOperationException(
                    $"MCP schema definition '{name}' is used by both '{existing.InputType.FullName}' and '{descriptor.InputType.FullName}'.");
            }
            return;
        }
        _definitions.Add(name, descriptor);
        _definitionOrder.Add(name);
    }

    public JsonObject BuildDefinitions()
    {
        var result = new JsonObject();
        for (int index = 0; index < _definitionOrder.Count; index++)
        {
            string name = _definitionOrder[index];
            result[name] = _definitions[name].BuildObjectSchema(includeDialect: false, this);
        }
        return result;
    }
}

internal sealed class AgasMcpConditionDescriptor
{
    public required AgasMcpPropertyDescriptor Discriminator { get; init; }
    public required object ExpectedValue { get; init; }
    public IReadOnlyList<AgasMcpPropertyDescriptor> RequiredProperties { get; init; } = Array.Empty<AgasMcpPropertyDescriptor>();
    public IReadOnlyList<AgasMcpPropertyDescriptor> AnyRequiredProperties { get; init; } = Array.Empty<AgasMcpPropertyDescriptor>();

    public JsonObject BuildSchema()
    {
        var conditionProperties = new JsonObject
        {
            [Discriminator.WireName] = new JsonObject { ["const"] = JsonSerializer.SerializeToNode(ExpectedValue) },
        };
        var ifSchema = new JsonObject
        {
            ["properties"] = conditionProperties,
            ["required"] = new JsonArray(Discriminator.WireName),
        };
        var thenSchema = new JsonObject();
        if (RequiredProperties.Count > 0)
        {
            thenSchema["required"] = new JsonArray(RequiredProperties.Select(item => JsonValue.Create(item.WireName)).ToArray());
        }
        if (AnyRequiredProperties.Count > 0)
        {
            var anyOf = new JsonArray();
            foreach (AgasMcpPropertyDescriptor property in AnyRequiredProperties)
            {
                anyOf.Add(new JsonObject { ["required"] = new JsonArray(property.WireName) });
            }
            thenSchema["anyOf"] = anyOf;
        }
        return new JsonObject { ["if"] = ifSchema, ["then"] = thenSchema };
    }

    public bool Validate(
        JsonElement value,
        string path,
        out AgasMcpContractValidationError? error)
    {
        if (!value.TryGetProperty(Discriminator.WireName, out JsonElement discriminatorValue)
            || !AgasMcpPropertyDescriptor.JsonEquals(discriminatorValue, ExpectedValue))
        {
            error = null;
            return true;
        }
        foreach (AgasMcpPropertyDescriptor property in RequiredProperties)
        {
            if (!value.TryGetProperty(property.WireName, out _))
            {
                error = AgasMcpContractDescriptor.Error(
                    AgasMcpContractValidationCodes.Required,
                    AgasMcpContractDescriptor.AppendProperty(path, property.WireName));
                return false;
            }
        }
        if (AnyRequiredProperties.Count > 0
            && !AnyRequiredProperties.Any(property => value.TryGetProperty(property.WireName, out _)))
        {
            error = AgasMcpContractDescriptor.Error(AgasMcpContractValidationCodes.AnyRequired, path);
            return false;
        }
        error = null;
        return true;
    }
}
