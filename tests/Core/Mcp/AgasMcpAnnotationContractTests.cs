using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AppAgasCs.Mcp.Contracts.Metadata;
using AppAgasCs.Mcp.Contracts.Runtime;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp;

public sealed class AgasMcpAnnotationContractTests
{
    [Fact]
    public void Descriptor_UsesOneAnnotationModelForSchemaAndNestedConditions()
    {
        AgasMcpContractDescriptor descriptor = AgasMcpContractRegistry.Get<SampleInput>();
        JsonElement schema = descriptor.InputSchema;
        JsonElement properties = schema.GetProperty("properties");

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.GetProperty("$schema").GetString());
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("Sample MCP input.", schema.GetProperty("description").GetString());
        Assert.Equal(new[] { "op" }, schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(new[] { "add", "rename", "move" },
            properties.GetProperty("op").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));

        JsonElement name = properties.GetProperty("name");
        Assert.Equal("Lowercase sample name.", name.GetProperty("description").GetString());
        Assert.Equal("base", name.GetProperty("default").GetString());
        Assert.Equal(2, name.GetProperty("minLength").GetInt32());
        Assert.Equal(8, name.GetProperty("maxLength").GetInt32());
        Assert.Equal("^[a-z]+$", name.GetProperty("pattern").GetString());
        Assert.Equal("fixed", properties.GetProperty("mode").GetProperty("const").GetString());

