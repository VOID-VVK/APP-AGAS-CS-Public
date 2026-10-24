using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp;

public sealed class AgasMcpToolContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string SafeIdentifierPattern = "^[A-Za-z0-9_-]+$";

    [Fact]
    public void ToolNames_AreExactlyTheCapabilityExpansionNamesInOrder()
    {
        string[] expected =
        {
            "agas.projects.recent",
            "agas.project.open",
            "agas.project.create",
            "agas.project.close",
            "agas.project.snapshot",
            "agas.assets.list",
            "agas.imports.list",
            "agas.assets.import",
            "agas.scene.get",
            "agas.scene.mutate",
            "agas.scene.package.import",
            "agas.scene.package.export",
            "agas.grid.mutate",
            "agas.layer.mutate",
            "agas.layer.mergeVisible",
            "agas.image.mutate",
            "agas.prompt.mutate",
            "agas.view.set",
            "agas.project.save",
            "agas.selection.set",
            "agas.selection.get",
            "agas.ai.profiles.list",
            "agas.ai.models.list",
            "agas.ai.profile.activate",
            "agas.ai.model.set",
            "agas.ai.diagnostics",
            "agas.ai.generate",
            "agas.ai.status",
            "agas.ai.cancel",
            "agas.ai.apply",
        };

        Assert.Equal(expected, AgasMcpToolNames.All);
        Assert.Equal(30, CreateTools().Count);
        Assert.Equal(expected, CreateTools().Select(tool => tool.ProtocolTool.Name).ToArray());
    }

    [Fact]
    public void Tools_DeclareParseableInputAndOutputSchemas()
    {
        foreach (McpServerTool tool in CreateTools())
        {
            Tool protocol = tool.ProtocolTool;
            Assert.False(string.IsNullOrWhiteSpace(protocol.Description));
            Assert.Equal(JsonValueKind.Object, protocol.InputSchema.ValueKind);
            using JsonDocument reparsed = JsonDocument.Parse(protocol.InputSchema.GetRawText());
            Assert.Equal(JsonValueKind.Object, reparsed.RootElement.ValueKind);
            Assert.True(protocol.InputSchema.TryGetProperty("type", out JsonElement type));
            Assert.Equal("object", type.GetString());
            Assert.NotNull(protocol.OutputSchema);
            using JsonDocument output = JsonDocument.Parse(protocol.OutputSchema.Value.GetRawText());
            Assert.Equal("https://json-schema.org/draft/2020-12/schema",
                output.RootElement.GetProperty("$schema").GetString());
            Assert.Equal("object", output.RootElement.GetProperty("type").GetString());
            Assert.True(output.RootElement.GetProperty("oneOf").GetArrayLength() >= 2);
        }
    }

    [Fact]
    public void ProjectSnapshotSchema_RemainsSelectionFree()
    {
        JsonElement properties = AgasMcpToolSchemas.ProjectSnapshot.GetProperty("properties");
        Assert.False(properties.TryGetProperty("selection", out _));
        Assert.False(properties.TryGetProperty("includeSelection", out _));
    }

    [Fact]
    public void MutatingTools_DeclareConditionalSchemasForOperationSpecificRequiredFields()
    {
        var mutatingSchemas = new Dictionary<string, JsonElement>
        {
            [AgasMcpToolNames.SceneMutate] = AgasMcpToolSchemas.SceneMutate,
            [AgasMcpToolNames.LayerMutate] = AgasMcpToolSchemas.LayerMutate,
            [AgasMcpToolNames.ImageMutate] = AgasMcpToolSchemas.ImageMutate,
            [AgasMcpToolNames.PromptMutate] = AgasMcpToolSchemas.PromptMutate,
            [AgasMcpToolNames.SelectionSet] = AgasMcpToolSchemas.SelectionSet,
        };

        foreach ((string toolName, JsonElement schema) in mutatingSchemas)
        {
            Assert.True(schema.TryGetProperty("$schema", out JsonElement schemaUri), toolName);
            Assert.Equal("https://json-schema.org/draft/2020-12/schema", schemaUri.GetString());
            Assert.True(schema.TryGetProperty("properties", out JsonElement properties), toolName);
            Assert.True(properties.TryGetProperty("op", out JsonElement op), toolName);
            Assert.True(op.TryGetProperty("enum", out JsonElement operations), toolName);
            Assert.True(operations.GetArrayLength() > 0, toolName);
            Assert.True(schema.TryGetProperty("allOf", out JsonElement allOf), toolName);
            Assert.Equal(operations.GetArrayLength(), allOf.GetArrayLength());

            foreach (JsonElement condition in allOf.EnumerateArray())
            {
                Assert.True(condition.TryGetProperty("if", out _), toolName);
                Assert.True(condition.TryGetProperty("then", out JsonElement then), toolName);
                Assert.True(then.TryGetProperty("required", out JsonElement required), toolName);
                Assert.True(required.GetArrayLength() > 0, toolName);
            }
        }
    }

    [Fact]
    public void IdentifierSchemas_MatchServerSafeIdentifierContract()
    {
        var schemas = new Dictionary<string, JsonElement>
        {
            [AgasMcpToolNames.SceneMutate] = AgasMcpToolSchemas.SceneMutate,
            [AgasMcpToolNames.LayerMutate] = AgasMcpToolSchemas.LayerMutate,
            [AgasMcpToolNames.ImageMutate] = AgasMcpToolSchemas.ImageMutate,
            [AgasMcpToolNames.PromptMutate] = AgasMcpToolSchemas.PromptMutate,
            [AgasMcpToolNames.ViewSet] = AgasMcpToolSchemas.ViewSet,
            [AgasMcpToolNames.SelectionSet] = AgasMcpToolSchemas.SelectionSet,
        };

        foreach ((string toolName, JsonElement schema) in schemas)
        {
            JsonElement properties = schema.GetProperty("properties");
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                if (!property.Name.EndsWith("Id", StringComparison.Ordinal))
                {
                    continue;
                }

                Assert.Equal("string", property.Value.GetProperty("type").GetString());
                Assert.Equal(1, property.Value.GetProperty("minLength").GetInt32());
                Assert.Equal(120, property.Value.GetProperty("maxLength").GetInt32());
                Assert.Equal(SafeIdentifierPattern, property.Value.GetProperty("pattern").GetString());
                Assert.False(AgasMcpPathValidator.IsSafeIdentifier("has.dot"), $"{toolName}:{property.Name}");
                Assert.True(AgasMcpPathValidator.IsSafeIdentifier("AZaz09_-"), $"{toolName}:{property.Name}");
            }
        }
    }

    [Fact]
    public void ToolResult_StructuredContentMatchesTextFallbackForSuccess()
    {
        var response = AgasMcpToolResponse.Success(saved: true, dirty: false, data: new { answer = 42 });

        CallToolResult result = ConvertToCallToolResult(response);

        Assert.False(result.IsError);
        Assert.NotNull(result.StructuredContent);
        TextContentBlock text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        using JsonDocument textJson = JsonDocument.Parse(text.Text);
        Assert.True(JsonElementDeepEquals(result.StructuredContent.Value, textJson.RootElement));
        Assert.True(result.StructuredContent.Value.GetProperty("ok").GetBoolean());
        Assert.Equal(42, result.StructuredContent.Value.GetProperty("data").GetProperty("answer").GetInt32());
    }

    [Fact]
    public void ToolResult_BusinessFailureSetsIsErrorAndKeepsStructuredTextFallbackInSync()
    {
        var response = AgasMcpToolResponse.Failure(
            saved: false,
            dirty: true,
            code: AgasMcpErrorCodes.InvalidOperation,
            message: "bad input",
            suggestion: "fix input");

        CallToolResult result = ConvertToCallToolResult(response);

        Assert.True(result.IsError);
        Assert.NotNull(result.StructuredContent);
        TextContentBlock text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        using JsonDocument textJson = JsonDocument.Parse(text.Text);
        Assert.True(JsonElementDeepEquals(result.StructuredContent.Value, textJson.RootElement));
        Assert.False(result.StructuredContent.Value.GetProperty("ok").GetBoolean());
        Assert.Equal(AgasMcpErrorCodes.InvalidOperation, result.StructuredContent.Value.GetProperty("error").GetProperty("code").GetString());
    }

    private static IReadOnlyList<McpServerTool> CreateTools()
    {
        var runtime = new AgasMcpToolRuntime(
            new StubToolExecutor(AgasMcpToolResponse.Success(true, false, new { })),
            null!,
            (AgasMcpProjectFacade?)null);
        return AgasMcpToolCatalog.Create(runtime);
    }

    private static CallToolResult ConvertToCallToolResult(AgasMcpToolResponse response)
    {
        MethodInfo method = typeof(AgasMcpServerToolBase).GetMethod("ToCallToolResult", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(AgasMcpServerToolBase), "ToCallToolResult");
        return (CallToolResult)method.Invoke(null, new object[] { response })!;
    }

    private static bool JsonElementDeepEquals(JsonElement left, JsonElement right) =>
        JsonSerializer.Serialize(left, JsonOptions) == JsonSerializer.Serialize(right, JsonOptions);

    private sealed class StubToolExecutor : IAgasMcpToolExecutor
    {
        private readonly AgasMcpToolResponse _response;

        public StubToolExecutor(AgasMcpToolResponse response)
        {
            _response = response;
        }

        public Task<T> ExecuteAsync<T>(string? sessionId, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default) =>
            Task.FromResult((T)(object)_response);

        public Task ExecuteAsync(string? sessionId, Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void Stop()
        {
        }
    }
}
