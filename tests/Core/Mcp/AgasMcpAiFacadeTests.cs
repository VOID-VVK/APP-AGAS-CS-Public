using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AppAgasCs.Application;
using AppAgasCs.Application.Contracts;
using AppAgasCs.Core.Resources;
using AppAgasCs.Features.Ai.Generation;
using AppAgasCs.Features.Ai.Workspace;
using AppAgasCs.Mcp;
using AppAgasCs.Mcp.Contracts;
using Godot;
using Xunit;

namespace AppAgasCs.Tests.Core.Mcp;

public sealed class AgasMcpAiFacadeTests
{
    private const string InvalidToolArgumentsMessage = "Tool arguments did not match the expected JSON shape.";

    public static IEnumerable<object[]> AiToolArguments()
    {
        yield return new object[] { AgasMcpToolNames.AiGenerate, Arguments("{\"sceneId\":\"scene-001\",\"layerId\":\"layer-001\"}") };
        yield return new object[] { AgasMcpToolNames.AiStatus, Arguments("{}") };
        yield return new object[] { AgasMcpToolNames.AiCancel, Arguments("{\"jobId\":\"aijob-00000000000000000000000000000000\"}") };
        yield return new object[] { AgasMcpToolNames.AiApply, Arguments("{\"jobId\":\"aijob-00000000000000000000000000000000\"}") };
    }

    public static IEnumerable<object[]> AiToolCaseAliasArguments()
    {
        yield return new object[] { AgasMcpToolNames.AiGenerate, Arguments("{\"sceneId\":\"scene-001\",\"layerId\":\"layer-001\",\"Prompt\":\"tree\"}") };
        yield return new object[] { AgasMcpToolNames.AiStatus, Arguments("{\"Limit\":1}") };
        yield return new object[] { AgasMcpToolNames.AiCancel, Arguments("{\"JobId\":\"aijob-00000000000000000000000000000000\"}") };
        yield return new object[] { AgasMcpToolNames.AiApply, Arguments("{\"JobId\":\"aijob-00000000000000000000000000000000\"}") };
    }

    [Fact]
    public void Generate_WithoutOpenProject_ReturnsNoOpenProject()
    {
        using var fixture = new Fixture();

        AgasMcpToolResponse response = fixture.Facade.Generate(
            new AgasAiGenerateInput("scene-001", "layer-001", "tree"));

        Assert.False(response.Ok);
        Assert.Equal(AgasMcpErrorCodes.NoOpenProject, response.Error?.Code);
    }

    [Theory]
    [InlineData(null, AiGenerationQuality.Medium)]
    [InlineData("low", AiGenerationQuality.Low)]
    public void Generate_GrokImagineImage20_FreezesDefaultOrExplicitQualityIntoRunnerRequest(
        string? quality,
        AiGenerationQuality expected)
    {
        using var fixture = new GenerationFixture(AiGenerationQualityPolicy.GrokImagineImage20ModelName);

        AgasMcpToolResponse response = fixture.Facade.Generate(new AgasAiGenerateInput(
            "scene-001",
            "layer-001",
            "tree",
            Quality: quality));

        Assert.True(response.Ok);
        Assert.True(fixture.Runner.WaitForRequest());
        Assert.Equal(expected, fixture.Runner.LastRequest?.Quality);
    }

    [Fact]
    public void Generate_NonGrokImagineImage20_RejectsExplicitQualityBeforeRunner()
    {
        using var fixture = new GenerationFixture("gpt-image-2");

        AgasMcpToolResponse response = fixture.Facade.Generate(new AgasAiGenerateInput(
            "scene-001",
            "layer-001",
            "tree",
            Quality: "low"));

        Assert.False(response.Ok);
        Assert.Equal(AgasMcpErrorCodes.InvalidOperation, response.Error?.Code);
        Assert.False(fixture.Runner.WasCalled);
    }

    [Fact]
    public void Generate_TextOnlyModelWithW_ReturnsUnsupportedOperationBeforeRunner()
    {
        using var fixture = new GenerationFixture(
            AiGenerationQualityPolicy.GrokImagineImage20ModelName,
            hasReplacementRegion: true,
            declaredCapabilities: null);

        AgasMcpToolResponse response = fixture.Facade.Generate(new AgasAiGenerateInput(
            "scene-001",
            "layer-001",
            "tree"));

        Assert.False(response.Ok);
        Assert.Equal(AgasMcpErrorCodes.AiUnsupportedOperation, response.Error?.Code);
        Assert.False(fixture.Runner.WasCalled);
    }

