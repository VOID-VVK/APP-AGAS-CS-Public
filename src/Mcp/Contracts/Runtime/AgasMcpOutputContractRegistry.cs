using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AppAgasCs.Mcp.Contracts.Output;

namespace AppAgasCs.Mcp.Contracts.Runtime;

public readonly record struct AgasMcpOutputValidationResult(bool Success, string ContractFailure)
{
    public static AgasMcpOutputValidationResult Valid() => new(true, string.Empty);
    public static AgasMcpOutputValidationResult Invalid(string contractFailure) => new(false, contractFailure);
}

/// <summary>
/// Wave 1 output-contract foundation. It is intentionally not wired into MCP descriptors or tools yet.
/// </summary>
public static class AgasMcpOutputContractRegistry
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    public static AgasMcpOutputValidationResult Validate(
        AgasMcpOutputContract contract,
        AgasMcpToolResponse response)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(response);
        try
        {
            return ValidateCore(contract, response);
        }
        catch
        {
            return AgasMcpOutputValidationResult.Invalid("validation-exception");
        }
    }

    private static AgasMcpOutputValidationResult ValidateCore(
        AgasMcpOutputContract contract,
        AgasMcpToolResponse response)
    {
        if (response.Warnings.Any(warning => warning == null))
            return AgasMcpOutputValidationResult.Invalid("warnings-null-item");

        if (response.Ok)
        {
            if (response.Error != null || response.Data == null)
                return AgasMcpOutputValidationResult.Invalid("success-envelope");
            AgasMcpOutputValidationResult typed = ValidateRegisteredData(contract.SuccessDataTypes, response.Data);
            return typed.Success ? ValidateSerialized(contract, response) : typed;
        }

        if (response.Error == null)
            return AgasMcpOutputValidationResult.Invalid("failure-error");
        if (response.Data == null)
            return contract.AllowsFailureWithoutData
                ? ValidateSerialized(contract, response)
                : AgasMcpOutputValidationResult.Invalid("failure-data-required");
        if (contract.FailureDataTypes.Count == 0)
            return AgasMcpOutputValidationResult.Invalid("failure-data-forbidden");
        AgasMcpOutputValidationResult failureTyped = ValidateRegisteredData(contract.FailureDataTypes, response.Data);
        return failureTyped.Success ? ValidateSerialized(contract, response) : failureTyped;
    }

    public static AgasMcpToolResponse ValidateOrFailClosed(
        AgasMcpOutputContract contract,
        AgasMcpToolResponse response)
    {
        AgasMcpOutputValidationResult validation = Validate(contract, response);
        return validation.Success
            ? response
            : AgasMcpToolResponse.Failure(
                saved: false,
                dirty: false,
                AgasMcpErrorCodes.InternalError,
                "AGAS MCP output contract validation failed.",
                "Retry after restarting AGAS. If the problem persists, report the tool name and correlation id.");
    }

    public static JsonElement SerializeToElement(AgasMcpToolResponse response) =>
        JsonSerializer.SerializeToElement(response, SerializerOptions);

    private static AgasMcpOutputValidationResult ValidateSerialized(
        AgasMcpOutputContract contract,
        AgasMcpToolResponse response)
    {
        JsonElement wire = SerializeToElement(response);
        return AgasMcpJsonSchemaRuntimeValidator.IsValid(contract.OutputSchema, wire)
            ? AgasMcpOutputValidationResult.Valid()
            : AgasMcpOutputValidationResult.Invalid("serialized-schema");
    }

    private static AgasMcpOutputValidationResult ValidateRegisteredData(
        IReadOnlyList<Type> registeredTypes,
        object data)
    {
        Type actualType = data.GetType();
        Type[] matches = registeredTypes.Where(type => type == actualType
            || type.GetCustomAttributes(typeof(AgasMcpOutputUnionAttribute), inherit: false)
                .OfType<AgasMcpOutputUnionAttribute>()
                .Any(union => union.Variants.Contains(actualType))).ToArray();
        Type? expected = matches.Length == 1 ? matches[0] : null;
        if (expected == null)
            return AgasMcpOutputValidationResult.Invalid("data-variant");
        return AgasMcpOutputSchemaBuilder.ValidateData(expected, data, out string reason)
            ? AgasMcpOutputValidationResult.Valid()
            : AgasMcpOutputValidationResult.Invalid(reason);
    }

    private static JsonSerializerOptions CreateSerializerOptions() => new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

