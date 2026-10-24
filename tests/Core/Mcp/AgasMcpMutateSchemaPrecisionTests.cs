using System.Text.Json;
using System.Text.RegularExpressions;
using AppAgasCs.Mcp.Contracts;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp;

public sealed class AgasMcpMutateSchemaPrecisionTests
{
    [Fact]
    public void MutateSchemas_DeclareExactPerOperationConditionalContracts()
    {
        AssertSchema(
            AgasMcpToolNames.SceneMutate,
            AgasMcpToolSchemas.SceneMutate,
            topRequired: new[] { "op" },
            new[]
            {
                Op("add", Required("name")),
                Op("rename", Required("sceneId", "name")),
                Op("delete", Required("sceneId")),
                Op("setActive", Required("sceneId")),
                Op("setReadme", Required("sceneId", "readme")),
            });

        AssertSchema(
            AgasMcpToolNames.LayerMutate,
            AgasMcpToolSchemas.LayerMutate,
            topRequired: new[] { "op", "sceneId" },
            new[]
            {
                Op("add", Required("name")),
                Op("rename", Required("layerId", "name")),
                Op("delete", Required("layerId")),
                Op("move", Required("layerId", "direction")),
                Op("setVisible", Required("layerId", "visible")),
                Op("setLocked", Required("layerId", "locked")),
                Op("setShared", Required("layerId", "shared")),
                Op("setCurrent", Required("layerId")),
            });

        AssertSchema(
            AgasMcpToolNames.ImageMutate,
            AgasMcpToolSchemas.ImageMutate,
            topRequired: new[] { "op", "sceneId", "layerId" },
            new[]
            {
                Op("add", Required("imagePath")),
                Op("delete", Required("imageId")),
                Op("move", Required("imageId"), AnyOf("x", "y")),
                Op("update", Required("imageId"), AnyOf("imagePath", "x", "y", "scaleX", "scaleY", "rotationDegrees", "opacity")),
            });

        AssertSchema(
            AgasMcpToolNames.PromptMutate,
            AgasMcpToolSchemas.PromptMutate,
            topRequired: new[] { "op", "sceneId", "layerId" },
            new[]
            {
                Op("add", Required("text")),
                Op("delete", Required("fragmentId")),
                Op("update", Required("fragmentId"), AnyOf("text", "role")),
                Op("move", Required("fragmentId", "direction")),
                Op("setEnabled", Required("fragmentId", "enabled")),
            });
    }

