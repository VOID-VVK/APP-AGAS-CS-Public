using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppAgasCs.Mcp.Contracts.Runtime;

public static class AgasMcpContractRegistry
{
    private static readonly ConcurrentDictionary<Type, Lazy<AgasMcpContractDescriptor>> Cache = new();
    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static AgasMcpContractDescriptor Get<T>() where T : class => Get(typeof(T));

    public static AgasMcpContractBindResult<T> Bind<T>(
        IReadOnlyDictionary<string, JsonElement>? arguments)
        where T : class
    {
        JsonElement value = arguments == null
            ? JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>())
            : JsonSerializer.SerializeToElement(arguments);
        return BindElement<T>(value);
    }

    public static AgasMcpContractBindResult<T> BindElement<T>(JsonElement value)
        where T : class
    {
        AgasMcpContractDescriptor descriptor = Get<T>();
        if (!descriptor.Validate(value, out AgasMcpContractValidationError? error))
        {
            return new AgasMcpContractBindResult<T>(false, null, error);
        }
        try
        {
            JsonElement materialized = descriptor.MaterializeDefaults(value);
            T? input = JsonSerializer.Deserialize<T>(materialized, StrictJsonOptions);
            return input == null
                ? DeserializeFailure<T>()
                : new AgasMcpContractBindResult<T>(true, input, null);
        }
        catch (JsonException)
        {
            return DeserializeFailure<T>();
        }
    }

    public static AgasMcpContractBindResult<T> ValidateInstance<T>(T? input)
        where T : class
    {
        try
        {
            JsonElement value = JsonSerializer.SerializeToElement(input, CanonicalJsonOptions);
            return BindElement<T>(value);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or NotSupportedException)
        {
            return DeserializeFailure<T>();
        }
    }

    internal static AgasMcpContractDescriptor Get(Type inputType) => Cache
        .GetOrAdd(
            inputType,
            type => new Lazy<AgasMcpContractDescriptor>(
                () => AgasMcpContractDescriptorBuilder.Build(type),
                isThreadSafe: true))
        .Value;

    private static AgasMcpContractBindResult<T> DeserializeFailure<T>() where T : class =>
        new(
            false,
            null,
            new AgasMcpContractValidationError(
                AgasMcpContractValidationCodes.Deserialize,
                "$",
                "Value could not be bound to the contract type."));
}
