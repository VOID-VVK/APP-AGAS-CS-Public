using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Contracts.Output;
using AppAgasCs.Mcp.Contracts.Runtime;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp.Contracts;

public sealed class AgasMcpOutputContractTests
{
    [Fact]
    public void Builder_EmitsClosedSuccessPlainFailureAndTypedFailureVariants()
    {
        AgasMcpOutputContract contract = Contract();
        JsonElement variants = contract.OutputSchema.GetProperty("oneOf");

        Assert.Equal(3, variants.GetArrayLength());
        Assert.All(variants.EnumerateArray(), variant => Assert.False(variant.GetProperty("additionalProperties").GetBoolean()));
        Assert.Equal(new[] { "ok", "saved", "dirty", "data", "warnings" }, Required(variants[0]));
        Assert.Equal(new[] { "ok", "saved", "dirty", "warnings", "error" }, Required(variants[1]));
        Assert.Equal(new[] { "ok", "saved", "dirty", "data", "warnings", "error" }, Required(variants[2]));

        JsonElement successData = variants[0].GetProperty("properties").GetProperty("data").GetProperty("oneOf");
        Assert.Equal(2, successData.GetArrayLength());
        Assert.All(successData.EnumerateArray(), item => Assert.False(item.GetProperty("additionalProperties").GetBoolean()));
        Assert.Equal(new[] { "state", "name", "items", "capabilities", "updatedAtUtc", "sizeBytes", "nodeState", "score" }, Required(successData[0]));
        Assert.Equal("date-time", successData[0].GetProperty("properties").GetProperty("updatedAtUtc").GetProperty("format").GetString());
        Assert.Equal(9_007_199_254_740_991, successData[0].GetProperty("properties").GetProperty("sizeBytes").GetProperty("maximum").GetInt64());
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, successData[0].GetProperty("properties").GetProperty("nodeState").GetProperty("enum").EnumerateArray().Select(item => item.GetInt32()));
    }

    [Fact]
    public void Serializer_OmitsNullAndPreservesEmptyArraysAndUtcWire()
    {
        AgasMcpToolResponse response = AgasMcpToolResponse.Success(false, false, ValidQueued(optionalNote: null));

        JsonElement json = AgasMcpOutputContractRegistry.SerializeToElement(response);

        Assert.False(json.TryGetProperty("error", out _));
        JsonElement data = json.GetProperty("data");
        Assert.False(data.TryGetProperty("optionalNote", out _));
        Assert.Equal(JsonValueKind.Array, data.GetProperty("items").ValueKind);
        Assert.Equal(0, data.GetProperty("items").GetArrayLength());
        Assert.EndsWith("+00:00", data.GetProperty("updatedAtUtc").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_AcceptsBothSuccessVariantsAndBothFailureShapes()
    {
        AgasMcpOutputContract contract = Contract();
        AgasMcpToolResponse[] responses =
        {
            AgasMcpToolResponse.Success(false, false, ValidQueued()),
            AgasMcpToolResponse.Success(false, false, new GoldenSucceeded("Succeeded", "done", new GoldenItem("one"))),
            AgasMcpToolResponse.Failure(false, false, "AGAS_TEST", "failed", "retry"),
            new AgasMcpToolResponse(false, false, true, new GoldenFailureReceipt(true, false, "partial"), Array.Empty<string>(), new AgasMcpToolError("AGAS_TEST", "failed", "retry")),
        };

        Assert.All(responses, response => Assert.True(AgasMcpOutputContractRegistry.Validate(contract, response).Success));
    }

    [Theory]
    [MemberData(nameof(InvalidResponses))]
    public void Runtime_InvalidOutputFailsClosedWithoutReturningOriginalData(AgasMcpToolResponse invalid)
    {
        AgasMcpToolResponse result = AgasMcpOutputContractRegistry.ValidateOrFailClosed(Contract(), invalid);

        Assert.False(result.Ok);
        Assert.Null(result.Data);
        Assert.Equal(AgasMcpErrorCodes.InternalError, result.Error?.Code);
        string json = JsonSerializer.Serialize(result, AgasMcpOutputContractRegistry.SerializerOptions);
        Assert.DoesNotContain("credential-value", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_RejectsOpenOrSerializationOverriddenFixtureTypes()
    {
        Assert.Throws<AgasMcpOutputContractException>(() =>
            AgasMcpOutputSchemaBuilder.Build(new[] { typeof(OpenOutput) }));
        Assert.Throws<AgasMcpOutputContractException>(() =>
            AgasMcpOutputSchemaBuilder.Build(new[] { typeof(OverriddenOutput) }));
    }

    [Fact]
    public void Builder_RejectsTypeLevelConverterThatWouldLeakASecretOnTheRealSerializerPath()
    {
        var dangerous = new TypeConvertedOutput("safe-display-value");
        string serializerProof = JsonSerializer.Serialize(
            AgasMcpToolResponse.Success(false, false, dangerous),
            AgasMcpOutputContractRegistry.SerializerOptions);

        Assert.Contains("credential-value", serializerProof, StringComparison.Ordinal);
        AgasMcpOutputContractException exception = Assert.Throws<AgasMcpOutputContractException>(() =>
            AgasMcpOutputSchemaBuilder.Build(new[] { typeof(TypeConvertedOutput) }));
        Assert.Contains("type-level JsonConverterAttribute", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_RejectsEnumTypeLevelConverterAnywhereInTheContractGraph()
    {
        var dangerous = new EnumConvertedOutput(TypeConvertedEnum.Safe);
        string serializerProof = JsonSerializer.Serialize(
            AgasMcpToolResponse.Success(false, false, dangerous),
            AgasMcpOutputContractRegistry.SerializerOptions);

        Assert.Contains("credential-value", serializerProof, StringComparison.Ordinal);
        AgasMcpOutputContractException exception = Assert.Throws<AgasMcpOutputContractException>(() =>
            AgasMcpOutputSchemaBuilder.Build(new[] { typeof(EnumConvertedOutput) }));
        Assert.Contains("type-level JsonConverterAttribute", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_RejectsCollectionTypeLevelConverterBeforeInspectingItsItems()
    {
        var dangerous = new CollectionConvertedOutput(new TypeConvertedStringList("safe-item"));
        string serializerProof = JsonSerializer.Serialize(
            AgasMcpToolResponse.Success(false, false, dangerous),
            AgasMcpOutputContractRegistry.SerializerOptions);

        Assert.Contains("credential-value", serializerProof, StringComparison.Ordinal);
        AgasMcpOutputContractException exception = Assert.Throws<AgasMcpOutputContractException>(() =>
            AgasMcpOutputSchemaBuilder.Build(new[] { typeof(CollectionConvertedOutput) }));
        Assert.Contains("type-level JsonConverterAttribute", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_RejectsPropertyNumberHandlingThatChangesTheRealWireType()
    {
        var dangerous = new PropertyNumberHandlingOutput(42);
        string serializerProof = JsonSerializer.Serialize(
            AgasMcpToolResponse.Success(false, false, dangerous),
            AgasMcpOutputContractRegistry.SerializerOptions);

        Assert.Contains("\"count\":\"42\"", serializerProof, StringComparison.Ordinal);
        Assert.Throws<AgasMcpOutputContractException>(() =>
            AgasMcpOutputSchemaBuilder.Build(new[] { typeof(PropertyNumberHandlingOutput) }));
    }

    [Fact]
    public void Builder_RejectsTypeNumberHandlingThatChangesNestedNumericWireTypes()
    {
        var dangerous = new TypeNumberHandlingOutput(42);
        string serializerProof = JsonSerializer.Serialize(
            AgasMcpToolResponse.Success(false, false, dangerous),
            AgasMcpOutputContractRegistry.SerializerOptions);

        Assert.Contains("\"count\":\"42\"", serializerProof, StringComparison.Ordinal);
        AgasMcpOutputContractException exception = Assert.Throws<AgasMcpOutputContractException>(() =>
            AgasMcpOutputSchemaBuilder.Build(new[] { typeof(TypeNumberHandlingOutput) }));
        Assert.Contains("type-level JsonNumberHandlingAttribute", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_ProviderIdPrimitivePreservesDotInSchemaAndRuntimeValidation()
    {
        AgasMcpOutputContract contract = AgasMcpOutputSchemaBuilder.Build(new[] { typeof(ProviderIdentityOutput) });
        JsonElement providerSchema = contract.OutputSchema.GetProperty("oneOf")[0]
            .GetProperty("properties").GetProperty("data")
            .GetProperty("properties").GetProperty("providerId");

        Assert.Equal(1, providerSchema.GetProperty("minLength").GetInt32());
        Assert.Equal(80, providerSchema.GetProperty("maxLength").GetInt32());
        string pattern = providerSchema.GetProperty("pattern").GetString()!;
        Assert.Equal("^[a-z0-9][a-z0-9._-]{0,79}$", pattern);
        Assert.Matches(new Regex(pattern, RegexOptions.CultureInvariant), "vendor.provider");
        Assert.True(AgasMcpOutputContractRegistry.Validate(
            contract,
            AgasMcpToolResponse.Success(false, false, new ProviderIdentityOutput("vendor.provider"))).Success);
        Assert.Equal(
            "provider-id",
            AgasMcpOutputContractRegistry.Validate(
                contract,
                AgasMcpToolResponse.Success(false, false, new ProviderIdentityOutput("Vendor.Provider")))
                .ContractFailure);
    }

    public static IEnumerable<object[]> InvalidResponses()
    {
        yield return new object[] { AgasMcpToolResponse.Success(false, false, new { secret = "credential-value" }) };
        yield return new object[] { AgasMcpToolResponse.Success(false, false, ValidQueued(state: "Running")) };
        yield return new object[] { AgasMcpToolResponse.Success(false, false, ValidQueued(nodeState: 9)) };
        yield return new object[] { AgasMcpToolResponse.Success(false, false, ValidQueued(sizeBytes: 9_007_199_254_740_992)) };
        yield return new object[] { AgasMcpToolResponse.Success(false, false, ValidQueued(score: double.NaN)) };
        yield return new object[] { AgasMcpToolResponse.Success(false, false, ValidQueued(updatedAt: new DateTimeOffset(2026, 7, 31, 1, 2, 3, TimeSpan.FromHours(8)))) };
        yield return new object[] { new AgasMcpToolResponse(false, false, false, ValidQueued(), Array.Empty<string>(), new AgasMcpToolError("AGAS_TEST", "failed", "retry")) };
    }

    private static AgasMcpOutputContract Contract() =>
        AgasMcpOutputSchemaBuilder.Build(
            new[] { typeof(GoldenQueued), typeof(GoldenSucceeded) },
            new[] { typeof(GoldenFailureReceipt) },
            allowsFailureWithoutData: true);

    private static GoldenQueued ValidQueued(
        string state = "Queued",
        string? optionalNote = null,
        int nodeState = 2,
        long sizeBytes = 42,
        double score = 0.5,
        DateTimeOffset? updatedAt = null) =>
        new(
            state,
            "fixture",
            Array.Empty<GoldenItem>(),
            Array.Empty<string>(),
            updatedAt ?? new DateTimeOffset(2026, 7, 31, 1, 2, 3, TimeSpan.Zero),
            sizeBytes,
            nodeState,
            score,
            optionalNote);

    private static string[] Required(JsonElement schema) =>
        schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).ToArray();

    [AgasMcpOutputObject]
    private sealed record GoldenItem(
        [property: AgasMcpOutputString(MinimumLength = 1, MaximumLength = 20)] string Value);

    [AgasMcpOutputObject]
    private sealed record GoldenQueued(
        [property: AgasMcpOutputConst("Queued")] string State,
        [property: AgasMcpOutputString(MinimumLength = 1, MaximumLength = 40, Pattern = "^[a-z-]+$")] string Name,
        IReadOnlyList<GoldenItem> Items,
        [property: AgasMcpOutputAllowedValues("textToImage", "referenceEdit", "regionEdit", "nativeAlphaMask", "guidedRegionEdit")] IReadOnlyList<string> Capabilities,
        DateTimeOffset UpdatedAtUtc,
        long SizeBytes,
        [property: AgasMcpOutputIntegerEnum(0, 1, 2, 3, 4)] int NodeState,
        [property: AgasMcpOutputNumber(Minimum = 0, Maximum = 1)] double Score,
        string? OptionalNote);

    [AgasMcpOutputObject]
    private sealed record GoldenSucceeded(
        [property: AgasMcpOutputConst("Succeeded")] string State,
        string Message,
        GoldenItem Item);

    [AgasMcpOutputObject]
    private sealed record GoldenFailureReceipt(
        bool ProjectStillOpen,
        bool Saved,
        [property: AgasMcpOutputAllowedValues("none", "partial", "complete")] string SaveState);

    private sealed record OpenOutput(string Value);

    [AgasMcpOutputObject]
    private sealed record OverriddenOutput([property: JsonPropertyName("drifted")] string Value);

    [AgasMcpOutputObject]
    [JsonConverter(typeof(SecretLeakingOutputConverter))]
    private sealed record TypeConvertedOutput(string DisplayValue);

    private sealed class SecretLeakingOutputConverter : JsonConverter<TypeConvertedOutput>
    {
        public override TypeConvertedOutput Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, TypeConvertedOutput value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("displayValue", value.DisplayValue);
            writer.WriteString("secret", "credential-value");
            writer.WriteEndObject();
        }
    }

    [AgasMcpOutputObject]
    private sealed record EnumConvertedOutput(TypeConvertedEnum State);

    [JsonConverter(typeof(SecretLeakingEnumConverter))]
    private enum TypeConvertedEnum
    {
        Safe = 0,
    }

    private sealed class SecretLeakingEnumConverter : JsonConverter<TypeConvertedEnum>
    {
        public override TypeConvertedEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, TypeConvertedEnum value, JsonSerializerOptions options) =>
            writer.WriteStringValue("credential-value");
    }

    [AgasMcpOutputObject]
    private sealed record CollectionConvertedOutput(TypeConvertedStringList Items);

    [JsonConverter(typeof(SecretLeakingStringListConverter))]
    private sealed class TypeConvertedStringList : IReadOnlyList<string>
    {
        private readonly string[] _items;

        public TypeConvertedStringList(params string[] items) => _items = items;

        public int Count => _items.Length;
        public string this[int index] => _items[index];
        public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)_items).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();
    }

    private sealed class SecretLeakingStringListConverter : JsonConverter<TypeConvertedStringList>
    {
        public override TypeConvertedStringList Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, TypeConvertedStringList value, JsonSerializerOptions options) =>
            writer.WriteStringValue("credential-value");
    }

    [AgasMcpOutputObject]
    private sealed record PropertyNumberHandlingOutput(
        [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] int Count);

    [AgasMcpOutputObject]
    [JsonNumberHandling(JsonNumberHandling.WriteAsString)]
    private sealed record TypeNumberHandlingOutput(int Count);

    [AgasMcpOutputObject]
    private sealed record ProviderIdentityOutput(
        [property: AgasMcpOutputProviderId] string ProviderId);
}