    [Fact]
    public void MinimalValidator_AcceptsAndRejectsRepresentativeMutateSamplesFromSchemaOnly()
    {
        var cases = new[]
        {
            Valid(AgasMcpToolNames.SceneMutate, AgasMcpToolSchemas.SceneMutate, """{"op":"add","name":"Scene"}"""),
            Invalid(AgasMcpToolNames.SceneMutate, AgasMcpToolSchemas.SceneMutate, """{"op":"add"}"""),
            Valid(AgasMcpToolNames.SceneMutate, AgasMcpToolSchemas.SceneMutate, """{"op":"rename","sceneId":"scene_1","name":"Renamed"}"""),
            Invalid(AgasMcpToolNames.SceneMutate, AgasMcpToolSchemas.SceneMutate, """{"op":"rename","sceneId":"scene_1"}"""),
            Valid(AgasMcpToolNames.SceneMutate, AgasMcpToolSchemas.SceneMutate, """{"op":"delete","sceneId":"scene_1"}"""),
            Invalid(AgasMcpToolNames.SceneMutate, AgasMcpToolSchemas.SceneMutate, """{"op":"delete","extra":true}"""),
            Valid(AgasMcpToolNames.SceneMutate, AgasMcpToolSchemas.SceneMutate, """{"op":"setActive","sceneId":"scene_1"}"""),
            Invalid(AgasMcpToolNames.SceneMutate, AgasMcpToolSchemas.SceneMutate, """{"op":"setActive"}"""),
            Valid(AgasMcpToolNames.SceneMutate, AgasMcpToolSchemas.SceneMutate, """{"op":"setReadme","sceneId":"scene_1","readme":"step 1"}"""),
            Invalid(AgasMcpToolNames.SceneMutate, AgasMcpToolSchemas.SceneMutate, """{"op":"setReadme","sceneId":"scene_1"}"""),

            Valid(AgasMcpToolNames.LayerMutate, AgasMcpToolSchemas.LayerMutate, """{"op":"add","sceneId":"scene_1","name":"Layer"}"""),
            Invalid(AgasMcpToolNames.LayerMutate, AgasMcpToolSchemas.LayerMutate, """{"op":"add","name":"Layer"}"""),
            Valid(AgasMcpToolNames.LayerMutate, AgasMcpToolSchemas.LayerMutate, """{"op":"rename","sceneId":"scene_1","layerId":"layer_1","name":"Renamed"}"""),
            Invalid(AgasMcpToolNames.LayerMutate, AgasMcpToolSchemas.LayerMutate, """{"op":"rename","sceneId":"scene_1","layerId":"layer_1"}"""),
            Valid(AgasMcpToolNames.LayerMutate, AgasMcpToolSchemas.LayerMutate, """{"op":"delete","sceneId":"scene_1","layerId":"layer_1"}"""),
            Invalid(AgasMcpToolNames.LayerMutate, AgasMcpToolSchemas.LayerMutate, """{"op":"delete","sceneId":"scene_1"}"""),
            Valid(AgasMcpToolNames.LayerMutate, AgasMcpToolSchemas.LayerMutate, """{"op":"move","sceneId":"scene_1","layerId":"layer_1","direction":1}"""),
            Invalid(AgasMcpToolNames.LayerMutate, AgasMcpToolSchemas.LayerMutate, """{"op":"move","sceneId":"scene_1","layerId":"layer_1"}"""),
            Valid(AgasMcpToolNames.LayerMutate, AgasMcpToolSchemas.LayerMutate, """{"op":"setVisible","sceneId":"scene_1","layerId":"layer_1","visible":false}"""),
            Invalid(AgasMcpToolNames.LayerMutate, AgasMcpToolSchemas.LayerMutate, """{"op":"setVisible","sceneId":"scene_1","layerId":"layer_1"}"""),
            Valid(AgasMcpToolNames.LayerMutate, AgasMcpToolSchemas.LayerMutate, """{"op":"setLocked","sceneId":"scene_1","layerId":"layer_1","locked":true}"""),
            Invalid(AgasMcpToolNames.LayerMutate, AgasMcpToolSchemas.LayerMutate, """{"op":"setLocked","sceneId":"scene_1","layerId":"layer_1"}"""),
            Valid(AgasMcpToolNames.LayerMutate, AgasMcpToolSchemas.LayerMutate, """{"op":"setShared","sceneId":"scene_1","layerId":"layer_1","shared":true}"""),
            Invalid(AgasMcpToolNames.LayerMutate, AgasMcpToolSchemas.LayerMutate, """{"op":"setShared","sceneId":"scene_1","layerId":"layer_1"}"""),
            Valid(AgasMcpToolNames.LayerMutate, AgasMcpToolSchemas.LayerMutate, """{"op":"setCurrent","sceneId":"scene_1","layerId":"layer_1"}"""),
            Invalid(AgasMcpToolNames.LayerMutate, AgasMcpToolSchemas.LayerMutate, """{"op":"setCurrent","sceneId":"scene_1","unknown":1}"""),

            Valid(AgasMcpToolNames.ImageMutate, AgasMcpToolSchemas.ImageMutate, """{"op":"add","sceneId":"scene_1","layerId":"layer_1","imagePath":"assets/a.png"}"""),
            Invalid(AgasMcpToolNames.ImageMutate, AgasMcpToolSchemas.ImageMutate, """{"op":"add","sceneId":"scene_1","layerId":"layer_1"}"""),
            Valid(AgasMcpToolNames.ImageMutate, AgasMcpToolSchemas.ImageMutate, """{"op":"delete","sceneId":"scene_1","layerId":"layer_1","imageId":"image_1"}"""),
            Invalid(AgasMcpToolNames.ImageMutate, AgasMcpToolSchemas.ImageMutate, """{"op":"delete","sceneId":"scene_1","layerId":"layer_1"}"""),
            Valid(AgasMcpToolNames.ImageMutate, AgasMcpToolSchemas.ImageMutate, """{"op":"move","sceneId":"scene_1","layerId":"layer_1","imageId":"image_1","x":10}"""),
            Invalid(AgasMcpToolNames.ImageMutate, AgasMcpToolSchemas.ImageMutate, """{"op":"move","sceneId":"scene_1","layerId":"layer_1","imageId":"image_1"}"""),
            Valid(AgasMcpToolNames.ImageMutate, AgasMcpToolSchemas.ImageMutate, """{"op":"update","sceneId":"scene_1","layerId":"layer_1","imageId":"image_1","opacity":0.5}"""),
            Invalid(AgasMcpToolNames.ImageMutate, AgasMcpToolSchemas.ImageMutate, """{"op":"update","sceneId":"scene_1","layerId":"layer_1","imageId":"image_1"}"""),

            Valid(AgasMcpToolNames.PromptMutate, AgasMcpToolSchemas.PromptMutate, """{"op":"add","sceneId":"scene_1","layerId":"layer_1","text":""}"""),
            Invalid(AgasMcpToolNames.PromptMutate, AgasMcpToolSchemas.PromptMutate, """{"op":"add","sceneId":"scene_1","layerId":"layer_1"}"""),
            Valid(AgasMcpToolNames.PromptMutate, AgasMcpToolSchemas.PromptMutate, """{"op":"delete","sceneId":"scene_1","layerId":"layer_1","fragmentId":"prompt_1"}"""),
            Invalid(AgasMcpToolNames.PromptMutate, AgasMcpToolSchemas.PromptMutate, """{"op":"delete","sceneId":"scene_1","layerId":"layer_1"}"""),
            Valid(AgasMcpToolNames.PromptMutate, AgasMcpToolSchemas.PromptMutate, """{"op":"update","sceneId":"scene_1","layerId":"layer_1","fragmentId":"prompt_1","text":"new"}"""),
            Valid(AgasMcpToolNames.PromptMutate, AgasMcpToolSchemas.PromptMutate, """{"op":"update","sceneId":"scene_1","layerId":"layer_1","fragmentId":"prompt_1","role":"environment"}"""),
            Invalid(AgasMcpToolNames.PromptMutate, AgasMcpToolSchemas.PromptMutate, """{"op":"update","sceneId":"scene_1","layerId":"layer_1","fragmentId":"prompt_1"}"""),
            Valid(AgasMcpToolNames.PromptMutate, AgasMcpToolSchemas.PromptMutate, """{"op":"move","sceneId":"scene_1","layerId":"layer_1","fragmentId":"prompt_1","direction":-1}"""),
            Invalid(AgasMcpToolNames.PromptMutate, AgasMcpToolSchemas.PromptMutate, """{"op":"move","sceneId":"scene_1","layerId":"layer_1","fragmentId":"prompt_1"}"""),
            Valid(AgasMcpToolNames.PromptMutate, AgasMcpToolSchemas.PromptMutate, """{"op":"setEnabled","sceneId":"scene_1","layerId":"layer_1","fragmentId":"prompt_1","enabled":true}"""),
            Invalid(AgasMcpToolNames.PromptMutate, AgasMcpToolSchemas.PromptMutate, """{"op":"setEnabled","sceneId":"scene_1","layerId":"layer_1","fragmentId":"prompt_1"}"""),
        };

        foreach (SchemaSample sample in cases)
        {
            using JsonDocument document = JsonDocument.Parse(sample.Json);

            bool valid = MinimalJsonSchemaValidator.IsValid(sample.Schema, document.RootElement, out string error);

            Assert.Equal(sample.ExpectedValid, valid);
            if (sample.ExpectedValid)
            {
                Assert.True(valid, $"{sample.ToolName}: {sample.Json}: {error}");
            }
            else
            {
                Assert.False(valid, $"{sample.ToolName}: {sample.Json}");
            }
        }
    }