internal static class AgasMcpJsonSchemaRuntimeValidator
{
    public static bool IsValid(JsonElement schema, JsonElement value)
    {
        if (schema.TryGetProperty("allOf", out JsonElement allOf)
            && allOf.EnumerateArray().Any(candidate => !IsValid(candidate, value))) return false;
        if (schema.TryGetProperty("anyOf", out JsonElement anyOf)
            && !anyOf.EnumerateArray().Any(candidate => IsValid(candidate, value))) return false;
        if (schema.TryGetProperty("not", out JsonElement not)
            && IsValid(not, value)) return false;
        if (schema.TryGetProperty("if", out JsonElement condition)
            && IsValid(condition, value)
            && schema.TryGetProperty("then", out JsonElement consequence)
            && !IsValid(consequence, value)) return false;
        if (schema.TryGetProperty("oneOf", out JsonElement oneOf))
        {
            int matches = oneOf.EnumerateArray().Count(candidate => IsValid(candidate, value));
            if (matches != 1) return false;
        }

        if (schema.TryGetProperty("type", out JsonElement type)
            && !MatchesType(type.GetString(), value.ValueKind)) return false;
        if (schema.TryGetProperty("type", out type)
            && type.GetString() == "integer"
            && !value.TryGetInt64(out _)) return false;
        if (schema.TryGetProperty("const", out JsonElement constant)
            && !JsonElement.DeepEquals(constant, value)) return false;
        if (schema.TryGetProperty("enum", out JsonElement enumeration)
            && !enumeration.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value))) return false;

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out JsonElement required))
            {
                foreach (JsonElement requiredName in required.EnumerateArray())
                    if (!value.TryGetProperty(requiredName.GetString()!, out _)) return false;
            }
            JsonElement properties = schema.TryGetProperty("properties", out JsonElement declared)
                ? declared : default;
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (properties.ValueKind == JsonValueKind.Object
                    && properties.TryGetProperty(property.Name, out JsonElement propertySchema))
                {
                    if (!IsValid(propertySchema, property.Value)) return false;
                }
                else if (schema.TryGetProperty("additionalProperties", out JsonElement additional)
                         && additional.ValueKind == JsonValueKind.False)
                {
                    return false;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array
                 && schema.TryGetProperty("items", out JsonElement items))
        {
            foreach (JsonElement item in value.EnumerateArray())
                if (!IsValid(items, item)) return false;
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString()!;
            if (schema.TryGetProperty("minLength", out JsonElement minLength) && text.Length < minLength.GetInt32()) return false;
            if (schema.TryGetProperty("maxLength", out JsonElement maxLength) && text.Length > maxLength.GetInt32()) return false;
            if (schema.TryGetProperty("pattern", out JsonElement pattern)
                && !Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant)) return false;
            if (schema.TryGetProperty("format", out JsonElement format)
                && format.GetString() == "date-time"
                && !DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out _)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Number)
        {
            double number = value.GetDouble();
            if (!double.IsFinite(number)) return false;
            if (schema.TryGetProperty("minimum", out JsonElement minimum) && number < minimum.GetDouble()) return false;
            if (schema.TryGetProperty("maximum", out JsonElement maximum) && number > maximum.GetDouble()) return false;
        }
        return true;
    }

    private static bool MatchesType(string? type, JsonValueKind kind) => type switch
    {
        "object" => kind == JsonValueKind.Object,
        "array" => kind == JsonValueKind.Array,
        "string" => kind == JsonValueKind.String,
        "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
        "integer" => kind == JsonValueKind.Number,
        "number" => kind == JsonValueKind.Number,
        _ => true,
    };
}