        JsonElement extensions = properties.GetProperty("extensions");
        Assert.Equal("array", extensions.GetProperty("type").GetString());
        Assert.Equal(1, extensions.GetProperty("minItems").GetInt32());
        Assert.Equal(3, extensions.GetProperty("maxItems").GetInt32());
        Assert.True(extensions.GetProperty("uniqueItems").GetBoolean());
        Assert.Equal(new[] { "png", "jpg" },
            extensions.GetProperty("items").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));

        JsonElement region = properties.GetProperty("region");
        Assert.Equal("#/$defs/region", region.GetProperty("$ref").GetString());
        region = schema.GetProperty("$defs").GetProperty("region");
        Assert.False(region.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(new[] { "column", "row", "width" },
            region.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(2, schema.GetProperty("allOf").GetArrayLength());
        Assert.Contains("\"anyOf\"", schema.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"null\"", schema.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_CachesDescriptor_AndBindsValidInputStrictly()
    {
        Assert.Same(
            AgasMcpContractRegistry.Get<SampleInput>(),
            AgasMcpContractRegistry.Get<SampleInput>());

        AgasMcpContractBindResult<SampleInput> result = Bind<SampleInput>("""
        {
          "op": "rename",
          "name": "forest",
          "x": 2.5,
          "extensions": ["png", "jpg"],
          "mode": "fixed",
          "region": { "column": 0, "row": 1, "width": 2 }
        }
        """);

        Assert.True(result.Success, result.Error?.ToString());
        Assert.NotNull(result.Value);
        Assert.Equal("forest", result.Value.Name);
        Assert.Equal(new[] { "png", "jpg" }, result.Value.Extensions);
        Assert.Equal(2, result.Value.Region?.Width);
    }

    public static TheoryData<string, string, string> InvalidSamples => new()
    {
        { "{\"op\":\"add\",\"extra\":true}", AgasMcpContractValidationCodes.AdditionalProperties, "$.extra" },
        { "{\"Op\":\"add\"}", AgasMcpContractValidationCodes.AdditionalProperties, "$.Op" },
        { "{}", AgasMcpContractValidationCodes.Required, "$.op" },
        { "{\"op\":null}", AgasMcpContractValidationCodes.Type, "$.op" },
        { "{\"op\":\"delete\"}", AgasMcpContractValidationCodes.Enum, "$.op" },
        { "{\"op\":\"add\",\"mode\":\"other\"}", AgasMcpContractValidationCodes.Const, "$.mode" },
        { "{\"op\":\"add\",\"name\":\"Aaa\"}", AgasMcpContractValidationCodes.Pattern, "$.name" },
        { "{\"op\":\"add\",\"name\":\"a\"}", AgasMcpContractValidationCodes.MinLength, "$.name" },
        { "{\"op\":\"add\",\"name\":\"abcdefghi\"}", AgasMcpContractValidationCodes.MaxLength, "$.name" },
        { "{\"op\":\"add\",\"x\":-11}", AgasMcpContractValidationCodes.Minimum, "$.x" },
        { "{\"op\":\"add\",\"x\":11}", AgasMcpContractValidationCodes.Maximum, "$.x" },
        { "{\"op\":\"add\",\"extensions\":[]}", AgasMcpContractValidationCodes.MinItems, "$.extensions" },
        { "{\"op\":\"add\",\"extensions\":[\"png\",\"jpg\",\"png\",\"jpg\"]}", AgasMcpContractValidationCodes.MaxItems, "$.extensions" },
        { "{\"op\":\"add\",\"extensions\":[\"png\",\"png\"]}", AgasMcpContractValidationCodes.UniqueItems, "$.extensions[1]" },
        { "{\"op\":\"add\",\"extensions\":[\"webp\"]}", AgasMcpContractValidationCodes.Enum, "$.extensions[0]" },
        { "{\"op\":\"add\",\"region\":{\"column\":0,\"width\":1}}", AgasMcpContractValidationCodes.Required, "$.region.row" },
        { "{\"op\":\"add\",\"region\":{\"column\":0,\"row\":0,\"width\":1,\"extra\":true}}", AgasMcpContractValidationCodes.AdditionalProperties, "$.region.extra" },
        { "{\"op\":\"rename\"}", AgasMcpContractValidationCodes.Required, "$.name" },
        { "{\"op\":\"move\"}", AgasMcpContractValidationCodes.AnyRequired, "$" },
    };

    [Theory]
    [MemberData(nameof(InvalidSamples))]
    public void SameAnnotations_RejectInvalidRawJsonWithStableCodeAndPath(
        string json,
        string expectedCode,
        string expectedPath)
    {
        AgasMcpContractBindResult<SampleInput> result = Bind<SampleInput>(json);

        Assert.False(result.Success);
        Assert.Null(result.Value);
        Assert.Equal(expectedCode, result.Error?.Code);
        Assert.Equal(expectedPath, result.Error?.Path);
        Assert.Equal(
            $"Value does not satisfy contract keyword '{expectedCode}'.",
            result.Error?.Message);
    }

    [Fact]
    public void NullArguments_NormalizeToEmptyObject_ThenApplyRequiredContract()
    {
        AgasMcpContractBindResult<OptionalInput> optional =
            AgasMcpContractRegistry.Bind<OptionalInput>(arguments: null);
        AgasMcpContractBindResult<SampleInput> required =
            AgasMcpContractRegistry.Bind<SampleInput>(arguments: null);

        Assert.True(optional.Success, optional.Error?.ToString());
        Assert.Equal(7, optional.Value?.Limit);
        Assert.False(required.Success);
        Assert.Equal(AgasMcpContractValidationCodes.Required, required.Error?.Code);
        Assert.Equal("$.op", required.Error?.Path);
    }

    [Fact]
    public void GoldenContract_MergesConditions_EmitsDefinitions_AndBindsAnnotationDefaults()
    {
        JsonElement schema = AgasMcpContractRegistry.Get<GoldenInput>().InputSchema;
        JsonElement properties = schema.GetProperty("properties");
        JsonElement condition = schema.GetProperty("allOf")[0].GetProperty("then");

        Assert.Equal(1, schema.GetProperty("allOf").GetArrayLength());
        Assert.Equal(new[] { "name", "enabled" },
            condition.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(new[] { "x", "y" },
            condition.GetProperty("anyOf").EnumerateArray()
                .Select(item => item.GetProperty("required")[0].GetString()));
        Assert.False(properties.GetProperty("enabled").GetProperty("default").GetBoolean());
        Assert.Equal(0, properties.GetProperty("offset").GetProperty("default").GetInt32());
        Assert.Equal(string.Empty, properties.GetProperty("query").GetProperty("default").GetString());
        Assert.Equal(new[] { "png" }, properties.GetProperty("extensions").GetProperty("default")
            .EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("#/$defs/region", properties.GetProperty("region").GetProperty("$ref").GetString());
        Assert.Equal("#/$defs/region", properties.GetProperty("replacement").GetProperty("$ref").GetString());
        Assert.True(schema.GetProperty("$defs").TryGetProperty("region", out _));

        AgasMcpContractBindResult<GoldenInput> bound = Bind<GoldenInput>("{\"op\":\"noop\"}");
        Assert.True(bound.Success, bound.Error?.ToString());
        Assert.False(bound.Value!.Enabled);
        Assert.Equal(0, bound.Value.Offset);
        Assert.Equal(string.Empty, bound.Value.Query);
        Assert.Equal(new[] { "png" }, bound.Value.Extensions);

        AgasMcpContractBindResult<GoldenInput> validUpdate = Bind<GoldenInput>(
            "{\"op\":\"update\",\"name\":\"\",\"enabled\":false,\"x\":0}");
        Assert.True(validUpdate.Success, validUpdate.Error?.ToString());
        AgasMcpContractBindResult<GoldenInput> missingAny = Bind<GoldenInput>(
            "{\"op\":\"update\",\"name\":\"\",\"enabled\":false}");
        Assert.Equal(AgasMcpContractValidationCodes.AnyRequired, missingAny.Error?.Code);
        Assert.Equal("$", missingAny.Error?.Path);
    }

    [Fact]
    public void Defaults_PreserveExplicitFalsyValues_CloneArrays_AndMaterializeNestedObjects()
    {
        AgasMcpContractBindResult<GoldenInput> explicitValues = Bind<GoldenInput>(
            "{\"op\":\"noop\",\"enabled\":false,\"offset\":0,\"query\":\"\",\"extensions\":[]}");
        Assert.True(explicitValues.Success, explicitValues.Error?.ToString());
        Assert.Empty(explicitValues.Value!.Extensions);

        GoldenInput first = Bind<GoldenInput>("{\"op\":\"noop\"}").Value!;
        GoldenInput second = Bind<GoldenInput>("{\"op\":\"noop\"}").Value!;
        Assert.NotSame(first.Extensions, second.Extensions);
        first.Extensions[0] = "jpg";
        Assert.Equal("png", second.Extensions[0]);

        AgasMcpContractBindResult<GoldenInput> nested = Bind<GoldenInput>(
            "{\"op\":\"noop\",\"region\":{}}");
        Assert.True(nested.Success, nested.Error?.ToString());
        Assert.Equal(0, nested.Value?.Region?.Column);
    }

    [Theory]
    [InlineData("{\"op\":\"noop\",\"query\":null}", "type", "$.query")]
    [InlineData("{\"op\":\"noop\",\"region\":null}", "type", "$.region")]
    [InlineData("{\"op\":\"noop\",\"Region\":{}}", "additionalProperties", "$.Region")]
    [InlineData("{\"op\":\"noop\",\"extensions\":[\"png\",1]}", "type", "$.extensions[1]")]
    [InlineData("{\"op\":\"update\",\"name\":\"\",\"enabled\":false,\"x\":null}", "type", "$.x")]
    public void GoldenRuntime_RejectsNullCaseAndArrayItemViolations(
        string json,
        string code,
        string path)
    {
        AgasMcpContractBindResult<GoldenInput> result = Bind<GoldenInput>(json);
        Assert.Equal(code, result.Error?.Code);
        Assert.Equal(path, result.Error?.Path);
    }

    [Fact]
    public async Task Cache_IsConcurrent_AndRecursiveContractsFailFast()
    {
        Task<AgasMcpContractDescriptor>[] reads = Enumerable.Range(0, 64)
            .Select(_ => Task.Run(AgasMcpContractRegistry.Get<GoldenInput>))
            .ToArray();
        AgasMcpContractDescriptor[] descriptors = await Task.WhenAll(reads);
        Assert.All(descriptors, descriptor => Assert.Same(descriptors[0], descriptor));

        Task recursive = Task.Run(() => Assert.Throws<InvalidOperationException>(
            AgasMcpContractRegistry.Get<RecursiveInput>));
        await recursive.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Builder_FailsFastForUnsupportedMetadataAndTypes()
    {
        AssertContractFailure<MissingClosedInput>("McpClosedObjectAttribute");
        AssertContractFailure<BadRangeInput>("RangeAttribute");
        AssertContractFailure<BadUniqueInput>("McpUniqueItemsAttribute");
        AssertContractFailure<BadDefaultInput>("default value");
        AssertContractFailure<BadConditionInput>("condition expected value");
        AssertContractFailure<BadDictionaryInput>("dictionary");
        AssertContractFailure<BadNumericInput>("unsupported numeric");
        AssertContractFailure<BadIgnoredInput>("JsonIgnoreAttribute");
        AssertContractFailure<BadDefinitionInput>("McpUseDefinitionAttribute");
        AssertContractFailure<MissingSetterInput>("public setter or init accessor");
        AssertContractFailure<PrivateSetterInput>("public setter or init accessor");
    }

    [Fact]
    public void UnicodeLengthAndRegexTimeout_ReturnStableValidationErrors()
    {
        Assert.True(Bind<UnicodeInput>("{\"text\":\"😀a\"}").Success);
        Assert.Equal(AgasMcpContractValidationCodes.MinLength,
            Bind<UnicodeInput>("{\"text\":\"😀\"}").Error?.Code);
        Assert.Equal(AgasMcpContractValidationCodes.MaxLength,
            Bind<UnicodeInput>("{\"text\":\"😀ab\"}").Error?.Code);

        string payload = JsonSerializer.Serialize(new { text = new string('a', 100_000) + "!" });
        Stopwatch stopwatch = Stopwatch.StartNew();
        AgasMcpContractBindResult<RegexInput> result = Bind<RegexInput>(payload);
        stopwatch.Stop();
        Assert.Equal(AgasMcpContractValidationCodes.Pattern, result.Error?.Code);
        Assert.Equal("$.text", result.Error?.Path);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    private static void AssertContractFailure<T>(string fragment) where T : class
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            AgasMcpContractRegistry.Get<T>);
        Assert.Contains(fragment, exception.Message, StringComparison.Ordinal);
    }

    private static AgasMcpContractBindResult<T> Bind<T>(string json) where T : class
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return AgasMcpContractRegistry.BindElement<T>(document.RootElement.Clone());
    }

    [McpClosedObject(IncludeSchemaKeyword = true)]
    [Description("Sample MCP input.")]
    [McpWhenEqualsRequired(nameof(Op), "rename", nameof(Name))]
    [McpWhenEqualsAnyRequired(nameof(Op), "move", nameof(X), nameof(Y))]
    private sealed class SampleInput
    {
        [Required]
        [AllowedValues("add", "rename", "move")]
        public string? Op { get; init; }

        [Description("Lowercase sample name.")]
        [DefaultValue("base")]
        [StringLength(8, MinimumLength = 2)]
        [RegularExpression("^[a-z]+$")]
        public string? Name { get; init; }

        [Range(-10, 10)]
        public double? X { get; init; }

        [Range(-10, 10)]
        public double? Y { get; init; }

        [MinLength(1)]
        [MaxLength(3)]
        [McpUniqueItems]
        [McpItemsAllowedValues("png", "jpg")]
        public IReadOnlyList<string>? Extensions { get; init; }

        [McpConst("fixed")]
        public string? Mode { get; init; }

        [McpUseDefinition("region")]
        public SampleRegion? Region { get; init; }
    }

    [McpClosedObject]
    private sealed class SampleRegion
    {
        [Required]
        public int? Column { get; init; }

        [Required]
        public int? Row { get; init; }

        [Required]
        [Range(1, int.MaxValue)]
        public int? Width { get; init; }
    }

    [McpClosedObject]
    private sealed class OptionalInput
    {
        [DefaultValue(7)]
        [Range(1, 10)]
        public int Limit { get; init; } = 7;
    }

    [McpClosedObject(IncludeSchemaKeyword = true)]
    [McpWhenEqualsRequired(nameof(Op), "update", nameof(Name))]
    [McpWhenEqualsRequired(nameof(Op), "update", nameof(Enabled), nameof(Name))]
    [McpWhenEqualsAnyRequired(nameof(Op), "update", nameof(X))]
    [McpWhenEqualsAnyRequired(nameof(Op), "update", nameof(X), nameof(Y))]
    private sealed class GoldenInput
    {
        [Required, AllowedValues("noop", "update")]
        public string? Op { get; init; }

        [DefaultValue(false)]
        public bool Enabled { get; init; } = true;

        [DefaultValue(0)]
        public int Offset { get; init; } = 9;

        [DefaultValue("")]
        public string Query { get; init; } = "ctor";

        [DefaultValue(new string[] { "png" })]
        [McpItemsAllowedValues("png", "jpg")]
        public string[] Extensions { get; init; } = { "ctor" };

        public string? Name { get; init; }
        public int? X { get; init; }
        public int? Y { get; init; }

        [McpUseDefinition("region")]
        public GoldenRegion? Region { get; init; }

        [McpUseDefinition("region")]
        public GoldenRegion? Replacement { get; init; }
    }

    [McpClosedObject]
    private sealed class GoldenRegion
    {
        [DefaultValue(0)]
        public int Column { get; init; } = 4;
    }

    private sealed class MissingClosedInput;

    [McpClosedObject]
    private sealed class BadRangeInput { [Range(1, 2)] public string? Text { get; init; } }

    [McpClosedObject]
    private sealed class BadUniqueInput { [McpUniqueItems] public string? Text { get; init; } }

    [McpClosedObject]
    private sealed class BadDefaultInput { [DefaultValue(11), Range(1, 10)] public int Value { get; init; } }

    [McpClosedObject]
    [McpWhenEqualsRequired(nameof(Op), "missing", nameof(Name))]
    private sealed class BadConditionInput
    {
        [AllowedValues("valid")] public string? Op { get; init; }
        public string? Name { get; init; }
    }

    [McpClosedObject]
    private sealed class BadDictionaryInput { public Dictionary<string, string>? Values { get; init; } }

    [McpClosedObject]
    private sealed class BadNumericInput { public byte Value { get; init; } }

    [McpClosedObject]
    private sealed class BadIgnoredInput { [JsonIgnore] public string? Value { get; init; } }

    [McpClosedObject]
    private sealed class BadDefinitionInput { [McpUseDefinition("value")] public string? Value { get; init; } }

    [McpClosedObject]
    private sealed class MissingSetterInput { public string? Value { get; } }

    [McpClosedObject]
    private sealed class PrivateSetterInput { public string? Value { get; private set; } }

    [McpClosedObject]
    private sealed class RecursiveInput { public RecursiveInput? Next { get; init; } }

    [McpClosedObject]
    private sealed class UnicodeInput
    {
        [Required, StringLength(2, MinimumLength = 2)]
        public string? Text { get; init; }
    }

    [McpClosedObject]
    private sealed class RegexInput
    {
        [Required, RegularExpression("^(a+)+$")]
        public string? Text { get; init; }
    }
}