    private static void AssertSchema(string toolName, JsonElement schema, IReadOnlyList<string> topRequired, IReadOnlyList<OpContract> ops)
    {
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.GetProperty("$schema").GetString());
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(topRequired, RequiredNames(schema.GetProperty("required")));

        JsonElement opEnum = schema.GetProperty("properties").GetProperty("op").GetProperty("enum");
        Assert.Equal(ops.Select(item => item.Op).ToArray(), opEnum.EnumerateArray().Select(item => item.GetString()).ToArray());
        JsonElement allOf = schema.GetProperty("allOf");
        Assert.Equal(ops.Count, allOf.GetArrayLength());

        foreach (OpContract expected in ops)
        {
            JsonElement condition = allOf.EnumerateArray().Single(item =>
                item.GetProperty("if").GetProperty("properties").GetProperty("op").GetProperty("const").GetString() == expected.Op);
            Assert.Equal(new[] { "if", "then" }, condition.EnumerateObject().Select(item => item.Name).ToArray());

            JsonElement ifSchema = condition.GetProperty("if");
            Assert.Equal(new[] { "properties", "required" }, ifSchema.EnumerateObject().Select(item => item.Name).ToArray());
            JsonElement ifProperties = ifSchema.GetProperty("properties");
            Assert.Equal(new[] { "op" }, ifProperties.EnumerateObject().Select(item => item.Name).ToArray());
            JsonElement opConstSchema = ifProperties.GetProperty("op");
            Assert.Equal(new[] { "const" }, opConstSchema.EnumerateObject().Select(item => item.Name).ToArray());
            Assert.Equal(expected.Op, opConstSchema.GetProperty("const").GetString());
            Assert.Equal(new[] { "op" }, RequiredNames(ifSchema.GetProperty("required")));

            JsonElement thenSchema = condition.GetProperty("then");
            string[] expectedThenKeys = expected.AnyOf.Count == 0 ? new[] { "required" } : new[] { "required", "anyOf" };
            Assert.Equal(expectedThenKeys, thenSchema.EnumerateObject().Select(item => item.Name).ToArray());
            Assert.Equal(expected.Required, RequiredNames(thenSchema.GetProperty("required")));

            if (expected.AnyOf.Count == 0)
            {
                Assert.False(thenSchema.TryGetProperty("anyOf", out _), $"{toolName}:{expected.Op}");
            }
            else
            {
                JsonElement anyOf = thenSchema.GetProperty("anyOf");
                Assert.Equal(expected.AnyOf, anyOf.EnumerateArray().Select(item => RequiredNames(item.GetProperty("required"))).ToArray());
            }
        }
    }

    private static IReadOnlyList<string> Required(params string[] names) => names;
    private static IReadOnlyList<IReadOnlyList<string>> AnyOf(params string[] names) => names.Select(name => (IReadOnlyList<string>)new[] { name }).ToArray();
    private static OpContract Op(string op, IReadOnlyList<string> required) => new(op, required, Array.Empty<IReadOnlyList<string>>());
    private static OpContract Op(string op, IReadOnlyList<string> required, IReadOnlyList<IReadOnlyList<string>> anyOf) => new(op, required, anyOf);
    private static IReadOnlyList<string> RequiredNames(JsonElement required) => required.EnumerateArray().Select(item => item.GetString()!).ToArray();
    private static SchemaSample Valid(string toolName, JsonElement schema, string json) => new(toolName, schema, json, true);
    private static SchemaSample Invalid(string toolName, JsonElement schema, string json) => new(toolName, schema, json, false);

    private sealed record OpContract(string Op, IReadOnlyList<string> Required, IReadOnlyList<IReadOnlyList<string>> AnyOf);
    private sealed record SchemaSample(string ToolName, JsonElement Schema, string Json, bool ExpectedValid);

    private static class MinimalJsonSchemaValidator
    {
        public static bool IsValid(JsonElement schema, JsonElement instance, out string error)
        {
            List<string> errors = new();
            Validate(schema, instance, "$", errors);
            error = string.Join("; ", errors);
            return errors.Count == 0;
        }

        private static void Validate(JsonElement schema, JsonElement instance, string path, List<string> errors)
        {
            if (schema.TryGetProperty("type", out JsonElement type))
            {
                ValidateType(type.GetString(), instance, path, errors);
            }

            if (schema.TryGetProperty("enum", out JsonElement enumValues)
                && !enumValues.EnumerateArray().Any(item => JsonElementDeepEquals(item, instance)))
            {
                errors.Add($"{path} is not one of enum values");
            }

            if (schema.TryGetProperty("const", out JsonElement constValue)
                && !JsonElementDeepEquals(constValue, instance))
            {
                errors.Add($"{path} does not equal const");
            }

            if (instance.ValueKind == JsonValueKind.Object)
            {
                ValidateObject(schema, instance, path, errors);
            }

            if (instance.ValueKind == JsonValueKind.String)
            {
                ValidateString(schema, instance, path, errors);
            }

            if (instance.ValueKind == JsonValueKind.Number)
            {
                ValidateNumber(schema, instance, path, errors);
            }

            if (schema.TryGetProperty("allOf", out JsonElement allOf))
            {
                foreach (JsonElement item in allOf.EnumerateArray())
                {
                    Validate(item, instance, path, errors);
                }
            }

            if (schema.TryGetProperty("if", out JsonElement ifSchema)
                && IsValid(ifSchema, instance, out _)
                && schema.TryGetProperty("then", out JsonElement thenSchema))
            {
                Validate(thenSchema, instance, path, errors);
            }

            if (schema.TryGetProperty("anyOf", out JsonElement anyOf)
                && !anyOf.EnumerateArray().Any(item => IsValid(item, instance, out _)))
            {
                errors.Add($"{path} does not match anyOf");
            }
        }

        private static void ValidateObject(JsonElement schema, JsonElement instance, string path, List<string> errors)
        {
            if (schema.TryGetProperty("required", out JsonElement required))
            {
                foreach (JsonElement name in required.EnumerateArray())
                {
                    string propertyName = name.GetString()!;
                    if (!instance.TryGetProperty(propertyName, out _))
                    {
                        errors.Add($"{path}.{propertyName} is required");
                    }
                }
            }

            JsonElement properties = default;
            bool hasProperties = schema.TryGetProperty("properties", out properties);
            if (hasProperties)
            {
                foreach (JsonProperty property in instance.EnumerateObject())
                {
                    if (properties.TryGetProperty(property.Name, out JsonElement propertySchema))
                    {
                        Validate(propertySchema, property.Value, $"{path}.{property.Name}", errors);
                    }
                }
            }

            if (schema.TryGetProperty("additionalProperties", out JsonElement additionalProperties)
                && additionalProperties.ValueKind == JsonValueKind.False
                && hasProperties)
            {
                HashSet<string> allowed = properties.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
                foreach (JsonProperty property in instance.EnumerateObject())
                {
                    if (!allowed.Contains(property.Name))
                    {
                        errors.Add($"{path}.{property.Name} is not allowed");
                    }
                }
            }
        }

        private static void ValidateString(JsonElement schema, JsonElement instance, string path, List<string> errors)
        {
            string value = instance.GetString() ?? string.Empty;
            if (schema.TryGetProperty("minLength", out JsonElement minLength)
                && value.Length < minLength.GetInt32())
            {
                errors.Add($"{path} is shorter than minLength");
            }

            if (schema.TryGetProperty("maxLength", out JsonElement maxLength)
                && value.Length > maxLength.GetInt32())
            {
                errors.Add($"{path} is longer than maxLength");
            }

            if (schema.TryGetProperty("pattern", out JsonElement pattern)
                && !Regex.IsMatch(value, pattern.GetString()!, RegexOptions.CultureInvariant))
            {
                errors.Add($"{path} does not match pattern");
            }
        }

        private static void ValidateNumber(JsonElement schema, JsonElement instance, string path, List<string> errors)
        {
            double value = instance.GetDouble();
            if (schema.TryGetProperty("minimum", out JsonElement minimum)
                && value < minimum.GetDouble())
            {
                errors.Add($"{path} is less than minimum");
            }

            if (schema.TryGetProperty("maximum", out JsonElement maximum)
                && value > maximum.GetDouble())
            {
                errors.Add($"{path} is greater than maximum");
            }
        }

        private static void ValidateType(string? type, JsonElement instance, string path, List<string> errors)
        {
            bool ok = type switch
            {
                "object" => instance.ValueKind == JsonValueKind.Object,
                "string" => instance.ValueKind == JsonValueKind.String,
                "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "number" => instance.ValueKind == JsonValueKind.Number,
                "integer" => instance.ValueKind == JsonValueKind.Number && instance.TryGetInt64(out _),
                _ => true,
            };
            if (!ok)
            {
                errors.Add($"{path} is not type {type}");
            }
        }

        private static bool JsonElementDeepEquals(JsonElement left, JsonElement right) =>
            left.ValueKind == right.ValueKind && left.GetRawText() == right.GetRawText();
    }
}
