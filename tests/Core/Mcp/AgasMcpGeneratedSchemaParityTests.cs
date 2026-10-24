using System.Text.Json;
using AppAgasCs.Mcp.Contracts;
using AppAgasCs.Mcp.Contracts.Runtime;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp;

public sealed class AgasMcpGeneratedSchemaParityTests
{
    [Fact]
    public void All16GeneratedSchemas_SemanticallyMatchFrozenWireContracts()
    {
        (string Name, JsonElement Frozen, JsonElement Generated)[] schemas =
        {
            ("project.snapshot", AgasMcpFrozenToolSchemas.ProjectSnapshot, AgasMcpToolSchemas.ProjectSnapshot),
            ("assets.list", AgasMcpFrozenToolSchemas.AssetsList, AgasMcpToolSchemas.AssetsList),
            ("scene.mutate", AgasMcpFrozenToolSchemas.SceneMutate, AgasMcpToolSchemas.SceneMutate),
            ("layer.mutate", AgasMcpFrozenToolSchemas.LayerMutate, AgasMcpToolSchemas.LayerMutate),
            ("image.mutate", AgasMcpFrozenToolSchemas.ImageMutate, AgasMcpToolSchemas.ImageMutate),
            ("prompt.mutate", AgasMcpFrozenToolSchemas.PromptMutate, AgasMcpToolSchemas.PromptMutate),
            ("view.set", AgasMcpFrozenToolSchemas.ViewSet, AgasMcpToolSchemas.ViewSet),
            ("project.save", AgasMcpFrozenToolSchemas.ProjectSave, AgasMcpToolSchemas.ProjectSave),
            ("selection.set", AgasMcpFrozenToolSchemas.SelectionSet, AgasMcpToolSchemas.SelectionSet),
            ("selection.get", AgasMcpFrozenToolSchemas.SelectionGet, AgasMcpToolSchemas.SelectionGet),
            ("ai.generate", AgasMcpFrozenToolSchemas.AiGenerate, AgasMcpToolSchemas.AiGenerate),
            ("ai.status", AgasMcpFrozenToolSchemas.AiStatus, AgasMcpToolSchemas.AiStatus),
            ("ai.cancel", AgasMcpFrozenToolSchemas.AiCancel, AgasMcpToolSchemas.AiCancel),
            ("ai.apply", AgasMcpFrozenToolSchemas.AiApply, AgasMcpToolSchemas.AiApply),
        };

        foreach ((string name, JsonElement frozen, JsonElement generated) in schemas)
        {
            AssertEquivalent(frozen, generated, "$", name);
            Assert.Equal(
                frozen.GetProperty("properties").EnumerateObject().Select(item => item.Name),
                generated.GetProperty("properties").EnumerateObject().Select(item => item.Name));
        }
    }

    [Fact]
    public void ProductionDefaults_AreMaterializedFromTheSameAnnotations()
    {
        AgasProjectSnapshotInput snapshot = Bind<AgasProjectSnapshotInput>("{}");
        Assert.True(snapshot.IncludeScenes);
        Assert.True(snapshot.IncludeLayers);
        Assert.False(snapshot.IncludeImages);
        Assert.False(snapshot.IncludePrompts);

        AgasAssetsListInput firstAssets = Bind<AgasAssetsListInput>("{}");
        AgasAssetsListInput secondAssets = Bind<AgasAssetsListInput>("{}");
        Assert.Equal(string.Empty, firstAssets.Query);
        Assert.Equal(new[] { "png", "jpg", "jpeg", "webp" }, firstAssets.Extensions);
        Assert.Equal(50, firstAssets.Limit);
        Assert.Equal(0, firstAssets.Offset);
        Assert.NotSame(firstAssets.Extensions, secondAssets.Extensions);

        AgasAiGenerateInput generate = Bind<AgasAiGenerateInput>(
            "{\"sceneId\":\"scene-001\",\"layerId\":\"layer-001\"}");
        Assert.Equal("NewImage", generate.Target);
        Assert.Equal("CurrentLayer", generate.LayerScope);
        Assert.Null(generate.Quality);
        Assert.Equal(10, Bind<AgasAiStatusInput>("{}").Limit);
    }

    [Theory]
    [InlineData("CurrentLayer")]
    [InlineData("AllVisibleLayers")]
    [InlineData("VisibleLayersExceptCurrent")]
    public void AiGenerate_AcceptsExplicitReferenceScope(string scope)
    {
        AgasAiGenerateInput generate = Bind<AgasAiGenerateInput>(
            $$"""{"sceneId":"scene-001","layerId":"layer-001","layerScope":"{{scope}}"}""");
        Assert.Equal(scope, generate.LayerScope);
        Assert.Equal("layer-001", generate.LayerId);
        Assert.Equal("NewImage", generate.Target);
    }

    [Theory]
    [InlineData("allVisibleLayers")]
    [InlineData("InvisibleLayers")]
    [InlineData("1")]
    [InlineData("")]
    public void AiGenerate_RejectsUnknownReferenceScope(string scope)
    {
        AssertValidation<AgasAiGenerateInput>(
            $$"""{"sceneId":"scene-001","layerId":"layer-001","layerScope":"{{scope}}"}""",
            AgasMcpContractValidationCodes.Enum,
            "$.layerScope");
    }