    [Fact]
    public void StatusAndApply_WithoutOpenProject_ReturnSafeNoOpenProject()
    {
        using var fixture = new Fixture();
        const string missing = "aijob-00000000000000000000000000000000";

        AgasMcpToolResponse status = fixture.Facade.Status(new AgasAiStatusInput(missing));
        AgasMcpToolResponse apply = fixture.Facade.Apply(new AgasAiApplyInput(missing));

        Assert.Equal(AgasMcpErrorCodes.NoOpenProject, status.Error?.Code);
        Assert.Equal(AgasMcpErrorCodes.NoOpenProject, apply.Error?.Code);
        Assert.DoesNotContain("/", status.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectTypedAiHandlers_RejectInvalidAnnotatedInputsBeforeProjectLookup()
    {
        using var fixture = new Fixture();

        AgasMcpToolResponse[] responses =
        {
            fixture.Facade.Generate(new AgasAiGenerateInput("scene-001", "layer-001", Target: "Force")),
            fixture.Facade.Generate(new AgasAiGenerateInput("scene-001", "layer-001", LayerScope: "AllLayers")),
            fixture.Facade.Generate(new AgasAiGenerateInput("scene-001", "layer-001", LayerScope: "1")),
            fixture.Facade.Status(new AgasAiStatusInput(Limit: 0)),
            fixture.Facade.Cancel(new AgasAiCancelInput("bad-job")),
            fixture.Facade.Apply(new AgasAiApplyInput("bad-job")),
        };

        Assert.All(responses, response =>
        {
            Assert.False(response.Ok);
            Assert.Equal(AgasMcpErrorCodes.InvalidOperation, response.Error?.Code);
            Assert.Equal(InvalidToolArgumentsMessage, response.Error?.Message);
            Assert.False(response.Dirty);
        });
    }

    [Fact]
    public void RunnerFailureMapping_DoesNotExposeProviderSecretOrRawException()
    {
        var exception = new InvalidOperationException("https://secret.example key=sk-private");

        AgasMcpAiJobFailure failure = AgasMcpAiFacade.MapRunnerFailure(exception);

        Assert.Equal(AgasMcpErrorCodes.AiProviderFailed, failure.ErrorCode);
        Assert.DoesNotContain("secret.example", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-private", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AiSchemas_ExcludeOperationAndInlineSelection_AndApplyAcceptsOnlyJobId()
    {
        string[] generateProperties = AgasMcpToolSchemas.AiGenerate
            .GetProperty("properties")
            .EnumerateObject()
            .Select(item => item.Name)
            .ToArray();
        string[] applyProperties = AgasMcpToolSchemas.AiApply
            .GetProperty("properties")
            .EnumerateObject()
            .Select(item => item.Name)
            .ToArray();

        Assert.Equal(new[] { "sceneId", "layerId", "prompt", "profileId", "target", "layerScope", "quality" }, generateProperties);
        Assert.DoesNotContain("operation", generateProperties);
        Assert.DoesNotContain("selection", generateProperties);
        Assert.DoesNotContain("replacement", generateProperties);
        Assert.DoesNotContain("selectedImageId", generateProperties);
        Assert.Equal(new[] { "jobId" }, applyProperties);
        Assert.False(AgasMcpToolSchemas.AiGenerate.GetProperty("additionalProperties").GetBoolean());
        Assert.False(AgasMcpToolSchemas.AiApply.GetProperty("additionalProperties").GetBoolean());
    }

    [Theory]
    [MemberData(nameof(AiToolArguments))]
    public void AiExecute_RejectsUnknownPropertiesForEveryTool(
        string toolName,
        IReadOnlyDictionary<string, JsonElement> validArguments)
    {
        using var fixture = new Fixture();
        var arguments = new Dictionary<string, JsonElement>(validArguments, StringComparer.Ordinal)
        {
            ["unexpectedField"] = JsonSerializer.SerializeToElement(true),
        };

        AgasMcpToolResponse response = fixture.Facade.Execute(toolName, arguments);

        Assert.False(response.Ok);
        Assert.Equal(AgasMcpErrorCodes.InvalidOperation, response.Error?.Code);
        Assert.Equal("Tool arguments did not match the expected JSON shape.", response.Error?.Message);
    }

    [Theory]
    [MemberData(nameof(AiToolCaseAliasArguments))]
    public void AiExecute_RejectsCaseMismatchedPropertyAliases(
        string toolName,
        IReadOnlyDictionary<string, JsonElement> arguments)
    {
        using var fixture = new Fixture();

        AgasMcpToolResponse response = fixture.Facade.Execute(toolName, arguments);

        Assert.False(response.Ok);
        Assert.Equal(AgasMcpErrorCodes.InvalidOperation, response.Error?.Code);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("selection")]
    [InlineData("replacement")]
    [InlineData("selectedImageId")]
    public void GenerateExecute_RejectsFrozenContractExtraFieldsAtRuntime(string forbiddenField)
    {
        using var fixture = new Fixture();
        Dictionary<string, JsonElement> arguments = Arguments(new Dictionary<string, object?>
        {
            ["sceneId"] = "scene-001",
            ["layerId"] = "layer-001",
            [forbiddenField] = forbiddenField == "operation" ? "TextToImage" : new { column = 0, row = 0, width = 1, height = 1 },
        });

        AgasMcpToolResponse response = fixture.Facade.Execute(AgasMcpToolNames.AiGenerate, arguments);

        Assert.False(response.Ok);
        Assert.Equal(AgasMcpErrorCodes.InvalidOperation, response.Error?.Code);
        Assert.Contains("expected JSON shape", response.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("imageId")]
    [InlineData("x")]
    [InlineData("y")]
    public void ApplyExecute_RejectsFrozenContractExtraFieldsAtRuntime(string forbiddenField)
    {
        using var fixture = new Fixture();
        Dictionary<string, JsonElement> arguments = Arguments(new Dictionary<string, object?>
        {
            ["jobId"] = "aijob-00000000000000000000000000000000",
            [forbiddenField] = forbiddenField is "x" or "y" ? 1 : "forbidden",
        });

        AgasMcpToolResponse response = fixture.Facade.Execute(AgasMcpToolNames.AiApply, arguments);

        Assert.False(response.Ok);
        Assert.Equal(AgasMcpErrorCodes.InvalidOperation, response.Error?.Code);
        Assert.Contains("expected JSON shape", response.Error?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"sceneId\":\"\",\"layerId\":\"layer-001\"}")]
    [InlineData("{\"sceneId\":\"scene.bad\",\"layerId\":\"layer-001\"}")]
    [InlineData("{\"sceneId\":\" scene-001\",\"layerId\":\"layer-001\"}")]
    [InlineData("{\"sceneId\":\"scene-001\",\"layerId\":null}")]
    [InlineData("{\"sceneId\":\"scene-001\",\"layerId\":\"layer.bad\"}")]
    [InlineData("{\"sceneId\":\"scene-001\",\"layerId\":\"layer-001 \"}")]
    [InlineData("{\"sceneId\":\"scene-001\",\"layerId\":\"layer-001\",\"prompt\":\"\"}")]
    [InlineData("{\"sceneId\":\"scene-001\",\"layerId\":\"layer-001\",\"prompt\":\"   \"}")]
    [InlineData("{\"sceneId\":\"scene-001\",\"layerId\":\"layer-001\",\"prompt\":null}")]
    [InlineData("{\"sceneId\":\"scene-001\",\"layerId\":\"layer-001\",\"profileId\":\"profile.bad\"}")]
    [InlineData("{\"sceneId\":\"scene-001\",\"layerId\":\"layer-001\",\"profileId\":\" profile-1 \"}")]
    [InlineData("{\"sceneId\":\"scene-001\",\"layerId\":\"layer-001\",\"profileId\":null}")]
    [InlineData("{\"sceneId\":\"scene-001\",\"layerId\":\"layer-001\",\"target\":\"Force\"}")]
    [InlineData("{\"sceneId\":\"scene-001\",\"layerId\":\"layer-001\",\"target\":null}")]
    public void GenerateExecute_RejectsSchemaValueViolations(string json)
    {
        using var fixture = new Fixture();

        AgasMcpToolResponse response = fixture.Facade.Execute(AgasMcpToolNames.AiGenerate, Arguments(json));

        Assert.False(response.Ok);
        Assert.Equal(AgasMcpErrorCodes.InvalidOperation, response.Error?.Code);
    }

    [Theory]
    [InlineData("{\"jobId\":\"\"}")]
    [InlineData("{\"jobId\":null}")]
    [InlineData("{\"jobId\":\"aijob-0000000000000000000000000000000G\"}")]
    [InlineData("{\"limit\":0}")]
    [InlineData("{\"limit\":51}")]
    [InlineData("{\"limit\":1.5}")]
    [InlineData("{\"limit\":null}")]
    [InlineData("{\"jobId\":\"aijob-00000000000000000000000000000000\",\"limit\":51}")]
    public void StatusExecute_RejectsSchemaValueViolationsEvenWhenJobIdIsPresent(string json)
    {
        using var fixture = new Fixture();

        AgasMcpToolResponse response = fixture.Facade.Execute(AgasMcpToolNames.AiStatus, Arguments(json));

        Assert.False(response.Ok);
        Assert.Equal(AgasMcpErrorCodes.InvalidOperation, response.Error?.Code);
    }

    [Theory]
    [InlineData(AgasMcpToolNames.AiCancel, "{}")]
    [InlineData(AgasMcpToolNames.AiCancel, "{\"jobId\":\"bad\"}")]
    [InlineData(AgasMcpToolNames.AiCancel, "{\"jobId\":null}")]
    [InlineData(AgasMcpToolNames.AiApply, "{}")]
    [InlineData(AgasMcpToolNames.AiApply, "{\"jobId\":\"aijob-0000000000000000000000000000000G\"}")]
    [InlineData(AgasMcpToolNames.AiApply, "{\"jobId\":null}")]
    public void CancelAndApplyExecute_RejectInvalidJobIds(string toolName, string json)
    {
        using var fixture = new Fixture();

        AgasMcpToolResponse response = fixture.Facade.Execute(toolName, Arguments(json));

        Assert.False(response.Ok);
        Assert.Equal(AgasMcpErrorCodes.InvalidOperation, response.Error?.Code);
    }

    [Theory]
    [InlineData("{\"sceneId\":\"scene-001\",\"layerId\":\"layer-001\"}")]
    [InlineData("{\"sceneId\":\"scene-001\",\"layerId\":\"layer-001\",\"prompt\":\"tree\",\"profileId\":\"profile-1\",\"target\":\"ReplaceSelected\"}")]
    public void GenerateExecute_AcceptsSchemaValidValuesBeforeBusinessContextValidation(string json)
    {
        using var fixture = new Fixture();

        AgasMcpToolResponse response = fixture.Facade.Execute(AgasMcpToolNames.AiGenerate, Arguments(json));

        Assert.Equal(AgasMcpErrorCodes.NoOpenProject, response.Error?.Code);
    }

    [Fact]
    public void GenerateExecute_RejectsSchemaMaximumLengths()
    {
        using var fixture = new Fixture();
        string[] fields = { "sceneId", "layerId", "profileId", "prompt" };
        foreach (string field in fields)
        {
            var source = new Dictionary<string, object?>
            {
                ["sceneId"] = "scene-001",
                ["layerId"] = "layer-001",
                [field] = new string('a', field == "prompt" ? 4001 : 121),
            };

            AgasMcpToolResponse response = fixture.Facade.Execute(
                AgasMcpToolNames.AiGenerate,
                Arguments(source));

            Assert.False(response.Ok);
            Assert.Equal(AgasMcpErrorCodes.InvalidOperation, response.Error?.Code);
        }
    }

    private static Dictionary<string, JsonElement> Arguments(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
            .ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
    }

    private static Dictionary<string, JsonElement> Arguments(Dictionary<string, object?> source) =>
        source.ToDictionary(item => item.Key, item => JsonSerializer.SerializeToElement(item.Value), StringComparer.Ordinal);

    private sealed class Fixture : IDisposable
    {
        private readonly AgasAiGenerationCoordinator _coordinator = new();
        private readonly AgasMcpAiJobStore _store = new();
        private readonly AgasMcpAiJobScheduler _scheduler;

        public Fixture()
        {
            _scheduler = new AgasMcpAiJobScheduler(_coordinator, _store, AgasMcpAiFacade.MapRunnerFailure);
            Facade = new AgasMcpAiFacade(
                new AgasMcpProjectContextProvider(),
                new AgasMcpDirtyState(),
                _store,
                _scheduler,
                new NeverRunner());
        }

        public AgasMcpAiFacade Facade { get; }

        public void Dispose()
        {
            _scheduler.Dispose();
            _store.Dispose();
        }
    }

    private sealed class GenerationFixture : IDisposable
    {
        private readonly string _root;
        private readonly AgasMcpAiJobStore _store = new();
        private readonly AgasMcpAiJobScheduler _scheduler;

        public GenerationFixture(
            string modelName,
            bool hasReplacementRegion = false,
            AiGenerationAdapterCapabilities? declaredCapabilities = AiGenerationAdapterCapabilities.TextToImage)
        {
            _root = Path.Combine(Path.GetTempPath(), "agas-mcp-quality", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            var project = (AgasProjectResource)RuntimeHelpers.GetUninitializedObject(typeof(AgasProjectResource));
            project.ProjectId = "project-001";
            project.ActiveSceneId = "scene-001";
            project.ResourceVersion = AgasProjectResource.CurrentResourceVersion;
            var layout = (WorkspaceLayoutResource)RuntimeHelpers.GetUninitializedObject(typeof(WorkspaceLayoutResource));
            var session = new ProjectSession();
            session.Reset(_root);
            session.AcceptProject(project);
            var contexts = new AgasMcpProjectContextProvider();
            contexts.RegisterContext(new AgasMcpProjectContext(
                session,
                layout,
                aiGenerationWorkspace: new GenerationBridge(modelName, hasReplacementRegion, declaredCapabilities)));
            Runner = new CapturingRunner();
            _scheduler = new AgasMcpAiJobScheduler(new AgasAiGenerationCoordinator(), _store, AgasMcpAiFacade.MapRunnerFailure);
            Facade = new AgasMcpAiFacade(contexts, new AgasMcpDirtyState(), _store, _scheduler, Runner);
        }

        public AgasMcpAiFacade Facade { get; }
        public CapturingRunner Runner { get; }

        public void Dispose()
        {
            _scheduler.Dispose();
            _store.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class GenerationBridge : IAgasAiGenerationWorkspaceBridge
    {
        private readonly AiGenerationProviderProfile _profile;
        private readonly AiGenerationCanvasContext _context;
        private readonly CanvasPatchCapture _capture;
        private readonly AiGenerationRequestPreparationService _preparation = new();

        public GenerationBridge(
            string modelName,
            bool hasReplacementRegion,
            AiGenerationAdapterCapabilities? declaredCapabilities)
        {
            _profile = new AiGenerationProviderProfile(
                "profile-001",
                "Shared connection",
                "axoncat-newapi",
                "newapi-compatible",
                modelName,
                "test-key",
                "https://relay.example/v1",
                declaredCapabilities);
            var q = new AiGenerationGridRegion(0, 0, 1, 1);
            AiGenerationGridRegion? w = hasReplacementRegion
                ? new AiGenerationGridRegion(0, 0, 1, 1)
                : null;
            _context = new AiGenerationCanvasContext(
                "/tmp/agas-mcp-quality",
                "project-001",
                "scene-001",
                "layer-001",
                HasSelectedImage: false,
                SelectedImageId: null,
                CanEditCurrentLayer: true,
                GridSizePx: 1,
                RegionMetadata: new AiGenerationRegionMetadata(q, w));
            _capture = new CanvasPatchCapture(
                new CanvasGridRegion(0, 0, 1, 1),
                new CanvasGridRegion(0, 0, 1, 1),
                1,
                new Rect2I(0, 0, 1, 1),
                new Rect2I(0, 0, 1, 1),
                new Rect2I(0, 0, 1, 1),
                Array.Empty<byte>(),
                Array.Empty<byte>(),
                Array.Empty<byte>(),
                "capture",
                null,
                CanvasPatchGenerationMode.FullRegionGeneration);
        }

        public AiGenerationCanvasContext CaptureCurrentContext(CanvasPatchLayerScope scope) =>
            _context with { LayerScope = scope };

        public AiGenerationProfileResolution ResolveProfile(string? profileId = null) =>
            AiGenerationProfileResolution.Ok(_profile);

        public string ResolvePrompt(string? promptOverride = null) => promptOverride ?? "tree";

        public AiGenerationPreparationResult Prepare(AiGenerationPreparationInput input) =>
            _preparation.Prepare(input, _capture);

        public AiGeneratedImageApplyResult Apply(
            AiGeneratedImageApplication application,
            AiGenerationApplyPersistence persistence,
            AiGenerationApplyContextPolicy contextPolicy = AiGenerationApplyContextPolicy.FrozenBoundaryOnly) =>
            AiGeneratedImageApplyResult.Ok("applied");

        public void Discard(AiGeneratedImageApplication application)
        {
        }
    }

    private sealed class CapturingRunner : IAiGenerationRunner
    {
        private readonly ManualResetEventSlim _called = new(initialState: false);

        public AiGenerationRequest? LastRequest { get; private set; }
        public bool WasCalled => _called.IsSet;

        public bool WaitForRequest() => _called.Wait(TimeSpan.FromSeconds(5));

        public Task<AiGenerationResult> GenerateAsync(
            AiGenerationProviderProfile profile,
            AiGenerationRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            _called.Set();
            return Task.FromResult(new AiGenerationResult("assets/generated.png", "image/png", 1));
        }
    }

    private sealed class NeverRunner : IAiGenerationRunner
    {
        public System.Threading.Tasks.Task<AiGenerationResult> GenerateAsync(
            AiGenerationProviderProfile profile,
            AiGenerationRequest request,
            System.Threading.CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Runner must not be called by these tests.");
    }
}