    [Fact]
    public void AiGenerate_RejectsNullReferenceScope()
    {
        AssertValidation<AgasAiGenerateInput>(
            """{"sceneId":"scene-001","layerId":"layer-001","layerScope":null}""",
            AgasMcpContractValidationCodes.Type,
            "$.layerScope");
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    public void AiGenerate_AcceptsExplicitGrok20Quality(string quality)
    {
        AgasAiGenerateInput generate = Bind<AgasAiGenerateInput>(
            $$"""{"sceneId":"scene-001","layerId":"layer-001","quality":"{{quality}}"}""");

        Assert.Equal(quality, generate.Quality);
    }

    [Theory]
    [InlineData("Low")]
    [InlineData("high")]
    [InlineData("")]
    public void AiGenerate_RejectsUnknownQuality(string quality)
    {
        AssertValidation<AgasAiGenerateInput>(
            $$"""{"sceneId":"scene-001","layerId":"layer-001","quality":"{{quality}}"}""",
            AgasMcpContractValidationCodes.Enum,
            "$.quality");
    }

    [Fact]
    public void AiGenerate_RejectsExplicitNullQuality()
    {
        AssertValidation<AgasAiGenerateInput>(
            """{"sceneId":"scene-001","layerId":"layer-001","quality":null}""",
            AgasMcpContractValidationCodes.Type,
            "$.quality");
    }

    [Fact]
    public void SelectionDefinition_AndGeneratedRuntimeValidationStayFrozen()
    {
        JsonElement schema = AgasMcpToolSchemas.SelectionSet;
        Assert.Equal("#/$defs/region", schema.GetProperty("properties").GetProperty("selection").GetProperty("$ref").GetString());
        Assert.Equal("#/$defs/region", schema.GetProperty("properties").GetProperty("replacement").GetProperty("$ref").GetString());
        Assert.Single(schema.GetProperty("$defs").EnumerateObject());

        AssertValidation<AgasSelectionSetInput>(
            "{\"op\":\"setRegions\",\"sceneId\":\"scene-001\",\"selection\":{\"column\":0,\"width\":1,\"height\":1}}",
            AgasMcpContractValidationCodes.Required,
            "$.selection.row");
        AssertValidation<AgasSelectionSetInput>(
            "{\"op\":\"setRegions\",\"sceneId\":\"scene-001\",\"selection\":null}",
            AgasMcpContractValidationCodes.Type,
            "$.selection");
        AssertValidation<AgasSelectionSetInput>(
            "{\"op\":\"setRegions\",\"sceneId\":\"scene-001\",\"selection\":{\"Column\":0,\"row\":0,\"width\":1,\"height\":1}}",
            AgasMcpContractValidationCodes.AdditionalProperties,
            "$.selection.Column");
        AssertValidation<AgasSelectionSetInput>(
            "{\"op\":\"setRegions\",\"sceneId\":\"scene-001\",\"selection\":{\"column\":0,\"row\":0,\"width\":0,\"height\":1}}",
            AgasMcpContractValidationCodes.Minimum,
            "$.selection.width");
    }

    private static T Bind<T>(string json) where T : class
    {
        using JsonDocument document = JsonDocument.Parse(json);
        AgasMcpContractBindResult<T> result = AgasMcpContractRegistry.BindElement<T>(document.RootElement.Clone());
        Assert.True(result.Success, result.Error?.ToString());
        return result.Value!;
    }

    private static void AssertValidation<T>(string json, string code, string path) where T : class
    {
        using JsonDocument document = JsonDocument.Parse(json);
        AgasMcpContractBindResult<T> result = AgasMcpContractRegistry.BindElement<T>(document.RootElement.Clone());
        Assert.False(result.Success);
        Assert.Equal(code, result.Error?.Code);
        Assert.Equal(path, result.Error?.Path);
    }

    private static void AssertEquivalent(
        JsonElement expected,
        JsonElement actual,
        string path,
        string schemaName)
    {
        if (expected.ValueKind == JsonValueKind.Number && actual.ValueKind == JsonValueKind.Number)
        {
            Assert.True(
                expected.TryGetDecimal(out decimal expectedNumber)
                && actual.TryGetDecimal(out decimal actualNumber)
                && expectedNumber == actualNumber,
                $"{schemaName} number mismatch at {path}: {expected.GetRawText()} != {actual.GetRawText()}");
            return;
        }

        Assert.True(expected.ValueKind == actual.ValueKind,
            $"{schemaName} kind mismatch at {path}: {expected.ValueKind} != {actual.ValueKind}");
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                Dictionary<string, JsonElement> expectedProperties = expected.EnumerateObject()
                    .ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);
                Dictionary<string, JsonElement> actualProperties = actual.EnumerateObject()
                    .ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);
                Assert.Equal(expectedProperties.Keys.Order(StringComparer.Ordinal), actualProperties.Keys.Order(StringComparer.Ordinal));
                foreach ((string name, JsonElement value) in expectedProperties)
                {
                    AssertEquivalent(value, actualProperties[name], path + "." + name, schemaName);
                }
                break;
            case JsonValueKind.Array:
                JsonElement[] expectedItems = expected.EnumerateArray().ToArray();
                JsonElement[] actualItems = actual.EnumerateArray().ToArray();
                Assert.Equal(expectedItems.Length, actualItems.Length);
                for (int index = 0; index < expectedItems.Length; index++)
                {
                    AssertEquivalent(expectedItems[index], actualItems[index], $"{path}[{index}]", schemaName);
                }
                break;
            case JsonValueKind.String:
                Assert.Equal(expected.GetString(), actual.GetString());
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                Assert.Equal(expected.GetBoolean(), actual.GetBoolean());
                break;
            case JsonValueKind.Null:
                break;
            default:
                Assert.Equal(expected.GetRawText(), actual.GetRawText());
                break;
        }
    }
}
